using System.Collections.Immutable;
using System.Linq;
using Overt.Runtime;
using static Overt.Runtime.Prelude;

namespace Overt.Tests;

/// <summary>
/// Exercises the runtime stdlib implementations directly — map / filter / fold /
/// par_map / Trace. These are the calls that transpiled Overt code lowers to, so
/// a regression here is a regression in every example that touches collections.
/// </summary>
public class StdlibRuntimeTests
{
    private static Overt.Runtime.List<T> L<T>(params T[] items) => new(ImmutableArray.Create(items));

    // ----------------------------------------------------------------- map

    [Fact]
    public void Map_PreservesOrderAndAppliesFunction()
    {
        var input = L(1, 2, 3, 4);
        var result = map<int, int>(input, x => x * x);
        Assert.Equal(new[] { 1, 4, 9, 16 }, result.Items);
    }

    [Fact]
    public void Map_EmptyListReturnsEmpty()
    {
        var result = map<int, int>(L<int>(), x => x + 1);
        Assert.Empty(result.Items);
    }

    // ------------------------------------------------------------- filter

    [Fact]
    public void Filter_KeepsOnlyMatching()
    {
        var input = L(1, 2, 3, 4, 5);
        var result = filter<int>(input, x => x % 2 == 0);
        Assert.Equal(new[] { 2, 4 }, result.Items);
    }

    [Fact]
    public void Filter_AllMatchReturnsAll()
    {
        var input = L(1, 2, 3);
        var result = filter<int>(input, _ => true);
        Assert.Equal(new[] { 1, 2, 3 }, result.Items);
    }

    [Fact]
    public void Filter_NoneMatchReturnsEmpty()
    {
        var result = filter<int>(L(1, 2, 3), _ => false);
        Assert.Empty(result.Items);
    }

    // --------------------------------------------------------------- fold

    [Fact]
    public void Fold_AccumulatesLeftToRight()
    {
        var input = L(1, 2, 3, 4);
        var sum = fold<int, int>(input, 0, (acc, x) => acc + x);
        Assert.Equal(10, sum);
    }

    [Fact]
    public void Fold_EmptyReturnsSeed()
    {
        var seed = 42;
        var result = fold<int, int>(L<int>(), seed, (acc, x) => acc + x);
        Assert.Equal(seed, result);
    }

    [Fact]
    public void Fold_OrderIsLeftAssociative()
    {
        // Build a string that records the traversal order.
        var input = L("a", "b", "c");
        var result = fold<string, string>(input, "|", (acc, x) => acc + x);
        Assert.Equal("|abc", result);
    }

    // ------------------------------------------------------------ par_map

    [Fact]
    public void ParMap_SuccessPreservesOrder()
    {
        var input = L(1, 2, 3, 4, 5);
        var result = par_map<int, int, string>(input, x => Ok(x * 10));
        var ok = Assert.IsType<ResultOk<Overt.Runtime.List<int>, string>>(result);
        Assert.Equal(new[] { 10, 20, 30, 40, 50 }, ok.Value.Items);
    }

    [Fact]
    public void ParMap_FirstErrByIndexWins()
    {
        var input = L(1, 2, 3, 4, 5);
        var result = par_map<int, int, string>(input, x =>
            x == 3 ? Err<string>("three")
            : x == 4 ? Err<string>("four")
            : Ok(x));
        var err = Assert.IsType<ResultErr<Overt.Runtime.List<int>, string>>(result);
        Assert.Equal("three", err.Error);
    }

    [Fact]
    public void ParMap_EmptyReturnsOkEmpty()
    {
        var result = par_map<int, int, string>(L<int>(), x => Ok(x));
        var ok = Assert.IsType<ResultOk<Overt.Runtime.List<int>, string>>(result);
        Assert.Empty(ok.Value.Items);
    }

    [Fact]
    public void ParMap_RunsConcurrently()
    {
        // Observation test: with a blocking callback over many items, the
        // thread pool should spread the work across multiple threads.
        //
        // Skipped on GitHub Actions and on single-CPU hosts. On a fresh
        // CI runner the xUnit thread is itself a pool thread, and
        // Task.WaitAll can legally inline-run every queued task on the
        // caller when other pool threads are cold. That's a scheduler
        // optimization, not a par_map contract violation: the correctness
        // tests (ParMap_ReturnsOk, ParMap_FirstErrWins, etc.) cover the
        // contract. This one confirms the local iteration story.
        if (System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            return;
        if (System.Environment.ProcessorCount < 2)
            return;

        var threadIds = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var input = L(Enumerable.Range(1, 32).ToArray());
        par_map<int, int, string>(input, x =>
        {
            threadIds.TryAdd(System.Environment.CurrentManagedThreadId, 0);
            System.Threading.Thread.Sleep(50);
            return Ok(x);
        });
        Assert.True(threadIds.Count > 1,
            $"expected parallel execution on a {System.Environment.ProcessorCount}-CPU host, saw only {threadIds.Count} thread(s)");
    }

    // -------------------------------------------------------------- Trace

    private sealed record TestEvent(string Tag) : TraceEvent;

    [Fact]
    public void Trace_SubscribeAndEmitDispatchesToSubscribers()
    {
        Prelude.Trace._reset();
        var seen = new System.Collections.Generic.List<string>();
        Prelude.Trace.subscribe(evt =>
        {
            seen.Add(((TestEvent)evt).Tag);
            return Unit.Value;
        });

        Prelude.Trace.emit(new TestEvent("one"));
        Prelude.Trace.emit(new TestEvent("two"));

        Assert.Equal(new[] { "one", "two" }, seen);
    }

    [Fact]
    public void Trace_MultipleSubscribersFireInRegistrationOrder()
    {
        Prelude.Trace._reset();
        var order = new System.Collections.Generic.List<string>();
        Prelude.Trace.subscribe(evt => { order.Add("A"); return Unit.Value; });
        Prelude.Trace.subscribe(evt => { order.Add("B"); return Unit.Value; });

        Prelude.Trace.emit(new TestEvent("x"));

        Assert.Equal(new[] { "A", "B" }, order);
    }

    [Fact]
    public void Trace_EmitWithNoSubscribersIsNoop()
    {
        Prelude.Trace._reset();
        Prelude.Trace.emit(new TestEvent("lonely"));
    }
}
