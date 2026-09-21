param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/simulator-win-x64')
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$archive = $destination + '.zip'
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $archive)) {
    throw "Output already exists. Choose a new -OutputDirectory; existing packages and data are preserved."
}

dotnet publish (Join-Path $repo 'tools/Precompute/Precompute.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $destination
if ($LASTEXITCODE -ne 0) { throw "Simulator publish failed ($LASTEXITCODE)." }

& (Join-Path $destination 'Precompute.exe') sim-config (Join-Path $destination 'generation.json')
if ($LASTEXITCODE -ne 0) { throw 'Default configuration generation failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'tools/Run-Simulator.ps1') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'docs/SIMULATOR.md') -Destination (Join-Path $destination 'README.md')
Copy-Item -LiteralPath (Join-Path $repo 'docs/REPLAY_IMPORT.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'tools/fetch_replay_samples.ps1') -Destination $destination
New-Item -ItemType Directory -Path (Join-Path $destination 'research') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'docs/research/MAHJONG_AI_APPROACH.md') -Destination (Join-Path $destination 'research')
Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Portable package: $archive"
Write-Host 'Extract on the compute box, open PowerShell there, and follow README.md.'
