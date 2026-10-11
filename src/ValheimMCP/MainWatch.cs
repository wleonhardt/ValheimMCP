using System;
using System.Diagnostics;
using System.Threading;

namespace ValheimMCP
{
    /// <summary>One report of the stall detector.</summary>
    internal sealed class StallReport
    {
        public string Kind;        // work: marked main-thread work runs too long | frame: no frame and no marked work | end: the stall is over
        public string Label;       // what the main thread is busy with (work), "" otherwise
        public long ElapsedMs;     // how long the work has run / how long since the last frame / how long the stall lasted
    }

    /// <summary>
    ///     Pure decision of the main-thread watchdog (unit-tested): given the clock, when the marked work started (-1 = none), its label and when the
    ///     last frame was seen, say whether to report a stall. A stall is reported when it is first seen, repeated every <see cref="RepeatMs" /> while it
    ///     lasts, and closed with an "end" report.
    /// </summary>
    internal sealed class StallDetector
    {
        public long WorkThresholdMs = 2000;    // marked work (a tool call, a queued job) running this long
        public long FrameThresholdMs = 5000;   // no frame and no marked work this long: the game itself stopped (loading, driver, minimised)
        public long RepeatMs = 60000;

        private bool _inStall;
        private long _stallStartMs, _lastReportMs;

        public StallReport Check(long nowMs, long workStartMs, string label, long lastFrameMs)
        {
            string kind = null; long elapsed = 0;
            if (workStartMs >= 0 && nowMs - workStartMs >= WorkThresholdMs) { kind = "work"; elapsed = nowMs - workStartMs; }
            else if (workStartMs < 0 && lastFrameMs >= 0 && nowMs - lastFrameMs >= FrameThresholdMs) { kind = "frame"; elapsed = nowMs - lastFrameMs; }

            if (kind == null)
            {
                if (!_inStall) return null;
                _inStall = false;
                return new StallReport { Kind = "end", Label = "", ElapsedMs = nowMs - _stallStartMs };
            }

            if (!_inStall)
            {
                _inStall = true; _stallStartMs = nowMs - elapsed; _lastReportMs = nowMs;
                return new StallReport { Kind = kind, Label = kind == "work" ? label ?? "?" : "", ElapsedMs = elapsed };
            }
            if (nowMs - _lastReportMs >= RepeatMs)
            {
                _lastReportMs = nowMs;
                return new StallReport { Kind = kind, Label = kind == "work" ? label ?? "?" : "", ElapsedMs = elapsed };
            }
            return null;
        }
    }

    /// <summary>
    ///     Main-thread watchdog. <see cref="Enter" /> / <see cref="Exit" /> mark work on the main thread (two interlocked writes), <see cref="Frame" /> is called
    ///     once per frame; a background thread looks four times a second and logs a stall with the name of the work, so "the game froze" has a culprit.
    /// </summary>
    internal static class MainWatch
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long _workStart = -1, _lastFrame = -1;
        private static string _label;
        private static Thread _thread;
        private static volatile bool _stop;
        public static Action<string> Log;
        public static int Reports;

        public static void Enter(string label) { _label = label; Interlocked.Exchange(ref _workStart, Clock.ElapsedMilliseconds); }
        public static void Exit() { Interlocked.Exchange(ref _workStart, -1); }
        public static void Frame() { Interlocked.Exchange(ref _lastFrame, Clock.ElapsedMilliseconds); }

        public static void Start()
        {
            if (_thread != null) return;
            _stop = false;
            var det = new StallDetector();
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    Thread.Sleep(250);
                    try
                    {
                        var r = det.Check(Clock.ElapsedMilliseconds, Interlocked.Read(ref _workStart), _label, Interlocked.Read(ref _lastFrame));
                        if (r == null) continue;
                        Reports++;
                        Log?.Invoke(Describe(r));
                    }
                    catch (Exception ex) { Log?.Invoke("[ValheimMCP] main-thread watchdog failed: " + ex.Message); }
                }
            }) { IsBackground = true, Name = "ValheimMCP-mainwatch" };
            _thread.Start();
        }

        public static void Stop() { _stop = true; _thread = null; }

        public static string Describe(StallReport r)
        {
            switch (r.Kind)
            {
                case "work": return "[ValheimMCP] main thread busy for " + r.ElapsedMs + " ms in " + r.Label;
                case "frame": return "[ValheimMCP] no frame for " + r.ElapsedMs + " ms and no ValheimMCP work running (loading, a driver stall, or the window is not updating)";
                default: return "[ValheimMCP] main-thread stall over after " + r.ElapsedMs + " ms";
            }
        }
    }
}
