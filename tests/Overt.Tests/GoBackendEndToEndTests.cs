using System.Diagnostics;
using Overt.Backend.Go;
using Overt.Compiler.Syntax;
using Xunit;

namespace Overt.Tests;

/// <summary>
/// End-to-end run for the Go back end. Lexes / parses a small Overt module,
/// emits Go source via <see cref="GoEmitter"/>, drops it next to the in-repo
/// runtime under <c>runtime/go</c> via a temp Go module, then invokes
/// <c>go build</c> + the resulting binary and asserts on stdout.
///
/// The test is skipped (via <see cref="SkippableFactAttribute"/>-style guard)
/// when the <c>go</c> toolchain is not on PATH, so a contributor without Go
/// installed sees a passing suite. CI runners that target the Go back end
/// install Go explicitly and this skip path is inert.
///
/// Scope: hello-world. As the GoEmitter grows additional features, parallel
/// tests should go here, each one a tight Overt → Go → run loop. The
/// existing C#-side tests (<c>StdlibTranspiledEndToEndTests</c>) are the
/// reference for which Overt features are expected to lower correctly.
/// </summary>
public class GoBackendEndToEndTests
{
    [Fact]
    public void Transpiled_Hello_PrintsToStdout()
    {
        // Go's fmt.Fprintln always writes a single LF regardless of OS,
        // while .NET's StreamReader on Windows doesn't translate the LF
        // to CRLF when reading from a pipe — so a literal "\n" matches.
        AssertOvertProgramPrints(
            """
            module hello

            fn main() !{io} -> Result<(), IoError> {
                println("hello from Go")?
                Ok(())
            }
            """,
            expectedStdout: "hello from Go\n");
    }

    [Fact]
    public void Transpiled_ExternGo_CallsStdlibFunction()
    {
        // Exercises `extern "go" fn` end-to-end: an Overt-side
        // declaration binding `strings.ToUpper` plus a main that
        // calls it. The emitter generates a Go shim that forwards
        // the call, adds `strings` to the import set, and the
        // result threads through string interpolation.
        AssertOvertProgramPrints(
            """
            module extern_e2e

            extern "go" fn upper(s: String) -> String binds "strings.ToUpper"

            fn main() !{io} -> Result<(), IoError> {
                let shouted: String = upper(s = "hello, world")
                println("got: ${shouted}")?
                Ok(())
            }
            """,
            expectedStdout: "got: HELLO, WORLD\n");
    }

    [Fact]
    public void Transpiled_ExternGo_ResultWrap_TError()
    {
        // Exercises the Result<T, IoError> shim convention. The Overt-
        // side declaration claims `Result<Int, IoError>`; the bound
        // Go function `strconv.Atoi` returns `(int, error)`. The
        // emitter recognizes the shape and inserts the err-check +
        // Ok/Err wrap.
        AssertOvertProgramPrints(
            """
            module result_wrap_e2e

            extern "go" fn atoi(s: String) -> Result<Int, IoError> binds "strconv.Atoi"

            fn main() !{io} -> Result<(), IoError> {
                let n: Int = atoi(s = "42")?
                println("got: ${n}")?
                Ok(())
            }
            """,
            expectedStdout: "got: 42\n");
    }

    [Fact]
    public void Transpiled_ExternGo_ResultWrap_ErrorOnly()
    {
        // Exercises the Result<(), IoError> shim convention for Go
        // functions that return just `error`. Bound to a hand-written
        // helper that always returns nil; the test confirms the
        // success path emits and runs cleanly.
        AssertOvertProgramPrints(
            """
            module result_wrap_unit_e2e

            extern "go" fn always_ok() !{io} -> Result<(), IoError> binds "AlwaysOk" from ""

            fn main() !{io} -> Result<(), IoError> {
                always_ok()?
                println("ok")?
                Ok(())
            }
            """,
            expectedStdout: "ok\n",
            extraGoSource: """
                package main

                func AlwaysOk() error {
                    return nil
                }
                """);
    }

    [Fact]
    public void Transpiled_ExternGo_OptionWrap_NilPointer()
    {
        // Exercises the Option<T> nil-pointer wrap. The Go-side
        // helper returns either `*Box{42}` for "found" or `nil` for
        // "missing". The shim nil-checks the return and emits
        // `overt.Some(...)` / `overt.None[...]()` accordingly.
        // Overt-side match-on-Option (handled by the existing
        // EmitMatchStdlibShape path) routes each arm.
        AssertOvertProgramPrints(
            """
            module option_wrap_e2e

            extern "go" type Box binds "*Box"
            extern "go" fn lookup(name: String) -> Option<Box> binds "Lookup" from ""
            extern "go" instance fn value(self: Box) -> Int binds "Value"

            fn main() !{io} -> Result<(), IoError> {
                match lookup(name = "found") {
                    Some(b) => println("got=${b.value()}")?,
                    None    => println("missing")?,
                }
                match lookup(name = "missing") {
                    Some(b) => println("got=${b.value()}")?,
                    None    => println("missing")?,
                }
                Ok(())
            }
            """,
            expectedStdout: "got=42\nmissing\n",
            extraGoSource: """
                package main

                type Box struct {
                    n int
                }

                func (b *Box) Value() int {
                    return b.n
                }

                func Lookup(name string) *Box {
                    if name == "found" {
                        return &Box{n: 42}
                    }
                    return nil
                }
                """);
    }

    [Fact]
    public void Transpiled_ExternGo_InstanceFn()
    {
        // Exercises `extern "go" instance fn`. The first Overt
        // parameter is `self`; the binds-target is the bare method
        // name (no package qualifier; receiver type implies the
        // package). Call site uses method-call syntax (`c.add(...)`)
        // which the type checker's MethodCallResolutions routes to
        // `add(self = c, ...)`.
        AssertOvertProgramPrints(
            """
            module instance_fn_e2e

            extern "go" type Counter binds "*Counter"

            extern "go" fn new_counter() -> Counter binds "NewCounter" from ""
            extern "go" instance fn add(self: Counter, x: Int) -> Int binds "Add"

            fn main() !{io} -> Result<(), IoError> {
                let c: Counter = new_counter()
                let n1: Int = c.add(x = 5)
                let n2: Int = c.add(x = 7)
                println("n1=${n1} n2=${n2}")?
                Ok(())
            }
            """,
            expectedStdout: "n1=5 n2=12\n",
            extraGoSource: """
                package main

                type Counter struct {
                    n int
                }

                func NewCounter() *Counter {
                    return &Counter{}
                }

                func (c *Counter) Add(x int) int {
                    c.n += x
                    return c.n
                }
                """);
    }

    [Fact]
    public void Transpiled_ExternGo_FunctionTypedParameter()
    {
        // Exercises a function-typed parameter on an `extern "go" fn`.
        // The Overt-side declaration uses `fn(String) -> String` as
        // the callback type; the GoEmitter lowers it to a Go
        // `func(string) string` parameter. The Overt-side named fn
        // (`shout`) is passed by name as the argument; the existing
        // IdentifierExpr emit produces `shout` in Go, which is a
        // valid function-value reference because Overt-side fns
        // emit at the same name on the Go side.
        //
        // The Go-side helper file (helper.go) provides the
        // RunCallback function the binding targets. Stdlib doesn't
        // have a clean string→string callback shape to bind to, so
        // the test ships its own.
        AssertOvertProgramPrints(
            """
            module fnparam_e2e

            extern "go" fn run_callback(name: String, cb: fn(String) -> String) -> String
                binds "RunCallback" from ""

            fn shout(s: String) -> String {
                "${s}!"
            }

            fn main() !{io} -> Result<(), IoError> {
                let result: String = run_callback(name = "hello", cb = shout)
                println(result)?
                Ok(())
            }
            """,
            expectedStdout: "hello!\n",
            extraGoSource: """
                package main

                func RunCallback(name string, cb func(string) string) string {
                    return cb(name)
                }
                """);
    }

    [Fact]
    public void Transpiled_ExternGo_OpaqueHostType()
    {
        // Exercises `extern "go" type` for an opaque host-type
        // round-trip. Declares `time.Time` as an opaque type, gets
        // a value via `time.Now()`, and discards it with `let _`.
        // The Go-side `time` import is added automatically based on
        // the binds-string. Without the opaque-type registry, the
        // emitter would throw on `LowerType(NamedType("Time"))`.
        AssertOvertProgramPrints(
            """
            module externtype_e2e

            extern "go" type Time binds "time.Time"

            extern "go" fn now() -> Time binds "time.Now"

            fn main() !{io} -> Result<(), IoError> {
                let _: Time = now()
                println("got time")?
                Ok(())
            }
            """,
            expectedStdout: "got time\n");
    }

    [Fact]
    public void Transpiled_ExternGo_FromClauseImportPath()
    {
        // Verifies the `from "<import-path>"` clause for cases where
        // the package selector and full Go import path differ. Uses
        // `path/filepath` whose declared package name is `filepath`
        // but whose import path is `path/filepath`. Without the
        // `from` clause the emitter would import a non-existent
        // top-level `filepath` package.
        AssertOvertProgramPrints(
            """
            module extern_path_e2e

            extern "go" fn base(p: String) -> String binds "filepath.Base" from "path/filepath"

            fn main() !{io} -> Result<(), IoError> {
                let leaf: String = base(p = "/usr/local/bin/overt")
                println("leaf: ${leaf}")?
                Ok(())
            }
            """,
            expectedStdout: "leaf: overt\n");
    }

    [Fact]
    public void Transpiled_ForEachOverList()
    {
        // Exercises `for x in iter` over a List<Int> built by Int.range,
        // a `?`-propagating call inside the loop body, and the loop's
        // unit-typed value (no return). Each iteration prints one line;
        // the deterministic three-line output proves the loop ran in
        // order through every element.
        AssertOvertProgramPrints(
            """
            module foreach_e2e

            fn main() !{io} -> Result<(), IoError> {
                for i in Int.range(start = 0, end = 3) {
                    println("i=${i}")?
                }
                Ok(())
            }
            """,
            expectedStdout: "i=0\ni=1\ni=2\n");
    }

    [Fact]
    public void Transpiled_ListAndPrelude_MapFilterFoldQuantifiers()
    {
        // Exercises Int.range, map / filter / fold / size / all / any
        // with named-fn callbacks (Overt has no inline-lambda syntax),
        // and string interpolation displaying the computed values.
        // Output is a single deterministic line that can only be
        // produced if the whole prelude pipeline emitted and ran
        // correctly.
        AssertOvertProgramPrints(
            """
            module list_e2e

            fn double(n: Int) -> Int { n * 2 }
            fn add(a: Int, b: Int) -> Int { a + b }
            fn is_even(n: Int) -> Bool { n - (n / 2) * 2 == 0 }
            fn is_zero(n: Int) -> Bool { n == 0 }

            fn main() !{io} -> Result<(), IoError> {
                let xs: List<Int> = Int.range(start = 1, end = 5)
                let doubled: List<Int> = map(list = xs, f = double)
                let evens: List<Int> = filter(list = xs, predicate = is_even)
                let total: Int = fold(list = xs, seed = 0, step = add)
                let n: Int = size(list = xs)
                let only_evens: Bool = all(list = evens, predicate = is_even)
                let has_zero: Bool = any(list = xs, predicate = is_zero)
                let first_doubled: Int = List.at(list = doubled, index = 0)

                println("size=${n} total=${total} all-even=${only_evens} any-zero=${has_zero} first2x=${first_doubled}")?
                Ok(())
            }
            """,
            // xs = [1,2,3,4]; doubled = [2,4,6,8]; evens = [2,4];
            // total = 1+2+3+4 = 10; size = 4;
            // all-even on evens = true; any-zero on xs = false; first2x = 2.
            expectedStdout: "size=4 total=10 all-even=true any-zero=false first2x=2\n");
    }

    [Fact]
    public void Transpiled_StringInterpolation_FmtSprintf()
    {
        // Exercises ${expr} interpolation across primitive types,
        // an arithmetic sub-expression inside an interpolation, and
        // multiple interpolations in one string. Output is fmt.Sprintf-
        // shaped: %v for each value, with the surrounding literal
        // re-encoded for Go (escapes preserved, % doubled).
        AssertOvertProgramPrints(
            """
            module interp_e2e

            fn main() !{io} -> Result<(), IoError> {
                let n: Int = 42
                let name: String = "alice"
                let flag: Bool = true
                println("name=${name} n=${n} flag=${flag}")?
                println("computed=${n + 8}")?
                println("literal-percent: 100%")?
                Ok(())
            }
            """,
            // Go's `%v` formats: int as decimal, string as itself, bool as
            // "true"/"false". The literal "%" survives the format-string
            // re-encoding and prints once.
            expectedStdout: "name=alice n=42 flag=true\ncomputed=50\nliteral-percent: 100%\n");
    }

    [Fact]
    public void Transpiled_EnumsAndMatch_DispatchesByVariant()
    {
        // Exercises enum decl emission (interface + struct-per-variant +
        // sealing method), bare and record-shape variant constructors,
        // match in return position with a type-switch, record-pattern
        // arms with field bindings, and bare-variant arms (no bindings).
        // The classify fn returns the area of each Shape; main verifies
        // each variant routes to the expected branch.
        AssertOvertProgramPrints(
            """
            module shape_e2e

            enum Shape {
                Circle { radius: Int },
                Square { side: Int },
                Point,
            }

            fn area(s: Shape) -> Int {
                match s {
                    Shape.Circle { radius = r } => r * r * 3,
                    Shape.Square { side = side } => side * side,
                    Shape.Point => 0,
                }
            }

            fn main() !{io} -> Result<(), IoError> {
                let c: Shape = Shape.Circle { radius = 4 }
                let sq: Shape = Shape.Square { side = 5 }
                let p: Shape = Shape.Point

                if area(s = c) == 48 {
                    println("circle ok")?
                }
                if area(s = sq) == 25 {
                    println("square ok")?
                }
                if area(s = p) == 0 {
                    println("point ok")?
                }
                Ok(())
            }
            """,
            expectedStdout: "circle ok\nsquare ok\npoint ok\n");
    }

    [Fact]
    public void Transpiled_Records_LiteralAndFieldAccess()
    {
        // Exercises record decl emission, record literal expression
        // (with named fields), field access on a record-typed value,
        // and a record passed across a fn boundary as both parameter
        // and return value.
        AssertOvertProgramPrints(
            """
            module records_e2e

            record Greeting {
                salutation: String,
                name: String,
            }

            fn make_greeting(who: String) -> Greeting {
                Greeting { salutation = "hello", name = who }
            }

            fn print_greeting(g: Greeting) !{io} -> Result<(), IoError> {
                println(g.salutation)?
                println(g.name)?
                Ok(())
            }

            fn main() !{io} -> Result<(), IoError> {
                let g: Greeting = make_greeting(who = "world")
                print_greeting(g = g)?
                Ok(())
            }
            """,
            expectedStdout: "hello\nworld\n");
    }

    [Fact]
    public void Transpiled_Arithmetic_LetIfElse()
    {
        // Exercises: integer literals, Int parameters, arithmetic
        // (+, *), comparison (==, <), let with type, statement-position
        // if/else (with an else-if chain), Bool branching. The user fn
        // returns Int and is called with named args; main uses the
        // result in an if-condition. Three branches, three distinct
        // print outputs — the chosen one tells us the whole pipeline
        // routed correctly.
        AssertOvertProgramPrints(
            """
            module arith

            fn classify(n: Int) -> Int {
                if n < 0 {
                    -1
                } else if n == 0 {
                    0
                } else {
                    1
                }
            }

            fn main() !{io} -> Result<(), IoError> {
                let x: Int = 3 + 4 * 2
                let cls: Int = classify(n = x - 11)
                if cls == 0 {
                    println("zero")?
                } else if cls < 0 {
                    println("negative")?
                } else {
                    println("positive")?
                }
                Ok(())
            }
            """,
            // x = 3 + 4*2 = 11; classify(0) = 0; prints "zero".
            expectedStdout: "zero\n");
    }

    [Fact]
    public void Transpiled_Parameters_ReceiveAndUseString()
    {
        // Exercises String parameters, identifier-expression references
        // inside the body, and a user-fn call from main with a named
        // argument. Each `greet` call propagates ? through the user fn
        // back into main's Result<Unit, IoError> return.
        AssertOvertProgramPrints(
            """
            module greet

            fn greet(name: String) !{io} -> Result<(), IoError> {
                println(name)?
                Ok(())
            }

            fn main() !{io} -> Result<(), IoError> {
                greet(name = "alice")?
                greet(name = "bob")?
                Ok(())
            }
            """,
            expectedStdout: "alice\nbob\n");
    }

    [Fact]
    public void NonGenericRefinement_RuntimeViolation_PanicsWithDescriptiveMessage()
    {
        // Mirrors the C# stdlib transpiled test of the same family:
        // `take(n)` passes an out-of-range Int into an `Age`-typed slot,
        // the type checker can't decide the predicate statically, and the
        // emitter wraps the call argument in `__Refinement_Age__Check`.
        // At runtime the helper panics an `overt.RefinementViolation`,
        // which Go formats to stderr along with the goroutine trace
        // before exiting non-zero. We assert on the violation message
        // text and a non-zero exit; the goroutine trace's exact shape
        // is Go-version-sensitive and not part of the contract.
        AssertOvertProgramPanicsWith(
            """
            module ref_nongen_e2e

            type Age = Int where 0 <= self && self <= 150

            fn take(a: Age) -> Age { a }

            fn main() !{io} -> Result<(), IoError> {
                let n: Int = 999
                let _: Age = take(n)
                Ok(())
            }
            """,
            expectedStderrSubstring:
                "value 999 does not satisfy refinement `Age` predicate: 0 <= self && self <= 150");
    }

    [Fact]
    public void NonGenericRefinement_ReturnPosition_PanicsWithDescriptiveMessage()
    {
        // Trailing-expression boundary: a fn typed `-> Age` returning a
        // bare `Int` triggers the helper at the return site. Verifies the
        // emitter wraps return-position values too, not just call args.
        AssertOvertProgramPanicsWith(
            """
            module ref_return_e2e

            type Age = Int where 0 <= self && self <= 150

            fn launder(n: Int) -> Age { n }

            fn main() !{io} -> Result<(), IoError> {
                let _: Age = launder(200)
                Ok(())
            }
            """,
            expectedStderrSubstring:
                "value 200 does not satisfy refinement `Age` predicate: 0 <= self && self <= 150");
    }

    /// <summary>
    /// Mirror of <see cref="AssertOvertProgramPrints"/> for programs that
    /// are expected to panic. Asserts the binary exits non-zero and that
    /// the captured stderr contains the substring callers expect to see
    /// (typically the formatted RefinementViolation message). The
    /// surrounding goroutine trace is Go-version-sensitive so we don't
    /// pin the full output, just the contract-relevant message.
    /// </summary>
    private static void AssertOvertProgramPanicsWith(
        string overtSource, string expectedStderrSubstring)
    {
        if (!IsGoOnPath())
        {
            return;
        }

        var lex = Lexer.Lex(overtSource);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);

        var resolved = Overt.Compiler.Semantics.NameResolver.Resolve(parse.Module);
        Assert.Empty(resolved.Diagnostics);
        var typed = Overt.Compiler.Semantics.TypeChecker.Check(parse.Module, resolved);
        Assert.Empty(typed.Diagnostics);

        var goSource = GoEmitter.Emit(parse.Module, typed);

        var workDir = Directory.CreateTempSubdirectory("overt-go-e2e-").FullName;
        try
        {
            var runtimePath = LocateRuntimePath();
            File.WriteAllText(
                Path.Combine(workDir, "go.mod"),
                $$"""
                module overt-app

                go 1.21

                require overt-runtime v0.0.0
                replace overt-runtime => {{runtimePath.Replace("\\", "/")}}
                """);
            File.WriteAllText(Path.Combine(workDir, "main.go"), goSource);

            RunGo(workDir, "mod", "tidy");

            var binPath = Path.Combine(workDir, IsWindows() ? "app.exe" : "app");
            RunGo(workDir, "build", "-o", binPath, ".");

            var (_, stderr, exit) = RunBinary(binPath);
            Assert.NotEqual(0, exit);
            Assert.Contains(expectedStderrSubstring, stderr);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Lex / parse / emit / `go build` / run / assert. Skips the whole
    /// pipeline when `go` is not on PATH; intentionally silent so the
    /// suite stays green for contributors without Go installed.
    ///
    /// <paramref name="extraGoSource"/>, when non-null, is written to a
    /// sibling <c>helper.go</c> next to the emitted <c>main.go</c>.
    /// Used by tests that need a small Go-side helper to bind into
    /// (e.g. function-typed extern parameters where stdlib doesn't
    /// have a clean callback shape to test against).
    /// </summary>
    private static void AssertOvertProgramPrints(
        string overtSource, string expectedStdout, string? extraGoSource = null)
    {
        if (!IsGoOnPath())
        {
            return;
        }

        var lex = Lexer.Lex(overtSource);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);

        var resolved = Overt.Compiler.Semantics.NameResolver.Resolve(parse.Module);
        Assert.Empty(resolved.Diagnostics);
        var typed = Overt.Compiler.Semantics.TypeChecker.Check(parse.Module, resolved);
        Assert.Empty(typed.Diagnostics);

        var goSource = GoEmitter.Emit(parse.Module, typed);

        var workDir = Directory.CreateTempSubdirectory("overt-go-e2e-").FullName;
        try
        {
            // Lay out a Go module that imports the in-repo runtime via a
            // `replace` directive, so the test never depends on a published
            // `overt-runtime` module on a registry.
            var runtimePath = LocateRuntimePath();
            File.WriteAllText(
                Path.Combine(workDir, "go.mod"),
                $$"""
                module overt-app

                go 1.21

                require overt-runtime v0.0.0
                replace overt-runtime => {{runtimePath.Replace("\\", "/")}}
                """);
            File.WriteAllText(Path.Combine(workDir, "main.go"), goSource);
            if (extraGoSource is not null)
            {
                File.WriteAllText(Path.Combine(workDir, "helper.go"), extraGoSource);
            }

            // `go mod tidy` to populate go.sum (Go 1.21+ refuses to build
            // without one even for a replace-only require).
            RunGo(workDir, "mod", "tidy");

            var binPath = Path.Combine(workDir, IsWindows() ? "app.exe" : "app");
            RunGo(workDir, "build", "-o", binPath, ".");

            var (stdout, stderr, exit) = RunBinary(binPath);
            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Equal(expectedStdout, stdout);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static bool IsGoOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo("go", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWindows()
        => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>Walk up from the test bin directory to the repo root,
    /// then down to <c>runtime/go</c>. The runtime is checked into the
    /// repo so the test doesn't need a network round trip.</summary>
    private static string LocateRuntimePath()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Overt.sln")))
        {
            here = here.Parent;
        }
        if (here is null)
        {
            throw new InvalidOperationException(
                "Could not locate Overt.sln walking up from " + AppContext.BaseDirectory);
        }
        var runtime = Path.Combine(here.FullName, "runtime", "go");
        if (!Directory.Exists(runtime))
        {
            throw new InvalidOperationException(
                "Go runtime directory not found at " + runtime);
        }
        return runtime;
    }

    private static void RunGo(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("go")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"`go {string.Join(" ", args)}` failed (exit {p.ExitCode})\n"
                + $"stdout:\n{stdout}\n"
                + $"stderr:\n{stderr}");
        }
    }

    private static (string Stdout, string Stderr, int Exit) RunBinary(string path)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (stdout, stderr, p.ExitCode);
    }
}
