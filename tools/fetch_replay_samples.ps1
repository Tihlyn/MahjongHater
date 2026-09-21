param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/tenhou-parser-samples'))
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new sample output directory.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$revision = 'df83948546d424ca8c2abd2e48aba72da1e224d3'
$base = "https://raw.githubusercontent.com/MahjongRepository/tenhou-python-bot/$revision"
$records = @()
# Small upstream parser/system-test fixtures, not a curated high-rank corpus.
foreach ($number in 3, 15, 37) {
    $url = "$base/project/system_testing/fixtures/$number.txt"
    $file = Join-Path $destination "tenhou-$number.xml"
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $file
    $records += [pscustomobject]@{ File = [IO.Path]::GetFileName($file); Url = $url; Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
}
Invoke-WebRequest -UseBasicParsing -Uri "$base/LICENSE.txt" -OutFile (Join-Path $destination 'UPSTREAM-LICENSE.md')
[pscustomobject]@{
    Purpose = 'Parser validation only. Includes mixed ranks and bot/test games; not evidence of competitive playing strength or a licensed bulk training corpus.'
    Revision = $revision
    Files = $records
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'provenance.json') -Encoding UTF8
Write-Host "Downloaded three validation fixtures to $destination"
