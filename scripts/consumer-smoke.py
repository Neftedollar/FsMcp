#!/usr/bin/env python3
"""Restore, build, and run clean consumers against the seven packed FsMcp artifacts."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape


PACKAGE_IDS = [
    "FsMcp.Client",
    "FsMcp.Core",
    "FsMcp.Sampling",
    "FsMcp.Server",
    "FsMcp.Server.Http",
    "FsMcp.TaskApi",
    "FsMcp.Testing",
]

FSHARP_CONSUMERS = [
    ("ClientModern", "FsMcp.Client", "open FsMcp.Client\nlet publicApi (_: ClientConfig) = ()\n", "10.1.400", None),
    ("ClientFloor", "FsMcp.Client", "open FsMcp.Client\nlet publicApi (_: ClientConfig) = ()\n", "10.1.203", "10.1.203"),
    ("Sampling", "FsMcp.Sampling", "open FsMcp.Sampling\nlet publicApi (_: SamplingRequest) = ()\n", "10.1.400", None),
    ("ServerHttp", "FsMcp.Server.Http", "open FsMcp.Server\nlet publicApi (_: ServerRegistration) = ()\n", "10.1.400", None),
    ("TaskApi", "FsMcp.TaskApi", "open FsMcp.TaskApi\nlet publicApi = ClientPipeline.listTools\n", "10.1.400", None),
    ("Testing", "FsMcp.Testing", "open FsMcp.Testing\nlet publicApi (_: ToolInfo) = ()\n", "10.1.400", None),
]

EXTERNAL_PACKAGE_PATTERNS = [
    "Expecto*",
    "FSharp.Core",
    "FsCheck",
    "FsToolkit.ErrorHandling*",
    "Microsoft.*",
    "ModelContextProtocol*",
    "Mono.Cecil",
    "System.*",
    "TypeShape",
]

BUILD_FLAGS = [
    "--maxcpucount:1",
    "-nodeReuse:false",
    "-p:BuildInParallel=false",
    "-p:UseSharedCompilation=false",
]


def run(command: list[str], cwd: Path, environment: dict[str, str]) -> None:
    completed = subprocess.run(command, cwd=cwd, env=environment, text=True, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"Command failed ({completed.returncode}): {' '.join(command)}")


def write_project(
    path: Path,
    references: list[str],
    version: str,
    fsharp_core_version: str | None = None,
) -> None:
    package_references = [
        f'    <PackageReference Include="{package}" Version="[{version}]" />'
        for package in references
    ]
    if fsharp_core_version is not None:
        package_references.append(
            f'    <PackageReference Include="FSharp.Core" Version="[{fsharp_core_version}]" />'
        )

    disable_implicit_core = (
        "\n    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>"
        if fsharp_core_version is not None
        else ""
    )
    compile_item = (
        "  <ItemGroup>\n    <Compile Include=\"Program.fs\" />\n  </ItemGroup>\n"
        if path.suffix == ".fsproj"
        else ""
    )

    path.write_text(
        f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    {disable_implicit_core.strip()}
  </PropertyGroup>
{compile_item}  <ItemGroup>
{chr(10).join(package_references)}
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )


def write_nuget_config(path: Path, package_dir: Path, external_source: str) -> None:
    external_patterns = "\n".join(
        f'      <package pattern="{pattern}" />' for pattern in EXTERNAL_PACKAGE_PATTERNS
    )
    path.write_text(
        f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="fsmcp-local" value="{escape(str(package_dir))}" />
    <add key="external" value="{escape(external_source)}" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="fsmcp-local">
      <package pattern="FsMcp.*" />
    </packageSource>
    <packageSource key="external">
{external_patterns}
    </packageSource>
  </packageSourceMapping>
</configuration>
""",
        encoding="utf-8",
    )


def validate_fsharp_assets(project_dir: Path, package: str, expected_core: str) -> None:
    assets = json.loads((project_dir / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    framework = assets["project"]["frameworks"]["net10.0"]
    direct_fsmcp = {
        name for name in framework["dependencies"] if name.lower().startswith("fsmcp.")
    }
    if direct_fsmcp != {package}:
        raise RuntimeError(
            f"{project_dir.name} has unexpected direct FsMcp references: {sorted(direct_fsmcp)}"
        )

    resolved_core = {
        library.split("/", 1)[1]
        for library in assets["libraries"]
        if library.lower().startswith("fsharp.core/")
    }
    if resolved_core != {expected_core}:
        raise RuntimeError(
            f"{project_dir.name} resolved FSharp.Core {sorted(resolved_core)}, expected {expected_core}"
        )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package-dir", type=Path, required=True)
    parser.add_argument("--expected-version", required=True)
    parser.add_argument("--expected-commit", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument(
        "--external-source",
        default="https://api.nuget.org/v3/index.json",
        help="Package source for non-FsMcp dependencies (defaults to the official NuGet feed).",
    )
    args = parser.parse_args()

    if not re.fullmatch(r"[0-9a-f]{40}", args.expected_commit):
        raise SystemExit("--expected-commit must be a lowercase full SHA")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", args.expected_version):
        raise SystemExit("--expected-version must be a SemVer value")

    package_dir = args.package_dir.resolve()
    expected_files = {f"{package}.{args.expected_version}.nupkg" for package in PACKAGE_IDS}
    actual_files = {
        path.name
        for path in package_dir.glob("FsMcp.*.nupkg")
        if not path.name.endswith(".snupkg")
    }
    if actual_files != expected_files:
        raise SystemExit(f"Unexpected package set: {sorted(actual_files)}")

    environment = os.environ.copy()
    environment.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    environment.setdefault("DOTNET_NOLOGO", "1")
    environment.setdefault("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1")
    environment.setdefault("MSBUILDDISABLENODEREUSE", "1")

    with tempfile.TemporaryDirectory(prefix="fsmcp-consumer-") as temp:
        root = Path(temp)
        environment["DOTNET_CLI_HOME"] = str(root / "dotnet-home")
        environment["NUGET_PACKAGES"] = str(root / "nuget-packages")
        shutil.copy2(Path(__file__).resolve().parents[1] / "global.json", root / "global.json")
        write_nuget_config(root / "NuGet.Config", package_dir, args.external_source)

        csharp = root / "CSharpAllPackages"
        csharp.mkdir()
        write_project(csharp / "CSharpAllPackages.csproj", PACKAGE_IDS, args.expected_version)
        (csharp / "Program.cs").write_text(
            f"""using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

var publicApi = new System.Type[] {{
    typeof(FsMcp.Core.Content),
    typeof(FsMcp.Client.ClientConfig),
    typeof(FsMcp.Server.ServerConfig),
    typeof(FsMcp.Server.Http.HttpServer),
    typeof(FsMcp.Sampling.SamplingRequest),
    typeof(FsMcp.TaskApi.ClientPipeline),
    typeof(FsMcp.Testing.McpArbitraries),
}};
foreach (var type in publicApi) {{
    var assembly = type.Assembly;
    if (assembly.GetName().Version?.ToString() != "{args.expected_version}.0")
        throw new InvalidOperationException($"{{assembly.GetName().Name}} has the wrong assembly version.");
    var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
    if (fileVersion != "{args.expected_version}.0")
        throw new InvalidOperationException($"{{assembly.GetName().Name}} has the wrong file version.");
    var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (informational is null || !informational.Contains("{args.expected_commit}", StringComparison.Ordinal))
        throw new InvalidOperationException($"{{assembly.GetName().Name}} lacks the expected commit metadata.");
}}
System.Console.WriteLine(string.Join(",", publicApi.Select(type => type.Assembly.GetName().Name)));
""",
            encoding="utf-8",
        )
        run(
            [args.dotnet, "restore", "--configfile", str(root / "NuGet.Config"), *BUILD_FLAGS],
            csharp,
            environment,
        )
        run(
            [args.dotnet, "build", "--configuration", "Release", "--no-restore", *BUILD_FLAGS],
            csharp,
            environment,
        )
        run(
            [args.dotnet, "run", "--configuration", "Release", "--no-build", "--no-restore"],
            csharp,
            environment,
        )

        for name, package, source, expected_core, explicit_core in FSHARP_CONSUMERS:
            consumer = root / f"FSharp{name}"
            consumer.mkdir()
            write_project(
                consumer / f"FSharp{name}.fsproj",
                [package],
                args.expected_version,
                explicit_core,
            )
            (consumer / "Program.fs").write_text(
                f"module FSharp{name}\n\n{source}\n[<EntryPoint>]\nlet main _ = 0\n",
                encoding="utf-8",
            )
            run(
                [args.dotnet, "restore", "--configfile", str(root / "NuGet.Config"), *BUILD_FLAGS],
                consumer,
                environment,
            )
            validate_fsharp_assets(consumer, package, expected_core)
            run(
                [args.dotnet, "build", "--configuration", "Release", "--no-restore", *BUILD_FLAGS],
                consumer,
                environment,
            )
            run(
                [args.dotnet, "run", "--configuration", "Release", "--no-build", "--no-restore"],
                consumer,
                environment,
            )

    print("C# all-package consumer and six F# leaf consumers restored, built, and ran successfully.")


if __name__ == "__main__":
    main()
