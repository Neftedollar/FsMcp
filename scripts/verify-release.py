#!/usr/bin/env python3
"""Verify the closed FsMcp package set and its release metadata."""

from __future__ import annotations

import argparse
import re
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


PACKAGE_IDS = {
    "FsMcp.Client",
    "FsMcp.Core",
    "FsMcp.Sampling",
    "FsMcp.Server",
    "FsMcp.Server.Http",
    "FsMcp.TaskApi",
    "FsMcp.Testing",
}

DESCRIPTIONS = {
    "FsMcp.Client": "Typed F# client for MCP, including Result-based error handling and enterprise-managed authorization",
    "FsMcp.Core": "Idiomatic F# domain types, validation, and serialization for the Model Context Protocol",
    "FsMcp.Sampling": "MCP sampling domain types and test helpers; legacy transport wiring fails closed in 2.0",
    "FsMcp.Server": "F# MCP server builder with computation expressions and typed cancellable handlers",
    "FsMcp.Server.Http": "Streamable HTTP transport for FsMcp MCP servers via ASP.NET Core",
    "FsMcp.TaskApi": "Ergonomic task-based pipeline API for FsMcp, powered by FsToolkit.ErrorHandling",
    "FsMcp.Testing": "Testing utilities for MCP servers: assertions, FsCheck generators, and in-process test server",
}


def expected_dependencies(package_id: str, version: str) -> dict[str, str]:
    shared = {"FSharp.Core": "10.1.203"}
    package_specific = {
        "FsMcp.Client": {"FsMcp.Core": version, "ModelContextProtocol": "[1.4.1]"},
        "FsMcp.Core": {
            "Microsoft.Extensions.Logging.Abstractions": "[10.0.7]",
            "ModelContextProtocol": "[1.4.1]",
        },
        "FsMcp.Sampling": {
            "FsMcp.Core": version,
            "FsMcp.Server": version,
            "ModelContextProtocol": "[1.4.1]",
        },
        "FsMcp.Server": {
            "FsMcp.Core": version,
            "Microsoft.Extensions.Hosting": "[10.0.7]",
            "Microsoft.Extensions.Logging.Console": "[10.0.7]",
            "ModelContextProtocol": "[1.4.1]",
            "TypeShape": "10.0.0",
        },
        "FsMcp.Server.Http": {
            "FsMcp.Core": version,
            "FsMcp.Server": version,
            "ModelContextProtocol": "[1.4.1]",
            "ModelContextProtocol.AspNetCore": "[1.4.1]",
        },
        "FsMcp.TaskApi": {
            "FsMcp.Client": version,
            "FsMcp.Core": version,
            "FsToolkit.ErrorHandling": "5.2.0",
            "FsToolkit.ErrorHandling.TaskResult": "4.18.0",
        },
        "FsMcp.Testing": {
            "FsMcp.Core": version,
            "FsMcp.Server": version,
            "Expecto": "10.2.3",
            "Expecto.FsCheck": "10.2.3",
            "FsCheck": "2.16.6",
            "ModelContextProtocol": "[1.4.1]",
        },
    }
    return shared | package_specific[package_id]


def source_version(props: Path = Path("Directory.Build.props")) -> str:
    root = ET.parse(props).getroot()
    versions = [item.text for item in root.findall(".//Version") if item.text]
    if len(versions) != 1:
        raise ValueError("Directory.Build.props must declare exactly one Version")
    return versions[0]


def text(element: ET.Element, name: str) -> str:
    value = element.findtext(f"{{*}}{name}")
    if value is None or not value.strip():
        raise ValueError(f"Package metadata is missing {name}")
    return value.strip()


def verify_package(path: Path, version: str, commit: str, readme: bytes | None = None) -> str:
    with zipfile.ZipFile(path) as archive:
        nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError(f"{path.name} must contain exactly one nuspec")
        root = ET.fromstring(archive.read(nuspecs[0]))
        metadata = root.find("{*}metadata")
        if metadata is None:
            raise ValueError(f"{path.name} has no metadata element")
        package_id = text(metadata, "id")
        if package_id not in PACKAGE_IDS:
            raise ValueError(f"Unexpected package id {package_id}")
        if text(metadata, "version") != version:
            raise ValueError(f"{package_id} does not have version {version}")
        if text(metadata, "authors") != "FsMcp Contributors":
            raise ValueError(f"{package_id} has unexpected authors")
        license_element = metadata.find("{*}license")
        if (
            license_element is None
            or license_element.get("type") != "expression"
            or (license_element.text or "").strip() != "MIT"
        ):
            raise ValueError(f"{package_id} does not use the MIT license expression")
        if text(metadata, "readme") != "README.md":
            raise ValueError(f"{package_id} has an unexpected readme path")
        if text(metadata, "description") != DESCRIPTIONS[package_id]:
            raise ValueError(f"{package_id} has an unexpected description")
        if text(metadata, "releaseNotes") != f"https://github.com/Neftedollar/FsMcp/blob/v{version}/CHANGELOG.md":
            raise ValueError(f"{package_id} has an unexpected release-notes URL")
        if text(metadata, "tags") != "fsharp mcp model-context-protocol ai llm tools":
            raise ValueError(f"{package_id} has unexpected tags")
        repository = metadata.find("{*}repository")
        if repository is None or repository.get("commit") != commit:
            raise ValueError(f"{package_id} does not carry repository commit {commit}")
        if repository.get("url") != "https://github.com/Neftedollar/FsMcp":
            raise ValueError(f"{package_id} has an unexpected repository URL")
        if text(metadata, "projectUrl") != "https://neftedollar.com/FsMcp/":
            raise ValueError(f"{package_id} has an unexpected project URL")
        if "README.md" not in archive.namelist():
            raise ValueError(f"{package_id} has no packaged README.md")
        if readme is not None and archive.read("README.md") != readme:
            raise ValueError(f"{package_id} does not contain the repository README.md bytes")
        dependencies = metadata.findall(".//{*}dependency")
        actual_dependencies = {item.get("id"): item.get("version") for item in dependencies}
        if len(actual_dependencies) != len(dependencies):
            raise ValueError(f"{package_id} contains a duplicate or unnamed dependency")
        if actual_dependencies != expected_dependencies(package_id, version):
            raise ValueError(f"{package_id} has an unexpected dependency policy: {actual_dependencies}")
        framework_references = {
            item.get("name") for item in metadata.findall(".//{*}frameworkReference")
        }
        expected_framework_references = (
            {"Microsoft.AspNetCore.App"} if package_id == "FsMcp.Server.Http" else set()
        )
        if framework_references != expected_framework_references:
            raise ValueError(f"{package_id} has unexpected framework references")
        return package_id


def verify(package_dir: Path, version: str, commit: str) -> None:
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("expected commit must be a lowercase 40-character SHA")
    packages = sorted(package_dir.glob("FsMcp.*.nupkg"))
    packages = [path for path in packages if not path.name.endswith(".snupkg")]
    if len(packages) != len(PACKAGE_IDS):
        raise ValueError(f"Expected 7 packages, found {len(packages)}")
    readme = Path("README.md").read_bytes()
    found = {verify_package(path, version, commit, readme) for path in packages}
    if found != PACKAGE_IDS:
        raise ValueError(f"Unexpected package set: {sorted(found)}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--print-source-version", action="store_true")
    parser.add_argument("--package-dir", type=Path)
    parser.add_argument("--expected-version")
    parser.add_argument("--expected-commit")
    args = parser.parse_args()
    try:
        if args.print_source_version:
            print(source_version())
            return
        if args.package_dir is None or args.expected_version is None or args.expected_commit is None:
            parser.error("--package-dir, --expected-version and --expected-commit are required")
        if source_version() != args.expected_version:
            raise ValueError("source Version does not match the expected release version")
        verify(args.package_dir, args.expected_version, args.expected_commit)
    except (OSError, ET.ParseError, zipfile.BadZipFile, ValueError) as error:
        raise SystemExit(f"Release verification failed: {error}") from error
    print(f"Verified 7 packages at version {args.expected_version} and commit {args.expected_commit}.")


if __name__ == "__main__":
    main()
