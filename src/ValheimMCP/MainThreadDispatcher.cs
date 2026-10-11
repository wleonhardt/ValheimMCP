using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace ValheimMCP
{
    /// <summary>The queued call belonged to an extension that was reloaded or unloaded before it ran: old code must not act on the game after its replacement loaded.</summary>
    internal sealed class ExtensionReloadedException : Exception
    {
        public ExtensionReloadedException(string assembly) : base("extension " + assembly + " was reloaded or unloaded before this call ran; it was dropped") { }
    }

    /// <summary>
    ///     Marshals work from the HTTP listener's background thread onto Unity's
    ///     main thread, where Valheim/Unity APIs are safe to touch. The queue is
    ///     drained once per frame from <see cref="Plugin" />'s Update.
    ///     No game types here: the unit tests link this file and drive <see cref="Pump" /> from a fake main thread.
    /// </summary>
    internal static class MainThreadDispatcher
    {
        private sealed class Item
        {
            public Action Run;
            public Action Release;       // wakes the waiting caller without running the call
            public Assembly Owner;       // the assembly whose code the call runs (an extension's handler); null = the core
            public string Label;         // what the main thread is busy with, for the stall watchdog
            public Exception Dropped;    // set when the item is dropped on purpose (retired owner)
            public int State;            // 0 queued, 1 running (claimed by the main thread), 2 cancelled (the caller gave up; dropped at dequeue instead of running late)
        }

        private static readonly ConcurrentQueue<Item> Queue = new();
        private static readonly HashSet<Assembly> Retired = new();
        private static int _dropped;

        /// <summary>Warnings go here (Plugin sets it to the BepInEx log); null in tests.</summary>
        public static Action<string> Warn;

        /// <summary>Main-thread time one <see cref="Pump" /> may spend; the rest of the queue waits for the next frame (at least one item always runs).</summary>
        public static long PumpBudgetMs = 25;

        /// <summary>Calls dropped since start: the caller gave up before they ran, or their extension was retired.</summary>
        public static int DroppedCount { get { return Volatile.Read(ref _dropped); } }

        public static bool RunBlocking<T>(Func<T> func, int timeoutMs, out T result, out Exception error)
        {
            return RunBlocking(func, timeoutMs, out result, out error, out _);
        }

        public static bool RunBlocking<T>(Func<T> func, int timeoutMs, out T result, out Exception error, out bool started)
        {
            return RunBlocking(func, timeoutMs, null, null, out result, out error, out started);
        }

        /// <summary>
        ///     Run <paramref name="func" /> on the main thread and block the calling
        ///     (background) thread until it completes or <paramref name="timeoutMs" />
        ///     elapses. Returns false on timeout (e.g. game paused / not ticking).
        ///     On timeout the queued action is cancelled: it will NOT run later, so a
        ///     client retry cannot execute a write twice. An action that already started
        ///     when the timeout fires runs to completion (its result is discarded);
        ///     <paramref name="started" /> tells the caller which of the two happened.
        ///     <paramref name="owner" /> is the assembly of the code that runs (for an extension tool: the assembly of its handler, NOT of the lambda
        ///     that wraps it, which is the core); when that assembly is <see cref="Retire">retired</see> before the call runs, the call is dropped and
        ///     the caller gets <see cref="ExtensionReloadedException" /> as <paramref name="error" />.
        /// </summary>
        public static bool RunBlocking<T>(Func<T> func, int timeoutMs, Assembly owner, string label, out T result, out Exception error, out bool started)
        {
            T captured = default;
            Exception err = null;
            var done = new ManualResetEventSlim(false);
            var item = new Item { Owner = owner, Label = label ?? OwnerLabel(func) };
            item.Run = () =>
            {
                try { captured = func(); }
                catch (Exception ex) { err = ex; }
                finally { done.Set(); }
            };
            item.Release = () => done.Set();
            Queue.Enqueue(item);

            var completed = done.Wait(timeoutMs);
            // one atomic step decides: still queued -> cancelled (never runs), otherwise it is already running (or was dropped) and finishes on its own
            started = completed || Interlocked.CompareExchange(ref item.State, 2, 0) != 0;
            result = captured;
            error = err ?? item.Dropped;
            if (item.Dropped != null) started = false;        // dropped for a retired owner: the handler never ran
            return completed;
        }

        private static string OwnerLabel(Delegate d)
        {
            try { var m = d.Method; return (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name; }
            catch (Exception) { return "?"; }
        }

        /// <summary>The assembly a delegate's code lives in (an extension's tool handler: that extension's assembly instance).</summary>
        public static Assembly OwnerOf(Delegate d)
        {
            try { return d == null ? null : d.Method.Module.Assembly; }
            catch (Exception) { return null; }
        }

        /// <summary>ExtensionHost, when an extension is replaced or unloaded: its queued and future main-thread calls are dropped.</summary>
        public static void Retire(Assembly a) { if (a == null) return; lock (Retired) Retired.Add(a); }
        public static bool IsRetired(Assembly a) { if (a == null) return false; lock (Retired) return Retired.Contains(a); }

        /// <summary>Drain queued actions. MUST be called from the main thread.</summary>
        public static void Pump()
        {
            var clock = Stopwatch.StartNew();
            var ran = 0;
            while ((ran == 0 || clock.ElapsedMilliseconds < PumpBudgetMs) && Queue.TryDequeue(out var item))
            {
                if (IsRetired(item.Owner))
                {
                    if (Interlocked.CompareExchange(ref item.State, 2, 0) == 0)
                    {
                        Interlocked.Increment(ref _dropped);
                        item.Dropped = new ExtensionReloadedException(item.Owner.GetName().Name);
                        item.Release();                   // wake the caller with the error; the handler is never called
                    }
                    continue;
                }
                if (Interlocked.CompareExchange(ref item.State, 1, 0) != 0)
                {
                    Interlocked.Increment(ref _dropped);
                    Warn?.Invoke("[ValheimMCP] dropped a queued action whose caller timed out before it ran");
                    continue;
                }
                ran++;
                MainWatch.Enter(item.Label);
                try { item.Run(); }
                catch (Exception ex) { Warn?.Invoke("[ValheimMCP] queued main-thread action threw: " + ex); }
                finally { MainWatch.Exit(); }
            }
        }
    }
}
