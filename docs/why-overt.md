# Why Overt

I've shaved some yaks in my career, but this is the emperor yak shave:
a new programming language called Overt, targeted at AI coding agents.
Let me tell you how I got here and why I think it matters.

## The Problem

AI agents have varying levels of success with different programming
languages, and part of the problem is that programming languages are
designed around humans and their many foibles. Short names, implicit
effects, positional arguments, exceptions that unwind invisibly,
reflection, macros. All of these are accommodations for *human*
cognitive limits: small working memory, strong pattern-matching,
and strong causal intuition. The syntax gets terser because the reader
is expected to fill in context from training, convention, and nearby
code.

LLMs have a different profile. They have a large context window and pattern-matching
as strong as a good human's, but causal tracking across calls is
genuinely weak. An agent reading a function needs to know *at the
call site* what can fail, what I/O might happen, what types flow
through, and whether the function's result needs to be handled. Following the call
transitively to find all of this out costs real tokens of attention and
introduces mistakes. Languages designed for humans hide exactly this
information.

I had an idea. If I were going to design a language for an
audience that reads, writes, and reasons about code differently from
a human, what would it look like? Since I was going to be working
with an LLM anyway, what better collaborator to ask than an LLM?

In the space of a couple of evenings and one very, very early
morning, Claude and I produced a working compiler for Overt with a
.NET back end, and a Go back end is next. The rest of this document
describes the language that came out of those sessions and the
reasoning behind its shape.

## The Inversion

Overt takes the usual tradeoffs and flips them:

- **Brevity for signatures that explain themselves.** Name every
  error type in the return signature. No "it might throw"; you'd
  better list it.
- **Inference for types restated at use sites.** Annotate every
  `let`. Yes, it's redundant for the author. That redundancy is
  exactly what lets a reader make sense of a fragment out of
  context.
- **"Idiomatic" for one canonical form.** No ternary *and* if/else,
  no `for` and `while` and `foreach` doing the same thing. One way
  to do each task. Pattern-matching is much stronger when there's
  only one pattern.

The target point on the language-design curve is *optimized for the
agent, tolerable for the auditor.* That places Overt at a different
point than any existing language occupies.

## Humans Can Read It, but Humans Are Not the Audience

This is the thing that trips people up when they first read the
repo, so let me say it plainly: humans can read Overt, and I expect
human auditors to *review* Overt code in practice. Humans are not,
however, the primary authors, and the language doesn't bend itself
to make human authorship more comfortable at the expense of agent
clarity.

Overt does away with ambiguity and sugar syntax. There is only one
way to do any given task. There is no undefined behavior, no
invisible exception unwinding, and no method-call syntax overloading
dots. Effect rows are declared on every function. The compiler
rejects code that omits a type annotation on a `let`. That's intentional: every one of those
rejections is somewhere an agent would otherwise have to *guess*
from context, and guessing is where the wrong answers happen.

When you audit Overt code, the audit is comfortable for a different
reason than the authoring is: the signature of a function already
tells you everything you'd need to ask of it (effects, error type,
return). You don't need to read the body to know what it can do to
the system around it.

## How This Shows Up in Practice

Here are a few of the decisions, with the motivation attached to each.

### Errors Are Values, Always

Exceptions fail the "visible at the call site" test catastrophically.
An agent reading `parse(text)` has no idea whether it might throw,
what it throws, or which caller will handle it. In Overt:

```overt
fn parse(text: String) -> Result<Tree, ParseError> { ... }

fn use_it(text: String) !{fails} -> Result<Int, ParseError> {
    let tree: Tree = parse(text)?
    Ok(count_nodes(tree))
}
```

The `Result<Tree, ParseError>` spells out what goes wrong. The `?`
at the call site says "propagate the error if it happens." It's
visible, with no hidden control flow. An agent reading these two functions can
reason about the error paths without following any other call.

### Effect Rows Are Mandatory

Every function declares what effects it performs: `!{io}`,
`!{async}`, `!{inference}`, `!{fails}`. That row is visible on every
call site because the callee's signature is part of the caller's
context:

```overt
fn compute(x: Int) -> Int { x * 2 }             // pure
fn log(x: Int) !{io} -> Int { println("${x}")?; x }
```

An agent reading the caller knows immediately whether `log` can be
called from a pure function. The compiler enforces this
structurally (OV0310). It's not a guideline.

### The Compiler Writes Error Messages for the Agent

When Overt rejects a program, the diagnostic names the rule and
points at exactly where in the docs to read more:

```
app.ov:12:5: error: OV0310: function `process` performs effect `io`
  but its effect row is empty
  help: add `io` to the signature: `!{io}`
  note: see AGENTS.md §5 (effect rows)
```

The fix is in the message. No "check the docs" round-trip, because
the docs pointer *is in the error.* (Come to think of it, maybe programming
languages for humans should do this too.)

## Overt Is a .NET Application. Or a Go Application. Or...

Here's the part that surprises people: when I show them a working
Overt program, they assume it's running on some Overt VM. It isn't.
It's a .NET application. I don't mean that it has a lot in common
with .NET because that's what the compiler is written in; I mean it
*is* a .NET application.
It compiles to IL, debugs in Visual Studio against the original `.ov`
source, imports NuGet packages, publishes via `dotnet publish`,
participates in the `dotnet` build graph through MSBuild.

When the Go back end ships, an Overt program targeting Go *is* a Go
application. It'll use `go mod`, link Go libraries, and produce tiny
static binaries. The same applies to TypeScript, Rust, Zig, Swift, or
whatever comes after.

**When you choose Overt for a project, you choose the language your
agent writes. You also choose the back-end language and platform that
fits the application.** Those are independent choices. Overt doesn't
try to be multi-platform in the "write once, run anywhere" sense,
and it doesn't hide standard library bindings behind some
be-all-end-all abstraction. Each backend is explicit about its
target.

That means you can introduce Overt into an existing project (as
long as Overt has a back end for that project) by installing one
NuGet package and dropping `.ov` files next to `.cs` files. You can
even mix languages and platforms: Go for the web back end, TypeScript
for the web front end, and the agent can use a single language for
both.

## Why "Transpile to a Host Language" Is Right, Not a Workaround

Two observations make this the right architecture rather than a
shortcut:

**The compiler host and the emission target are independent axes.**
The compiler is a C# program today. It runs on .NET. It emits `.cs`
today and will emit `.go` next. Nothing about targeting Go requires
the compiler to *run on* Go's toolchain. Someone targeting Go just
needs .NET installed to run the compiler; the output runs on Go's
toolchain. Same pattern TypeScript uses: `tsc` is written in
TypeScript, runs on Node, emits JavaScript for any environment.

The practical consequence: **new backends are libraries, not
compilers.** A backend author doesn't rewrite the lexer, parser, or
type-checker. They write one project that walks the AST and emits
target source. The language is the AST shape. Everything above that
is shared across every backend.

**Inheriting the host's runtime is free power.** An Overt program
targeting .NET inherits the GC, BCL, async/await, NativeAOT, Blazor
WASM, and every NuGet package ever published. A Go-targeted Overt
program will inherit goroutines, channels, the Go module ecosystem,
and fast cold starts. I don't need to build my own stdlib for the
normal things; I bind to the target's stdlib through explicit, typed
FFI. Agents get to use their training priors about `System.Text.Json`
directly, and there's no "Overt JSON" that subtly differs from it.

Eventually there'll be a portable back end for Overt, but before
then there'll be many back ends that are all easier for AI to read,
write, and reason about (RWRA) because of Overt.

## What Overt Is Not

Here are a few things to get out of the way:

- **It's not a replacement for languages experienced engineers write
  by hand.** If you're a strong C# developer writing a
  performance-critical inner loop and you know exactly what IL you
  want, write C#. Overt's tradeoffs are for the author who *isn't*
  already expert, which describes every agent regardless of how
  much of the internet it's read.
- **It's not trying to be portable-across-backends.** An Overt
  program written against the C# back end uses C#-flavored stdlib
  facades and won't compile against Go. A portable back end will
  eventually exist, but it'll be its own deliberate thing, not
  something you get for free by emitting to multiple targets.
- **It's not done.** There's working support for records, enums,
  match, effect rows, refinement types, async with `.await`, and FFI
  into .NET's BCL. There's no LSP yet, no cross-file module system
  beyond the in-repo graph, no self-hosted compiler, no Go emission.
  The roadmap lives in [`CARRYOVER.md`](../CARRYOVER.md).

## What I Know vs. What I'm Hypothesizing

I'd rather be honest about this up front than hit someone with a
surprise later.

**What I know works:**

- Agents can write Overt. The full `examples/` directory was written
  collaboratively and runs end-to-end.
- The ecosystem mechanics hold up. `.ov` files compile in a
  `.csproj`. `overt` installs as a .NET global tool. `overt run`
  transpiles and executes in one shot.
- The docs-pointer-in-diagnostics pattern genuinely shortens the
  loop between "compiler rejected my code" and "here's the fix."

**What I'm guessing at:**

- That agents produce *better* Overt than they produce Python or
  C# on the same task. I believe this, but I haven't run controlled
  measurements. Individual impressions have been encouraging;
  individual impressions aren't data.
- That the specific knobs Overt turned (mandatory `let`
  annotations, `.await` postfix, no shadowing, etc.) each earn their
  keep. Some probably matter more than others, and the order isn't
  yet measured.
- That the "agent writes, human audits" workflow is practical at
  scale. Overt is deliberately tolerable-to-audit rather than
  pleasant-to-author, but how much faster the audit is than reading
  idiomatic Python, I haven't measured.

The path to validation is usage, which brings us to the next
section.

## Try It

The [README](../README.md) has a quick-try: clone the repo, run one
command, and you're executing an Overt program through the full
pipeline. To pull Overt into an existing C# project, see [AGENTS.md
§11](../AGENTS.md#11-building-with-msbuild-c-backend) and the working
sample at [`samples/msbuild-smoke/`](../samples/msbuild-smoke/).

I'm especially interested in feedback from people whose first
reaction is "this is going to be annoying." If the language is
optimized for the wrong thing, that's where I'll find out.

---

*Overt is open source under Apache 2.0. The design rationale lives in
[`DESIGN.md`](../DESIGN.md); the working reference for authoring code
is [`AGENTS.md`](../AGENTS.md); examples under
[`examples/`](../examples/) are the living test of what actually
works today.*
