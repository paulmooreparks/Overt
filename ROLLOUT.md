# Rollout plan

Living plan for taking Overt from "usable in-repo" to "discoverable and
tryable by the broader developer community, with a feedback loop." Sibling
document to CARRYOVER.md (session state) and AGENTS.md (working
reference). Update in-place as decisions settle.

## Goal

Get Overt in front of developers who are curious about agent-first
language design so that it picks up **usage, coverage, and feedback**.
Not a recruitment drive for contributors; not a thought-leadership
essay exercise. The asked-for outcome is "people try it and tell us
what works and what doesn't."

## Audience

Primary: developers who read programming-language discussions (HN,
Lobsters, language-design corners of Twitter/X) and who would try a
new language if it had a clear thesis, a usable install path, and a
small-but-real demo. Secondary: people working with coding agents
(Claude Code, Cursor, Aider, etc.) who would be curious how a language
designed for agents differs in practice.

Not the audience (yet): compiler-internals contributors, enterprise
adopters, language-spec reviewers. Those come later if the first wave
produces signal.

## Non-goals (this phase)

- No nuget.org publish until the landing story exists. Publishing is
  a one-way door on the package name and version line; first visitors
  to a bare package page will bounce. Gated behind Phase 2.
- No Go back end, no formatter, no LSP as prerequisites. Those ship if
  demand surfaces; they aren't launch-blocking.
- No exhaustive language reference / spec document. DESIGN.md exists;
  AGENTS.md is the working reference. A formal spec is a post-traction
  artifact.

## Phases and sequencing

Dependencies flow top-down: each phase's exit criteria gate the next.

### Phase 1: Foundation (repo-only, no external surface)

Deliverables:
- [ ] **Refresh `README.md`.** Not a greenfield write; a tightening pass
  on the existing 230-line file. Concrete edits: trim the leading status
  blockquote, fix stale test count (file says 354; currently 389), drop
  the unfinished Compiler Explorer sentence, lead with the thesis instead
  of status, add a "Quick try" section near the top once the global-tool
  install path works. Target: a visitor in under a minute knows whether
  to click deeper.
- [ ] `docs/why-overt.md`: the thesis. "Languages were designed for
  humans; AI agents are now primary authors, so what changes?" Names
  the tradeoffs (redundant annotations, effect rows, no shadowing,
  explicit awaits, etc.) and what each buys an agent. This is the
  piece a curious reader will actually argue with, so it needs to be
  honest about what's hypothesis vs. validated.
- [ ] One real sample application under `samples/`. Not an isolated
  feature demo (examples/ already plays that role), but a small
  end-to-end CLI tool written in Overt. Candidates: `json-flatten`,
  `config-validate`, `log-summarize`. Pick the one most agent-friendly
  to write.
- [ ] `overt` CLI installable via `dotnet tool install --global`.
  Prerequisite for anyone wanting to "try it" without cloning the repo.
  Requires packing `Overt.Cli` as a tool package and wiring the
  `dotnet-tool` manifest.

Exit criteria: someone can land on the repo, read the README, skim
the thesis, install `overt` globally, build the sample app, and form
an opinion, all within 10 minutes and without reading DESIGN.md.

### Phase 2: Soft-launch (low-friction public channels)

Deliverables:
- [ ] Publish `Overt.Build` to nuget.org.
- [ ] Publish `overt` as a .NET global tool on nuget.org.
- [ ] `docs/tutorial.md`: guided walk from hello-world through
  records/match/refinements/FFI/async, built on the existing examples/
  corpus. Target: a reader who finished the thesis and wants to "try
  more" has a 30-minute path.
- [ ] `docs/howto/`: 4–6 task-shaped pages, each: problem statement →
  idiomatic Overt → resulting output → where the design shows.
  Candidates: JSON roundtrip, file I/O with Result, async fetch, state
  machine, refinement validation, error-propagation chain.
- [ ] Issue templates under `.github/ISSUE_TEMPLATE/`: bug,
  language-design feedback, "I tried to write X and got stuck."
  Low ceremony; the goal is lowering friction for the feedback loop.

Exit criteria: someone unfamiliar with Overt can `dotnet tool install
-g overt`, follow the tutorial or a howto, and get a working program;
and has a clear place to report what broke.

### Phase 3: Public awareness

Deliverables:
- [ ] Blog post hosting (GitHub Pages, Substack, or personal blog)
  carrying the `why-overt.md` thesis plus a "try it" section pointing
  at the README.
- [ ] Show HN post. Link to blog post (canonical URL) rather than the
  README so the conversation has a stable anchor.
- [ ] LinkedIn post (shorter, more personal framing than HN).
- [ ] Twitter/X, Lobsters, Mastodon posts as secondary amplifiers.

Exit criteria: the thesis lands in at least one public forum with
enough substance that commenters can engage with it rather than just
reacting to the tagline.

### Phase 4: Ongoing (after first wave of feedback)

Deliverables (priority shaped by what Phase 3 surfaces):
- [ ] 3-minute "what is Overt" video, for readers who bounced off the
  written thesis.
- [ ] 10-minute "live code with an agent" demo, literally Claude Code
  or Cursor writing Overt; the most differentiating artifact if the
  agent-RWRA properties actually hold up.
- [ ] More samples as feedback reveals patterns people actually want.
- [ ] Language-feature work driven by real blockers users hit (not
  the current roadmap, which is design-driven).

## Open questions

- **Where does the blog post live?** GitHub Pages on the repo (lowest
  friction, owned by the project) vs. Paul's personal blog (more
  personal voice, but the project reads as one-person-shop). Tradeoff
  to decide before Phase 3.
- **What's the contribution posture?** Issues open for bug reports + design
  feedback; PRs accepted for obvious fixes. Anything more formal
  (contributor agreement, style guide, PR template beyond basics)
  deferred until we know there are contributors.
- **Feedback channel beyond issues?** GitHub Discussions is cheap and
  aligns with the "we want feedback, not commits" posture. Discord
  feels premature; a channel with three people in it is worse than
  no channel.
- ~~**Version cadence?**~~ Resolved — see *Versioning policy* below.
- ~~**Licensing statement?**~~ Apache 2.0 already committed
  (see [`LICENSE`](LICENSE)); no action needed.

## Versioning policy

Three version concepts, each with its own discipline:

### Release version

Auto-bumped per release through the `release.yml` workflow. Tag shape
selects the channel:

| Tag             | Channel | Cadence       | Suffix   |
|-----------------|---------|---------------|----------|
| `v0.X.0-dev.N`  | dev     | per feature   | `-dev.N` |
| `v0.X.0-beta.N` | beta    | when promoted | `-beta.N`|
| `v0.X.0`        | stable  | when promoted | (none)   |

The `N` counter rolls within a `(MAJOR.MINOR, channel)` pair. Workflow
dispatch computes the next dev counter automatically; tag-push works
too. No manual decision per dev release.

### MINOR version (the `0.X` in `VERSION`)

Pre-1.0 SemVer is technically "anything goes," but the MINOR slot
still carries a deliberate signal: **a coherent set of changes worth
noticing has crossed a line**. We bump it manually, not on every
breaking change. The criteria:

1. A coherent batch of related work has landed (typically a foundational
   set or major-feature arc), not a single change.
2. At least one breaking change is in the batch — additive-only sets
   stay within the current MINOR.
3. The OSL spec (or DESIGN.md / AGENTS.md, where relevant) is updated
   to reflect the post-bump shape.

The signal a MINOR bump sends is "if you were pinned to the previous
MINOR, you should look before upgrading." Pre-1.0, that's the
strongest signal we make.

What this means in practice — examples we've already accumulated or
will accumulate:

- **`0.1` → `0.2`** triggers when the foundational OSL set (per
  [`docs/osl.md`](docs/osl.md)) is complete and the queued breaking
  cleanups land:
  - All ⏳ foundational items shipped (List ops, String ops, Bytes,
    Console expansions, File / Directory / Path expansions, Log via
    Trace).
  - TraceEvent reshape from `{ description }` to `{ level, message }`.
  - `len` synonym removed in favor of `size`.
  - Form-3 polish: clean OV-coded diagnostic for old-form
    `Type.method()` calls.
  - OSL spec stamped `osl-1.0` (foundational complete).
- **`0.2` → `0.3`** would trigger on a major feature arc:
  real-concurrency through the Go emitter (chat-relay Phase 2+),
  generic-refinement runtime checks, LSP Phase 1, or CLI self-host
  Layer 2 — each is months of work.
- **`0.x` → `1.0`** is the SemVer commitment threshold: breaking
  changes require a deprecation cycle. Gated on language-design
  stability + a sample-app constellation that validates the design
  against real programs.

### Language version

Tracked implicitly by the MINOR bump for now. If the language and the
toolchain ever need to version independently (e.g. a compiler version
that supports multiple language versions), we'll introduce a separate
`language` field. Not necessary today.

### Don't-do list (anti-patterns to avoid)

- Don't bump MINOR for additive-only releases. The dev-counter rolling
  is the signal there.
- Don't bump MINOR for a single breaking change. Wait for a coherent
  batch — bumps that signal "look here" are diluted by bumps that
  signal "one tiny change."
- Don't pin transitively-dependent consumers to a specific dev tag in
  documentation. Floating `0.X.0-*` is the contract for dev consumers;
  the `-*` is part of the deal.

## What this plan deliberately leaves out

- MSBuild integration polish (already ships and works).
- Feature work (the CARRYOVER "what next" list), treated as orthogonal
  to rollout. Rollout can proceed with the current feature set; feature
  work continues independently based on user feedback.
- International / non-English docs; premature.
- A spec document; the DESIGN + AGENTS pair covers this for now.
