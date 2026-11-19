# PotatoLLM long-context benchmark (Gemma models).
# Measures reply speed and peak memory with 16k and 32k tokens already in the chat memory,
# with normal (16-bit) and compact (8-bit) memory. Takes about 1.5 hours.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinDir = Join-Path $Root 'llama'
$ModDir = Join-Path $Root 'models'
$OldKit = Join-Path ([Environment]::GetFolderPath('Desktop')) 'x250-llm-benchmark'
$LogDir = Join-Path $Root 'long-context-logs'
New-Item -ItemType Directory -Force -Path $BinDir, $ModDir, $LogDir | Out-Null

function Say($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }

$cpu     = Get-CimInstance Win32_Processor | Select-Object -First 1
$cores   = [int]$cpu.NumberOfCores
$logical = [int]$cpu.NumberOfLogicalProcessors
$ramGB   = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
$bat     = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
$onAC    = -not ($bat -and $bat.BatteryStatus -eq 1)
Write-Host "CPU: $($cpu.Name.Trim()) ($cores cores / $logical threads), RAM: $ramGB GB"
if (-not $onAC) { Write-Host "WARNING: on battery. Plug in for accurate results." -ForegroundColor Yellow }

# ---------- llama.cpp ----------
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

# ---------- Models ----------
function Get-Model($pattern, $repo) {
    $f = @($ModDir, (Join-Path $OldKit 'models')) | ForEach-Object {
        Get-ChildItem -Path $_ -Filter '*.gguf' -ErrorAction SilentlyContinue
    } | Where-Object { $_.Name -match $pattern -and $_.Name -notmatch 'mmproj' -and $_.Length -gt 100MB } | Select-Object -First 1
    if ($f) { return $f.FullName }
    Say "Downloading $repo"
    $info = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$repo" -Headers @{ 'User-Agent' = 'potatollm' }
    $file = $info.siblings.rfilename | Where-Object { $_ -match 'Q4_K_M\.gguf$' -and $_ -notmatch 'mmproj' } | Select-Object -First 1
    $dest = Join-Path $ModDir (Split-Path $file -Leaf)
    curl.exe -L --fail -C - -o $dest "https://huggingface.co/$repo/resolve/main/$file"
    return $dest
}
$gemma1 = Get-Model 'gemma-3-1b.*Q4_K_M' 'bartowski/google_gemma-3-1b-it-GGUF'
$gemma4 = Get-Model 'gemma-3-4b.*Q4_K_M' 'bartowski/google_gemma-3-4b-it-GGUF'

# ---------- Runner (also tracks peak memory) ----------
function Run-Bench($modelPath, [string[]]$extra, $logName) {
    $attempts = @(, $extra)
    if ($extra -contains '-fa') { $attempts += , @($extra | ForEach-Object { if ($_ -eq '1') { 'on' } else { $_ } }) }
    foreach ($argList in $attempts) {
        $psi = New-Object Diagnostics.ProcessStartInfo
        $psi.FileName = $bench.FullName
        $psi.Arguments = ((@('-m', "`"$modelPath`"") + $argList + @('-o', 'csv')) -join ' ')
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        $outTask = $p.StandardOutput.ReadToEndAsync()
        $errTask = $p.StandardError.ReadToEndAsync()
        $peak = 0
        while (-not $p.HasExited) {
            try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
            Start-Sleep -Milliseconds 1000
        }
        $p.WaitForExit()
        $out = $outTask.Result; $err = $errTask.Result
        @("args: $($psi.Arguments)", "exit code: $($p.ExitCode)", "--- output ---", $out, "--- messages ---", $err) |
            Set-Content (Join-Path $LogDir $logName)
        $lines = @($out -split "`r?`n")
        $start = -1
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^build_commit,') { $start = $i; break } }
        if ($p.ExitCode -eq 0 -and $start -ge 0) {
            $rows = @($lines[$start..($lines.Count - 1)] | Where-Object { $_ -match '^(build_commit|")' } | ConvertFrom-Csv)
            return @{ rows = $rows; peak_mb = [math]::Round($peak / 1MB) }
        }
    }
    Write-Host "    failed (see $LogDir\$logName)" -ForegroundColor Red
    return $null
}

# ---------- Tests ----------
$f16 = @('-ctk', 'f16', '-ctv', 'f16')
$q8  = @('-ctk', 'q8_0', '-ctv', 'q8_0', '-fa', '1')
$tests = @(
    @{ model='Gemma-3-1B'; path=$gemma1; memory='16-bit'; kv=$f16; depth=0;     mins=1 },
    @{ model='Gemma-3-1B'; path=$gemma1; memory='8-bit';  kv=$q8;  depth=0;     mins=1 },
    @{ model='Gemma-3-1B'; path=$gemma1; memory='16-bit'; kv=$f16; depth=16384; mins=6 },
    @{ model='Gemma-3-1B'; path=$gemma1; memory='8-bit';  kv=$q8;  depth=16384; mins=6 },
    @{ model='Gemma-3-4B'; path=$gemma4; memory='16-bit'; kv=$f16; depth=16384; mins=18 },
    @{ model='Gemma-3-4B'; path=$gemma4; memory='8-bit';  kv=$q8;  depth=16384; mins=18 },
    @{ model='Gemma-3-4B'; path=$gemma4; memory='8-bit';  kv=$q8;  depth=32768; mins=40 }
)
$total = ($tests | ForEach-Object { $_.mins } | Measure-Object -Sum).Sum
Write-Host ""
Write-Host "About $total minutes in total. Plug in the charger, close other programs and leave the laptop alone." -ForegroundColor Yellow

$results = @()
$n = 0
foreach ($t in $tests) {
    $n++
    $label = "$($t.model), $($t.memory) memory, $($t.depth) tokens in memory"
    Say "[$n/$($tests.Count)] $label (about $($t.mins) min)"
    $args1 = @('-t', "$cores", '-p', '0', '-n', '32', '-d', "$($t.depth)", '-r', '1') + $t.kv
    $safe = ("$($t.model)-$($t.memory)-$($t.depth)" -replace '[^A-Za-z0-9.-]', '_') + '.txt'
    $r = Run-Bench $t.path $args1 $safe
    $tg = if ($r) { $r.rows | Where-Object { [int]$_.n_gen -gt 0 } | Select-Object -First 1 } else { $null }
    $row = [pscustomobject]@{
        model     = $t.model
        memory    = $t.memory
        in_memory = $t.depth
        reply_tps = if ($tg) { [math]::Round([double]$tg.avg_ts, 2) } else { $null }
        peak_mb   = if ($r) { $r.peak_mb } else { $null }
        build     = if ($tg) { $tg.build_commit } else { '' }
    }
    $results += $row
    Write-Host ("    reply {0} tok/s, peak memory {1} MB" -f $row.reply_tps, $row.peak_mb)
}

# ---------- Save ----------
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$csvPath = Join-Path $Root "long-context-results-$env:COMPUTERNAME-$stamp.csv"
$txtPath = Join-Path $Root "long-context-results-$env:COMPUTERNAME-$stamp.txt"
$results | Export-Csv -NoTypeInformation -Path $csvPath
$table = $results | Format-Table model, memory, in_memory, reply_tps, peak_mb -AutoSize | Out-String
@("Machine: $env:COMPUTERNAME", "CPU: $($cpu.Name.Trim()) ($cores C / $logical T)", "RAM: $ramGB GB",
  "Power: $(if ($onAC) {'AC'} else {'BATTERY'})", "Date: $(Get-Date)", "", $table) | Set-Content $txtPath

Say "Done"
Write-Host $table
Write-Host "Send back: $txtPath"
