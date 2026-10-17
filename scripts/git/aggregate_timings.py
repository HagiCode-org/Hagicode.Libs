#!/usr/bin/env python3
"""Render the per-operation git timing comparison for the GitHub job summary.

Reads the ``git-timing-<platform>-<arch>.json`` files written by the git test suite and prints one Markdown table with a
column group per operating system. The statistics are the values stored in each file (computed once, by
``TimingStatistics`` in the test project); they are not recomputed here.

The numbers are informational. Nothing in this script, and nothing in the workflow, turns a duration into a pass or fail result.

Usage:
    aggregate_timings.py <directory> [--expect label ...]

``<directory>`` is searched recursively. ``--expect`` lists runner labels that should have produced a file, so a runner that
produced nothing is reported as missing instead of silently absent.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

STATISTIC_COLUMNS = [
    ("count", "count"),
    ("min", "min"),
    ("median", "median"),
    ("p95", "p95"),
    ("max", "max"),
]


def load_runs(root: Path) -> dict[str, dict]:
    """Return ``{label: document}`` for every timing file under ``root``. A label is ``<platform>-<architecture>``."""
    runs: dict[str, dict] = {}
    for path in sorted(root.rglob("git-timing-*.json")):
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            print(f"warning: could not read {path}: {error}", file=sys.stderr)
            continue

        metadata = document.get("metadata") or {}
        label = f"{metadata.get('platform', 'unknown')}-{metadata.get('architecture', 'unknown')}"
        if label in runs:
            print(f"warning: duplicate timing file for {label}: {path}", file=sys.stderr)
            continue
        runs[label] = document
    return runs


def _format(value) -> str:
    if value is None:
        return "-"
    if isinstance(value, (int, float)):
        return f"{value:.2f}" if isinstance(value, float) else str(value)
    return str(value)


def _row_keys(runs: dict[str, dict]) -> list[tuple[str, str]]:
    keys = set()
    for document in runs.values():
        for statistic in document.get("statistics") or []:
            keys.add((statistic["operation"], statistic["scenario"]))
    return sorted(keys)


def render(runs: dict[str, dict], expected: list[str] | None = None) -> str:
    """Return the Markdown summary for the given runs."""
    labels = sorted(runs)
    expected = sorted(set(expected or []))
    missing = [label for label in expected if label not in runs]

    lines: list[str] = []
    lines.append("## Git operation timings by operating system")
    lines.append("")
    lines.append(
        "Informational only: durations are reported, never used as a pass or fail gate. "
        "Times are milliseconds. Warm-up iterations are excluded; the scenario rows use 7 measured iterations."
    )
    lines.append("")

    if missing:
        lines.append(f"No timing file from: {', '.join(missing)}")
        lines.append("")

    if not runs:
        lines.append("No timing files were produced by any runner.")
        return "\n".join(lines) + "\n"

    header = ["Operation", "Scenario"]
    divider = ["---", "---"]
    for label in labels:
        for _, title in STATISTIC_COLUMNS:
            header.append(f"{label} {title}")
            divider.append("---:")
    lines.append("| " + " | ".join(header) + " |")
    lines.append("| " + " | ".join(divider) + " |")

    lookup: dict[str, dict[tuple[str, str], dict]] = {}
    for label, document in runs.items():
        lookup[label] = {
            (statistic["operation"], statistic["scenario"]): statistic
            for statistic in document.get("statistics") or []
        }

    for operation, scenario in _row_keys(runs):
        cells = [operation, scenario]
        for label in labels:
            statistic = lookup[label].get((operation, scenario))
            for key, _ in STATISTIC_COLUMNS:
                cells.append(_format(statistic.get(key) if statistic else None))
        lines.append("| " + " | ".join(cells) + " |")

    lines.append("")
    lines.append("Runners: " + ", ".join(
        f"{label} ({runs[label].get('metadata', {}).get('gitVersion', 'unknown')})" for label in labels))
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--expect", nargs="*", default=[], help="runner labels that should have produced a file")
    args = parser.parse_args(argv)

    if not args.directory.is_dir():
        print(f"error: {args.directory} is not a directory", file=sys.stderr)
        return 2

    sys.stdout.write(render(load_runs(args.directory), args.expect))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
