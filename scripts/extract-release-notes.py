#!/usr/bin/env python3
"""Extract one version section from CHANGELOG.md without including the next heading."""

from __future__ import annotations

import argparse
import re
from pathlib import Path


def extract_release_notes(changelog: str, version: str) -> str:
    heading = re.compile(rf"^## \[{re.escape(version)}\](?:\s+-\s+.*)?$", re.MULTILINE)
    match = heading.search(changelog)
    if match is None:
        raise ValueError(f"CHANGELOG.md has no [{version}] release heading")

    following = re.search(r"^## \[", changelog[match.end() :], re.MULTILINE)
    end = match.end() + following.start() if following else len(changelog)
    notes = changelog[match.end() : end].strip()
    if not notes:
        raise ValueError(f"CHANGELOG.md [{version}] release notes are empty")
    return notes + "\n"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--changelog", type=Path, default=Path("CHANGELOG.md"))
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    notes = extract_release_notes(args.changelog.read_text(encoding="utf-8"), args.version)
    args.output.write_text(notes, encoding="utf-8")


if __name__ == "__main__":
    main()
