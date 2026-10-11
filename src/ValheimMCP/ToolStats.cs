// Per-tool call statistics: how many calls, how they ended, how long the handler ran. Game-free, so tests/ links it. Telemetry only; nothing here decides anything.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ValheimMCP
{
    internal enum CallOutcome { Ok, Error, Threw, Timeout }

    internal sealed class ToolStats
    {
        /// <summary>A main-thread slice longer than this makes a frame hitch the player can feel (the sliced jobs budget 8 ms; the pump 25 ms).</summary>
        public const double SlowMs = 25;
        private const int Samples = 256;

        private sealed class Row
        {
            public long Calls, Errors, Threw, Timeouts, Slow;
            public double MaxMs, TotalMs;
            public readonly double[] Ring = new double[Samples]; public int Filled, Next;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();

        public void Record(string tool, double handlerMs, CallOutcome outcome)
        {
            lock (_lock)
            {
                Row r; if (!_rows.TryGetValue(tool, out r)) _rows[tool] = r = new Row();
                r.Calls++;
                if (outcome == CallOutcome.Error) r.Errors++; else if (outcome == CallOutcome.Threw) r.Threw++; else if (outcome == CallOutcome.Timeout) r.Timeouts++;
                if (handlerMs > SlowMs) r.Slow++;
                if (handlerMs > r.MaxMs) r.MaxMs = handlerMs;
                r.TotalMs += handlerMs; r.Ring[r.Next] = handlerMs; r.Next = (r.Next + 1) % Samples; if (r.Filled < Samples) r.Filled++;
            }
        }

        /// <summary>The p-th percentile (0..1) of the samples, nearest rank.</summary>
        public static double Percentile(IList<double> samples, double p)
        {
            if (samples.Count == 0) return 0;
            var s = samples.OrderBy(x => x).ToList();
            var i = (int)Math.Ceiling(p * s.Count) - 1;
            return s[Math.Max(0, Math.Min(s.Count - 1, i))];
        }

        public string Json()
        {
            lock (_lock)
            {
                var rows = _rows.OrderByDescending(kv => kv.Value.Slow).ThenByDescending(kv => kv.Value.Calls).Select(kv =>
                {
                    var r = kv.Value; var samples = r.Ring.Take(r.Filled).ToList();
                    return "{\"tool\":\"" + Safe(kv.Key) + "\",\"calls\":" + r.Calls + ",\"errors\":" + r.Errors + ",\"threw\":" + r.Threw + ",\"timeouts\":" + r.Timeouts + ",\"slow\":" + r.Slow +
                        ",\"p50Ms\":" + N(Percentile(samples, 0.5)) + ",\"p95Ms\":" + N(Percentile(samples, 0.95)) + ",\"maxMs\":" + N(r.MaxMs) + "}";
                });
                return "{\"slowMs\":" + N(SlowMs) + ",\"tools\":[" + string.Join(",", rows.ToArray()) + "],\"note\":\"threw and timeouts are ours to look at; errors are refusals and bad arguments the caller can fix. Times are the handler's own run time on the game thread, not the wait for the frame.\"}";
            }
        }

        private static string Safe(string name) { return new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-').ToArray()); }

        private static string N(double v) { return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
