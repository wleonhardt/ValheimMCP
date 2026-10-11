// Game-free ownership and placement-area rules (no Unity types, so tests/ links this file). The game-side readers that fill the facts are in Neighbours.cs.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    /// <summary>A guard stone as the ZDO table shows it.</summary>
    internal sealed class WardFact
    {
        public double X, Z, Radius;
        public bool Enabled;
        public long Creator;
        public List<long> Permitted = new List<long>();
        public string Name = "";
    }

    internal sealed class OwnFacts
    {
        public bool PlanOwned;            // carries hub_plan: this harness made or adopted it
        public bool Tombstone;
        public bool InWard;               // inside an enabled guard stone's area
        public bool ContainerWithItems;
        public int Creator0Neighbours;    // other creator-0 pieces (not plan-owned) within 6 m
    }

    /// <summary>
    ///     "May a server-side tool touch this?" for the things the creator field does not cover. Protected already refuses players, living things and anything a player built
    ///     (creator != 0); these rules add a dead player's tombstone, objects inside an enabled ward, containers that hold items, and structures of creator-0 pieces that another
    ///     tool (or an older build) left. Plan-owned objects are ours and pass. Force with the confirm string overrides, as for everything else.
    /// </summary>
    internal static class Ownership
    {
        public const int StructureNeighbours = 2;         // a piece with this many creator-0 pieces near it (three in all) belongs to somebody's build
        public const double ClusterMetres = 6.0;

        public static string Why(OwnFacts f)
        {
            if (f.PlanOwned) return null;
            if (f.Tombstone) return "tombstone: it holds a dead player's items";
            if (f.InWard) return "inside an enabled guard stone's area";
            if (f.ContainerWithItems) return "a container that holds items";
            if (f.Creator0Neighbours >= StructureNeighbours) return "part of a build of creator-0 pieces (" + (f.Creator0Neighbours + 1) + " within " + ClusterMetres + " m) that another tool or an older build left";
            return null;
        }

        public static bool IsTombstonePrefab(string prefab) { return prefab != null && prefab.IndexOf("tombstone", StringComparison.OrdinalIgnoreCase) >= 0; }

        public static bool WardCovers(IEnumerable<WardFact> wards, double x, double z, double margin)
        {
            foreach (var w in wards)
            {
                if (!w.Enabled) continue;
                var dx = w.X - x; var dz = w.Z - z;
                if (Math.Sqrt(dx * dx + dz * dz) < w.Radius + margin) return true;
            }
            return false;
        }

        public static bool Permitted(WardFact w, long playerId) { return w.Creator == playerId || w.Permitted.Contains(playerId); }
    }

    /// <summary>Which locations forbid building (Location.IsInside with buildCheck: the override radius when set, else the largest radius; the pivot strictly inside).</summary>
    internal static class LocationRules
    {
        public static bool NoBuildInside(double distanceXZ, double maxRadius, double noBuildRadiusOverride, bool noBuild)
        {
            if (!noBuild) return false;
            var r = noBuildRadiusOverride > 0 ? noBuildRadiusOverride : maxRadius;
            return distanceXZ < r;
        }

        public static double MaxRadius(bool hasInterior, double exterior, double interior) { return hasInterior ? Math.Max(exterior, interior) : exterior; }

        /// <summary>The numbers of a Location prefab that decide whether building is refused. Known = they were read from the prefab's Location component.</summary>
        public struct Facts
        {
            public bool Known, NoBuild, HasInterior;
            public double Exterior, Interior, Override;
        }

        /// <summary>Location.IsInsideNoBuildLocation for one location: m_noBuild and DistanceXZ under the override radius (when above 0) or GetMaxRadius (the exterior radius, the larger of both only with an interior). A location whose prefab could not be read is assumed to forbid building within its larger radius (advice, not a fact).</summary>
        public static bool NoBuildHere(Facts f, double distanceXZ)
        {
            if (!f.Known) return distanceXZ < Math.Max(f.Exterior, f.Interior);
            return NoBuildInside(distanceXZ, MaxRadius(f.HasInterior, f.Exterior, f.Interior), f.Override, f.NoBuild);
        }
    }

    /// <summary>The rules of Bed.Interact (decompiled). Claiming an unowned bed needs only the exposure check; sleeping in the current bed also needs the time, no enemy sensed, a Heat area at the bed and no Wet status, in that order.</summary>
    internal static class SleepRules
    {
        public const double CoverMin = 0.8;

        public static bool Exposure(double cover, bool underRoof) { return underRoof && cover >= CoverMin; }

        /// <summary>The message token of the first rule that stops sleeping, in the game's order; null when nothing does.</summary>
        public static string FirstFailure(bool timeOk, bool enemySensed, bool exposureOk, bool heatOk, bool wet)
        {
            if (!timeOk) return "$msg_cantsleep";
            if (enemySensed) return "$msg_bedenemiesnearby";
            if (!exposureOk) return "$msg_bedneedroof";
            if (!heatOk) return "$msg_bednofire";
            if (wet) return "$msg_bedwet";
            return null;
        }
    }

    /// <summary>The keys a plan_apply item understands, and what a missing one means. A key the tool does not know is dropped silently, so a typo (rotY) would build the wrong thing without a word.</summary>
    internal static class PlanItemRules
    {
        public static readonly HashSet<string> Known = new HashSet<string> { "prefab", "x", "y", "z", "yaw", "rot", "seat", "key", "label", "text", "tag", "ctag", "ints", "floats" };

        public static List<string> Unknown(IEnumerable<string> keys)
        {
            var res = new List<string>(); foreach (var k in keys) if (!Known.Contains(k)) res.Add(k); return res;
        }

        /// <summary>Without a y an item used to be built at y 0 (far under the sea, which is at 30): y is required unless the item says seat:"ground".</summary>
        public static bool MissingY(bool hasY, string seat) { return !hasY && seat != "ground"; }
    }

    /// <summary>What a fireplace's ZDO says about burning (Fireplace.IsBurning, decompiled): on (state 1, the default), and fuel above 0 or infinite fuel. The game also puts a fire out when it is blocked above, under water or its smoke is blocked; those are not stored in the ZDO.</summary>
    internal static class FireRules
    {
        public static bool Burning(int state, double fuel, bool infinite) { return state == 1 && (fuel > 0 || infinite); }
        public static double FuelToFull(double fuel, double maxFuel, bool infinite) { return infinite ? 0 : Math.Max(0, Math.Floor(maxFuel - fuel)); }
    }

    /// <summary>The order a plan is built in. A spawned piece that stands on nothing is destroyed by its owner's support sweep a few seconds after it appears, so a sliced job that builds the roof before the walls loses the roof. Lowest first keeps every piece's supporter ahead of it (stable: equal heights keep the plan's order).</summary>
    internal static class PlanOrder
    {
        /// <summary>Items that stand on the ground (seat:"ground") go first; others by y.</summary>
        public static double Rank(double? y, string seat) { return seat == "ground" ? double.NegativeInfinity : (y.HasValue ? y.Value : 0); }

        public static List<T> LowestFirst<T>(IList<T> items, Func<T, double> rank)
        {
            var idx = new List<KeyValuePair<int, T>>(); for (var i = 0; i < items.Count; i++) idx.Add(new KeyValuePair<int, T>(i, items[i]));
            idx.Sort((a, b) => { var c = rank(a.Value).CompareTo(rank(b.Value)); return c != 0 ? c : a.Key.CompareTo(b.Key); });
            var res = new List<T>(items.Count); foreach (var kv in idx) res.Add(kv.Value); return res;
        }
    }

    /// <summary>The rows of the ownership summary.</summary>
    internal static class OwnershipSummary
    {
        public static Dictionary<long, int> CountByCreator(IEnumerable<long> creators)
        {
            var d = new Dictionary<long, int>();
            foreach (var c in creators) d[c] = d.ContainsKey(c) ? d[c] + 1 : 1;
            return d;
        }
    }
}

namespace HubnerExt
{
    /// <summary>One piece that appeared in the ZDO table.</summary>
    internal sealed class PieceSeen
    {
        public double T;                // seconds, any monotone clock
        public long Session;            // ZDOID.UserID: the session of whoever created it. 1 = "was in the save when the server started" (never an event)
        public double X, Z;
        public string Prefab;
    }

    /// <summary>A group of pieces one creator put down close together and close in time.</summary>
    internal sealed class StructureEvent
    {
        public long Session;
        public int Count;
        public double MinX, MaxX, MinZ, MaxZ, FirstT, LastT;
        public bool SpawnedAtOnce;      // all within two seconds: a tool, a blueprint mod, or a paste, not hands
        public List<KeyValuePair<string, int>> Top = new List<KeyValuePair<string, int>>();
        public double CentreX { get { return (MinX + MaxX) / 2; } }
        public double CentreZ { get { return (MinZ + MaxZ) / 2; } }
    }

    /// <summary>
    ///     Clusters new pieces into "a structure appeared" events. Pieces of one session within <see cref="LinkMetres" /> of a cluster join it; a cluster is reported once it has been
    ///     quiet for <see cref="QuietSeconds" /> and has at least <see cref="MinPieces" /> pieces. Pieces from session 1 (loaded from the save) are never events.
    /// </summary>
    internal sealed class StructureDetector
    {
        public const double LinkMetres = 12, QuietSeconds = 20, AtOnceSeconds = 2;
        public const int MinPieces = 3;
        private sealed class Open { public StructureEvent E = new StructureEvent(); public Dictionary<string, int> Prefabs = new Dictionary<string, int>(); }
        private readonly List<Open> _open = new List<Open>();

        public int OpenClusters { get { return _open.Count; } }

        public void Add(PieceSeen p)
        {
            if (p.Session == 1L || p.Session == 0L) return;
            Open hit = null;
            foreach (var o in _open)
            {
                if (o.E.Session != p.Session) continue;
                if (p.X >= o.E.MinX - LinkMetres && p.X <= o.E.MaxX + LinkMetres && p.Z >= o.E.MinZ - LinkMetres && p.Z <= o.E.MaxZ + LinkMetres) { hit = o; break; }
            }
            if (hit == null)
            {
                hit = new Open();
                hit.E.Session = p.Session; hit.E.MinX = hit.E.MaxX = p.X; hit.E.MinZ = hit.E.MaxZ = p.Z; hit.E.FirstT = p.T;
                _open.Add(hit);
            }
            var e = hit.E;
            e.Count++; e.LastT = p.T;
            e.MinX = Math.Min(e.MinX, p.X); e.MaxX = Math.Max(e.MaxX, p.X); e.MinZ = Math.Min(e.MinZ, p.Z); e.MaxZ = Math.Max(e.MaxZ, p.Z);
            hit.Prefabs[p.Prefab ?? "?"] = (hit.Prefabs.ContainsKey(p.Prefab ?? "?") ? hit.Prefabs[p.Prefab ?? "?"] : 0) + 1;
        }

        /// <summary>The clusters that have gone quiet by <paramref name="now" />; small ones are dropped silently.</summary>
        public List<StructureEvent> Flush(double now)
        {
            var done = new List<StructureEvent>();
            for (var i = _open.Count - 1; i >= 0; i--)
            {
                var o = _open[i];
                if (now - o.E.LastT < QuietSeconds) continue;
                _open.RemoveAt(i);
                if (o.E.Count < MinPieces) continue;
                o.E.SpawnedAtOnce = o.E.LastT - o.E.FirstT <= AtOnceSeconds;
                o.E.Top = o.Prefabs.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(5).ToList();
                done.Add(o.E);
            }
            done.Reverse();
            return done;
        }
    }

    /// <summary>Who came and went between two looks at the peer list.</summary>
    internal static class Presence
    {
        public static void Diff(ICollection<string> before, ICollection<string> now, out List<string> arrived, out List<string> left)
        {
            arrived = now.Where(n => !before.Contains(n)).ToList();
            left = before.Where(n => !now.Contains(n)).ToList();
        }
    }

    /// <summary>A bounded log of events with a sequence number, so a poller asks "since seq N".</summary>
    internal sealed class EventLog
    {
        public sealed class Row { public long Seq; public double T; public string Kind; public string Json; }
        private readonly List<Row> _rows = new List<Row>();
        private long _seq;
        private readonly int _cap;
        public EventLog(int cap) { _cap = cap; }
        public long Last { get { return _seq; } }
        public Row Add(double t, string kind, string json)
        {
            var r = new Row { Seq = ++_seq, T = t, Kind = kind, Json = json };
            _rows.Add(r);
            if (_rows.Count > _cap) _rows.RemoveAt(0);
            return r;
        }
        public List<Row> Query(long since, int n, string kind)
        {
            var q = _rows.Where(r => r.Seq > since && (string.IsNullOrEmpty(kind) || r.Kind == kind)).ToList();
            return q.Count > n ? q.GetRange(q.Count - n, n) : q;
        }
    }
}

namespace HubnerExt
{
    /// <summary>Chat text going out (chat_send) or coming in (chat_tail) is data from people, and a model reads it: it is cleaned, rate limited and tagged, never trusted.</summary>
    internal static class ChatRules
    {
        private static readonly System.Text.RegularExpressions.Regex Tags = new System.Text.RegularExpressions.Regex("<[^>\n]{0,40}>", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
        private static readonly System.Text.RegularExpressions.Regex Urls = new System.Text.RegularExpressions.Regex(@"(?i)\b(?:https?://|www\.)\S+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
        private static readonly System.Text.RegularExpressions.Regex Manipulation = new System.Text.RegularExpressions.Regex(
            @"(?i)\b(ignore|disregard|forget)\b.{0,30}\b(previous|prior|above|earlier|all)\b.{0,20}\b(instructions?|rules?|prompts?)\b|\bsystem prompt\b|\byou are now\b|\bdeveloper mode\b|\b(as|i am|i'm)\b.{0,12}\b(admin|owner|operator|developer|the server)\b.{0,40}\b(run|execute|give|send|delete|spawn|teleport)\b|\b(api[ _-]?key|access token|password|secret)\b|^\s*/\w+",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));

        /// <summary>Is this character one that hides text from a reader (controls, zero-width, bidi marks)?</summary>
        public static bool Invisible(char c)
        {
            return c < 0x20 || (c >= 0x7F && c <= 0x9F) || (c >= 0x200B && c <= 0x200F) || (c >= 0x2028 && c <= 0x202E) || (c >= 0x2060 && c <= 0x2064) || c == 0xFEFF || c == 0x00AD;
        }

        /// <summary>The text a person would read: compatibility-normalised, no invisible characters, no markup tags, no links, no leading console prefix, one line, at most maxLen characters.</summary>
        public static string Clean(string text, int maxLen) { return Clean(text, maxLen, true); }

        private static string Clean(string text, int maxLen, bool stripPrefix)
        {
            var s = text ?? "";
            try { s = s.Normalize(System.Text.NormalizationForm.FormKC); } catch (ArgumentException) { /* allow: text with lone surrogates cannot be normalised; it is used as it is */ }
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s) sb.Append(c == '\n' || c == '\r' || c == '\t' ? ' ' : (Invisible(c) ? '\0' : c));
            s = sb.ToString().Replace("\0", "");
            try { s = Tags.Replace(s, ""); s = Urls.Replace(s, ""); } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { s = ""; /* allow: a line that makes the cleaner time out is dropped, which is the safe answer */ }
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            while (stripPrefix && s.Length > 0 && "/\\!#".IndexOf(s[0]) >= 0) s = s.Substring(1).TrimStart();      // a leading / \ ! # would make the game (or a bot) treat the line as a command
            if (s.Length > maxLen)
            {
                var cut = maxLen;
                if (char.IsHighSurrogate(s[cut - 1])) cut--;
                s = s.Substring(0, cut).TrimEnd();
            }
            return s;
        }

        /// <summary>A line for a reader: invisible characters, markup and links gone, one line, cut; a leading / or ! kept (it is what makes a line look like a command).</summary>
        public static string ForReading(string text, int maxLen) { return Clean(text, maxLen, false); }

        /// <summary>Hints for whoever reads a chat line: never an order to refuse it, only a warning that it looks like an attempt to steer a model, a machine's output, or noise.</summary>
        public static List<string> Hints(string text)
        {
            var hints = new List<string>();
            var s = Clean(text ?? "", 2000, false);       // keep a leading / or !: that is what makes a line a command
            var raw = text ?? "";
            try { if (Manipulation.IsMatch(s) || (raw != s && Manipulation.IsMatch(Fold(raw)))) hints.Add("manipulation"); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { hints.Add("manipulation"); /* allow: a line built to stall the matcher is flagged */ }
            if (s.Length > 200 || s.StartsWith("{") || s.StartsWith("[") || s.Contains("tool_use") || CountDigits(s) >= 6 && s.Contains("=")) hints.Add("machine");
            if (s.Length >= 8 && Letters(s) < s.Length * 0.4) hints.Add("noise");
            return hints;
        }

        private static string Fold(string raw) { return Clean(raw, 2000, false).ToLowerInvariant(); }
        private static int CountDigits(string s) { var n = 0; foreach (var c in s) if (char.IsDigit(c)) n++; return n; }
        private static int Letters(string s) { var n = 0; foreach (var c in s) if (char.IsLetter(c)) n++; return n; }

        /// <summary>Trust by platform id, never by name: a name can be copied, an id cannot. A name counts only while exactly one player online carries it.</summary>
        public static string Trust(string name, string platformId, ICollection<string> trusted, int playersWithThatName)
        {
            if (trusted == null || trusted.Count == 0) return "other";
            if (!string.IsNullOrEmpty(platformId) && trusted.Contains(platformId)) return "trusted";
            if (!string.IsNullOrEmpty(name) && trusted.Contains(name) && playersWithThatName == 1) return "trusted";
            return "other";
        }
    }

    /// <summary>How fast the harness may speak: a global gap, a gap per speaker, and no repeating itself.</summary>
    internal sealed class ChatLimiter
    {
        public double GlobalGap = 6, SpeakerGap = 15, RepeatWindow = 300;
        private double _lastAny = double.NegativeInfinity;
        private readonly Dictionary<string, double> _lastBy = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _seen = new Dictionary<string, double>();

        /// <summary>null = go ahead (and the call is recorded); otherwise why not.</summary>
        public string Check(double now, string speaker, string text)
        {
            if (now - _lastAny < GlobalGap) return "too soon after the last line (" + GlobalGap + " s between lines)";
            double t;
            if (speaker != null && _lastBy.TryGetValue(speaker, out t) && now - t < SpeakerGap) return "too soon after the last line to " + speaker + " (" + SpeakerGap + " s)";
            if (_seen.TryGetValue(text, out t) && now - t < RepeatWindow) return "the same line was sent less than " + RepeatWindow + " s ago";
            _lastAny = now; if (speaker != null) _lastBy[speaker] = now; _seen[text] = now;
            if (_seen.Count > 200) foreach (var k in _seen.Where(kv => now - kv.Value > RepeatWindow).Select(kv => kv.Key).ToList()) _seen.Remove(k);
            return null;
        }
    }
}

namespace HubnerExt
{
    /// <summary>Tameable's arithmetic as the game does it (Tameable.IsHungry, GetTameness).</summary>
    internal static class Husbandry
    {
        public const long TicksPerSecond = 10000000L;

        /// <summary>Seconds of being fed left; negative when hungry. lastFedTicks is the DateTime.Ticks the game stores in TameLastFeeding (0 = never fed).</summary>
        public static double FedSecondsLeft(long nowTicks, long lastFedTicks, double fedDuration) { return fedDuration - (nowTicks - lastFedTicks) / (double)TicksPerSecond; }
        public static bool Hungry(long nowTicks, long lastFedTicks, double fedDuration) { return FedSecondsLeft(nowTicks, lastFedTicks, fedDuration) < 0; }

        /// <summary>0 to 100: how far taming has come (TameTimeLeft counts down from the taming time).</summary>
        public static int TamenessPercent(double timeLeft, double tamingTime)
        {
            if (tamingTime <= 0) return 100;
            var f = Math.Max(0, Math.Min(1, timeLeft / tamingTime));
            return (int)((1 - f) * 100);
        }
    }
}
