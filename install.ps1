# Offline Assistant installer.
# Picks the best model for this computer, downloads the engine and model,
# builds the chat app and adds shortcuts. No admin rights needed.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$AppName = 'Offline Assistant'
$Here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Install = Join-Path $env:LOCALAPPDATA 'OfflineAssistant'
$Engine  = Join-Path $Install 'engine'
$Models  = Join-Path $Install 'models'

function Step($n, $msg) { Write-Host ""; Write-Host "[$n/6] $msg" -ForegroundColor Cyan }
function Fail($msg) {
    Write-Host ""
    Write-Host $msg -ForegroundColor Red
    exit 1
}

Write-Host "Installing $AppName" -ForegroundColor White

# ---------- 1. Check the computer ----------
Step 1 "Checking this computer"
if (-not [Environment]::Is64BitOperatingSystem) { Fail "Sorry, $AppName needs 64-bit Windows." }
$cpu      = Get-CimInstance Win32_Processor | Select-Object -First 1
$cores    = [int]$cpu.NumberOfCores
$logical  = [int]$cpu.NumberOfLogicalProcessors
$ramGB    = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
Write-Host "  $($cpu.Name.Trim()), $cores cores, $ramGB GB memory"

# Choose the model from memory size. Speed on these laptops is roughly
# 11 GB/s divided by the model file size, so bigger = smarter but slower.
if ($ramGB -ge 7) {
    $model = @{ label='standard (3B)'; gb=2.0; file='Llama-3.2-3B-Instruct-Q4_K_M.gguf'
                url='https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf' }
} else {
    $model = @{ label='compact (1B)'; gb=0.8; file='Llama-3.2-1B-Instruct-Q4_K_M.gguf'
                url='https://huggingface.co/bartowski/Llama-3.2-1B-Instruct-GGUF/resolve/main/Llama-3.2-1B-Instruct-Q4_K_M.gguf' }
}
Write-Host "  Chosen assistant size: $($model.label)"

New-Item -ItemType Directory -Force -Path $Install, $Engine, $Models | Out-Null
$freeGB = [math]::Round((Get-PSDrive ($Install.Substring(0,1))).Free / 1GB, 1)
if ($freeGB -lt ($model.gb + 1)) { Fail "Not enough disk space. Please free up at least $($model.gb + 1) GB and try again." }

# Close any running copy so files can be replaced.
Get-Process OfflineAssistant -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process llama-server -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Install*" } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# ---------- 2. Engine (llama.cpp) ----------
Step 2 "Getting the AI engine"
$server = Get-ChildItem -Path $Engine -Recurse -Filter 'llama-server.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $server) {
    $hdr  = @{ 'User-Agent' = 'offline-assistant-installer' }
    $rels = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30' -Headers $hdr
    $asset = $null
    foreach ($r in $rels) {
        $asset = $r.assets | Where-Object {
            $_.name -match 'win' -and $_.name -match 'x64' -and $_.name -match '\.zip$' -and
            $_.name -match 'cpu|avx2' -and $_.name -notmatch 'cuda|vulkan|hip|sycl|opencl|arm64|cudart'
        } | Select-Object -First 1
        if ($asset) { Write-Host "  llama.cpp $($r.tag_name)"; break }
    }
    if (-not $asset) { Fail "Couldn't find the AI engine download. Please check your internet connection and try again." }
    $zip = Join-Path $Install 'engine.zip'
    curl.exe -L --fail -o $zip $asset.browser_download_url
    if ($LASTEXITCODE -ne 0) { Fail "The engine download failed. Please check your internet connection and try again." }
    Expand-Archive -Path $zip -DestinationPath $Engine -Force
    Remove-Item $zip
    $server = Get-ChildItem -Path $Engine -Recurse -Filter 'llama-server.exe' | Select-Object -First 1
    if (-not $server) { Fail "The engine download didn't contain llama-server.exe." }
} else {
    Write-Host "  Already installed"
}

# ---------- 3. Model ----------
Step 3 "Getting the assistant ($($model.label), about $($model.gb) GB)"
$modelPath = Join-Path $Models $model.file
if (-not (Test-Path $modelPath) -or (Get-Item $modelPath).Length -lt 100MB) {
    # Reuse a copy from the benchmark kit if there is one, to save a download.
    $candidates = @(
        (Join-Path $Here "models\$($model.file)"),
        (Join-Path $Here "benchmark\models\$($model.file)"),
        (Join-Path (Split-Path $Here -Parent) "x250-llm-benchmark\models\$($model.file)"),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) "x250-llm-benchmark\models\$($model.file)")
    )
    $existing = $candidates | Where-Object { (Test-Path $_) -and (Get-Item $_).Length -gt 100MB } | Select-Object -First 1
    if ($existing) {
        Write-Host "  Copying from $existing"
        Copy-Item $existing $modelPath -Force
    } else {
        Write-Host "  Downloading. This can take a while on a slow connection."
        curl.exe -L --fail -C - -o $modelPath $model.url
        if ($LASTEXITCODE -ne 0) { Fail "The download stopped. Run the installer again and it will carry on where it left off." }
    }
} else {
    Write-Host "  Already downloaded"
}

# ---------- 4. Build the chat app ----------
Step 4 "Building the chat window"
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Fail "This computer is missing the .NET Framework 4, which Windows normally includes." }
$src = Join-Path $Here 'src\OfflineAssistant.cs'
$exe = Join-Path $Install 'OfflineAssistant.exe'
$ErrorActionPreference = 'Continue'
$buildOut = & $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $src 2>&1
$buildCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($buildCode -ne 0) {
    $errFile = Join-Path $Here 'build-errors.txt'
    $buildOut | ForEach-Object { "$_" } | Set-Content $errFile
    $buildOut | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
    Fail "Building the chat window failed. Details saved to $errFile"
}

# ---------- 5. Settings (chosen automatically, never shown to the user) ----------
Step 5 "Choosing the best settings for this computer"
# Benchmarks on an X250: replies run equally fast on 1 thread per physical core,
# reading long messages benefits from all logical threads.
$threads      = [math]::Max(1, $cores)
$threadsBatch = [math]::Max($threads, $logical)
@(
    "# Chosen automatically by the installer.",
    "engine=$($server.FullName)",
    "model=$modelPath",
    "context=4096",
    "threads=$threads",
    "threads_batch=$threadsBatch",
    "max_reply=768"
) | Set-Content -Path (Join-Path $Install 'config.ini') -Encoding UTF8
Write-Host "  $threads threads for writing, $threadsBatch for reading"

# ---------- 6. Shortcuts ----------
Step 6 "Adding shortcuts"
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $lnk = $shell.CreateShortcut((Join-Path $dir "$AppName.lnk"))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $Install
    $lnk.Description = 'A private AI assistant that runs on this computer'
    $lnk.Save()
}

Write-Host ""
Write-Host "All done! Opening $AppName..." -ForegroundColor Green
Write-Host "Next time, use the '$AppName' icon on your desktop."
Start-Process $exe
Start-Sleep -Seconds 3
