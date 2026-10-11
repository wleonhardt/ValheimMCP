using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace ValheimMCP
{
    /// <summary>
    ///     Marshals work from the HTTP listener's background thread onto Unity's
    ///     main thread, where Valheim/Unity APIs are safe to touch. The queue is
    ///     drained once per frame from <see cref="Plugin" />'s Update.
    /// </summary>
    internal static class MainThreadDispatcher
    {
        private sealed class Item
        {
            public Action Run;
            public int State;            // 0 queued, 1 running (claimed by the main thread), 2 cancelled (the caller gave up; dropped at dequeue instead of running late)
        }

        private static readonly ConcurrentQueue<Item> Queue = new();

        /// <summary>Main-thread time one <see cref="Pump" /> may spend; the rest of the queue waits for the next frame (at least one item always runs).</summary>
        private const long PumpBudgetMs = 25;

        public static bool RunBlocking<T>(Func<T> func, int timeoutMs, out T result, out Exception error)
        {
            return RunBlocking(func, timeoutMs, out result, out error, out _);
        }

        /// <summary>
        ///     Run <paramref name="func" /> on the main thread and block the calling
        ///     (background) thread until it completes or <paramref name="timeoutMs" />
        ///     elapses. Returns false on timeout (e.g. game paused / not ticking).
        ///     On timeout the queued action is cancelled: it will NOT run later, so a
        ///     client retry cannot execute a write twice. An action that already started
        ///     when the timeout fires runs to completion (its result is discarded);
        ///     <paramref name="started" /> tells the caller which of the two happened.
        /// </summary>
        public static bool RunBlocking<T>(Func<T> func, int timeoutMs, out T result, out Exception error, out bool started)
        {
            T captured = default;
            Exception err = null;
            var done = new ManualResetEventSlim(false);
            var item = new Item();
            item.Run = () =>
            {
                try { captured = func(); }
                catch (Exception ex) { err = ex; }
                finally { done.Set(); }
            };
            Queue.Enqueue(item);

            var completed = done.Wait(timeoutMs);
            // one atomic step decides: still queued -> cancelled (never runs), otherwise it is already running and finishes on its own
            started = completed || Interlocked.CompareExchange(ref item.State, 2, 0) != 0;
            result = captured;
            error = err;
            return completed;
        }

        /// <summary>Drain queued actions. MUST be called from the main thread.</summary>
        public static void Pump()
        {
            var clock = Stopwatch.StartNew();
            var ran = 0;
            while ((ran == 0 || clock.ElapsedMilliseconds < PumpBudgetMs) && Queue.TryDequeue(out var item))
            {
                if (Interlocked.CompareExchange(ref item.State, 1, 0) != 0) { Plugin.Log?.LogWarning("[ValheimMCP] dropped a queued action whose caller timed out before it ran"); continue; }
                ran++;
                try { item.Run(); }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[ValheimMCP] queued main-thread action threw: {ex}");
                }
            }
        }
    }
}
