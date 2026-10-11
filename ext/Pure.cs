// Game-free rules shared by the tools (no Unity types, so tests/ links this file): the server write gate, name validation, world bounds.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    /// <summary>The decision behind the dedicated server's write gate (ALLOW_SERVER_WRITES flag file + confirm string).</summary>
    internal static class WriteGate
    {
        public const string Confirm = "server-write";

        /// <summary>
        ///     Null when the call may proceed, else the refusal. A dry run skips the gate only when the handler itself honours
        ///     <c>dry</c> (reports instead of writing): a tool that ignores the argument would otherwise write with the gate closed.
        /// </summary>
        public static string Check(bool dry, bool handlerHonoursDry, bool flagExists, bool flagFresh, int flagMinutes, string flagPath, string confirm)
        {
            if (dry && handlerHonoursDry) return null;
            if (!flagExists) return "server writes are off: create " + flagPath + " (and remove it when done)";
            if (!flagFresh) return "server write flag expired (older than " + flagMinutes + " min): touch " + flagPath + " again";
            if (confirm != Confirm) return "pass confirm:'" + Confirm + "' with every server write";
            return null;
        }

        /// <summary>Is this error text one of the gate's refusals (as opposed to a failure of the tool itself)?</summary>
        public static bool IsRefusal(string text)
        {
            return text != null && (text.StartsWith("server writes are off", StringComparison.Ordinal) || text.StartsWith("server write flag expired", StringComparison.Ordinal) || text.StartsWith("pass confirm:", StringComparison.Ordinal));
        }
    }

    internal static class SafeNames
    {
        /// <summary>Snapshot names become file names: letters, digits, '_', '-' and '.', never a path and never '..'.</summary>
        public static bool IsFileStem(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name == "." || name.Contains("..")) return false;
            foreach (var c in name)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }
    }

    /// <summary>Valheim's world is a disc of about 10 km radius; tools that take coordinates refuse non-finite values and anything far outside it.</summary>
    internal static class WorldBounds
    {
        public const float Limit = 11000f;          // metres from the origin in x and z
        public const float HeightLimit = 5000f;

        public static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public static bool InWorld(double x, double y, double z)
        {
            return Finite(x) && Finite(y) && Finite(z) && Math.Abs(x) <= Limit && Math.Abs(z) <= Limit && Math.Abs(y) <= HeightLimit;
        }

        /// <summary>Clamp a box coordinate into the world so a typo such as 1e9 cannot make a zone scan loop for hours.</summary>
        public static float Clamp(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return Math.Max(-Limit, Math.Min(Limit, v));
        }
    }
}

namespace HubnerExt
{
    /// <summary>What the journal resolver knows about one object in the world (no Unity types).</summary>
    internal sealed class ObjFacts
    {
        public string Id, Prefab, Plan, Key;
        public double X, Y, Z;
        public object Handle;           // the caller's own reference (the ZDO)
    }

    internal enum Match { Found, NotFound, Ambiguous, OtherWorld, Unloaded }

    /// <summary>The identity of a world: its name and the uid the game gives it at creation. Ids, plan tags and positions in the journal mean nothing in another world.</summary>
    internal static class WorldKey
    {
        public static string Of(string name, long uid) { return string.IsNullOrEmpty(name) && uid == 0 ? "" : (name ?? "") + "#" + uid.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        public static bool Differs(string entryWorld, string currentWorld) { return !string.IsNullOrEmpty(entryWorld) && !string.IsNullOrEmpty(currentWorld) && entryWorld != currentWorld; }
    }

    /// <summary>
    ///     Finds the object a journal entry is about. ZDO ids are only valid within one world load: when the game loads a save it renumbers every ZDO
    ///     (ZDO.Load: m_uid.SetID(++m_loadID), user key 1). An entry written in an earlier run therefore cannot be found by id; it is found by what survives a
    ///     restart: the plan tag (hub_plan + hub_key) and the prefab, or for a plain spawn the prefab and the position (saved to 1 cm).
    /// </summary>
    internal static class Identity
    {
        public const double PositionTolerance = 0.05;      // metres: the save keeps positions to 1 cm
        public const double NearestWins = 0.5;             // duplicate plan keys: the one this close to the entry's position

        /// <param name="entryBoot">the world-load id stored with the entry ("" for old entries)</param>
        /// <param name="currentBoot">the id of the current world load</param>
        /// <param name="positionReliable">the entry's position is where the object stands now (a spawn), not where it stood before an edit</param>
        public static Match Find(string entryBoot, string currentBoot, string id, string prefab, string idPlan, string idKey, double x, double y, double z, bool positionReliable, IList<ObjFacts> candidates, out ObjFacts found)
        {
            found = null;
            if (candidates == null || candidates.Count == 0) return Match.NotFound;

            if (!string.IsNullOrEmpty(entryBoot) && entryBoot == currentBoot)
            {
                foreach (var c in candidates) if (c.Id == id && c.Prefab == prefab) { found = c; return Match.Found; }
                return Match.NotFound;                       // same load: the id is exact, and it names nothing (or something else) now
            }

            // a previous run: ids mean nothing
            var same = new List<ObjFacts>();
            if (!string.IsNullOrEmpty(idPlan) && !string.IsNullOrEmpty(idKey))
            {
                foreach (var c in candidates) if (c.Prefab == prefab && c.Plan == idPlan && c.Key == idKey) same.Add(c);
                if (same.Count == 0) return Match.NotFound;
                if (same.Count == 1) { found = same[0]; return Match.Found; }
                return Nearest(same, x, y, z, NearestWins, out found);
            }

            if (!positionReliable) return Match.NotFound;     // no tag and the position is from before the edit: nothing to go by
            foreach (var c in candidates) if (c.Prefab == prefab && Dist(c, x, y, z) <= PositionTolerance) same.Add(c);
            if (same.Count == 0) return Match.NotFound;
            if (same.Count == 1) { found = same[0]; return Match.Found; }
            return Match.Ambiguous;                           // two identical objects at one spot: do not guess which one to destroy
        }

        /// <summary>
        ///     What a search result means. An entry of another world is not touched whatever matches. "Not found" proves the object is gone only where the game has the area loaded
        ///     (a joined client holds the objects of the zones it has been near): elsewhere it is unknown, and the entry stays for a later try.
        /// </summary>
        public static Match Judge(Match found, string entryWorld, string currentWorld, bool areaLoaded)
        {
            if (WorldKey.Differs(entryWorld, currentWorld)) return Match.OtherWorld;
            if (found == Match.NotFound && !areaLoaded) return Match.Unloaded;
            return found;
        }

        private static Match Nearest(List<ObjFacts> list, double x, double y, double z, double within, out ObjFacts found)
        {
            found = null;
            var ordered = list.OrderBy(c => Dist(c, x, y, z)).ToList();
            var best = Dist(ordered[0], x, y, z);
            if (best > within) return Match.Ambiguous;
            if (Dist(ordered[1], x, y, z) - best < 0.01) return Match.Ambiguous;      // a tie
            found = ordered[0]; return Match.Found;
        }

        private static double Dist(ObjFacts c, double x, double y, double z)
        {
            var dx = c.X - x; var dy = c.Y - y; var dz = c.Z - z;
            return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}

namespace HubnerExt
{
    /// <summary>Writes a file so that a crash leaves either the old content or the new, never half of one.</summary>
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string text)
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, text);
            if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null); else System.IO.File.Move(tmp, path);
        }
    }

    /// <summary>Writes the dictionaries, lists, strings and numbers a tool call is made of back to JSON (the core's own writer is internal).</summary>
    internal static class JsonOut
    {
        public static string Write(object o) { var sb = new System.Text.StringBuilder(); Put(sb, o); return sb.ToString(); }

        private static void Put(System.Text.StringBuilder sb, object o)
        {
            switch (o)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: Str(sb, s); break;
                case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                case int i: sb.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case System.Collections.IDictionary dict:
                    sb.Append('{'); var firstD = true;
                    foreach (System.Collections.DictionaryEntry kv in dict) { if (!firstD) sb.Append(','); firstD = false; Str(sb, Convert.ToString(kv.Key, System.Globalization.CultureInfo.InvariantCulture)); sb.Append(':'); Put(sb, kv.Value); }
                    sb.Append('}'); break;
                case System.Collections.IEnumerable list:
                    sb.Append('['); var first = true;
                    foreach (var item in list) { if (!first) sb.Append(','); first = false; Put(sb, item); }
                    sb.Append(']'); break;
                default: Str(sb, Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture)); break;
            }
        }

        private static void Str(System.Text.StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>How a job ended, as a code a caller can branch on (the text is for people).</summary>
    internal static class JobCodes
    {
        public const string Done = "done", Cancelled = "cancelled", Error = "error", Crash = "crash", Unloaded = "unloaded", BadArgs = "bad_args", Refused = "refused";

        /// <summary>The code of a finished tool result: ok, a refusal by the write gate, or another error.</summary>
        public static string ForResult(bool isError, string text)
        {
            if (!isError) return Done;
            return WriteGate.IsRefusal(text) ? Refused : Error;
        }
    }

    /// <summary>A job interrupted by a hot reload is resubmitted by the next load, but only for the same game process and only when the interruption is recent.</summary>
    internal static class ReloadLedger
    {
        public const int MaxAgeSeconds = 120;
        public static bool ShouldResume(int ledgerPid, int currentPid, long writtenUnix, long nowUnix)
        {
            if (ledgerPid != currentPid) return false;                         // another game process: the world was restarted, nothing is half-done
            var age = nowUnix - writtenUnix;
            return age >= 0 && age <= MaxAgeSeconds;
        }
    }

    /// <summary>Repeated log lines folded to one: the same line with different numbers (a position, a counter) is one line seen N times. A run of 95 identical warnings hides everything else in a tail of 60 lines.</summary>
    internal static class LogCollapse
    {
        static readonly System.Text.RegularExpressions.Regex Addr = new System.Text.RegularExpressions.Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b|\b(?:[0-9A-Fa-f]{1,4}:){4,7}[0-9A-Fa-f]{1,4}\b|\b7656\d{13}\b", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));

        /// <summary>A log line without the addresses and Steam ids of players in it (the game logs "Peer 1.2.3.4:2456 ..."): those are personal data, shown only when the operator turned hubner.exposePeerHosts on.</summary>
        public static string Redact(string line)
        {
            try { return Addr.Replace(line ?? "", "<addr>"); } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return "<line withheld>"; /* allow: the safe answer */ }
        }

        public static string Key(string line)
        {
            var sb = new System.Text.StringBuilder(line.Length);
            var inDigits = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (char.IsDigit(c)) { if (!inDigits) sb.Append('#'); inDigits = true; }
                else if (inDigits && c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])) { /* the point inside a decimal number */ }
                else { sb.Append(c); inDigits = false; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>The first line of each distinct key, in order of first appearance, with how many lines shared the key.</summary>
        public static List<KeyValuePair<string, int>> Fold(IEnumerable<string> lines)
        {
            var order = new List<string>(); var first = new Dictionary<string, string>(); var count = new Dictionary<string, int>();
            foreach (var l in lines)
            {
                var k = Key(l);
                if (!count.ContainsKey(k)) { count[k] = 0; first[k] = l; order.Add(k); }
                count[k]++;
            }
            var res = new List<KeyValuePair<string, int>>(); foreach (var k in order) res.Add(new KeyValuePair<string, int>(first[k], count[k]));
            return res;
        }

        public static string Render(KeyValuePair<string, int> e) { return e.Value > 1 ? e.Key + "  (x" + e.Value + ")" : e.Key; }
    }

    /// <summary>One group of a digest: how many objects of a kind and the box they lie in.</summary>
    internal sealed class DigestGroup
    {
        public string Prefab = "", Name = ""; public int Count, Built;
        public double MinX = double.MaxValue, MaxX = double.MinValue, MinZ = double.MaxValue, MaxZ = double.MinValue;
    }

    /// <summary>Compact summaries for a reader with a token budget: a hundred identical trees are one line with a count.</summary>
    internal static class Digest
    {
        /// <summary>A name a person would say: Pickable_Mushroom -> Mushroom, piece_walltorch -> walltorch, Beech_small1 -> Beech small.</summary>
        public static string Readable(string prefab)
        {
            var s = prefab ?? "";
            foreach (var pre in new[] { "Pickable_", "piece_", "Piece_" }) if (s.StartsWith(pre, StringComparison.Ordinal) && s.Length > pre.Length) { s = s.Substring(pre.Length); break; }
            s = s.Replace('_', ' ').TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd();
            return s.Length > 0 ? s : prefab ?? "";
        }

        /// <summary>The most numerous kinds first, at most <paramref name="max" /> groups; everything else is counted in <paramref name="others" />.</summary>
        public static List<DigestGroup> Groups(IEnumerable<KeyValuePair<string, double[]>> items, int max, out int others)
        {
            // item: prefab -> [x, z, built(0/1)]
            var by = new Dictionary<string, DigestGroup>();
            foreach (var kv in items)
            {
                DigestGroup g; if (!by.TryGetValue(kv.Key, out g)) by[kv.Key] = g = new DigestGroup { Prefab = kv.Key, Name = Readable(kv.Key) };
                g.Count++; if (kv.Value[2] > 0) g.Built++;
                g.MinX = Math.Min(g.MinX, kv.Value[0]); g.MaxX = Math.Max(g.MaxX, kv.Value[0]); g.MinZ = Math.Min(g.MinZ, kv.Value[1]); g.MaxZ = Math.Max(g.MaxZ, kv.Value[1]);
            }
            var ordered = by.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Prefab, StringComparer.Ordinal).ToList();
            others = ordered.Skip(max).Sum(g => g.Count);
            return ordered.Take(max).ToList();
        }
    }
}
