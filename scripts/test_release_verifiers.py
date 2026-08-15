#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT_ROOT = Path(__file__).resolve().parent


def load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, SCRIPT_ROOT / filename)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


notes = load("extract_release_notes", "extract-release-notes.py")
audit = load("verify_nuget_audit", "verify-nuget-audit.py")
release = load("verify_release", "verify-release.py")


def clean_report():
    return {
        "version": 1,
        "parameters": audit.EXPECTED_PARAMETERS,
        "sources": audit.EXPECTED_SOURCES,
        "projects": [
            {"path": f"/checkout/{path}"}
            for path in sorted(audit.EXPECTED_PROJECT_PATHS)
        ]
    }


class ReleaseVerifierTests(unittest.TestCase):
    def test_extracts_requested_release(self):
        value = "# Changelog\n\n## [2.0.0]\n\nNew.\n\n## [1.0.0]\n\nOld.\n"
        self.assertEqual("New.\n", notes.extract_release_notes(value, "2.0.0"))

    def test_release_notes_require_heading(self):
        with self.assertRaises(ValueError):
            notes.extract_release_notes("# Changelog\n", "2.0.0")

    def test_release_notes_require_body(self):
        with self.assertRaises(ValueError):
            notes.extract_release_notes("## [2.0.0]\n\n## [1.0.0]\nOld\n", "2.0.0")

    def test_clean_nuget_report_passes(self):
        audit.validate(clean_report(), 14)

    def test_nuget_report_requires_exact_project_count(self):
        report = clean_report()
        report["projects"].pop()
        with self.assertRaises(ValueError):
            audit.validate(report, 14)

    def test_nuget_report_rejects_duplicate_project(self):
        report = clean_report()
        report["projects"][-1] = report["projects"][0]
        with self.assertRaises(ValueError):
            audit.validate(report, 14)

    def test_nuget_report_rejects_substituted_project(self):
        report = clean_report()
        report["projects"][-1] = {"path": "/checkout/tests/Other.Tests/Other.Tests.fsproj"}
        with self.assertRaises(ValueError):
            audit.validate(report, 14)

    def test_nuget_report_requires_audit_parameters_and_source(self):
        report = clean_report()
        report["parameters"] = "--vulnerable"
        with self.assertRaises(ValueError):
            audit.validate(report, 14)

    def test_top_level_vulnerability_fails(self):
        report = clean_report()
        report["projects"][0]["frameworks"] = [{"framework": "net10.0", "topLevelPackages": []}]
        report["projects"][0]["frameworks"][0]["topLevelPackages"] = [
            {"id": "Bad", "vulnerabilities": [{"severity": "High"}]}
        ]
        with self.assertRaises(ValueError):
            audit.validate(report, 14)

    def test_transitive_vulnerability_fails(self):
        report = clean_report()
        report["projects"][0]["frameworks"] = [{"framework": "net10.0"}]
        report["projects"][0]["frameworks"][0]["transitivePackages"] = [
            {"id": "Bad.Transitive", "vulnerabilities": [{}]}
        ]
        with self.assertRaises(ValueError):
            audit.validate(report, 14)

    def test_package_without_advisory_details_fails_closed(self):
        report = clean_report()
        report["projects"][0]["frameworks"] = [
            {"framework": "net10.0", "topLevelPackages": [{"id": "Truncated.Finding"}]}
        ]
        with self.assertRaises(ValueError):
            audit.validate(report, 14)

    def test_nuget_report_requires_projects_array(self):
        with self.assertRaises(ValueError):
            audit.validate({}, 14)

    def test_source_version_reads_single_value(self):
        with tempfile.TemporaryDirectory() as temporary:
            props = Path(temporary) / "Directory.Build.props"
            props.write_text("<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>")
            self.assertEqual("2.0.0", release.source_version(props))

    def test_source_version_rejects_ambiguous_values(self):
        with tempfile.TemporaryDirectory() as temporary:
            props = Path(temporary) / "Directory.Build.props"
            props.write_text("<Project><Version>1</Version><Version>2</Version></Project>")
            with self.assertRaises(ValueError):
                release.source_version(props)

    def test_release_rejects_non_sha_commit(self):
        with self.assertRaises(ValueError):
            release.verify(Path("missing"), "2.0.0", "short")

    def test_release_rejects_missing_package_set(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(ValueError):
                release.verify(Path(temporary), "2.0.0", "a" * 40)

    def test_release_rejects_bad_archive(self):
        with tempfile.TemporaryDirectory() as temporary:
            package = Path(temporary) / "bad.nupkg"
            package.write_text("not a zip")
            with self.assertRaises(zipfile.BadZipFile):
                release.verify_package(package, "2.0.0", "a" * 40)


if __name__ == "__main__":
    unittest.main()
