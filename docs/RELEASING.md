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
that push. Older releases cannot replace a newer repository entry. The initial
`repo.json` points to v1.1.0; its download becomes available only after that release
is published.

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

Offline verification passed with both the local fallback and `DALAMUD_HOME`: a stamped
1.1.0.0 package containing the DLL, manifest and layout, valid repository JSON, and the
full test suite. Release-helper behaviour was checked with mocked Git and dotnet
commands. Distribution downloads, hosted Actions, GitHub Releases, and the authenticated
push to `main` are verified by the first real release.
