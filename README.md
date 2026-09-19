# MahjongHater

A Dalamud plugin for FFXIV Doman Mahjong with discard advice, opponent risk estimates, and han/fu scoring.

## Install

In Dalamud Settings, open **Experimental**, paste
`https://raw.githubusercontent.com/Tihlyn/MahjongHater/main/repo.json` into the custom plugin
repositories list, and save. Open the plugin installer and install **MahjongHater**.
The repository requires a published release before installation is available.

## Releasing

From a clean, up-to-date `main` checkout, run `./tools/release.ps1 -Bump patch` in PowerShell.
The script tests, bumps the version and changelog, commits, tags, and pushes; CI builds and tests
the plugin, publishes the release, and updates the custom repository. See
[the release guide](docs/RELEASING.md) for explicit versions, dry runs, and recovery.
