#!/usr/bin/env python3
"""Internal corpus consistency check — NOT part of the bake-off.

This file lives one level up from `tests/` deliberately: bake-off
agents see `tests/cases.jsonl` and `tests/run.py` as the acceptance
harness; they do NOT see this file. Showing a reference
implementation would defeat the point. Keep it outside `tests/`.

Purpose: run the test corpus against a known-good implementation
before trials begin. If a case fails here, the case is wrong (or
our spec interpretation is wrong) and we fix it before any trials
are polluted.

Uses the `semver` PyPI package for Version parse/compare; range
matching is hand-rolled because `semver`'s built-in range support
diverges from npm's grammar in ways that don't match our pre-reg.

Run:
    cd experiments/semver-bakeoff
    python tests/run.py internal_corpus_check.py --exec-prefix python

Expected outcome: 124/124 pass. Any failure is a corpus bug to
fix before trials begin.
"""

from __future__ import annotations

import re
import sys
from typing import Optional

try:
    from semver import Version
except ImportError:
    print("install with: pip install semver", file=sys.stderr)
    sys.exit(99)


VERSION_RE = re.compile(
    r"^(0|[1-9]\d*)\."
    r"(0|[1-9]\d*)\."
    r"(0|[1-9]\d*)"
    r"(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)


def parse_version(s: str) -> Optional[Version]:
    """Strict SemVer 2.0.0 parser. Returns None on any malformed input."""
    if not VERSION_RE.match(s):
        return None
    try:
        return Version.parse(s)
    except (ValueError, TypeError):
        return None


# ----------------------------------------------------- range matching


def _has_prerelease(v: Version) -> bool:
    return v.prerelease is not None and v.prerelease != ""


def _bump_major(v: Version) -> Version:
    return Version(v.major + 1, 0, 0)


def _bump_minor(v: Version) -> Version:
    return Version(v.major, v.minor + 1, 0)


def _expand_caret(spec: str) -> list[str]:
    """`^1.2.3` -> `>=1.2.3 <2.0.0`, with 0.x and 0.0.x rules."""
    v = parse_version(spec)
    if v is None:
        raise ValueError(f"invalid caret target: {spec}")
    if v.major > 0:
        return [f">={v}", f"<{_bump_major(v)}"]
    if v.minor > 0:
        return [f">={v}", f"<{Version(0, v.minor + 1, 0)}"]
    return [f">={v}", f"<{Version(0, 0, v.patch + 1)}"]


def _expand_tilde(spec: str) -> list[str]:
    """`~1.2.3` -> `>=1.2.3 <1.3.0`. Partial forms supported."""
    parts = spec.split(".")
    if len(parts) == 1:
        major = int(parts[0])
        return [f">={major}.0.0", f"<{major + 1}.0.0"]
    if len(parts) == 2:
        major = int(parts[0])
        minor = int(parts[1])
        return [f">={major}.{minor}.0", f"<{major}.{minor + 1}.0"]
    if len(parts) == 3:
        v = parse_version(spec)
        if v is None:
            raise ValueError(f"invalid tilde target: {spec}")
        return [f">={v}", f"<{Version(v.major, v.minor + 1, 0)}"]
    raise ValueError(f"invalid tilde target: {spec}")


def _expand_xrange(spec: str) -> list[str]:
    """`1.x` / `1.2.x` / `*` -> equivalent `>=` / `<` pair."""
    if spec in ("*", "x", "X"):
        return [">=0.0.0"]
    parts = spec.split(".")
    parts = [p for p in parts]
    if len(parts) == 2 and parts[1].lower() in ("x", "*"):
        major = int(parts[0])
        return [f">={major}.0.0", f"<{major + 1}.0.0"]
    if len(parts) == 3 and parts[2].lower() in ("x", "*"):
        major = int(parts[0])
        minor = int(parts[1])
        return [f">={major}.{minor}.0", f"<{major}.{minor + 1}.0"]
    raise ValueError(f"invalid x-range: {spec}")


def _expand_hyphen(lhs: str, rhs: str) -> list[str]:
    """`1.2.3 - 2.3.4` -> `>=1.2.3 <=2.3.4`. Strict full-version both sides."""
    lo = parse_version(lhs)
    hi = parse_version(rhs)
    if lo is None or hi is None:
        raise ValueError(f"hyphen range needs full versions: {lhs} - {rhs}")
    return [f">={lo}", f"<={hi}"]


def _expand_comparator_set(text: str) -> list[str]:
    """Expand a single conjunctive comparator set (no `||`) into atomic
    comparators (`>= X`, `< Y`, `= Z`). Tokens are whitespace-separated."""
    text = text.strip()
    # Detect hyphen range first (whitespace-padded ` - `).
    m = re.match(r"^(\S+)\s+-\s+(\S+)$", text)
    if m:
        return _expand_hyphen(m.group(1), m.group(2))

    out: list[str] = []
    for tok in text.split():
        if not tok:
            continue
        if tok.startswith("^"):
            out.extend(_expand_caret(tok[1:]))
        elif tok.startswith("~"):
            out.extend(_expand_tilde(tok[1:]))
        elif tok in ("*", "x", "X"):
            out.append(">=0.0.0")
        elif tok.startswith(">=") or tok.startswith("<="):
            v = parse_version(tok[2:])
            if v is None:
                raise ValueError(f"invalid comparator target: {tok}")
            out.append(f"{tok[:2]}{v}")
        elif tok[0] in "<>" :
            v = parse_version(tok[1:])
            if v is None:
                raise ValueError(f"invalid comparator target: {tok}")
            out.append(f"{tok[0]}{v}")
        elif tok.startswith("="):
            v = parse_version(tok[1:])
            if v is None:
                raise ValueError(f"invalid = target: {tok}")
            out.append(f"={v}")
        elif "x" in tok.lower() or "*" in tok:
            out.extend(_expand_xrange(tok))
        else:
            v = parse_version(tok)
            if v is None:
                raise ValueError(f"invalid bare-version target: {tok}")
            out.append(f"={v}")
    return out


def _atomic_satisfies(v: Version, atomic: str) -> bool:
    op = atomic[:2] if atomic[:2] in (">=", "<=") else atomic[0]
    rest = atomic[len(op):]
    target = parse_version(rest)
    if target is None:
        raise ValueError(f"invalid atomic comparator: {atomic}")
    if op == ">=":
        return v >= target
    if op == "<=":
        return v <= target
    if op == ">":
        return v > target
    if op == "<":
        return v < target
    if op == "=":
        return v == target
    raise ValueError(f"unknown op: {op}")


def satisfies(v: Version, range_expr: str) -> bool:
    """npm-style range satisfaction. Supports caret, tilde, comparators,
    conjunctions, disjunctions, hyphen, and x-ranges."""
    sets = [s.strip() for s in range_expr.split("||")]
    for cset in sets:
        atomics = _expand_comparator_set(cset)
        if not atomics:
            continue
        # Pre-release safety: a pre-release version satisfies a comparator
        # set only if at least one atomic mentions a version with the same
        # major.minor.patch and a pre-release.
        if _has_prerelease(v):
            mentions_pre = False
            for a in atomics:
                op = a[:2] if a[:2] in (">=", "<=") else a[0]
                target = parse_version(a[len(op):])
                if target is not None and _has_prerelease(target):
                    if (target.major, target.minor, target.patch) == (v.major, v.minor, v.patch):
                        mentions_pre = True
                        break
            if not mentions_pre:
                continue
        if all(_atomic_satisfies(v, a) for a in atomics):
            return True
    return False


# -------------------------------------------------------------- CLI


def cmd_parse(arg: str) -> int:
    v = parse_version(arg)
    if v is None:
        return 1
    print(str(v))
    return 0


def cmd_compare(a: str, b: str) -> int:
    va = parse_version(a)
    vb = parse_version(b)
    if va is None or vb is None:
        return 1
    if va < vb:
        print("lt")
    elif va > vb:
        print("gt")
    else:
        print("eq")
    return 0


def cmd_match(v_str: str, range_str: str) -> int:
    v = parse_version(v_str)
    if v is None:
        return 2
    try:
        ok = satisfies(v, range_str)
    except ValueError:
        return 2
    return 0 if ok else 1


def cmd_sort() -> int:
    out: list[Version] = []
    raw_lines = sys.stdin.read().splitlines()
    for line in raw_lines:
        if not line:
            continue
        v = parse_version(line)
        if v is None:
            return 1
        out.append(v)
    out.sort()
    for v in out:
        print(str(v))
    return 0


def main(argv: list[str]) -> int:
    if len(argv) < 1:
        return 1
    cmd = argv[0]
    args = argv[1:]
    if cmd == "parse" and len(args) == 1:
        return cmd_parse(args[0])
    if cmd == "compare" and len(args) == 2:
        return cmd_compare(args[0], args[1])
    if cmd == "match" and len(args) == 2:
        return cmd_match(args[0], args[1])
    if cmd == "sort" and len(args) == 0:
        return cmd_sort()
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
