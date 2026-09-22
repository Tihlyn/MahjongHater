<#
.SYNOPSIS
  End-to-end training pipeline for the MahjongHater learned policy on one Windows box:
  fetch Tenhou Phoenix archives -> import corpus -> export dataset -> train (CUDA) -> parity check -> evaluate.

.DESCRIPTION
  Every stage is resumable and skips work whose output already exists. Outputs live under
  -WorkDirectory (default: .\work). Logs go to work\logs. See README.md for sizes and timings.

  Stages: fetch, import, export, train, check, eval (or 'all').

.EXAMPLE
  .\Run-Training.ps1                       # everything, default archives n1-n30, 24 000 games, res-net on the GPU
  .\Run-Training.ps1 -Stage fetch,import   # only download and build the corpus
  .\Run-Training.ps1 -Stage train,check,eval -Blocks 10 -Channels 192 -Epochs 30 -RunName res10
  .\Run-Training.ps1 -HandsInMatch 4 -RunName quick   # a Quick Match (East-only) model from the tonpuusen games
#>
param(
    [string[]]$Stage = @('all'),
    [string]$WorkDirectory = (Join-Path $PSScriptRoot 'work'),
    # Tenhou Phoenix archive numbers to fetch (https://tenhou.net/0/log/mjlog_pf4-20_n<N>.zip).
    [int[]]$Archives = (1..30),
    # Acting-player minimum rank (16 = 7 dan). Lower ranks add games of weaker players.
    [int]$MinimumRank = 16,
    # 8 = hanchan (Full Match), 4 = tonpuusen (Quick Match). The archives hold both; a model
    # only serves the length it was trained on (the plugin checks), so train one per length.
    [ValidateSet(4, 8)][int]$HandsInMatch = 8,
    # Games exported to the dense dataset (uniform hash sample). ~600 rows per game, 5.6 KB per row
    # in float16: 24 000 games ~ 80 GB. The export stage refuses to start without the disk space.
    [int]$MaxGames = 24000,
    [ValidateSet('float16', 'float32')][string]$Dtype = 'float16',
    [int]$Workers = [Math]::Max(1, [Environment]::ProcessorCount - 1),
    # Network and optimisation. blocks 8 / channels 160 / hidden 512 is ~5 M parameters; with every
    # split streamed through GPU memory the card, not the loader, sets the epoch time, so batches
    # are large. -WindowRows 0 sizes the two device-resident windows from free GPU memory.
    [int]$Blocks = 8,
    [int]$Channels = 160,
    [int]$Hidden = 512,
    [int]$Epochs = 20,
    [int]$Batch = 4096,
    [double]$LearningRate = 0.001,
    [int]$BufferRows = 262144,
    [int]$WindowRows = 0,
    # Regularisation: the first 11 M-row run started overfitting at epoch 6 without it.
    [double]$Dropout = 0.1,
    [ValidateSet('auto', 'cuda', 'cpu')][string]$Device = 'auto',
    [string]$RunName = 'res',
    # Evaluation: held-out test split of the corpus; 0 = every test game (slow: ~1 s per game per thread).
    [int]$EvalGames = 1500,
    [switch]$Resume
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'Precompute.exe'
$python = Join-Path $root '.venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Run this script from the portable package produced by tools/publish_training.ps1.' }
$work = [IO.Path]::GetFullPath($WorkDirectory)
$raw = Join-Path $work 'raw'
$length = if ($HandsInMatch -eq 8) { '' } else { "-$HandsInMatch" }
$rules = Join-Path $root $(if ($HandsInMatch -eq 8) { 'generation.json' } else { "generation-$HandsInMatch.json" })
$corpus = Join-Path $work "corpus$length"
$dataset = Join-Path $work "dataset-$MaxGames-$Dtype$length"
$model = Join-Path $work "model-$RunName"
$logs = Join-Path $work 'logs'
$evalDir = Join-Path $work 'eval'
foreach ($d in $work, $raw, $logs, $evalDir) { if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d | Out-Null } }
$stages = if ($Stage -contains 'all') { 'fetch', 'import', 'export', 'train', 'check', 'eval' } else { $Stage }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Invoke-Logged([string]$name, [scriptblock]$block) {
    $log = Join-Path $logs "$name-$stamp.log"
    Write-Host "== $name (log: $log)"
    $started = Get-Date
    # Native tools report progress on stderr; Windows PowerShell 5.1 would turn every such
    # line into a terminating error under 'Stop', so relax it for the call and go by exit code.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $block 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE; see $log" }
    Write-Host ("== $name done in {0:N0} s" -f ((Get-Date) - $started).TotalSeconds)
}

if ($stages -contains 'fetch') {
    $record = Join-Path $raw 'archives.json'
    $known = @{}
    if (Test-Path -LiteralPath $record) { (Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $known[$_.Name] = $_.Value } }
    foreach ($n in $Archives) {
        $name = "mjlog_pf4-20_n$n.zip"
        $file = Join-Path $raw $name
        $url = "https://tenhou.net/0/log/$name"
        if ((Test-Path -LiteralPath $file) -and $known.ContainsKey($name) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $known[$name].Sha256) {
            Write-Host "fetch: $name present ($([Math]::Round((Get-Item -LiteralPath $file).Length / 1MB)) MB)"
            continue
        }
        Write-Host "fetch: $url"
        $partial = "$file.partial"
        curl.exe --fail --location --silent --show-error --retry 5 --retry-delay 10 --output $partial $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
        Move-Item -LiteralPath $partial -Destination $file -Force
        $known[$name] = [pscustomobject]@{ Url = $url; Bytes = (Get-Item -LiteralPath $file).Length; Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; Fetched = (Get-Date).ToString('o') }
        [pscustomobject]$known | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $record -Encoding UTF8
    }
    $total = ($Archives | ForEach-Object { (Get-Item -LiteralPath (Join-Path $raw "mjlog_pf4-20_n$_.zip")).Length } | Measure-Object -Sum).Sum
    Write-Host ("fetch: {0} archives, {1:N0} MB, provenance in {2}" -f @($Archives.Count, ($total / 1MB), $record))
}

if ($stages -contains 'import') {
    if (Test-Path -LiteralPath $corpus) { Write-Host "import: corpus exists at $corpus (delete it to re-import)" }
    else {
        Invoke-Logged 'import' { & $exe replay-import $rules $raw $corpus $MinimumRank $Workers }
    }
}

if ($stages -contains 'export') {
    if (Test-Path -LiteralPath $dataset) { Write-Host "export: dataset exists at $dataset" }
    else {
        $bytesPerRow = 2820 * $(if ($Dtype -eq 'float16') { 2 } else { 4 })
        $needed = [long]$MaxGames * 600 * $bytesPerRow
        $free = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($work)).Substring(0, 1)).Free
        Write-Host ("export: ~{0:N1} GB expected for {1} games ({2}), {3:N0} GB free" -f @(($needed / 1GB), $MaxGames, $Dtype, ($free / 1GB)))
        if ($free -lt $needed * 1.15) { throw "Not enough disk space for the export; lower -MaxGames (each 1 000 games ~ $([Math]::Round(600 * $bytesPerRow / 1GB * 1000, 1)) GB) or free space." }
        Invoke-Logged 'export' { & $exe learn-data $corpus $dataset - $MaxGames $Dtype $Workers }
    }
}

if ($stages -contains 'train') {
    if (-not (Test-Path -LiteralPath $python)) { throw 'Run .\Setup-Training.ps1 first (creates .venv with torch).' }
    $trainArgs = @((Join-Path $root 'train.py'), $dataset, $model, '--epochs', $Epochs, '--batch', $Batch, '--threads', $Workers, '--lr', $LearningRate,
        '--channels', $Channels, '--hidden', $Hidden, '--blocks', $Blocks, '--device', $Device, '--buffer-rows', $BufferRows, '--window-rows', $WindowRows,
        '--dropout', $Dropout, '--memory-log')
    if ($Resume -or (Test-Path -LiteralPath (Join-Path $model 'checkpoint.pt'))) { $trainArgs += '--resume' }
    Invoke-Logged "train-$RunName" { & $python @trainArgs }
}

if ($stages -contains 'check') {
    Invoke-Logged "check-$RunName" { & $exe learn-check (Join-Path $model 'learned_policy.json') (Join-Path $model 'parity.json') }
}

if ($stages -contains 'eval') {
    $report = Join-Path $evalDir "$RunName-test.json"
    $games = if ($EvalGames -gt 0) { $EvalGames } else { 2147483647 }
    Invoke-Logged "eval-$RunName" { & $exe learn-eval $corpus $report (Join-Path $model 'learned_policy.json') test $games $Workers }
    Write-Host "eval: report at $report"
}

Write-Host ''
Write-Host 'Deliverables to send back:'
Write-Host "  $model\learned_policy.json  (the model: drop into the plugin's config folder as learned_policy.json)"
Write-Host "  $model\metrics.json         (training curves, test metrics)"
Write-Host "  $evalDir\*.json + logs\eval-*.log (harness comparison against the heuristics)"
Write-Host "  $raw\archives.json          (archive provenance)"
