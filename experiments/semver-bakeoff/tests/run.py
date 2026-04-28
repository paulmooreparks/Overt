#!/usr/bin/env python3
"""SemVer bake-off acceptance test runner.

Invokes a candidate binary against every case in cases.jsonl,
captures stdout / exit code, compares against expected, writes a
JSON report.

Usage:
    python run.py /path/to/binary [--out results.json] [--filter category]
    python run.py --validate    # check cases.jsonl parses and IDs are unique

The runner is deliberately minimal. No fancy comparators, no
fuzzy matching, no test-framework noise. The point is reproducible
black-box scoring: same input produces same pass/fail.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from dataclasses import dataclass, asdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
CASES_PATH = HERE / "cases.jsonl"
PER_TEST_TIMEOUT_SECONDS = 10


@dataclass
class Case:
    id: str
    category: str
    description: str
    argv: list[str]
    stdin: str
    stdout: str
    exit: int
    source: str


@dataclass
class Result:
    id: str
    category: str
    description: str
    passed: bool
    expected_stdout: str
    actual_stdout: str
    expected_exit: int
    actual_exit: int
    stderr: str
    duration_ms: float
    timed_out: bool
    error: str | None  # populated when the runner itself failed (not the binary)


def load_cases(path: Path) -> list[Case]:
    cases: list[Case] = []
    seen_ids: set[str] = set()
    with path.open(encoding="utf-8") as f:
        for lineno, raw in enumerate(f, start=1):
            stripped = raw.strip()
            if not stripped or stripped.startswith("#"):
                continue
            try:
                obj = json.loads(stripped)
            except json.JSONDecodeError as e:
                raise SystemExit(f"cases.jsonl:{lineno}: invalid JSON: {e}") from e
            if obj["id"] in seen_ids:
                raise SystemExit(f"cases.jsonl:{lineno}: duplicate id `{obj['id']}`")
            seen_ids.add(obj["id"])
            cases.append(Case(
                id=obj["id"],
                category=obj["category"],
                description=obj.get("description", ""),
                argv=list(obj["argv"]),
                stdin=obj.get("stdin", ""),
                stdout=obj["stdout"],
                exit=int(obj["exit"]),
                source=obj.get("source", ""),
            ))
    return cases


def run_one(binary: list[str], case: Case) -> Result:
    """Invoke `binary case.argv` with `case.stdin` piped in."""
    cmd = list(binary) + case.argv
    start = time.monotonic()
    try:
        proc = subprocess.run(
            cmd,
            input=case.stdin,
            capture_output=True,
            text=True,
            timeout=PER_TEST_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        duration = (time.monotonic() - start) * 1000.0
        return Result(
            id=case.id, category=case.category, description=case.description,
            passed=False,
            expected_stdout=case.stdout, actual_stdout="",
            expected_exit=case.exit, actual_exit=-1,
            stderr="<timed out>",
            duration_ms=duration,
            timed_out=True,
            error=None,
        )
    except FileNotFoundError as e:
        return Result(
            id=case.id, category=case.category, description=case.description,
            passed=False,
            expected_stdout=case.stdout, actual_stdout="",
            expected_exit=case.exit, actual_exit=-1,
            stderr="",
            duration_ms=0.0,
            timed_out=False,
            error=f"binary not found: {e}",
        )
    duration = (time.monotonic() - start) * 1000.0
    actual_stdout = proc.stdout
    actual_exit = proc.returncode
    passed = actual_stdout == case.stdout and actual_exit == case.exit
    return Result(
        id=case.id, category=case.category, description=case.description,
        passed=passed,
        expected_stdout=case.stdout, actual_stdout=actual_stdout,
        expected_exit=case.exit, actual_exit=actual_exit,
        stderr=proc.stderr,
        duration_ms=duration,
        timed_out=False,
        error=None,
    )


def summarize(results: list[Result]) -> None:
    by_category: dict[str, list[Result]] = {}
    for r in results:
        by_category.setdefault(r.category, []).append(r)

    print()
    print(f"{'category':14} {'pass':>5} / {'total':>5}  {'rate':>6}")
    print("-" * 42)
    total_pass = 0
    total_count = 0
    for cat in sorted(by_category):
        rs = by_category[cat]
        npass = sum(1 for r in rs if r.passed)
        ntotal = len(rs)
        rate = (npass / ntotal) if ntotal else 0.0
        total_pass += npass
        total_count += ntotal
        print(f"{cat:14} {npass:>5} / {ntotal:>5}  {rate:>5.1%}")
    print("-" * 42)
    rate = (total_pass / total_count) if total_count else 0.0
    print(f"{'TOTAL':14} {total_pass:>5} / {total_count:>5}  {rate:>5.1%}")
    print()


def print_failures(results: list[Result], limit: int = 10) -> None:
    failures = [r for r in results if not r.passed]
    if not failures:
        return
    print(f"Failures ({len(failures)} total; showing first {min(limit, len(failures))}):")
    print()
    for r in failures[:limit]:
        print(f"  [{r.id}] {r.description}")
        print(f"      expected exit={r.expected_exit}  actual exit={r.actual_exit}")
        print(f"      expected stdout: {r.expected_stdout!r}")
        print(f"      actual stdout:   {r.actual_stdout!r}")
        if r.stderr:
            stderr_first_line = r.stderr.split('\n', 1)[0][:200]
            print(f"      stderr: {stderr_first_line}")
        if r.error:
            print(f"      runner error: {r.error}")
        if r.timed_out:
            print("      (TIMED OUT)")
        print()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("binary", nargs="?", help="path to the candidate binary (or `--validate`)")
    parser.add_argument("--out", default="results.json", help="where to write per-case results")
    parser.add_argument("--filter", default=None, help="only run cases matching this category")
    parser.add_argument("--validate", action="store_true",
                        help="just validate cases.jsonl parses and IDs are unique; don't run")
    parser.add_argument("--exec-prefix", default=None,
                        help="optional command prefix for the binary (e.g. 'dotnet' or 'python')")
    parser.add_argument("--show-failures", type=int, default=10,
                        help="number of failures to print after the summary (default 10)")
    args = parser.parse_args()

    cases = load_cases(CASES_PATH)
    if args.filter:
        cases = [c for c in cases if c.category == args.filter]
    if args.validate:
        print(f"cases.jsonl: {len(cases)} cases, all parse, all IDs unique.")
        return 0

    if not args.binary:
        parser.error("binary is required (or pass --validate)")
    binary_cmd = ([args.exec_prefix] if args.exec_prefix else []) + [args.binary]

    results = [run_one(binary_cmd, c) for c in cases]
    Path(args.out).write_text(
        json.dumps([asdict(r) for r in results], indent=2),
        encoding="utf-8",
    )

    summarize(results)
    print_failures(results, limit=args.show_failures)

    npass = sum(1 for r in results if r.passed)
    return 0 if npass == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
