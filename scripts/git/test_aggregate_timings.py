"""Unit tests for aggregate_timings.py, using sample timing files written to a temporary directory.

Run with: python3 -m unittest discover -s scripts/git -p 'test_*.py'
"""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import aggregate_timings


def _statistic(operation: str, scenario: str, **values) -> dict:
    base = {"count": 7, "min": 1.0, "mean": 2.0, "median": 2.0, "p95": 3.0, "max": 4.0, "total": 14.0, "failures": 0}
    base.update(values)
    return {"operation": operation, "scenario": scenario, **base}


def _document(platform: str, architecture: str, statistics: list[dict]) -> dict:
    return {
        "metadata": {
            "platform": platform,
            "architecture": architecture,
            "gitVersion": f"git version 2.{platform}",
        },
        "records": [],
        "statistics": statistics,
    }


class AggregateTimingsTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.root = Path(self._temp.name)

    def tearDown(self) -> None:
        self._temp.cleanup()

    def _write(self, folder: str, document: dict) -> None:
        directory = self.root / folder
        directory.mkdir(parents=True, exist_ok=True)
        (directory / f"git-timing-{document['metadata']['platform']}-{document['metadata']['architecture']}.json").write_text(
            json.dumps(document), encoding="utf-8")

    def test_loads_one_run_per_label_from_nested_artifact_folders(self) -> None:
        self._write("timing-linux-x64", _document("linux", "x64", [_statistic("GetStatusAsync", "read-path")]))
        self._write("timing-windows-x64", _document("windows", "x64", [_statistic("GetStatusAsync", "read-path")]))

        runs = aggregate_timings.load_runs(self.root)

        self.assertEqual(sorted(runs), ["linux-x64", "windows-x64"])

    def test_table_has_one_column_group_per_operating_system(self) -> None:
        self._write("a", _document("linux", "x64", [
            _statistic("GetStatusAsync", "read-path", min=1.25, median=2.5, p95=3.75, max=4.5, count=7)]))
        self._write("b", _document("windows", "x64", [
            _statistic("GetStatusAsync", "read-path", min=10.0, median=20.0, p95=30.0, max=40.0, count=7)]))

        table = aggregate_timings.render(aggregate_timings.load_runs(self.root))
        header = next(line for line in table.splitlines() if line.startswith("| Operation"))

        self.assertIn("linux-x64 count", header)
        self.assertIn("windows-x64 p95", header)
        self.assertLess(header.index("linux-x64 count"), header.index("windows-x64 count"))
        row = next(line for line in table.splitlines() if line.startswith("| GetStatusAsync"))
        self.assertIn("| 7 | 1.25 | 2.50 | 3.75 | 4.50 |", row)
        self.assertIn("| 7 | 10.00 | 20.00 | 30.00 | 40.00 |", row)

    def test_stored_values_are_shown_without_recomputation(self) -> None:
        self._write("a", _document("linux", "x64", [_statistic("PushAsync", "write-path", median=123.456, p95=999.0)]))

        table = aggregate_timings.render(aggregate_timings.load_runs(self.root))

        self.assertIn("123.46", table)
        self.assertIn("999.00", table)

    def test_missing_runner_is_reported_and_partial_data_shows_dashes(self) -> None:
        self._write("a", _document("linux", "x64", [
            _statistic("GetStatusAsync", "read-path"),
            _statistic("PushAsync", "write-path")]))
        self._write("b", _document("windows", "x64", [_statistic("GetStatusAsync", "read-path")]))

        table = aggregate_timings.render(
            aggregate_timings.load_runs(self.root),
            expected=["linux-x64", "windows-x64", "osx-arm64"])

        self.assertIn("No timing file from: osx-arm64", table)
        push_row = next(line for line in table.splitlines() if line.startswith("| PushAsync"))
        self.assertIn("| - | - | - | - | - |", push_row, "windows has no PushAsync row, so its cells are dashes")

    def test_no_files_gives_an_explicit_message(self) -> None:
        table = aggregate_timings.render({}, expected=["linux-x64"])

        self.assertIn("No timing file from: linux-x64", table)
        self.assertIn("No timing files were produced by any runner.", table)

    def test_says_the_numbers_are_informational(self) -> None:
        table = aggregate_timings.render({})

        self.assertIn("Informational only", table)


if __name__ == "__main__":
    unittest.main()
