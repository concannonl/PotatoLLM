# X250 LLM benchmark - measures how fast small language models run on this laptop.
# Downloads llama.cpp (CPU build) and a few small models, runs llama-bench,
# and writes a results file you can send back.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinDir = Join-Path $Root 'llama'
$ModDir = Join-Path $Root 'models'
New-Item -ItemType Directory -Force -Path $BinDir, $ModDir | Out-Null

function Say($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------- 1. Machine info ----------
Say "Checking this machine"
$cpu   = (Get-CimInstance Win32_Processor | Select-Object -First 1)
$ramGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
$freeGB = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1)
$os    = (Get-CimInstance Win32_OperatingSystem).Caption
$sticks = Get-CimInstance Win32_PhysicalMemory
$memInfo = ($sticks | ForEach-Object { "$([math]::Round($_.Capacity/1GB))GB@$($_.ConfiguredClockSpeed)MHz" }) -join ' + '
$onAC = $true
$bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
if ($bat -and $bat.BatteryStatus -eq 1) { $onAC = $false }

Write-Host "CPU:   $($cpu.Name.Trim())  ($($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads)"
Write-Host "RAM:   $ramGB GB total, $freeGB GB free   [$memInfo]"
Write-Host "OS:    $os"
if (-not $onAC) {
    Write-Host "WARNING: running on battery. Plug in the charger for accurate results." -ForegroundColor Yellow
}

$diskFreeGB = [math]::Round((Get-PSDrive ($Root.Substring(0,1))).Free / 1GB, 1)
if ($diskFreeGB -lt 6) {
    Write-Host "Only $diskFreeGB GB disk space free here; need about 6 GB." -ForegroundColor Red
    exit 1
}

# ---------- 2. Get llama.cpp ----------
$bench = Get-ChildItem -Path $BinDir -Recurse -Filter 'llama-bench.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $bench) {
    Say "Downloading llama.cpp (latest Windows CPU build)"
    # The newest "release" may have no files (e.g. v1.0.0 just points at a nightly),
    # so look through recent releases, nightlies included, for a Windows x64 CPU build.
    $hdr = @{ 'User-Agent' = 'x250-bench' }
    $rels = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30' -Headers $hdr
    $rel = $null; $asset = $null
    foreach ($r in $rels) {
        $asset = $r.assets | Where-Object {
            $_.name -match 'win' -and $_.name -match 'x64' -and $_.name -match '\.zip$' -and
            $_.name -match 'cpu|avx2' -and $_.name -notmatch 'cuda|vulkan|hip|sycl|opencl|arm64|cudart'
        } | Select-Object -First 1
        if ($asset) { $rel = $r; break }
    }
    if (-not $asset) {
        Write-Host "Windows files seen in recent releases:" -ForegroundColor Yellow
        $rels | Select-Object -First 5 | ForEach-Object {
            Write-Host "  $($_.tag_name): $((($_.assets | Where-Object { $_.name -match 'win' }).name) -join ', ')"
        }
        throw "Could not find a Windows CPU build of llama.cpp. Send a screenshot of this window."
    }
    Write-Host "Release $($rel.tag_name): $($asset.name)"
    $zip = Join-Path $Root $asset.name
    curl.exe -L --fail -o $zip $asset.browser_download_url
    Expand-Archive -Path $zip -DestinationPath $BinDir -Force
    Remove-Item $zip
    $bench = Get-ChildItem -Path $BinDir -Recurse -Filter 'llama-bench.exe' | Select-Object -First 1
    if (-not $bench) { throw "llama-bench.exe not found in the download." }
}

# ---------- 3. Get models ----------
# Speed test only - quality comes later. Sizes are approximate file sizes.
$models = @(
    @{ name='Llama-3.2-1B Q4_0';   gb=0.8; file='Llama-3.2-1B-Instruct-Q4_0.gguf';
       url='https://huggingface.co/bartowski/Llama-3.2-1B-Instruct-GGUF/resolve/main/Llama-3.2-1B-Instruct-Q4_0.gguf' },
    @{ name='Llama-3.2-1B Q4_K_M'; gb=0.8; file='Llama-3.2-1B-Instruct-Q4_K_M.gguf';
       url='https://huggingface.co/bartowski/Llama-3.2-1B-Instruct-GGUF/resolve/main/Llama-3.2-1B-Instruct-Q4_K_M.gguf' },
    @{ name='Qwen2.5-1.5B Q4_K_M'; gb=1.1; file='qwen2.5-1.5b-instruct-q4_k_m.gguf';
       url='https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf' },
    @{ name='Llama-3.2-3B Q4_K_M'; gb=2.0; file='Llama-3.2-3B-Instruct-Q4_K_M.gguf';
       url='https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf' }
)

$toRun = @()
foreach ($m in $models) {
    # Skip models that won't fit comfortably in RAM (leaves room for Windows).
    if ($m.gb -gt ($ramGB - 2.5)) {
        Write-Host "Skipping $($m.name): too big for $ramGB GB RAM" -ForegroundColor Yellow
        continue
    }
    $path = Join-Path $ModDir $m.file
    if (-not (Test-Path $path)) {
        Say "Downloading $($m.name) (~$($m.gb) GB)"
        curl.exe -L --fail -C - -o $path $m.url
    }
    $toRun += @{ name=$m.name; path=$path }
}

# ---------- 4. Benchmark ----------
# pp = prompt processing (reading your input), tg = text generation (writing the reply).
# Tests 2 threads (one per physical core) vs 4 (with hyperthreading).
$csvAll = @()
$i = 0
foreach ($m in $toRun) {
    $i++
    Say "Benchmarking $($m.name)  [$i of $($toRun.Count)]  - takes a few minutes, leave the laptop alone"
    $log = Join-Path $Root ("log-" + ($m.name -replace '[^A-Za-z0-9.-]','_') + ".txt")
    $ErrorActionPreference = 'Continue'
    $all = & $bench.FullName -m $m.path -t '2,4' -p 128 -n 64 -r 3 -o csv 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $errLines = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    $out      = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    (@("exit code: $code", "--- output ---") + $out + @("--- messages ---") + $errLines) | Set-Content -Path $log
    $csvStart = [array]::FindIndex([string[]]$out, [Predicate[string]]{ param($l) $l -match 'n_threads' })
    if ($code -ne 0 -or $csvStart -lt 0) {
        Write-Host "  FAILED for $($m.name) (exit code $code). Last messages:" -ForegroundColor Red
        ($errLines + $out) | Select-Object -Last 12 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
        Write-Host "  Full log: $log"
        if ($code -eq -1073741515) {
            Write-Host "  CAUSE: a Windows system file (DLL) is missing. Usually fixed by installing the" -ForegroundColor Yellow
            Write-Host "  Microsoft Visual C++ Redistributable: https://aka.ms/vs/17/release/vc_redist.x64.exe" -ForegroundColor Yellow
        } elseif ($code -eq -1073741795) {
            Write-Host "  CAUSE: this llama.cpp build uses CPU instructions this laptop doesn't have." -ForegroundColor Yellow
        }
        continue
    }
    $out = $out[$csvStart..($out.Count-1)]
    $rows = $out | ConvertFrom-Csv
    foreach ($r in $rows) {
        $test = if ([int]$r.n_prompt -gt 0) { "pp$($r.n_prompt)" } else { "tg$($r.n_gen)" }
        $csvAll += [pscustomobject]@{
            model      = $m.name
            threads    = $r.n_threads
            test       = $test
            tokens_sec = [math]::Round([double]$r.avg_ts, 2)
            stdev      = [math]::Round([double]$r.stddev_ts, 2)
            backend    = $r.backends
            build      = $r.build_commit
        }
        Write-Host ("  {0,-3} threads  {1,-6} {2,7} tok/s" -f $r.n_threads, $test, [math]::Round([double]$r.avg_ts, 2))
    }
}

# ---------- 5. Save results ----------
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$name  = "$env:COMPUTERNAME-$stamp"
$csvPath = Join-Path $Root "results-$name.csv"
$txtPath = Join-Path $Root "results-$name.txt"
$csvAll | Export-Csv -NoTypeInformation -Path $csvPath

$header = @(
    "Machine: $env:COMPUTERNAME",
    "CPU:     $($cpu.Name.Trim()) ($($cpu.NumberOfCores)C/$($cpu.NumberOfLogicalProcessors)T)",
    "RAM:     $ramGB GB [$memInfo]",
    "OS:      $os",
    "Power:   $(if ($onAC) {'AC'} else {'BATTERY'})",
    "Date:    $(Get-Date)",
    ""
)
$table = $csvAll | Format-Table model, threads, test, tokens_sec, stdev -AutoSize | Out-String
($header + $table) | Set-Content -Path $txtPath

Say "Done"
Write-Host $table
Write-Host "Results saved to:"
Write-Host "  $txtPath"
Write-Host "  $csvPath"
Write-Host "Send those two files back."
