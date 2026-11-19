# PotatoLLM quality test.
# Asks each candidate model the same everyday questions using the app's own settings,
# then writes a blind side-by-side report (models shown as A / B / C).
# Run with -ReportOnly to rebuild the report from the latest saved results without re-testing.

# Run with -Small to test the models for 4 GB laptops instead.
param([switch]$ReportOnly, [switch]$Small)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$BinDir = Join-Path $Root 'llama'
$ModDir = Join-Path $Root 'models'
$OldKit = Join-Path ([Environment]::GetFolderPath('Desktop')) 'x250-llm-benchmark'
New-Item -ItemType Directory -Force -Path $BinDir, $ModDir | Out-Null

function Say($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }

# Same as the app.
$Today = (Get-Date).ToString('dddd d MMMM yyyy', [Globalization.CultureInfo]::GetCultureInfo('en-GB'))
$SystemPrompt = "You are a helpful assistant running privately on the user's own computer, with no internet access. " +
                "Today is $Today. " +
                "If you are asked about recent or future events, or a fact you don't know, say you don't know instead of guessing. " +
                "Answer clearly and concisely in plain text. Do not use markdown, tables or headings."

$Paragraph = "The council has announced that from 1 March, household waste collections will move from weekly to fortnightly " +
  "for general rubbish, while food waste will continue to be collected every week. Residents will receive a new 23-litre food " +
  "caddy by the end of February and are asked to put all food scraps, including tea bags and eggshells, in it rather than in the " +
  "black bin. The council says the change will save around 1.2 million pounds a year and help it reach its target of recycling 65% " +
  "of household waste by 2035. Households with six or more people, or with medical needs, can apply for a larger black bin free of " +
  "charge through the council's website or by phone."

$Questions = @(
    @{ q = "Write a short, polite email to my landlord asking him to fix a leaking kitchen tap this week.";
       look = "Polite, short, asks for a specific timeframe, ready to send." },
    @{ q = "My electricity bill has a standing charge of 53p per day and a unit rate of 24.5p per kWh. I used 210 kWh in 30 days. How much should the bill be? Show the working.";
       look = "Correct answer: 30 x 53p = 15.90 + 210 x 24.5p = 51.45, total 67.35 pounds." },
    @{ q = "Explain what a pension is to someone who has never had a job. Use simple words and keep it under 100 words.";
       look = "Simple, accurate, actually under 100 words." },
    @{ q = "Give me a simple vegetarian dinner recipe for two, using things most people already have in the cupboard.";
       look = "Really vegetarian, realistic cupboard ingredients, clear steps." },
    @{ q = "Summarise this in one sentence:`n`n$Paragraph";
       look = "One sentence. Key points: general rubbish goes fortnightly from 1 March, food waste stays weekly (new caddy). No invented details." },
    @{ q = "A bat and a ball cost 1.10 pounds in total. The bat costs 1.00 pound more than the ball. How much does the ball cost?";
       look = "Correct answer: 5p (not 10p)." },
    @{ q = "What is the capital of Australia, and roughly how many people live in Australia?";
       look = "Canberra (not Sydney). About 27 million people." },
    @{ q = "My 8-year-old asked why the sky is blue. How can I explain it to her?";
       look = "Child-friendly, roughly right (sunlight is all colours; blue light gets scattered most by the air)." },
    @{ q = "Rewrite this text message so it sounds friendlier: `"Can't come tonight. Busy. Another time.`"";
       look = "Warmer, still short, keeps the meaning." },
    @{ q = "Who won the 2030 World Cup?";
       look = "Should say it doesn't know (it hasn't happened as far as the model knows) rather than invent a winner." }
)

if (-not $ReportOnly) {
# ---------- Engine ----------
$server = @($BinDir, (Join-Path $OldKit 'llama')) | ForEach-Object {
    Get-ChildItem -Path $_ -Recurse -Filter 'llama-server.exe' -ErrorAction SilentlyContinue
} | Select-Object -First 1
if (-not $server) {
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
    $server = Get-ChildItem -Path $BinDir -Recurse -Filter 'llama-server.exe' | Select-Object -First 1
}

# ---------- Models ----------
if ($Small) {
    $models = @(
        @{ name='Llama-3.2-1B (current)'; pattern='Llama-3\.2-1B.*Q4_K_M'; repo='bartowski/Llama-3.2-1B-Instruct-GGUF' },
        @{ name='LFM2.5-1.2B';            pattern='LFM2\.5-1\.2B.*Q4_K_M';  repo='LiquidAI/LFM2.5-1.2B-Instruct-GGUF' },
        @{ name='Gemma-3-1B';             pattern='gemma-3-1b.*Q4_K_M';     repo='bartowski/google_gemma-3-1b-it-GGUF' }
    )
} else {
    $models = @(
        @{ name='Llama-3.2-3B';           pattern='Llama-3\.2-3B.*Q4_K_M'; repo='bartowski/Llama-3.2-3B-Instruct-GGUF' },
        @{ name='LFM2.5-2.6B';            pattern='LFM2\.5-2\.6B.*Q4_K_M';  repo='LiquidAI/LFM2.5-2.6B-GGUF' },
        @{ name='Gemma-3-4B (current)';   pattern='gemma-3-4b.*Q4_K_M';     repo='bartowski/google_gemma-3-4b-it-GGUF' }
    )
}

function Find-Local($pattern) {
    @($ModDir, (Join-Path $OldKit 'models')) | ForEach-Object {
        Get-ChildItem -Path $_ -Filter '*.gguf' -ErrorAction SilentlyContinue
    } | Where-Object { $_.Name -match $pattern -and $_.Name -notmatch 'mmproj' -and $_.Length -gt 100MB } |
        Select-Object -First 1
}

foreach ($m in $models) {
    $f = Find-Local $m.pattern
    if (-not $f) {
        Say "Downloading $($m.name)"
        $info = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$($m.repo)" -Headers @{ 'User-Agent' = 'potatollm' }
        $file = $info.siblings.rfilename | Where-Object { $_ -match 'Q4_K_M\.gguf$' -and $_ -notmatch 'mmproj' } | Select-Object -First 1
        $dest = Join-Path $ModDir (Split-Path $file -Leaf)
        curl.exe -L --fail -C - -o $dest "https://huggingface.co/$($m.repo)/resolve/main/$file"
        $f = Get-Item $dest
    }
    $m.path = $f.FullName
}

# ---------- Run ----------
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$cores = [int]$cpu.NumberOfCores
$logical = [int]$cpu.NumberOfLogicalProcessors
$port = 18089
$base = "http://127.0.0.1:$port"
$logDir = Join-Path $Root 'quality-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Wait-Healthy($proc) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 300) {
        if ($proc.HasExited) { return $false }
        try {
            $r = Invoke-WebRequest -Uri "$base/health" -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Start-Engine($modelPath, $logName) {
    $tries = @(
        "-m `"$modelPath`" -c 4096 -t $cores -tb $logical -np 1 --no-webui --host 127.0.0.1 --port $port",
        "-m `"$modelPath`" -c 4096 -t $cores --host 127.0.0.1 --port $port"
    )
    $i = 0
    foreach ($argLine in $tries) {
        $i++
        $out = Join-Path $logDir "$logName-$i.out.txt"
        $err = Join-Path $logDir "$logName-$i.err.txt"
        $p = Start-Process -FilePath $server.FullName -ArgumentList $argLine -WindowStyle Hidden -PassThru `
             -RedirectStandardOutput $out -RedirectStandardError $err
        if (Wait-Healthy $p) { return $p }
        try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    }
    return $null
}

function Ask($question) {
    $body = @{
        messages    = @(@{ role = 'system'; content = $SystemPrompt }, @{ role = 'user'; content = $question })
        temperature = 0.7
        seed        = 42
        max_tokens  = 512
        stream      = $false
    } | ConvertTo-Json -Depth 5
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $resp = Invoke-WebRequest -Uri "$base/v1/chat/completions" -Method Post -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -UseBasicParsing -TimeoutSec 1200
    $secs = $sw.Elapsed.TotalSeconds
    $json = [Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray()) | ConvertFrom-Json
    $answer = [string]$json.choices[0].message.content
    $thinking = [string]$json.choices[0].message.reasoning_content
    $generated = $null
    if ($json.timings -and $json.timings.predicted_n) { $generated = [int]$json.timings.predicted_n }
    $tps = $null
    if ($json.timings -and $json.timings.predicted_per_second) { $tps = [math]::Round([double]$json.timings.predicted_per_second, 2) }
    return [pscustomobject]@{ answer = $answer.Trim(); seconds = [math]::Round($secs, 1); reply_tps = $tps
                              tokens = $generated; thinking_chars = $thinking.Length }
}

# Close any engine left over from an earlier run.
Get-Process llama-server -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $server.FullName } |
    Stop-Process -Force -ErrorAction SilentlyContinue

$results = @()
Write-Host "This takes about 30-45 minutes. Plug in the charger and leave the laptop alone." -ForegroundColor Yellow
$mi = 0
foreach ($m in $models) {
    $mi++
    Say "[$mi/$($models.Count)] Starting $($m.name)"
    $proc = Start-Engine $m.path ($m.name -replace '[^A-Za-z0-9.-]','_')
    if (-not $proc) { Write-Host "  Couldn't start this model; see $logDir" -ForegroundColor Red; continue }
    try {
        for ($qi = 0; $qi -lt $Questions.Count; $qi++) {
            Write-Host ("  Question {0}/{1}..." -f ($qi + 1), $Questions.Count) -NoNewline
            try {
                $r = Ask $Questions[$qi].q
                Write-Host (" {0}s" -f $r.seconds)
            } catch {
                Write-Host " failed: $($_.Exception.Message)" -ForegroundColor Red
                $r = [pscustomobject]@{ answer = "(no answer: $($_.Exception.Message))"; seconds = $null; reply_tps = $null; tokens = $null; thinking_chars = 0 }
            }
            $peak = $null
            try { $proc.Refresh(); $peak = [math]::Round($proc.PeakWorkingSet64 / 1MB) } catch { }
            $results += [pscustomobject]@{ model = $m.name; question = $qi; answer = $r.answer; seconds = $r.seconds
                                           reply_tps = $r.reply_tps; tokens = $r.tokens; thinking_chars = $r.thinking_chars; peak_mb = $peak }
        }
    } finally {
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        Start-Sleep -Seconds 2
    }
}


$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$jsonPath = Join-Path $Root ("quality-results-" + $(if ($Small) { 'small-' } else { '' }) + "$stamp.json")
$doc = [pscustomobject]@{
    machine = $env:COMPUTERNAME; date = (Get-Date).ToString('s'); system_prompt = $SystemPrompt
    questions = @($Questions | ForEach-Object { $_.q }); results = $results
}
$doc | ConvertTo-Json -Depth 5 | Out-File -FilePath $jsonPath -Encoding utf8
} else {
    $latest = Get-ChildItem -Path $Root -Filter 'quality-results-*.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { throw "No quality-results-*.json found in $Root. Run the full test first." }
    $jsonPath = $latest.FullName
    $stamp = $latest.BaseName -replace '^quality-results-', ''
    $raw = [IO.File]::ReadAllText($jsonPath, [Text.Encoding]::UTF8)
    $results = @((ConvertFrom-Json $raw).results)
    Write-Host "Rebuilding the report from $jsonPath"
}

# ---------- Report ----------
$htmlPath = Join-Path $Root "quality-report-$stamp.html"

# Blind labels in random order.
$names = @($results | ForEach-Object { $_.model } | Select-Object -Unique)
$shuffled = @($names | Get-Random -Count $names.Count)
$labels = @{}
for ($i = 0; $i -lt $shuffled.Count; $i++) { $labels[$shuffled[$i]] = [string][char](65 + $i) }
function HtmlText($s) { [System.Net.WebUtility]::HtmlEncode([string]$s) }

$sb = New-Object System.Text.StringBuilder
for ($qi = 0; $qi -lt $Questions.Count; $qi++) {
    [void]$sb.Append("<section><h2>Question $($qi + 1)</h2><p class='q'>$(HtmlText $Questions[$qi].q)</p>")
    [void]$sb.Append("<p class='look'><b>What to look for:</b> $(HtmlText $Questions[$qi].look)</p><div class='grid'>")
    foreach ($name in $shuffled) {
        $lab = $labels[$name]
        $r = $results | Where-Object { $_.model -eq $name -and [int]$_.question -eq $qi } | Select-Object -First 1
        [void]$sb.Append("<div class='card'><div class='head'><span class='label'>$lab</span>")
        [void]$sb.Append("<label class='pick'><input type='radio' name='q$qi' value='$lab'> Best</label></div>")
        [void]$sb.Append("<div class='ans'>$(HtmlText $r.answer)</div></div>")
    }
    [void]$sb.Append("</div></section>")
}

$reveal = New-Object System.Text.StringBuilder
foreach ($name in $shuffled) {
    $rs = @($results | Where-Object { $_.model -eq $name -and $_.seconds })
    $avgSecs = if ($rs.Count) { [math]::Round(($rs | Measure-Object seconds -Average).Average, 0) } else { '' }
    $tpsList = @($rs | Where-Object { $_.reply_tps } | ForEach-Object { $_.reply_tps })
    $avgTps = if ($tpsList.Count) { [math]::Round(($tpsList | Measure-Object -Average).Average, 1) } else { '' }
    $peaks = @($results | Where-Object { $_.model -eq $name -and $_.peak_mb } | ForEach-Object { [double]$_.peak_mb })
    $peakText = if ($peaks.Count) { "$((($peaks | Measure-Object -Maximum).Maximum)) MB" } else { '' }
    $thinks = @($results | Where-Object { $_.model -eq $name -and [int]$_.thinking_chars -gt 0 }).Count
    [void]$reveal.Append("<tr><td><b>$($labels[$name])</b></td><td>$(HtmlText $name)</td><td>$avgSecs s</td><td>$avgTps</td><td>$peakText</td><td>$thinks of $($Questions.Count) answers</td></tr>")
}

$html = @'
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>PotatoLLM blind quality test</title>
<style>
body{font-family:"Segoe UI",system-ui,sans-serif;margin:0;background:#f3f4f6;color:#1b1b1b;line-height:1.5}
main{max-width:1300px;margin:0 auto;padding:24px}
h1{margin:0 0 4px}.sub{color:#555;margin:0 0 24px}
section{background:#fff;border-radius:10px;padding:18px 20px;margin-bottom:18px;box-shadow:0 1px 2px rgba(0,0,0,.06)}
h2{font-size:15px;color:#666;margin:0}.q{font-size:17px;font-weight:600;white-space:pre-wrap;margin:6px 0}
.look{background:#fef9c3;border-radius:6px;padding:6px 10px;font-size:14px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px;margin-top:12px}
.card{border:1px solid #e3e5e8;border-radius:8px;padding:12px}
.head{display:flex;justify-content:space-between;align-items:center;margin-bottom:6px}
.label{background:#1d4ed8;color:#fff;border-radius:50%;width:28px;height:28px;display:inline-flex;align-items:center;justify-content:center;font-weight:700}
.pick{font-size:14px;color:#333;cursor:pointer}.ans{white-space:pre-wrap;font-size:14.5px}
#score{position:sticky;bottom:0;background:#111827;color:#fff;padding:12px 20px;border-radius:10px;display:flex;gap:20px;flex-wrap:wrap;align-items:center}
button{font:inherit;padding:6px 14px;border-radius:6px;border:0;background:#fbbf24;cursor:pointer}
table{border-collapse:collapse;margin-top:10px}td,th{padding:6px 14px;border-bottom:1px solid #ddd;text-align:left}
#reveal{display:none}
</style></head><body><main>
<h1>Blind quality test</h1>
<p class="sub">The same 10 questions, answered by each model. Models are hidden behind letters, shown in a random order. Pick the best answer for each question, then reveal.</p>
__QUESTIONS__
<section id="reveal"><h2>Which model is which</h2>
<table><tr><th></th><th>Model</th><th>Avg time per answer</th><th>Reply speed (tok/s)</th><th>Peak memory</th><th>Hidden thinking</th></tr>__REVEAL__</table></section>
<div id="score"><span id="tally">Pick a favourite for each question.</span><button onclick="document.getElementById('reveal').style.display='block';this.remove();location.hash='reveal'">Reveal models</button></div>
</main><script>
function tally(){var c={};document.querySelectorAll('input[type=radio]:checked').forEach(function(r){c[r.value]=(c[r.value]||0)+1});
var k=Object.keys(c).sort();document.getElementById('tally').textContent=k.length?'Your picks: '+k.map(function(x){return x+' = '+c[x]}).join(',  '):'Pick a favourite for each question.';}
document.addEventListener('change',tally);
</script></body></html>
'@
$html = $html.Replace('__QUESTIONS__', $sb.ToString()).Replace('__REVEAL__', $reveal.ToString())
[IO.File]::WriteAllText($htmlPath, $html, (New-Object Text.UTF8Encoding($false)))

Say "Done"
Write-Host "Opening the report in your browser..."
Write-Host "Report:  $htmlPath"
Write-Host "Send me: $jsonPath"
Start-Process $htmlPath
