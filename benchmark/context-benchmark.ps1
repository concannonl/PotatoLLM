# PotatoLLM context benchmark.
# Measures how reply speed holds up as the chat memory fills, with normal (16-bit)
# vs compact (8-bit) memory, on the current models and some newer candidates.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinDir  = Join-Path $Root 'llama'
$ModDir  = Join-Path $Root 'models'
$OldKit  = Join-Path ([Environment]::GetFolderPath('Desktop')) 'x250-llm-benchmark'
$Depth   = 8192   # how full the memory is for the "long chat" test
New-Item -ItemType Directory -Force -Path $BinDir, $ModDir | Out-Null

function Say($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------- 1. Machine ----------
Say "Checking this machine"
$cpu   = Get-CimInstance Win32_Processor | Select-Object -First 1
$cores = [int]$cpu.NumberOfCores
$logical = [int]$cpu.NumberOfLogicalProcessors
$ramGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
$os    = (Get-CimInstance Win32_OperatingSystem).Caption
$bat   = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
$onAC  = -not ($bat -and $bat.BatteryStatus -eq 1)
Write-Host "CPU: $($cpu.Name.Trim()) ($cores cores / $logical threads), RAM: $ramGB GB, $os"
if (-not $onAC) { Write-Host "WARNING: on battery. Plug in for accurate results." -ForegroundColor Yellow }

# ---------- 2. llama.cpp ----------
$bench = @($BinDir, (Join-Path $OldKit 'llama')) | ForEach-Object {
    Get-ChildItem -Path $_ -Recurse -Filter 'llama-bench.exe' -ErrorAction SilentlyContinue
} | Select-Object -First 1
if (-not $bench) {
    Say "Downloading llama.cpp (Windows CPU build)"
    $rels = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30' -Headers @{ 'User-Agent' = 'potatollm' }
    $asset = $null
    foreach ($r in $rels) {
        $asset = $r.assets | Where-Object {
            $_.name -match 'win' -and $_.name -match 'x64' -and $_.name -match '\.zip$' -and
            $_.name -match 'cpu|avx2' -and $_.name -notmatch 'cuda|vulkan|hip|sycl|opencl|arm64|cudart'
        } | Select-Object -First 1
        if ($asset) { break }
    }
    if (-not $asset) { throw "Could not find a Windows CPU build of llama.cpp." }
    $zip = Join-Path $Root $asset.name
    curl.exe -L --fail -o $zip $asset.browser_download_url
    Expand-Archive -Path $zip -DestinationPath $BinDir -Force
    Remove-Item $zip
    $bench = Get-ChildItem -Path $BinDir -Recurse -Filter 'llama-bench.exe' | Select-Object -First 1
}
Write-Host "Using $($bench.FullName)"

# ---------- 3. Models ----------
# 'repos' are tried in order; the Q4_K_M file name is looked up on Hugging Face.
$models = @(
    @{ name='Llama-3.2-1B (current, 4GB)';  gb=0.8; repos=@('bartowski/Llama-3.2-1B-Instruct-GGUF') },
    @{ name='LFM2.5-1.2B (candidate)';      gb=0.8; repos=@('LiquidAI/LFM2.5-1.2B-Instruct-GGUF','LiquidAI/LFM2.5-1.2B-GGUF') },
    @{ name='LFM2.5-2.6B (candidate)';      gb=1.6; repos=@('LiquidAI/LFM2.5-2.6B-GGUF') },
    @{ name='Llama-3.2-3B (current, 8GB)';  gb=2.0; repos=@('bartowski/Llama-3.2-3B-Instruct-GGUF') },
    @{ name='Gemma-3-4B (candidate)';       gb=2.5; repos=@('bartowski/google_gemma-3-4b-it-GGUF') }
)

function Find-Q4KM($repo) {
    try {
        $info = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$repo" -Headers @{ 'User-Agent' = 'potatollm' }
        $file = $info.siblings.rfilename | Where-Object { $_ -match 'Q4_K_M\.gguf$' -and $_ -notmatch 'mmproj' } | Select-Object -First 1
        if ($file) { return $file }
    } catch { }
    return $null
}

$toRun = @()
foreach ($m in $models) {
    if ($m.gb -gt ($ramGB - 2.5)) { Write-Host "Skipping $($m.name): too big for $ramGB GB" -ForegroundColor Yellow; continue }
    $path = $null
    foreach ($repo in $m.repos) {
        $file = Find-Q4KM $repo
        if (-not $file) { continue }
        $local = Split-Path $file -Leaf
        $existing = @((Join-Path $ModDir $local), (Join-Path $OldKit "models\$local")) |
            Where-Object { (Test-Path $_) -and (Get-Item $_).Length -gt 100MB } | Select-Object -First 1
        if ($existing) { $path = $existing; break }
        Say "Downloading $($m.name): $local"
        $path = Join-Path $ModDir $local
        curl.exe -L --fail -C - -o $path "https://huggingface.co/$repo/resolve/main/$file"
        if ($LASTEXITCODE -eq 0) { break } else { $path = $null }
    }
    if ($path) { $toRun += @{ name=$m.name; path=$path; gb=$m.gb } }
    else { Write-Host "Skipping $($m.name): couldn't find or download it" -ForegroundColor Yellow }
}

# ---------- 4. Benchmark ----------
# For each model and memory type:
#   short chat: prompt reading (512 tokens) + replies, memory empty
#   long chat:  replies with the memory already holding $Depth tokens
$kvTypes = @(
    @{ label='16-bit'; args=@('-ctk','f16','-ctv','f16') },
    @{ label='8-bit';  args=@('-ctk','q8_0','-ctv','q8_0','-fa','1') }
)
$results = @()
$logDir = Join-Path $Root 'context-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Run-Bench($modelPath, $extra, $logName) {
    $log = Join-Path $logDir $logName
    $ErrorActionPreference = 'Continue'
    $all = & $bench.FullName -m $modelPath @extra -o csv 2>&1
    $code = $LASTEXITCODE
    # Newer llama.cpp spells flash attention 'on' instead of '1'.
    if ($code -ne 0 -and ($extra -contains '-fa')) {
        $retry = @($extra | ForEach-Object { if ($_ -eq '1') { 'on' } else { $_ } })
        $all = & $bench.FullName -m $modelPath @retry -o csv 2>&1
        $code = $LASTEXITCODE
    }
    $ErrorActionPreference = 'Stop'
    $lines = @($all | ForEach-Object { "$_" })
    (@("exit code: $code", "args: $extra") + $lines) | Set-Content $log
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^build_commit,') { $start = $i; break } }
    if ($code -ne 0 -or $start -lt 0) {
        Write-Host "    failed (see $log)" -ForegroundColor Red
        $lines | Select-Object -Last 6 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkYellow }
        return @()
    }
    return @($lines[$start..($lines.Count-1)] | Where-Object { $_ -match '^(build_commit|")' } | ConvertFrom-Csv)
}

$n = 0
foreach ($m in $toRun) {
    $n++
    Say "[$n/$($toRun.Count)] $($m.name) - roughly $([math]::Ceiling($m.gb * 6)) minutes, leave the laptop alone"
    foreach ($kv in $kvTypes) {
        $safe = ($m.name -replace '[^A-Za-z0-9.-]','_') + '-' + $kv.label
        Write-Host "  $($kv.label) memory: short chat..."
        $short = Run-Bench $m.path (@('-t', "$logical", '-p', '512', '-n', '64', '-d', '0', '-r', '2') + $kv.args) "$safe-short.txt"
        Write-Host "  $($kv.label) memory: long chat ($Depth tokens)..."
        $long  = Run-Bench $m.path (@('-t', "$cores", '-p', '0', '-n', '64', '-d', "$Depth", '-r', '1') + $kv.args) "$safe-long.txt"

        $pp  = $short | Where-Object { [int]$_.n_prompt -gt 0 } | Select-Object -First 1
        $tg0 = $short | Where-Object { [int]$_.n_gen -gt 0 }    | Select-Object -First 1
        $tgD = $long  | Where-Object { [int]$_.n_gen -gt 0 }    | Select-Object -First 1
        $row = [pscustomobject]@{
            model        = $m.name
            memory       = $kv.label
            reading_tps  = if ($pp)  { [math]::Round([double]$pp.avg_ts, 1) }  else { $null }
            reply_empty  = if ($tg0) { [math]::Round([double]$tg0.avg_ts, 2) } else { $null }
            reply_8k     = if ($tgD) { [math]::Round([double]$tgD.avg_ts, 2) } else { $null }
            slowdown_pct = if ($tg0 -and $tgD) { [math]::Round(100 * (1 - [double]$tgD.avg_ts / [double]$tg0.avg_ts)) } else { $null }
            model_type   = if ($tg0) { $tg0.model_type } else { '' }
            build        = if ($tg0) { $tg0.build_commit } else { '' }
        }
        $results += $row
        Write-Host ("    reading {0} tok/s | reply {1} tok/s empty, {2} tok/s at 8k ({3}% slower)" -f `
            $row.reading_tps, $row.reply_empty, $row.reply_8k, $row.slowdown_pct)
    }
}

# ---------- 5. Save ----------
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$csvPath = Join-Path $Root "context-results-$env:COMPUTERNAME-$stamp.csv"
$txtPath = Join-Path $Root "context-results-$env:COMPUTERNAME-$stamp.txt"
$results | Export-Csv -NoTypeInformation -Path $csvPath
$table = $results | Format-Table model, memory, reading_tps, reply_empty, reply_8k, slowdown_pct -AutoSize | Out-String
@(
    "Machine: $env:COMPUTERNAME",
    "CPU:     $($cpu.Name.Trim()) ($cores C / $logical T)",
    "RAM:     $ramGB GB",
    "OS:      $os",
    "Power:   $(if ($onAC) {'AC'} else {'BATTERY'})",
    "Date:    $(Get-Date)",
    "Columns: reading = prompt reading speed; reply_empty / reply_8k = reply speed with empty memory / $Depth tokens in memory",
    "",
    $table
) | Set-Content $txtPath

Say "Done"
Write-Host $table
Write-Host "Send back:"
Write-Host "  $txtPath"
Write-Host "  $csvPath"
Write-Host "  and the 'context-logs' folder if anything failed."
