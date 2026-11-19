# Builds Offline Assistant: compiles the app, gathers the engine (llama.cpp) and PDF reader
# (Poppler pdftotext) with their licences into dist\, then makes output\OfflineAssistant-Setup.exe.
# Runs on GitHub Actions (see .github/workflows/build.yml) or on any Windows PC.

param([string]$Version = "0.0.0-dev")

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Root 'dist'
$Tmp  = Join-Path $Root 'build-tmp'
Remove-Item $Dist, $Tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Dist, "$Dist\engine", "$Dist\tools\poppler", "$Dist\licenses", $Tmp | Out-Null

$hdr = @{ 'User-Agent' = 'potatollm-build' }
if ($env:GITHUB_TOKEN) { $hdr['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

function Step($msg) { Write-Host ""; Write-Host "==> $msg" }

# ---------- 1. Compile the app ----------
Step "Compiling the app"
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$Dist\OfflineAssistant.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.Management.dll `
    (Join-Path $Root 'src\OfflineAssistant.cs')
if ($LASTEXITCODE -ne 0) { throw "The app didn't compile (see the errors above)." }

# ---------- 2. Engine: llama.cpp Windows CPU build ----------
Step "Getting llama.cpp"
$rels = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30' -Headers $hdr
$llamaTag = $null; $asset = $null
foreach ($r in $rels) {
    $asset = $r.assets | Where-Object {
        $_.name -match 'win' -and $_.name -match 'x64' -and $_.name -match '\.zip$' -and
        $_.name -match 'cpu|avx2' -and $_.name -notmatch 'cuda|vulkan|hip|sycl|opencl|arm64|cudart'
    } | Select-Object -First 1
    if ($asset) { $llamaTag = $r.tag_name; break }
}
if (-not $asset) { throw "No Windows CPU build of llama.cpp found." }
Write-Host "llama.cpp $llamaTag ($($asset.name))"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile "$Tmp\llama.zip"
Expand-Archive "$Tmp\llama.zip" -DestinationPath "$Tmp\llama"
# The server plus every DLL, side by side (the CPU backends are loaded from the exe's folder).
Get-ChildItem "$Tmp\llama" -Recurse -Include 'llama-server.exe', '*.dll' | Copy-Item -Destination "$Dist\engine"
if (-not (Test-Path "$Dist\engine\llama-server.exe")) { throw "llama-server.exe missing from the llama.cpp download." }
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/ggml-org/llama.cpp/$llamaTag/LICENSE" -OutFile "$Dist\licenses\llama.cpp-LICENSE.txt"

# ---------- 3. PDF reader: Poppler pdftotext ----------
Step "Getting Poppler (pdftotext)"
$pop = Invoke-RestMethod -Uri 'https://api.github.com/repos/oschwartz10612/poppler-windows/releases/latest' -Headers $hdr
$popAsset = $pop.assets | Where-Object { $_.name -match '\.zip$' } | Select-Object -First 1
if (-not $popAsset) { throw "No Poppler for Windows download found." }
Write-Host "Poppler $($pop.tag_name) ($($popAsset.name))"
Invoke-WebRequest -Uri $popAsset.browser_download_url -OutFile "$Tmp\poppler.zip"
Expand-Archive "$Tmp\poppler.zip" -DestinationPath "$Tmp\poppler"
$pdftotext = Get-ChildItem "$Tmp\poppler" -Recurse -Filter 'pdftotext.exe' | Select-Object -First 1
if (-not $pdftotext) { throw "pdftotext.exe missing from the Poppler download." }
Copy-Item $pdftotext.FullName "$Dist\tools\poppler"
Get-ChildItem $pdftotext.DirectoryName -Filter '*.dll' | Copy-Item -Destination "$Dist\tools\poppler"
$popVersion = ($pop.tag_name -replace '^v', '') -replace '-\d+$', ''
Invoke-WebRequest -Uri "https://gitlab.freedesktop.org/poppler/poppler/-/raw/poppler-$popVersion/COPYING" -OutFile "$Dist\licenses\poppler-COPYING.txt"
@(
    "Offline Assistant includes these open-source programs:",
    "",
    "llama.cpp $llamaTag (MIT licence) - https://github.com/ggml-org/llama.cpp",
    "  Source: https://github.com/ggml-org/llama.cpp/tree/$llamaTag",
    "",
    "Poppler $popVersion, pdftotext and its libraries (GPL) - https://poppler.freedesktop.org",
    "  Windows build: https://github.com/oschwartz10612/poppler-windows/releases/tag/$($pop.tag_name)",
    "  Source: https://poppler.freedesktop.org/poppler-$popVersion.tar.xz",
    "  Other libraries in this build come from conda-forge; their sources are listed at https://conda-forge.org",
    "",
    "The AI model is downloaded separately on first run, under its own licence (Gemma Terms of Use)."
) | Set-Content "$Dist\licenses\SOURCES.txt"

# ---------- 4. Installer ----------
Step "Making the installer"
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $c = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'; if (Test-Path $c) { $iscc = $c } }
if (-not $iscc) {
    choco install innosetup -y --no-progress | Out-Host
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
$env:APP_VERSION = $Version
& $iscc (Join-Path $Root 'installer\OfflineAssistant.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
Remove-Item $Tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Done: $(Join-Path $Root 'output\OfflineAssistant-Setup.exe')"
