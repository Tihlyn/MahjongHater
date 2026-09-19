[CmdletBinding(DefaultParameterSetName = 'Bump')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Version')]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,

    [Parameter(Mandatory, ParameterSetName = 'Bump')]
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $args failed (exit $LASTEXITCODE)." }
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if ((Invoke-Git branch --show-current) -ne 'main') {
        throw 'Releases must be made from main.'
    }
    if (Invoke-Git status --porcelain) { throw 'The working tree must be clean, including untracked files.' }

    Write-Host 'Fetching origin/main and tags to verify the release starting point...'
    Invoke-Git fetch origin main --tags
    if ((Invoke-Git rev-parse HEAD) -ne (Invoke-Git rev-parse origin/main)) {
        throw 'main must exactly match origin/main. Pull or push your changes first.'
    }

    $propsPath = Join-Path (Get-Location) 'Directory.Build.props'
    $propsText = [IO.File]::ReadAllText($propsPath)
    $versionNodes = @(([xml]$propsText).SelectNodes('/Project/PropertyGroup/Version'))
    if ($versionNodes.Count -ne 1) { throw 'Expected exactly one <Version> in Directory.Build.props.' }
    $current = $versionNodes[0].InnerText.Trim()
    if ($current -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'The current version must be major.minor.patch.'
    }
    if ($PSCmdlet.ParameterSetName -eq 'Bump') {
        $parts = @($current.Split('.') | ForEach-Object { [int]$_ })
        switch ($Bump) {
            'patch' { $parts[2]++ }
            'minor' { $parts[1]++; $parts[2] = 0 }
            'major' { $parts[0]++; $parts[1] = 0; $parts[2] = 0 }
        }
        $Version = $parts -join '.'
    }
    if (@($Version.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count) {
        throw 'Version components must fit a .NET assembly version (0..65534).'
    }
    if ([version]$Version -le [version]$current) { throw "The new version must be greater than $current." }
    $tag = "v$Version"
    if (Invoke-Git tag --list $tag) { throw "Tag $tag already exists." }

    $changelogPath = Join-Path (Get-Location) 'CHANGELOG.md'
    $changelog = [IO.File]::ReadAllText($changelogPath).Replace("`r`n", "`n")
    $unreleased = [regex]'(?m)^## Unreleased[ \t]*$'
    if ($unreleased.Matches($changelog).Count -ne 1) {
        throw 'CHANGELOG.md must have exactly one ## Unreleased heading.'
    }
    if ($changelog -match "(?m)^## \[?$([regex]::Escape($Version))\]?(?: |$)") {
        throw "CHANGELOG.md already has a heading for $Version."
    }

    Write-Host 'Running tests before changing the version...'
    & dotnet test MahjongHater.Tests -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no version changes were made.' }
    if (Invoke-Git status --porcelain) { throw 'Tests changed the working tree; inspect the changes first.' }

    $date = Get-Date -Format 'yyyy-MM-dd'
    $newChangelog = $unreleased.Replace($changelog, "## Unreleased`n`n## [$Version] - $date", 1)
    $newProps = [regex]::Replace($propsText, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($propsPath, $newProps.Replace("`r`n", "`n"), $utf8)
    [IO.File]::WriteAllText($changelogPath, $newChangelog, $utf8)
    Write-Host "Prepared $current -> $Version and dated the Unreleased notes $date."

    if ($DryRun) {
        Invoke-Git --no-pager diff -- Directory.Build.props CHANGELOG.md
        Write-Host 'Dry run: version and changelog edits remain for inspection; no commit, tag, or push.'
        Write-Host 'Restore those two files before running a real release from a clean tree.'
    } else {
        Invoke-Git add -- Directory.Build.props CHANGELOG.md
        Invoke-Git commit -m "chore(release): $tag"
        Invoke-Git tag -a $tag -m "MahjongHater $tag"
        # Publish the commit and tag together so a rejected main update cannot release a tag alone.
        Invoke-Git push --atomic origin main "refs/tags/$tag"
        Write-Host "Pushed main and $tag. CI will build, test, publish both ZIP assets, and update repo.json."
    }
    Write-Host 'Release workflow: https://github.com/Tihlyn/MahjongHater/actions/workflows/release.yml'
} finally {
    Pop-Location
}
