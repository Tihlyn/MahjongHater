param(
    [int]$Workers = 12,
    [int]$Matches = 10000,
    [string]$Config = (Join-Path $PSScriptRoot 'generation.json'),
    [string]$RunDirectory = (Join-Path $PSScriptRoot 'runs/first')
)

$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'Precompute.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw 'Run this script from the portable package produced by tools/publish_simulator.ps1.'
}
& $executable sim-run $Config $RunDirectory $Workers $Matches
exit $LASTEXITCODE
