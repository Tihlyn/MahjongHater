param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/training-win-x64')
)

# Self-contained Windows package: Precompute.exe (fetch/import/export/check/eval), train.py,
# the setup/run scripts and the docs. No repository, .NET SDK or game client on the target box.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$archive = $destination + '.zip'
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $archive)) {
    throw "Output already exists. Choose a new -OutputDirectory; existing packages and data are preserved."
}

dotnet publish (Join-Path $repo 'tools/Precompute/Precompute.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $destination
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
Remove-Item -LiteralPath (Join-Path $destination 'Precompute.pdb') -ErrorAction SilentlyContinue

& (Join-Path $destination 'Precompute.exe') sim-config (Join-Path $destination 'generation.json')
if ($LASTEXITCODE -ne 0) { throw 'Default configuration generation failed.' }
# Same profile for Quick Match (tonpuusen) imports: only the match length differs.
$hanchan = Get-Content -LiteralPath (Join-Path $destination 'generation.json') -Raw
if ($hanchan -notmatch '"HandsInMatch": 8') { throw 'Unexpected generation.json layout.' }
Set-Content -LiteralPath (Join-Path $destination 'generation-4.json') -Value ($hanchan -replace '"HandsInMatch": 8', '"HandsInMatch": 4') -Encoding UTF8
foreach ($file in 'Setup-Training.ps1', 'Run-Training.ps1', 'check_env.py', 'requirements.txt') {
    Copy-Item -LiteralPath (Join-Path $repo "tools/training/$file") -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $repo 'tools/learning/train.py') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'docs/TRAINING_PACKAGE.md') -Destination (Join-Path $destination 'README.md')
Copy-Item -LiteralPath (Join-Path $repo 'docs/REPLAY_IMPORT.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'docs/research/EVALUATION.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'docs/research/STRENGTH_COMPARISON.md') -Destination $destination
$commit = (git -C $repo rev-parse HEAD 2>$null)
[pscustomobject]@{ Repository = 'MahjongHater'; Branch = (git -C $repo rev-parse --abbrev-ref HEAD 2>$null); Commit = $commit; Built = (Get-Date).ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'PACKAGE.json') -Encoding UTF8
Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Portable training package: $archive"
Write-Host 'On the compute box: extract, open PowerShell in the folder, run .\Setup-Training.ps1 then .\Run-Training.ps1 (README.md).'
