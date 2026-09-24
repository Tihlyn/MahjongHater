# Releasing MahjongHater

`Directory.Build.props` is the version source (`major.minor.patch`). The SDK turns it
into an assembly version with a fourth `.0` component, and DalamudPackager stamps
the packaged manifest. No source file carries a version attribute. The development
DLL stays at `bin/Release/net10.0-windows/MahjongHater.dll`.

CI uses Windows and .NET 10. It downloads Dalamud and sets `DALAMUD_HOME`; local builds
fall back to `%APPDATA%\XIVLauncher\addon\Hooks\dev` when that variable is unset.
The repository variable `DALAMUD_TRACK` selects `release` (default) or `staging`.
Manual CI runs can override it with the `dalamud_track` input. Downloads are fresh
on every run because the distribution's `latest.zip` changes over time.

## Opt-in testing builds

The same custom repository can offer a newer testing build while stable users keep the
published release. From a clean, tested `main` that includes the desired fixes:

```powershell
git fetch origin main --tags
# Incorporate any remote changes before publishing. Choose an unused version greater
# than the stable AssemblyVersion and the previous TestingAssemblyVersion in repo.json.
git tag -a testing-3.0.0.1 -m "MahjongHater 3.0.0.1 testing"
git push --atomic origin main refs/tags/testing-3.0.0.1
```

`testing-*` tags run `.github/workflows/testing-release.yml`; they do not trigger the
stable `v*` release workflow. The version must contain four numeric components. CI
overrides MSBuild `Version` for the candidate without changing `Directory.Build.props`,
tests it, validates the stamped manifest, and publishes a GitHub **prerelease** that is
not marked Latest. It then adds `TestingAssemblyVersion`, `TestingDalamudApiLevel`,
`TestingChangelog`, and `DownloadLinkTesting` to the existing `repo.json`. Stable version,
API level, install/update URLs, and other metadata are preserved (apart from LastUpdate).
Both release workflows share a concurrency lock. Older testing builds cannot replace
newer ones; rerunning the same version is allowed. Never move an existing published tag.

For players, keep the existing custom repository URL, enable plugin testing in Dalamud,
then opt MahjongHater into testing in the plugin installer. Confirm the installed version
is `3.0.0.1` for this first candidate. Users who do not opt in continue receiving v3.0.0.
To return to stable, opt out of testing and reinstall MahjongHater from the normal channel
if the installer does not automatically downgrade it. Disable any development-plugin copy
before loading the repository-installed plugin so only one copy is active.

A subsequent stable release uses the normal process below and clears the testing metadata
when it generates its new entry. Validate that release against the last candidate first.
The four-component candidate versions (`3.0.0.1`, `3.0.0.2`, ...) sort below the next stable
patch (`3.0.1.0`), allowing testers to return to normal updates.

Testing fields follow the [Dalamud custom repository protocol](https://dalamud.dev/plugin-publishing/custom-repositories/).

## Stable releases

From a clean `main` checkout matching `origin/main`, run:

```powershell
./tools/release.ps1 -Bump patch
# Alternatively: ./tools/release.ps1 -Version 1.2.3
```

The script fetches origin and tags, tests, bumps the version, dates the Unreleased
changelog notes, commits, creates an annotated tag, and atomically pushes main and
the tag. `-DryRun` still fetches and tests, and leaves version/changelog edits for
review, but does not commit, tag, or push. Restore those two files before a real run.

A `v*` tag triggers [the release workflow](https://github.com/Tihlyn/MahjongHater/actions/workflows/release.yml).
It rejects tag/version mismatches, builds and tests, generates notes from commits
since the previous reachable version tag, and publishes `latest.zip` plus
`MahjongHater-v<version>.zip`. It then generates `repo.json` from the stamped manifest
and commits it to `main` with `[skip ci]` using `GITHUB_TOKEN`. Branch rules must permit
that push. Older releases cannot replace a newer repository entry. `repo.json` on
`main` is whatever the last release workflow committed (v1.2.2 as of 2026-09-19); pull
that bot commit before starting the next release or the script's clean-checkout check
fails.

To regenerate the repository entry locally after a Release build:

```powershell
python tools/make_repo_json.py --manifest bin/Release/net10.0-windows/MahjongHater/MahjongHater.json
```

For a hotfix, merge the fix into `main`, pull the bot's latest `repo.json` commit,
add Unreleased notes, and release another patch version. Do not move published tags.
If a workflow fails, inspect its failing step. Correct source/version problems on
`main` and release a new version; rerun the existing workflow for transient download
or service failures. If only the repository push fails, check token permissions and
branch rules, then rerun; existing release assets can be uploaded again. Alternatively,
commit the generated entry manually from the affected tag's stamped manifest and props.
If the local atomic push fails, the release commit and tag remain locally: resolve the
push rejection before retrying that push, rather than running the bump again.

The pipeline has shipped v1.2.0 and v1.2.2 end to end (release workflow, GitHub Release
assets, and the bot's `chore(release): update repository for vX.Y.Z [skip ci]` commits
`99fe44f` / `94dd725`). Local builds work with both the `%APPDATA%` fallback and
`DALAMUD_HOME`; the packaged zip contains the DLL, the stamped manifest and
`resources/layouts/emj.json`.
