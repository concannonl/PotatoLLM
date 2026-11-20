# Builds PotatoLLM: compiles the app, gathers the engine (llama.cpp) and PDF reader
# (Xpdf pdftotext) with their licences into dist\, then makes output\PotatoLLM-Setup.exe.
# Runs on GitHub Actions (see .github/workflows/build.yml) or on any Windows PC.

param([string]$Version = "0.0.0-dev")

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Root 'dist'
$Tmp  = Join-Path $Root 'build-tmp'
Remove-Item $Dist, $Tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Dist, "$Dist\engine", "$Dist\tools", "$Dist\licenses", $Tmp | Out-Null

$hdr = @{ 'User-Agent' = 'potatollm-build' }
if ($env:GITHUB_TOKEN) { $hdr['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

function Step($msg) { Write-Host ""; Write-Host "==> $msg" }

# ---------- 1. Compile the app ----------
Step "Compiling the app"
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$Dist\PotatoLLM.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.Management.dll `
    (Join-Path $Root 'src\PotatoLLM.cs')
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
# llama.cpp's own third-party licences (cpp-httplib, nlohmann/json, ...), if it lists them.
New-Item -ItemType Directory -Force -Path "$Dist\licenses\llama.cpp-third-party" | Out-Null
try {
    $files = Invoke-RestMethod -Uri "https://api.github.com/repos/ggml-org/llama.cpp/contents/licenses?ref=$llamaTag" -Headers $hdr
    foreach ($f in $files) { if ($f.type -eq 'file') { Invoke-WebRequest -Uri $f.download_url -OutFile "$Dist\licenses\llama.cpp-third-party\$($f.name).txt" } }
} catch { Write-Host "llama.cpp has no licenses folder at $llamaTag" }
Write-Host "llama.cpp third-party licences: $((Get-ChildItem "$Dist\licenses\llama.cpp-third-party").Count)"
if (Test-Path "$Dist\engine\libomp.dll") {
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/llvm/llvm-project/main/openmp/LICENSE.TXT" -OutFile "$Dist\licenses\llvm-openmp-LICENSE.txt"
}

# ---------- 3. PDF reader: Xpdf pdftotext ----------
# Xpdf's pdftotext is one self-contained program (no extra libraries). Its licence asks that the
# README, help files, COPYING and COPYING3 travel with it, and we attach its source to releases.
Step "Getting Xpdf (pdftotext)"
$XpdfVersion = '4.06'
Invoke-WebRequest -Uri "https://dl.xpdfreader.com/xpdf-tools-win-$XpdfVersion.zip" -OutFile "$Tmp\xpdf.zip"
Expand-Archive "$Tmp\xpdf.zip" -DestinationPath "$Tmp\xpdf"
$pdftotext = Get-ChildItem "$Tmp\xpdf" -Recurse -Filter 'pdftotext.exe' | Where-Object { $_.FullName -match 'bin64' } | Select-Object -First 1
if (-not $pdftotext) { throw "pdftotext.exe (64-bit) missing from the Xpdf download." }
New-Item -ItemType Directory -Force -Path "$Dist\tools\xpdf", "$Dist\licenses\xpdf" | Out-Null
Copy-Item $pdftotext.FullName "$Dist\tools\xpdf"
$xpdfRoot = Get-ChildItem "$Tmp\xpdf" -Directory | Select-Object -First 1
foreach ($name in 'README', 'COPYING', 'COPYING3', 'ANNOUNCE', 'CHANGES') {
    $f = Join-Path $xpdfRoot.FullName $name
    if (Test-Path $f) { Copy-Item $f "$Dist\licenses\xpdf\$name.txt" }
}
Get-ChildItem $xpdfRoot.FullName -Recurse -Filter 'pdftotext*' | Where-Object { $_.Extension -in '.txt', '.1', '.html' } |
    Copy-Item -Destination "$Dist\licenses\xpdf"
Write-Host "Xpdf ${XpdfVersion}: $((Get-ChildItem "$Dist\licenses\xpdf").Count) licence/help files: $((Get-ChildItem "$Dist\licenses\xpdf" | ForEach-Object { $_.Name }) -join ', ')"
if (-not (Test-Path "$Dist\licenses\xpdf\COPYING.txt")) { throw "Xpdf's COPYING file is missing; can't ship it without its licence." }
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'output') | Out-Null
Invoke-WebRequest -Uri "https://dl.xpdfreader.com/xpdf-$XpdfVersion.tar.gz" -OutFile (Join-Path $Root "output\xpdf-$XpdfVersion-source.tar.gz")

@(
    "PotatoLLM includes these open-source programs:",
    "",
    "llama.cpp $llamaTag (MIT licence) - https://github.com/ggml-org/llama.cpp",
    "  Source: https://github.com/ggml-org/llama.cpp/tree/$llamaTag",
    "  libomp.dll: LLVM OpenMP runtime (Apache License 2.0 with LLVM Exceptions) - https://github.com/llvm/llvm-project",
    "",
    "Xpdf $XpdfVersion pdftotext (GNU GPL v2 or v3) - https://www.xpdfreader.com",
    "  Unmodified Windows 64-bit binary from https://dl.xpdfreader.com/xpdf-tools-win-$XpdfVersion.zip",
    "  Source: https://dl.xpdfreader.com/xpdf-$XpdfVersion.tar.gz (also attached to each PotatoLLM release on GitHub)",
    "  Its README, COPYING (GPL v2) and COPYING3 (GPL v3) are in the xpdf folder.",
    "",
    "The AI model is downloaded separately on first run, under its own licence (Gemma Terms of Use)."
) | Set-Content "$Dist\licenses\SOURCES.txt"
Copy-Item (Join-Path $Root 'LICENSE') "$Dist\licenses\PotatoLLM-LICENSE.txt"
Copy-Item (Join-Path $Root 'THIRD_PARTY_NOTICES.md') "$Dist\licenses\THIRD_PARTY_NOTICES.txt"
Copy-Item (Join-Path $Root 'installer\terms.txt') "$Dist\licenses\TERMS-OF-USE.txt"

# ---------- 4. Installer ----------
Step "Making the installer"
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $c = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'; if (Test-Path $c) { $iscc = $c } }
if (-not $iscc) {
    choco install innosetup -y --no-progress | Out-Host
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
$env:APP_VERSION = $Version
& $iscc (Join-Path $Root 'installer\PotatoLLM.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
Remove-Item $Tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Done: $(Join-Path $Root 'output\PotatoLLM-Setup.exe')"
