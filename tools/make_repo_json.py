#!/usr/bin/env python3
"""Build the Dalamud repository entry from a manifest and Directory.Build.props."""

import argparse
import json
from pathlib import Path
import re
import time
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent.parent


def read_version(props):
    versions = ET.parse(props).getroot().findall("./PropertyGroup/Version")
    if len(versions) != 1 or not versions[0].text:
        raise ValueError("Expected exactly one <Version> in Directory.Build.props")
    version = versions[0].text.strip()
    validate_version(version)
    return version


def validate_version(version):
    # major.minor.patch, optionally with the .NET assembly's fourth component (2.0.2.1).
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(\.(0|[1-9]\d*))?", version):
        raise ValueError("Version must be major.minor.patch, optionally with a fourth component")
    if any(int(part) > 65534 for part in version.split(".")):
        raise ValueError("Version components must fit a .NET assembly version (0..65534)")


def version_tuple(version):
    validate_version(version)
    return tuple(int(part) for part in version.split(".")) + (0,) * (4 - len(version.split(".")))


def make_entry(manifest, version, timestamp):
    entry = dict(manifest)
    assembly_version = version if version.count(".") == 3 else f"{version}.0"
    if entry.get("AssemblyVersion", assembly_version) != assembly_version:
        raise ValueError("Manifest AssemblyVersion does not match <Version>; rebuild first")
    if entry.get("InternalName") != "MahjongHater":
        raise ValueError("Expected the MahjongHater manifest")
    if type(entry.get("DalamudApiLevel")) is not int:
        raise ValueError("Manifest must contain an integer DalamudApiLevel")
    url = f"https://github.com/Tihlyn/MahjongHater/releases/download/v{version}/latest.zip"
    entry.update(
        AssemblyVersion=assembly_version,
        DownloadLinkInstall=url,
        DownloadLinkUpdate=url,
        DownloadLinkTesting=url,
        IsHide=False,
        IsTestingExclusive=False,
        LastUpdate=timestamp,
    )
    return entry


def make_testing_entry(stable, manifest, version, timestamp, changelog):
    """Preserve stable metadata/URLs; change only the explicitly opted-in channel."""
    candidate = version_tuple(version)
    if len(version.split(".")) != 4:
        raise ValueError("Testing versions must have four components, e.g. 3.0.0.1")
    if stable.get("InternalName") != "MahjongHater" or manifest.get("InternalName") != "MahjongHater":
        raise ValueError("Expected the MahjongHater manifest and repository entry")
    if candidate <= version_tuple(stable["AssemblyVersion"]):
        raise ValueError("Testing version must be newer than the stable version")
    previous = stable.get("TestingAssemblyVersion")
    if previous and candidate < version_tuple(previous):
        raise ValueError("A newer testing version is already published")
    if manifest.get("AssemblyVersion") != version:
        raise ValueError("Packaged AssemblyVersion does not match the testing version; rebuild first")
    if type(manifest.get("DalamudApiLevel")) is not int:
        raise ValueError("Manifest must contain an integer DalamudApiLevel")
    if stable.get("IsTestingExclusive") or not all(stable.get(k) for k in ("DownloadLinkInstall", "DownloadLinkUpdate")):
        raise ValueError("An existing stable release is required")
    entry = dict(stable)
    entry.update(
        TestingAssemblyVersion=version,
        TestingDalamudApiLevel=manifest["DalamudApiLevel"],
        TestingChangelog=changelog,
        DownloadLinkTesting=f"https://github.com/Tihlyn/MahjongHater/releases/download/testing-{version}/latest.zip",
        LastUpdate=timestamp,
    )
    return entry


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=ROOT / "MahjongHater.json")
    parser.add_argument("--props", type=Path, default=ROOT / "Directory.Build.props")
    parser.add_argument("--output", type=Path, default=ROOT / "repo.json")
    parser.add_argument("--tag", help="Require this tag to equal v<Version>")
    parser.add_argument("--timestamp", type=int, help="Unix seconds; defaults to the current time")
    parser.add_argument("--testing-version", help="Publish only this four-component testing version")
    parser.add_argument("--base-repo", type=Path, help="Existing repository JSON whose stable release must be preserved")
    parser.add_argument("--testing-changelog", default="", help="Notes displayed for the testing build")
    args = parser.parse_args()
    try:
        version = args.testing_version or read_version(args.props)
        validate_version(version)
        expected_tag = f"testing-{version}" if args.testing_version else f"v{version}"
        if args.tag is not None and args.tag != expected_tag:
            raise ValueError(f"Tag {args.tag!r} does not match {expected_tag}")
        timestamp = int(time.time()) if args.timestamp is None else args.timestamp
        if timestamp < 0:
            raise ValueError("Timestamp must be nonnegative")
        manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
        if args.testing_version:
            if args.base_repo is None:
                raise ValueError("--testing-version requires --base-repo")
            existing = json.loads(args.base_repo.read_text(encoding="utf-8-sig"))
            if not isinstance(existing, list) or len(existing) != 1:
                raise ValueError("Expected exactly one existing repository entry")
            entry = make_testing_entry(existing[0], manifest, version, timestamp, args.testing_changelog)
        else:
            if args.base_repo is not None:
                raise ValueError("--base-repo is only supported with --testing-version")
            entry = make_entry(manifest, version, timestamp)
        args.output.write_text(json.dumps([entry], ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8", newline="\n")
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        parser.exit(1, f"error: {error}\n")


if __name__ == "__main__":
    main()
