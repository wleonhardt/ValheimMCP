using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ValheimMCP;
using Xunit;

// The dispatcher is process-wide state: its tests run one after the other.
[CollectionDefinition("dispatcher", DisableParallelization = true)]
public class DispatcherCollection { }

[Collection("dispatcher")]
public class MainThreadDispatcherTests
{
    static void Drain() { for (var i = 0; i < 20; i++) MainThreadDispatcher.Pump(); }

    [Fact]
    public void ACallRunsWhenTheMainThreadPumps()
    {
        var ran = 0;
        var t = Task.Run(() => { var ok = MainThreadDispatcher.RunBlocking(() => { Interlocked.Increment(ref ran); return 7; }, 5000, out var r, out var e, out var started); return (ok, r, e, started); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < 5000) { MainThreadDispatcher.Pump(); Thread.Sleep(2); }
        var (ok2, r2, e2, st) = t.Result;
        Assert.True(ok2); Assert.Equal(7, r2); Assert.Null(e2); Assert.True(st); Assert.Equal(1, ran);
    }

    [Fact]
    public void ACallThatTimedOutBeforeItStartedNeverRunsLate()
    {
        var ran = 0;
        var ok = MainThreadDispatcher.RunBlocking(() => { Interlocked.Increment(ref ran); return 1; }, 40, out _, out _, out var started);
        Assert.False(ok); Assert.False(started);
        Drain();
        Assert.Equal(0, ran);                                  // a retry cannot apply the write twice
    }

    [Fact]
    public void ACallThatAlreadyStartedFinishesAndTheCallerIsTold()
    {
        var gate = new ManualResetEventSlim(false); var began = new ManualResetEventSlim(false); var finished = 0;
        var pump = Task.Run(() => { var sw = System.Diagnostics.Stopwatch.StartNew(); while (finished == 0 && sw.ElapsedMilliseconds < 5000) { MainThreadDispatcher.Pump(); Thread.Sleep(2); } });
        var ok = MainThreadDispatcher.RunBlocking(() => { began.Set(); gate.Wait(5000); Interlocked.Exchange(ref finished, 1); return 1; }, 150, out _, out _, out var started);
        Assert.False(ok); Assert.True(started);                // timed out while running: it finishes on its own, the caller must not assume "never ran"
        gate.Set(); pump.Wait(5000);
        Assert.Equal(1, finished);
    }

    [Fact]
    public void ACallOfARetiredExtensionIsDroppedAndTheCallerFailsAtOnce()
    {
        var retired = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("RetiredExt"), AssemblyBuilderAccess.Run);
        MainThreadDispatcher.Retire(retired);
        var ran = 0; var before = MainThreadDispatcher.DroppedCount;
        var t = Task.Run(() => { var ok = MainThreadDispatcher.RunBlocking(() => { Interlocked.Increment(ref ran); return 1; }, 5000, retired, "x", out var r, out var e, out var started); return (ok, e, started); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < 5000) { MainThreadDispatcher.Pump(); Thread.Sleep(2); }
        var (ok2, e2, st) = t.Result;
        Assert.True(ok2); Assert.IsType<ExtensionReloadedException>(e2); Assert.False(st);
        Assert.Equal(0, ran);
        Assert.True(MainThreadDispatcher.DroppedCount > before);
    }

    [Fact]
    public void ACallOfALiveExtensionStillRuns()
    {
        var live = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LiveExt"), AssemblyBuilderAccess.Run);
        var t = Task.Run(() => MainThreadDispatcher.RunBlocking(() => 5, 5000, live, "x", out var r, out var e, out _) ? r : -1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < 5000) { MainThreadDispatcher.Pump(); Thread.Sleep(2); }
        Assert.Equal(5, t.Result);
    }

    [Fact]
    public void OnePumpStopsStartingCallsOnceItsBudgetIsSpent()
    {
        var old = MainThreadDispatcher.PumpBudgetMs; MainThreadDispatcher.PumpBudgetMs = 1;
        try
        {
            var ran = 0;
            var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() => MainThreadDispatcher.RunBlocking(() => { Thread.Sleep(25); Interlocked.Increment(ref ran); return 1; }, 10000, out int _r, out Exception _e, out bool _s))).ToArray();
            Thread.Sleep(300);                                 // all three are queued
            MainThreadDispatcher.Pump();
            Assert.Equal(1, ran);                              // the first used up the budget; the rest wait for the next frame
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (ran < 3 && sw.ElapsedMilliseconds < 5000) { MainThreadDispatcher.Pump(); Thread.Sleep(2); }
            Assert.Equal(3, ran);
            Task.WaitAll(tasks, 5000);
        }
        finally { MainThreadDispatcher.PumpBudgetMs = old; }
    }

    [Fact]
    public void OwnerOfIsTheAssemblyOfTheHandlersCode()
    {
        Func<string> inCoreLib = "x".ToUpperInvariant;
        Func<int, int> inTests = x => x + 1;
        Assert.Equal(typeof(string).Assembly, MainThreadDispatcher.OwnerOf(inCoreLib));
        Assert.Equal(typeof(MainThreadDispatcherTests).Assembly, MainThreadDispatcher.OwnerOf(inTests));
        Assert.Null(MainThreadDispatcher.OwnerOf(null));
    }
}

public class StallDetectorTests
{
    [Fact] public void QuietMainThreadReportsNothing() => Assert.Null(new StallDetector().Check(1000, -1, null, 990));

    [Fact]
    public void WorkRunningTooLongIsReportedWithItsLabelThenRepeatedThenClosed()
    {
        var d = new StallDetector();
        Assert.Null(d.Check(1500, 0, "plan_apply", 1490));                    // 1.5 s: still fine
        var r = d.Check(2100, 0, "plan_apply", 1490);
        Assert.Equal("work", r.Kind); Assert.Equal("plan_apply", r.Label); Assert.Equal(2100, r.ElapsedMs);
        Assert.Null(d.Check(30000, 0, "plan_apply", 1490));                   // no repeat before 60 s
        Assert.Equal("work", d.Check(62200, 0, "plan_apply", 1490).Kind);
        var end = d.Check(63000, -1, null, 62900);
        Assert.Equal("end", end.Kind); Assert.True(end.ElapsedMs >= 60000);
        Assert.Null(d.Check(64000, -1, null, 63900));                         // closed once
    }

    [Fact]
    public void NoFrameAndNoWorkIsAFrameStall()
    {
        var d = new StallDetector();
        Assert.Null(d.Check(4000, -1, null, 0));
        var r = d.Check(5100, -1, null, 0);
        Assert.Equal("frame", r.Kind); Assert.Equal("", r.Label);
    }

    [Fact] public void NoFrameYetIsNotAStall() => Assert.Null(new StallDetector().Check(100000, -1, null, -1));      // before the first frame

    [Fact]
    public void DescribeNamesTheCulprit()
    {
        Assert.Contains("plan_apply", MainWatch.Describe(new StallReport { Kind = "work", Label = "plan_apply", ElapsedMs = 2500 }));
        Assert.Contains("no frame", MainWatch.Describe(new StallReport { Kind = "frame", ElapsedMs = 6000 }));
    }
}

public class BuildStampTests
{
    [Fact] public void FormatShowsCommitDirtyBranchWorktree() => Assert.Equal("abc1234.dirty main @ValheimMCP", BuildStamp.Format("abc1234", "main", "true", "ValheimMCP"));
    [Fact] public void CleanBuildHasNoDirtyMark() => Assert.Equal("abc1234 main", BuildStamp.Format("abc1234", "main", "false", ""));
    [Fact] public void NoGitMeansNoStamp() => Assert.Equal("no build stamp", BuildStamp.Format("", "", "", ""));
    [Fact] public void AnAssemblyWithoutStampSaysSo() => Assert.Equal("no build stamp", BuildStamp.Of(typeof(object).Assembly));
}

public class BindCheckTests
{
    static byte[] Compile(string name, string source, params byte[][] references)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        foreach (var r in references) tpa.Add(MetadataReference.CreateFromImage(r));
        var comp = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, tpa, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var res = comp.Emit(ms);
        Assert.True(res.Success, string.Join("\n", res.Diagnostics));
        return ms.ToArray();
    }

    const string CoreV1 = "namespace FxCore { public static class Api { public static int A() => 1; } }";
    const string CoreV2 = "namespace FxCore { public static class Api { public static int A() => 1; public static int B() => 2; } }";
    const string ExtUsesA = "namespace FxExt { public static class E { public static int Run() => FxCore.Api.A(); } }";
    const string ExtUsesB = "namespace FxExt { public static class E { public static int Run() => FxCore.Api.A() + FxCore.Api.B(); } }";

    static Assembly LoadExtAgainstRunningCore(byte[] runningCore, byte[] ext)
    {
        var ctx = new AssemblyLoadContext("fx-" + Guid.NewGuid(), true);
        ctx.LoadFromStream(new MemoryStream(runningCore));        // the core the game started with
        return ctx.LoadFromStream(new MemoryStream(ext));
    }

    [Fact]
    public void AnExtensionBuiltAgainstTheRunningCoreBinds()
    {
        var v1 = Compile("FxCore", CoreV1);
        var asm = LoadExtAgainstRunningCore(v1, Compile("FxExt", ExtUsesA, v1));
        Assert.Empty(BindCheck.UnboundCoreRefs(asm, "FxCore", 5));
    }

    [Fact]
    public void AnExtensionBuiltAgainstANewerCoreIsRefusedBeforeItRuns()
    {
        var v1 = Compile("FxCore", CoreV1); var v2 = Compile("FxCore", CoreV2);
        var asm = LoadExtAgainstRunningCore(v1, Compile("FxExt", ExtUsesB, v2));   // uses Api.B, which the running core (v1) lacks
        var bad = BindCheck.UnboundCoreRefs(asm, "FxCore", 5);
        Assert.NotEmpty(bad);
        Assert.Contains("FxCore", bad[0]);
    }

    [Fact]
    public void AnAssemblyThatDoesNotReferenceTheCoreIsNotChecked()
    {
        var asm = LoadExtAgainstRunningCore(Compile("FxCore", CoreV1), Compile("FxPlain", "namespace P { public static class Q { public static int R() => 3; } }"));
        Assert.Empty(BindCheck.UnboundCoreRefs(asm, "FxCore", 5));
    }

    [Fact]
    public void OnlyMessagesNamingTheCoreCount()
    {
        Assert.True(BindCheck.Names("Method not found: 'Int32 ValheimMCP.Api.B()'.", "ValheimMCP"));
        Assert.False(BindCheck.Names("Could not load type 'Player' from assembly 'assembly_valheim'", "ValheimMCP"));
    }

    [Fact] public void TheLimitStopsTheWalk()
    {
        var v1 = Compile("FxCore", CoreV1); var v2 = Compile("FxCore", "namespace FxCore { public static class Api { public static int A() => 1; public static int B() => 2; public static int C() => 3; public static int D() => 4; } }");
        var asm = LoadExtAgainstRunningCore(v1, Compile("FxExt", "namespace FxExt { public static class E { public static int Run() => FxCore.Api.B() + FxCore.Api.C() + FxCore.Api.D(); } }", v2));
        Assert.Single(BindCheck.UnboundCoreRefs(asm, "FxCore", 1));
    }
}

public class NotificationTests
{
    [Fact] public void AMessageWithoutAnIdIsANotification() => Assert.True(JsonRpc.IsNotification(new Dictionary<string, object> { { "method", "tools/call" }, { "params", new Dictionary<string, object>() } }));
    [Fact] public void AMessageWithAnIdIsARequest() => Assert.False(JsonRpc.IsNotification(new Dictionary<string, object> { { "method", "tools/call" }, { "id", 1.0 } }));
    [Fact] public void ANullIdIsStillAnId() => Assert.False(JsonRpc.IsNotification(new Dictionary<string, object> { { "method", "x" }, { "id", null } }));
    [Fact] public void NullIsNotANotification() => Assert.False(JsonRpc.IsNotification(null));
}
