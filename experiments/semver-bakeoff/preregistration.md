# SemVer bake-off: pre-registration

**Status: DRAFT, locked on commit.** This document fixes the
hypothesis, predictions, method, measurements, and decision rules
*before* any trials run. The point is to remove post-hoc framing
freedom: when results land, we compare them against this document
verbatim, and we publish whatever we find.

If anything in this document changes after trials begin, the change
is logged in a section at the bottom with rationale. Silent edits
defeat the purpose.

**Date drafted:** 2026-04-28.
**Author:** Paul Parks, with Claude (Opus 4.7) as collaborator.
**Lock signature:** the git commit that introduces this file. Any
substantive change after that commit must appear in §11 with
rationale.

---

## 1. Background and central claim

Overt's design thesis is that a programming language built around how
LLM agents read, write, and reason about code (RWRA) produces code
that has fewer defects, requires fewer iterations to ship, and reads
more clearly than the same task written in a language designed for
humans. The thesis rests on agent-cognition asymmetries: large
context, strong pattern-matching, weak causal-chain simulation. The
language inverts the usual brevity-vs-explicitness tradeoffs to play
to those strengths.

This experiment tests one direct prediction of that thesis: **on a
fixed task with a mechanical acceptance test, agents authoring in
Overt will produce fewer defects on first compile and require fewer
correction iterations than the same agents authoring in established
mainstream languages.**

The experiment is **not** designed to test:
- Long-term maintainability of agent-authored code
- Performance of the resulting programs
- Library-ecosystem quality (the task is deliberately stdlib-only)
- Multi-agent collaboration scenarios
- Anything about human-authored Overt

Those are separate experiments.

---

## 2. The task

A SemVer 2.0.0 parser, comparator, and range-matcher CLI. The agent
is given a task description, a target language, and a project
skeleton; it produces source code that, when invoked from the
shell, satisfies the acceptance test suite.

### 2.1. CLI surface

The agent's deliverable must respond to four subcommands:

```
semver parse "<version>"
  → exit 0 with normalised version on stdout if parses
  → exit 1 with error message on stderr if doesn't

semver compare "<a>" "<b>"
  → exit 0; stdout is one of "lt", "eq", "gt"
  → exit 1 if either fails to parse

semver match "<version>" "<range>"
  → exit 0 if version satisfies range
  → exit 1 if it doesn't
  → exit 2 if either fails to parse

semver sort < lines.txt
  → reads versions from stdin, one per line
  → writes them sorted ascending to stdout, one per line
  → exit 0 on success; exit 1 if any line fails to parse
```

### 2.2. Spec coverage

- SemVer 2.0.0 grammar in full (major, minor, patch, pre-release,
  build metadata).
- Pre-release precedence rules per spec §11.
- Build metadata ignored for precedence per spec §10.
- Range expressions: caret (`^1.2.3`), tilde (`~1.2.3`), comparators
  (`>=1.0.0`, `<2.0.0`), conjunctions (`>=1.0.0 <2.0.0`),
  disjunctions (`||`), X-ranges (`1.x`, `1.2.x`), hyphen ranges
  (`1.0.0 - 2.0.0`).

### 2.3. What the agent receives

A directory containing:
- `TASK.md` — task description, including the CLI surface above and
  pointers to the SemVer 2.0.0 spec
- `tests/` — golden input/output test cases (the same suite the
  acceptance harness will run against the submission)
- A language-appropriate project skeleton (just enough to compile/run
  a hello-world; no library hints, no stub implementations)

For Overt trials only, when the experimental condition calls for
it, the directory also contains `AGENTS.md` (Overt's grounding
doc). This is the **only** asymmetry between conditions, and it is
itself a measured variable (see §4.3).

### 2.4. What the agent does NOT receive

- Existing SemVer libraries in the target language are not banned,
  but the project skeleton does not import any. Whether the agent
  reaches for a library is itself observable behavior. (Realistically:
  for Python the agent will probably reach for `semver`; we should
  decide before runs whether to disable network/package-install or
  let it ride. **Decision: package install permitted, but its
  occurrence is logged as a metric — see §5.2.5.**)
- Information that this is a comparison study, that other agents are
  attempting the same task in other languages, or that any specific
  metric is being captured. The framing is operational: "build this
  thing."

---

## 3. Languages and models

### 3.1. Languages (target back ends)

- **Overt** (C# back end), versioned at the commit-locked tip.
- **Python** 3.12, stdlib only unless the agent reaches for a package.
- **TypeScript** on Node 20, stdlib + `@types/node`. No additional
  dependencies in the skeleton.
- **C#** on .NET 9, stdlib only.

### 3.2. Models

- **Claude Sonnet 4.6** (`claude-sonnet-4-6`)
- **Claude Opus 4.7** (`claude-opus-4-7`)

Both with default sampling parameters. Temperature pinned to the
API default for reproducibility, recorded in the trial log.

### 3.3. Conditions

- For Python / TypeScript / C#: one condition per language —
  agent gets the task and skeleton, nothing more.
- For Overt: **two conditions**:
  - **Overt-cold**: same as the others. Agent gets task + skeleton,
    no AGENTS.md.
  - **Overt-grounded**: AGENTS.md is in the working directory.

The Overt-cold condition is a meaningful comparison ("how does an
agent do with a novel language and just a spec"). The
Overt-grounded condition is the production-realistic test ("how
does an agent do with the language plus its grounding doc"). Both
get reported.

### 3.4. Trial validity

A trial counts as valid if and only if:
- The agent received the prompt and produced at least one tool call.
- The session terminated either by agent submission, budget
  exhaustion, or all-tests-passing.
- The transcript is complete and parseable.

A trial is invalid (and re-run from scratch) if:
- API error / network outage interrupted the session before the
  agent could submit.
- Harness bug corrupted the transcript.
- Test infrastructure failure (acceptance suite couldn't run).

Re-runs are noted in the trial directory's `metadata.json` with
the reason. The original (invalid) trial's directory is preserved
for audit but excluded from the analysis. **Re-run trials are NOT
discarded if the agent merely failed to make progress** — that's a
real outcome and is what the budget-out flag captures.

### 3.5. Cells and trial count

```
            Sonnet 4.6  Opus 4.7
Python         5          5
TypeScript     5          5
C#             5          5
Overt-cold     5          5
Overt-grounded 5          5
```

**50 trials total.** Each trial is independent: cold session, no
shared context across trials, no human intervention during the
run.

---

## 4. Predictions

Each prediction is numbered, falsifiable, and load-bearing or
texture-flagged. Load-bearing predictions are ones whose failure
materially weakens the central claim. Texture predictions provide
context but don't move the headline.

### P1 — First-compile defect rate (LOAD-BEARING)

On the first submission that compiles successfully, **Overt-grounded
trials will pass at least 25% more acceptance tests on average than
Python trials, holding model constant.**

Concretely: if Python's mean first-compile pass rate across 5 trials
is `P`, Overt-grounded's mean must be at least `P × 1.25` (or
equivalently `P + 0.25 × (1 − P)` if working in absolute percentage
points — pick one before runs and stick with it; **decision:
multiplicative**).

The 25% threshold is chosen because smaller effects are not
distinguishable from noise at N=5 per cell with the variance we
expect in agent runs. If Overt's effect is real but smaller, this
experiment cannot detect it; we'd need a follow-up with N≥20.

**Falsification:** if Overt-grounded does not clear the 25%
threshold over Python on first-compile pass rate, the load-bearing
RWRA claim is significantly weakened. Publish that result.

### P2 — Iteration count to all-tests-passing (LOAD-BEARING)

**Overt-grounded trials will require fewer compile-test-fix
iterations on average than Python trials before all acceptance
tests pass**, holding model constant.

Magnitude not pre-specified because iteration counts are
high-variance and we don't have priors. Direction is the prediction.

**Falsification:** if Overt-grounded mean iteration count is greater
than or equal to Python's, the second leg of the central claim is
significantly weakened.

### P3 — Grounding doc effect (LOAD-BEARING for grounding strategy)

**Overt-grounded trials will pass at least 15% more acceptance tests
on first compile than Overt-cold trials.**

If P3 fails (AGENTS.md doesn't measurably help), it tells us the
agent has a usable Overt prior already (unlikely, given training
cutoff and the language's recency) OR that AGENTS.md isn't well-
calibrated to what the agent needs. Both are actionable.

### P4 — Cross-language ordering (TEXTURE)

On first-compile defect rate, languages will order:
**Python > TypeScript > C# > Overt-cold > Overt-grounded** (where
higher = more defects). The intuition: Python permits more silent
mistakes; TypeScript adds structural typing; C# adds nominal types
and visibility; Overt adds effects, refinement types, and mandatory
annotations.

If this ordering doesn't hold (e.g., Python beats TypeScript on
defects), it's a finding about the languages but doesn't
invalidate the central claim.

### P5 — Idiomatic-fit parity (TEXTURE / SANITY CHECK)

On the human-rated idiomatic-fit score (1-5 scale, blind review,
rubric in §6), **Overt's mean score will be within 0.5 points of the
mean of the three established languages.**

The bet is "Overt looks competently written, not awkward." Failure
here means agents are producing fluent-looking-but-wrong code in
the established languages and stilted-looking code in Overt — which
would mean we have to discount the defect-rate win.

### P6 — Failure-mode separation (TEXTURE)

**The qualitative failure log will show categorically different
errors across languages.** Specifically: Python failures will
disproportionately involve runtime type errors and `None` handling;
TypeScript failures will involve type-system fights without runtime
manifestations; C# failures will involve null-reference exceptions;
Overt failures will involve agent confusion about effect rows or
refinement constructors.

This isn't quantified — it's a reading of the failure logs.

---

## 5. Measurements

### 5.1. What's captured per trial

Every trial produces a directory under
`experiments/semver-bakeoff/runs/{model}/{language}/{condition}/{trial-n}/`
containing:

- `prompt.md` — the verbatim system + user prompt the agent received
- `transcript.jsonl` — line-per-turn log of every agent message and
  every tool call (timestamp, role, content, tool name, tool args,
  tool result)
- `submission/` — the final source code state when the agent
  submitted (or when the budget expired)
- `submissions/` — every intermediate state the agent had compile
  successfully, snapshotted (one subdir per snapshot)
- `test-results.json` — the acceptance test outcome on the final
  submission, broken down per test
- `first-compile-results.json` — same, but on the first
  successfully-compiling submission

### 5.2. Primary metrics (computed from the above)

#### 5.2.1. First-compile pass rate
Number of acceptance tests passing on the first successfully-
compiling submission, divided by total tests.

#### 5.2.2. Final pass rate
Number of acceptance tests passing on the final submission,
divided by total tests. Capped at 1.0; budget-out leaves whatever
the agent had at termination.

#### 5.2.3. Iteration count to all-passing
Number of distinct edit→compile→test cycles between session start
and the first all-tests-green state. If never reached: budget-out
flag, count = total cycles attempted.

#### 5.2.4. Wall-clock time to all-passing
Timestamp delta from first agent message to first all-tests-green
state. Budget-out flag if not reached.

#### 5.2.5. Library reach
Boolean: did the agent install or import a third-party SemVer
library? Logged but does not count as failure. (For Python this
is permitted; for the others it's effectively unavailable in the
skeleton.)

### 5.3. Secondary metrics

#### 5.3.1. Final lines of code
Count of non-blank, non-comment lines in the final submission's
source files. Per-file breakdown.

#### 5.3.2. File count
Number of source files in the final submission.

#### 5.3.3. Idiomatic-fit score
Human-rated 1-5 (rubric §6). Reviewer is blind to the
language-condition mapping (i.e., does not know whether a file is
Python or anonymized).

#### 5.3.4. Surprises log
Free-form notes from the same human reviewer, capturing any
non-obvious idioms the agent reached for that worked or didn't.

### 5.4. Tertiary (qualitative)

For each trial, an annotation pass:
- Failure-mode category (if applicable): `compile-fight`,
  `runtime-type`, `null-handling`, `effect-row-confusion`,
  `refinement-constructor-confusion`, `algorithm-error`,
  `spec-misread`, `out-of-budget`, `other`.
- Single-paragraph summary of what went well or poorly.

---

## 6. Idiomatic-fit rubric (locked before runs)

A single reviewer (not Paul, not Claude) scores each final
submission 1-5 using this rubric:

| Score | Description |
|-------|-------------|
| 5 | Reads as if written by an experienced practitioner of this language. Uses the language's standard idioms naturally. No unnecessary verbosity, no obvious anti-patterns, structure matches what a senior engineer would produce. |
| 4 | Reads as competent. Uses the language's main features correctly. Minor stylistic infelicities (over-explicit in a few places, missed a small idiom, etc.) but nothing that would fail code review. |
| 3 | Reads as journeyman. Works but has one or two clear style issues — verbosity that should have been a built-in, missed opportunities to use a language feature, awkward structure. Code review would request revisions. |
| 2 | Reads as labored. The author is fighting the language, reaching for primitives instead of stdlib, or reinventing things the language already provides. Works but feels off. |
| 1 | Reads as if produced by someone who doesn't know the language. Pervasive anti-patterns, primitives where idioms exist, structural choices that fight the language's grain. |

Score is per-language: a 5 in Python means "looks like good Python";
a 5 in Overt means "looks like good Overt." The reviewer is given
a one-page-per-language style reference (idioms, common stdlib
patterns) so they can score consistently across languages without
needing to be expert in all four. **The Overt style reference is
this repo's `docs/why-overt.md` plus `samples/portcheck/portcheck.ov`
as a canonical exemplar.**

The reviewer does NOT see the test results when scoring (avoids
"this passed all tests so it must be good Python" bias). The
reviewer scores stylistic quality only.

---

## 7. Decision rules

After all 50 trials run and all metrics are captured:

### 7.1. Central claim outcomes

- **P1 holds AND P2 holds**: central claim survives, headline is
  "Overt produces materially fewer defects on first compile and
  requires fewer iterations to ship, on this task, with these
  models."
- **P1 holds but P2 fails**: half-survival — Overt is more correct
  but not faster. Interesting; might mean agents over-iterate in
  Overt when uncertain. Worth qualifying the headline.
- **P1 fails but P2 holds**: half-survival in the other direction.
  Agents iterate less in Overt but don't end up correcter on first
  pass. Likely means the language's compile-time gates are
  catching things but not enough things.
- **P1 fails AND P2 fails**: central claim is significantly
  weakened. Publish that.

### 7.2. Grounding doc

- **P3 holds**: AGENTS.md is doing useful work; keep investing in
  it; consider it a non-optional part of agent-Overt deployment.
- **P3 fails AND Overt-cold tracks Overt-grounded**: agents have
  enough Overt prior already, OR the spec is verbose enough to
  bridge the gap. Either way, AGENTS.md is over-engineered for the
  agent's needs at this task scale — re-evaluate.
- **P3 fails AND Overt-cold is markedly worse than Overt-grounded
  but Overt-grounded is also worse than Python**: AGENTS.md helps
  but not enough. Diagnose what was missing.

### 7.3. Idiomatic fit

- **P5 holds**: the defect-rate win is real, not bought by
  producing-stilted-but-correct code. Keep the headline.
- **P5 fails (Overt rates more than 0.5 below comparison mean)**:
  the win comes with a stylistic cost. Note it. The product question
  becomes "is the defect reduction worth the style cost?" — that's
  a separate judgment call.

---

## 8. Constraints and limitations (acknowledged upfront)

- **Single task.** SemVer is one task; results may not generalize
  to (a) tasks with heavy I/O, (b) tasks with concurrent execution,
  (c) tasks requiring large amounts of code, (d) tasks with rich
  domain modeling beyond bounded numerics and strings.
- **Single model family.** All trials use Claude. A different
  model family might have different language priors. We do NOT
  claim "agents prefer Overt"; we claim "this model family produces
  fewer defects in Overt on this task."
- **Small N per cell.** Five trials per cell. Detects large
  effects cleanly; subtle effects are below this study's resolution.
- **Single neutral framing.** Agents are told to build the thing,
  not that it's a comparison. Different framings (contest, throwaway,
  production) might shift behavior. The neutral framing is closer
  to default user behavior; that's why we picked it; it's still
  one calibration point among many.
- **Author bias risk.** Paul designed Overt, has authoring intuitions
  that may have shaped the task and rubric. Mitigation: blind
  review pass for the qualitative scoring; pre-registration of all
  numeric thresholds.
- **Library-reach asymmetry.** Python has a popular SemVer library;
  Overt does not. We chose to permit library install but log it as
  metric §5.2.5; we do NOT exclude library-using trials from the
  core analysis. If many Python trials reach for `semver`, the
  comparison effectively becomes "Overt agent vs. Python's library
  ecosystem," which is a real production scenario but not a pure
  agent-language comparison. We'll segment the analysis if library
  reach is high (>50% of Python trials).

---

## 9. Publication commitment

We commit, in advance, to publishing the results of this
experiment regardless of outcome. Specifically:

- A writeup containing: this pre-reg verbatim, trial-by-trial
  numeric results, the per-cell summary table, blind-review
  qualitative scores, predictions-vs-results comparison,
  limitations as observed (in addition to those pre-registered).
- The full trial corpus (transcripts, submissions, test results)
  in this repository under `experiments/semver-bakeoff/runs/`.
- A summary post (likely a blog entry or arxiv-style writeup)
  linking to the corpus.

If P1 and P2 both fail, we publish that with the same prominence
as if they held. The experiment's value is in the answer, not in
which answer we get.

---

## 10. Pre-registered analyses

Beyond reporting raw numbers, the analysis pass will:

1. Compute per-cell means and standard deviations for every
   primary and secondary metric.
2. Run pairwise comparisons (Overt-grounded vs. Python,
   Overt-grounded vs. TypeScript, etc.) on first-compile pass
   rate. Use Welch's t-test for independent samples; report effect
   size (Cohen's d) alongside p-values. **N is too small to lean
   on p-values alone**; effect sizes are the primary read.
3. Plot first-compile pass rate per cell as a strip chart (one dot
   per trial), so distribution shape is visible alongside means.
4. Cross-tabulate failure modes (§5.4) by language; report counts.
5. For Overt-cold vs. Overt-grounded specifically, paired analysis
   on the cell pairs sharing model + trial-index; the same model's
   behavior on the same trial-index across conditions is the
   tightest comparison.

Anything else done to the data after results land is exploratory
and labeled as such in the writeup.

---

## 11. Modifications log

Changes to this document after locking are recorded here with
date, what changed, and why. **Empty modifications log at lock
time.**

(none yet)

---

## 12. Lock signature

This document is considered locked when committed to the
repository. The commit hash is the cryptographic signature of the
content; the lock timestamp is the commit timestamp.

When trials begin, the run script reads the locked content and
records its hash in every transcript. If the document changes
between commit and run-script execution, the recorded hash
mismatches and the run is invalid.
