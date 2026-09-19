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
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", version):
        raise ValueError("Version must be a stable major.minor.patch version")
    if any(int(part) > 65534 for part in version.split(".")):
        raise ValueError("Version components must fit a .NET assembly version (0..65534)")
    return version


def make_entry(manifest, version, timestamp):
    entry = dict(manifest)
    assembly_version = f"{version}.0"
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=ROOT / "MahjongHater.json")
    parser.add_argument("--props", type=Path, default=ROOT / "Directory.Build.props")
    parser.add_argument("--output", type=Path, default=ROOT / "repo.json")
    parser.add_argument("--tag", help="Require this tag to equal v<Version>")
    parser.add_argument("--timestamp", type=int, help="Unix seconds; defaults to the current time")
    args = parser.parse_args()
    try:
        version = read_version(args.props)
        if args.tag is not None and args.tag != f"v{version}":
            raise ValueError(f"Tag {args.tag!r} does not match v{version}")
        timestamp = int(time.time()) if args.timestamp is None else args.timestamp
        if timestamp < 0:
            raise ValueError("Timestamp must be nonnegative")
        manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
        entry = make_entry(manifest, version, timestamp)
        args.output.write_text(json.dumps([entry], ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8", newline="\n")
    except (OSError, ValueError, ET.ParseError) as error:
        parser.exit(1, f"error: {error}\n")


if __name__ == "__main__":
    main()
