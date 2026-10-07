using System;
using System.Collections.Concurrent;
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
            public int Cancelled;        // 1 once the caller gave up: the action is dropped at dequeue instead of running late
        }

        private static readonly ConcurrentQueue<Item> Queue = new();

        /// <summary>
        ///     Run <paramref name="func" /> on the main thread and block the calling
        ///     (background) thread until it completes or <paramref name="timeoutMs" />
        ///     elapses. Returns false on timeout (e.g. game paused / not ticking).
        ///     On timeout the queued action is cancelled: it will NOT run later, so a
        ///     client retry cannot execute a write twice. An action that already started
        ///     when the timeout fires runs to completion (its result is discarded).
        /// </summary>
        public static bool RunBlocking<T>(Func<T> func, int timeoutMs, out T result, out Exception error)
        {
            T captured = default;
            Exception err = null;
            var done = new ManualResetEventSlim(false);
            var item = new Item();
            item.Run = () =>
            {
                try { captured = func(); }
                catch (Exception ex) { err = ex; }
                finally { try { done.Set(); } catch (ObjectDisposedException) { } }
            };
            Queue.Enqueue(item);

            var completed = done.Wait(timeoutMs);
            if (!completed) Interlocked.Exchange(ref item.Cancelled, 1);
            else done.Dispose();
            result = captured;
            error = err;
            return completed;
        }

        /// <summary>Drain queued actions. MUST be called from the main thread.</summary>
        public static void Pump()
        {
            while (Queue.TryDequeue(out var item))
            {
                if (Volatile.Read(ref item.Cancelled) == 1) { Plugin.Log?.LogWarning("[ValheimMCP] dropped a queued action whose caller timed out before it ran"); continue; }
                try { item.Run(); }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[ValheimMCP] queued main-thread action threw: {ex}");
                }
            }
        }

        public static int Pending => Queue.Count;
    }
}
