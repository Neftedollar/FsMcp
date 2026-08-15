#!/usr/bin/env python3
"""Fail unless a `dotnet package list --vulnerable --format json` report is clean."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


EXPECTED_PROJECT_PATHS = frozenset(
    {
        "src/FsMcp.Client/FsMcp.Client.fsproj",
        "src/FsMcp.Core/FsMcp.Core.fsproj",
        "src/FsMcp.Sampling/FsMcp.Sampling.fsproj",
        "src/FsMcp.Server.Http/FsMcp.Server.Http.fsproj",
        "src/FsMcp.Server/FsMcp.Server.fsproj",
        "src/FsMcp.TaskApi/FsMcp.TaskApi.fsproj",
        "src/FsMcp.Testing/FsMcp.Testing.fsproj",
        "tests/FsMcp.Client.Tests/FsMcp.Client.Tests.fsproj",
        "tests/FsMcp.Core.Tests/FsMcp.Core.Tests.fsproj",
        "tests/FsMcp.Sampling.Tests/FsMcp.Sampling.Tests.fsproj",
        "tests/FsMcp.Server.Http.Tests/FsMcp.Server.Http.Tests.fsproj",
        "tests/FsMcp.Server.Tests/FsMcp.Server.Tests.fsproj",
        "tests/FsMcp.TaskApi.Tests/FsMcp.TaskApi.Tests.fsproj",
        "tests/FsMcp.Testing.Tests/FsMcp.Testing.Tests.fsproj",
    }
)
EXPECTED_PARAMETERS = "--vulnerable --include-transitive"
EXPECTED_SOURCES = ["https://api.nuget.org/v3/index.json"]


def project_entries(report: dict[str, Any]) -> list[dict[str, Any]]:
    projects = report.get("projects")
    if not isinstance(projects, list):
        raise ValueError("NuGet audit report has no projects array")
    return projects


def project_identity(project: dict[str, Any]) -> str:
    if not isinstance(project, dict):
        raise ValueError("NuGet audit project entry is not an object")
    raw_path = project.get("path")
    if not isinstance(raw_path, str) or not raw_path.strip():
        raise ValueError("NuGet audit project entry has no path")
    normalized = raw_path.replace("\\", "/").rstrip("/")
    matches = [
        expected
        for expected in EXPECTED_PROJECT_PATHS
        if normalized == expected or normalized.endswith(f"/{expected}")
    ]
    if len(matches) != 1:
        raise ValueError(f"Unexpected NuGet audit project path: {raw_path}")
    return matches[0]


def vulnerabilities(report: dict[str, Any]) -> list[tuple[str, str, dict[str, Any]]]:
    found: list[tuple[str, str, dict[str, Any]]] = []
    for project in project_entries(report):
        if not isinstance(project, dict):
            raise ValueError("NuGet audit project entry is not an object")
        project_path = str(project.get("path") or project.get("name") or "<unknown>")
        frameworks = project.get("frameworks") or []
        if not isinstance(frameworks, list):
            raise ValueError(f"Invalid frameworks entry for {project_path}")
        for framework in frameworks:
            if not isinstance(framework, dict):
                raise ValueError(f"Invalid framework entry for {project_path}")
            framework_name = str(framework.get("framework") or "<unknown>")
            for key in ("topLevelPackages", "transitivePackages"):
                packages = framework.get(key) or []
                if not isinstance(packages, list):
                    raise ValueError(f"Invalid {key} entry for {project_path}")
                for package in packages:
                    if not isinstance(package, dict):
                        raise ValueError(f"Invalid package entry for {project_path}")
                    # `--vulnerable` emits package entries only for findings. Treat
                    # every emitted package as unsafe even if a truncated report
                    # drops its advisory details.
                    found.append((project_path, framework_name, package))
    return found


def validate(report: dict[str, Any], expected_projects: int) -> None:
    if report.get("version") != 1:
        raise ValueError("NuGet audit report version must be 1")
    if report.get("parameters") != EXPECTED_PARAMETERS:
        raise ValueError("NuGet audit report parameters do not match the required audit")
    if report.get("sources") != EXPECTED_SOURCES:
        raise ValueError("NuGet audit report must use only the official NuGet source")
    if expected_projects != len(EXPECTED_PROJECT_PATHS):
        raise ValueError(
            f"Expected-project count must be {len(EXPECTED_PROJECT_PATHS)}, got {expected_projects}"
        )
    projects = project_entries(report)
    if len(projects) != expected_projects:
        raise ValueError(f"Expected {expected_projects} project entries, found {len(projects)}")
    identities = [project_identity(project) for project in projects]
    if len(set(identities)) != len(identities):
        raise ValueError("NuGet audit report contains duplicate project entries")
    if set(identities) != EXPECTED_PROJECT_PATHS:
        missing = sorted(EXPECTED_PROJECT_PATHS.difference(identities))
        raise ValueError(f"NuGet audit report is missing expected projects: {', '.join(missing)}")
    found = vulnerabilities(report)
    if found:
        details = "; ".join(
            f"{project} ({framework}): {package.get('id', '<unknown>')}"
            for project, framework, package in found
        )
        raise ValueError(f"NuGet vulnerability audit is not clean: {details}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("--expected-projects", type=int, default=14)
    args = parser.parse_args()
    try:
        report = json.loads(args.report.read_text(encoding="utf-8-sig"))
        validate(report, args.expected_projects)
    except (OSError, json.JSONDecodeError, ValueError) as error:
        raise SystemExit(f"NuGet audit verification failed: {error}") from error
    print(f"NuGet audit is clean for {args.expected_projects} project entries.")


if __name__ == "__main__":
    main()
