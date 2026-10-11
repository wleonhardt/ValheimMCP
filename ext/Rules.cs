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
        public static string FirstFailure(bool timeOk, bool enemySensed, double cover, bool underRoof, bool heatOk, bool wet)
        {
            if (!timeOk) return "$msg_cantsleep";
            if (enemySensed) return "$msg_bedenemiesnearby";
            if (!underRoof) return "$msg_bedneedroof";                         // Bed.CheckExposure: no roof, then a roof with too little cover
            if (cover < CoverMin) return "$msg_bedtooexposed";
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

        /// <summary>An item has a y only when it is a number: null, a string or NaN count as missing (a generator that writes None used to build at y 0).</summary>
        public static bool HasY(double yOrNaN) { return !double.IsNaN(yOrNaN) && !double.IsInfinity(yOrNaN); }
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

    /// <summary>
    ///     The game's day (EnvMan, decompiled). Raw day fraction = world time mod day length. The game rescales it so that the lit part is 0.25..0.75: raw 0.15..0.85 (70 % of the day)
    ///     maps there, the rest is night. Day = rescaled 0.25..0.75, afternoon = 0.5..0.75, night = up to 0.25 or from 0.75. The day length is a field of EnvMan: read it, never assume it.
    /// </summary>
    internal static class DayClock
    {
        public const double SleepCooldownSeconds = 30;           // EnvMan.m_sleepCooldownSeconds after waking

        public static double Raw(double totalSeconds, double dayLengthSec)
        {
            if (dayLengthSec <= 0) return 0;
            var t = (totalSeconds * 1000.0) % (dayLengthSec * 1000.0);
            return Math.Max(0, Math.Min(1, t / 1000.0 / dayLengthSec));
        }

        public static double Rescale(double raw)
        {
            if (raw >= 0.15 && raw <= 0.85) return 0.25 + (raw - 0.15) / 0.7 * 0.5;
            if (raw < 0.5) return raw / 0.15 * 0.25;
            return 0.75 + (raw - 0.85) / 0.15 * 0.25;
        }

        /// <summary>The inverse of <see cref="Rescale" />.</summary>
        public static double Unscale(double f)
        {
            if (f >= 0.25 && f <= 0.75) return 0.15 + (f - 0.25) / 0.5 * 0.7;
            if (f < 0.25) return f / 0.25 * 0.15;
            return 0.85 + (f - 0.75) / 0.25 * 0.15;
        }

        public static double Fraction(double totalSeconds, double dayLengthSec) { return Rescale(Raw(totalSeconds, dayLengthSec)); }
        public static bool IsDay(double f) { return f >= 0.25 && f <= 0.75; }
        public static bool IsAfternoon(double f) { return f >= 0.5 && f <= 0.75; }
        public static bool IsNight(double f) { return f <= 0.25 || f >= 0.75; }
        public static string Phase(double f) { return f < 0.25 ? "night (before dawn)" : f < 0.5 ? "morning" : f <= 0.75 ? "afternoon" : "night (after dusk)"; }

        /// <summary>Seconds until the rescaled fraction next reaches <paramref name="target" /> (0.25 dawn, 0.75 dusk); at least 0.</summary>
        public static double SecondsUntil(double totalSeconds, double dayLengthSec, double target)
        {
            var raw = Raw(totalSeconds, dayLengthSec); var want = Unscale(target);
            var d = want - raw; if (d < 0) d += 1;
            return d * dayLengthSec;
        }

        /// <summary>Is sleeping allowed by the clock: afternoon or night, and the cooldown after waking is over. EnvMan.CalculateCanSleep.</summary>
        public static bool CanSleepNow(double f, double secondsSinceWake) { return (IsAfternoon(f) || IsNight(f)) && secondsSinceWake > SleepCooldownSeconds; }

        /// <summary>Game.UpdateSleeping on the server: the night is skipped when the clock allows it and EVERY character ZDO is in bed (and there is at least one).</summary>
        public static bool NightSkips(double f, int characters, int inBed) { return (IsAfternoon(f) || IsNight(f)) && characters > 0 && inBed == characters; }
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
            @"(?i)\b(ignore|disregard|forget)\b.{0,30}\b(previous|prior|above|earlier|all)\b.{0,20}\b(instructions?|rules?|prompts?)\b|\bsystem prompt\b|\byour (prompt|instructions)\b|\bnew instructions?\b|\byou are now\b|\b(developer|dan|jailbreak|god) mode\b|\bjailbreak\b|\bobey me\b|\b(as|i am|i'm)\b.{0,12}\b(admin|owner|operator|developer|the server)\b.{0,40}\b(run|execute|give|send|delete|spawn|teleport)\b|\b(run|execute|give|send|delete|spawn|teleport)\b.{0,40}\b(i am|i'm|as)\b.{0,12}\b(admin|owner|operator|developer|the server)\b|\b(api[ _-]?key|access token|password|secret)\b|\.env\b|\b(devcommands|imacheater|killall|removedrops|resetcharacter|exploremap|debugmode)\b|^\s*/\w+",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
        private static readonly System.Text.RegularExpressions.Regex Machine = new System.Text.RegularExpressions.Regex(
            @"```|""\w+""\s*:|\b\w+=\S+\s+\w+=\S+|\b(INFO|WARN|WARNING|ERROR|DEBUG)\b\s*[:\]\-]|=>|\btool_(use|result|call)\b|\b\d{1,2}:\d\d:\d\d\b",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));

        /// <summary>Is this character one that hides text from a reader (controls, zero-width, bidi marks)?</summary>
        public static bool Invisible(char c)
        {
            return c < 0x20 || (c >= 0x7F && c <= 0x9F) || (c >= 0x200B && c <= 0x200F) || (c >= 0x2028 && c <= 0x202E) || (c >= 0x2060 && c <= 0x2064) || c == 0xFEFF || c == 0x00AD;
        }

        /// <summary>
        ///     A code point that shows nothing but a reader of the raw text still sees: any format, control, private-use or unassigned character (this takes in the Unicode Tag block U+E0000..E007F that can
        ///     spell a whole instruction invisibly, and the bidi isolates), the variation selectors and the fillers that render as blanks.
        /// </summary>
        public static bool InvisibleCodePoint(int cp, System.Globalization.UnicodeCategory cat)
        {
            if (cat == System.Globalization.UnicodeCategory.Format || cat == System.Globalization.UnicodeCategory.Control || cat == System.Globalization.UnicodeCategory.PrivateUse || cat == System.Globalization.UnicodeCategory.OtherNotAssigned) return true;
            return cp == 0x034F || cp == 0x061C || cp == 0x115F || cp == 0x1160 || cp == 0x3164 || cp == 0xFFA0 || (cp >= 0x180B && cp <= 0x180E) || (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF);
        }

        /// <summary>The text a person would read: compatibility-normalised, no invisible characters, no markup tags, no links, no leading console prefix, one line, at most maxLen characters.</summary>
        public static string Clean(string text, int maxLen) { return Clean(text, maxLen, true); }

        private static string Clean(string text, int maxLen, bool stripPrefix)
        {
            var s = text ?? "";
            try { s = s.Normalize(System.Text.NormalizationForm.FormKC); } catch (ArgumentException) { /* allow: text with lone surrogates cannot be normalised; it is used as it is */ }
            if (s.Length > 4000) s = s.Substring(0, char.IsHighSurrogate(s[3999]) ? 3999 : 4000);          // nothing past this is read: do not spend regexes on a megabyte
            var sb = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\n' || c == '\r' || c == '\t') { sb.Append(' '); continue; }
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    var cp = char.ConvertToUtf32(c, s[i + 1]);
                    if (!InvisibleCodePoint(cp, System.Globalization.CharUnicodeInfo.GetUnicodeCategory(s, i))) { sb.Append(c); sb.Append(s[i + 1]); }
                    i++; continue;
                }
                if (char.IsSurrogate(c)) continue;                                                    // a lone surrogate is not text
                if (!Invisible(c) && !InvisibleCodePoint(c, System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c))) sb.Append(c);
            }
            s = sb.ToString();
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

        /// <summary>Hints for whoever reads a chat line: never an order to refuse it, only a warning that it looks like an attempt to steer a model, a machine's output, a call of one of our tools, or noise.</summary>
        public static List<string> Hints(string text) { return Hints(text, null); }

        public static List<string> Hints(string text, ICollection<string> toolNames)
        {
            var hints = new List<string>();
            var s = Clean(text ?? "", 2000, false);       // keep a leading / or !: that is what makes a line a command
            var raw = text ?? "";
            try { if (Manipulation.IsMatch(s) || (raw != s && Manipulation.IsMatch(Fold(raw)))) hints.Add("manipulation"); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { hints.Add("manipulation"); /* allow: a line built to stall the matcher is flagged */ }
            var machine = s.Length > 200 || s.StartsWith("{") || s.StartsWith("[") || CountDigits(s) >= 6 && s.Contains("=");
            try { machine = machine || Machine.IsMatch(s); } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { machine = true; /* allow: a line built to stall the matcher is flagged */ }
            if (machine) hints.Add("machine");
            if (toolNames != null && MentionsTool(s, toolNames)) hints.Add("tool_talk");
            if (s.Length >= 8 && Letters(s) < s.Length * 0.4) hints.Add("noise");
            return hints;
        }

        /// <summary>Does the line name one of the tools a reader of this chat has? A player has no reason to type those.</summary>
        public static bool MentionsTool(string line, ICollection<string> toolNames)
        {
            foreach (var w in System.Text.RegularExpressions.Regex.Split((line ?? "").ToLowerInvariant(), @"[^a-z0-9_]+"))
                if (w.Length >= 5 && w.IndexOf('_') > 0 && toolNames.Contains(w)) return true;
            return false;
        }

        /// <summary>A speaker's name arrives from the client as a string and is shown beside the text: the same hints apply.</summary>
        public static bool NameLooksOff(string name) { return Hints(name ?? "").Count > 0; }

        /// <summary>An injection can be cut across two lines. Hints that appear only when the previous line of the same speaker is joined to this one (prefixed split_).</summary>
        public static List<string> AcrossLines(string previous, string text)
        {
            var res = new List<string>(); if (string.IsNullOrEmpty(previous)) return res;
            var own = new HashSet<string>(Hints(previous)); foreach (var h in Hints(text)) own.Add(h);
            foreach (var h in Hints(previous + " " + text)) if (!own.Contains(h)) res.Add("split_" + h);
            return res;
        }

        /// <summary>Who a line is for: "me" when it names one of my names, "someone_else" when it opens with another player's name, else "everyone".</summary>
        public static string Addressing(string text, ICollection<string> myNames, ICollection<string> otherNames)
        {
            var s = Clean(text ?? "", 300, false).ToLowerInvariant();
            foreach (var n in myNames) if (!string.IsNullOrEmpty(n) && ContainsWord(s, n.ToLowerInvariant())) return "me";
            foreach (var n in otherNames)
            {
                if (string.IsNullOrEmpty(n)) continue; var l = n.ToLowerInvariant();
                if (s.StartsWith(l, StringComparison.Ordinal) && (s.Length == l.Length || !char.IsLetterOrDigit(s[l.Length]))) return "someone_else";
            }
            return "everyone";
        }

        private static bool ContainsWord(string s, string w)
        {
            var i = 0;
            while ((i = s.IndexOf(w, i, StringComparison.Ordinal)) >= 0)
            {
                var before = i == 0 || !char.IsLetterOrDigit(s[i - 1]); var after = i + w.Length >= s.Length || !char.IsLetterOrDigit(s[i + w.Length]);
                if (before && after) return true; i += w.Length;
            }
            return false;
        }

        /// <summary>
        ///     The guard on what the harness says: a model that read an injected line must not repeat it, hand out a position to a stranger, or echo an untrusted line back.
        ///     null = fine; otherwise why not.
        /// </summary>
        public static string SayVerdict(string text, bool listenerTrusted, IEnumerable<string> untrustedLines)
        {
            var s = Clean(text ?? "", 300, false);
            try { if (Manipulation.IsMatch(s)) return "the line repeats a manipulation phrase"; } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return "the line stalls the matcher"; /* allow: the refusal is the answer */ }
            if (!listenerTrusted && Coordinates.IsMatch(s)) return "the line gives a position to someone who is not trusted";
            var mine = Words(s); var mineNorm = Norm(s);
            if (untrustedLines != null) foreach (var u in untrustedLines)
            {
                if (SharesRun(mine, Words(Clean(u, 300, false)), 4) || SharesWindow(mineNorm, Norm(u), 12)) return "the line repeats part of an untrusted line";          // words for spaced languages, a character window for the rest
            }
            return null;
        }

        // A position, however it is written: any number of three digits or more (a coordinate is hundreds or thousands), x= / z= labels, or two numbers split by a comma, semicolon or slash.
        private static readonly System.Text.RegularExpressions.Regex Coordinates = new System.Text.RegularExpressions.Regex(@"\d{3,}|\b[xyz]\s*[=:]\s*-?\d|-?\d{2}\s*[,;/]\s*-?\d{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));

        private static string[] Words(string s) { return System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToArray(); }

        private static bool SharesWindow(string a, string b, int len)
        {
            if (a.Length < len || b.Length < len) return false;
            for (var i = 0; i + len <= a.Length; i++) if (b.IndexOf(a.Substring(i, len), StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool SharesRun(string[] a, string[] b, int run)
        {
            for (var i = 0; i + run <= a.Length; i++)
                for (var j = 0; j + run <= b.Length; j++)
                {
                    var k = 0; while (k < run && a[i + k] == b[j + k]) k++;
                    if (k == run) return true;
                }
            return false;
        }

        /// <summary>The text with case, spacing and punctuation gone, to recognise a line we sent when the game hands it back (it shouts in capitals, whispers in lower case, turns brackets into spaces).</summary>
        public static string Norm(string text) { return string.Concat(Words(Clean(text ?? "", 500, false))); }

        private static string Fold(string raw) { return Clean(raw, 2000, false).ToLowerInvariant(); }
        private static int CountDigits(string s) { var n = 0; foreach (var c in s) if (char.IsDigit(c)) n++; return n; }
        private static int Letters(string s) { var n = 0; foreach (var c in s) if (char.IsLetter(c)) n++; return n; }

        private static readonly System.Text.RegularExpressions.Regex IdShape = new System.Text.RegularExpressions.Regex(@"^[A-Za-z]{2,16}_[A-Za-z0-9]{1,40}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));

        /// <summary>A platform id as the game writes it (Steam_7656..., PlayFab_...). The payload of a chat message is text a client chose: anything else is dropped, so it can neither carry instructions to a reader nor flood it.</summary>
        public static string CleanId(string id) { try { return id != null && IdShape.IsMatch(id) ? id : ""; } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return ""; /* allow: unmatched is the safe answer */ } }

        /// <summary>Does the id a chat payload claims belong to the connection it arrived on? The host name of a Steam connection is the bare Steam id; the payload has the platform prefix.</summary>
        public static bool IdMatches(string payloadId, string hostName)
        {
            if (string.IsNullOrEmpty(payloadId) || string.IsNullOrEmpty(hostName)) return false;
            var tail = payloadId.IndexOf('_') >= 0 ? payloadId.Substring(payloadId.IndexOf('_') + 1) : payloadId;
            var host = hostName.IndexOf('_') >= 0 ? hostName.Substring(hostName.LastIndexOf('_') + 1) : hostName;
            return string.Equals(tail, host, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Trust by platform id, never by name: a name can be copied, an id cannot. "trusted" = an id on the list. "name_only" = only the name is on the list and exactly one player online carries it; that is a hint, not trust.</summary>
        public static string Trust(string name, string platformId, ICollection<string> trusted, int playersWithThatName)
        {
            if (trusted == null || trusted.Count == 0) return "other";
            if (!string.IsNullOrEmpty(platformId) && trusted.Contains(platformId)) return "trusted";
            if (!string.IsNullOrEmpty(name) && trusted.Contains(name) && playersWithThatName == 1) return "name_only";       // a name can be copied while the real player is away: weaker than an id match, still untrusted
            return "other";
        }
    }

    /// <summary>How fast the harness may speak: a global gap, a gap per speaker, and no repeating itself.</summary>
    internal sealed class ChatLimiter
    {
        public double GlobalGap = 6, SpeakerGap = 15, RepeatWindow = 300, BudgetWindow = 600;
        public int SpeakerBudget = 6, GlobalBudget = 20;          // GlobalBudget: lines per BudgetWindow whoever they are for, so a varying or missing 'to' cannot get round the speaker budget
        private readonly List<double> _all = new List<double>();
        static string Key(string speaker) { return speaker == null ? null : ChatRules.Clean(speaker, 40).ToLowerInvariant(); }                       // lines to one speaker per BudgetWindow: two bots answering each other stop here
        private readonly Dictionary<string, List<double>> _budget = new Dictionary<string, List<double>>();
        private double _lastAny = double.NegativeInfinity;
        private readonly Dictionary<string, double> _lastBy = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _seen = new Dictionary<string, double>();

        /// <summary>null = go ahead (and the call is recorded); otherwise why not.</summary>
        public string Check(double now, string speaker, string text)
        {
            if (now - _lastAny < GlobalGap) return "too soon after the last line (" + GlobalGap + " s between lines)";
            speaker = Key(speaker);
            _all.RemoveAll(u => now - u > BudgetWindow);
            if (_all.Count >= GlobalBudget) return "already " + _all.Count + " lines in " + BudgetWindow + " s; let the chat be";
            double t;
            if (speaker != null && _lastBy.TryGetValue(speaker, out t) && now - t < SpeakerGap) return "too soon after the last line to " + speaker + " (" + SpeakerGap + " s)";
            if (_seen.TryGetValue(text, out t) && now - t < RepeatWindow) return "the same line was sent less than " + RepeatWindow + " s ago";
            if (speaker != null)
            {
                List<double> used; if (!_budget.TryGetValue(speaker, out used)) _budget[speaker] = used = new List<double>();
                used.RemoveAll(u => now - u > BudgetWindow);
                if (used.Count >= SpeakerBudget) return "already " + used.Count + " lines to " + speaker + " in " + BudgetWindow + " s; let them be (two bots can answer each other for ever)";
                used.Add(now);
            }
            _lastAny = now; _all.Add(now); if (speaker != null) _lastBy[speaker] = now; _seen[text] = now;
            if (_seen.Count > 200) foreach (var k in _seen.Where(kv => now - kv.Value > RepeatWindow).Select(kv => kv.Key).ToList()) _seen.Remove(k);
            return null;
        }
    }
}

namespace HubnerExt
{
    /// <summary>Spam in the chat ring: four lines in ten seconds, or the same line twice in a minute, from one speaker. A flooder would push the real lines out of the 500-row ring.</summary>
    internal sealed class ChatPace
    {
        public double Window = 10, RepeatWindow = 60; public int MaxInWindow = 4;
        private readonly Dictionary<string, List<KeyValuePair<double, string>>> _by = new Dictionary<string, List<KeyValuePair<double, string>>>();

        /// <summary>Records the line and says whether this speaker is flooding. Speaker should be the platform id (a name can change between lines).</summary>
        public bool Flooding(double now, string speaker, string text)
        {
            List<KeyValuePair<double, string>> l; if (!_by.TryGetValue(speaker ?? "", out l)) _by[speaker ?? ""] = l = new List<KeyValuePair<double, string>>();
            l.RemoveAll(e => now - e.Key > RepeatWindow);
            var norm = ChatRules.Norm(text);
            var repeats = l.Count(e => e.Value == norm) >= 1;
            var recent = l.Count(e => now - e.Key <= Window) + 1;
            l.Add(new KeyValuePair<double, string>(now, norm));
            if (_by.Count > 300) foreach (var k in _by.Where(kv => kv.Value.Count == 0 || now - kv.Value[kv.Value.Count - 1].Key > RepeatWindow).Select(kv => kv.Key).ToList()) _by.Remove(k);
            return recent >= MaxInWindow || repeats;
        }
    }

    /// <summary>The lines this harness sent lately, to recognise them when the game hands them back as chat.</summary>
    internal sealed class SentLog
    {
        public double Window = 120;
        private readonly List<KeyValuePair<double, string>> _sent = new List<KeyValuePair<double, string>>();
        public void Add(double now, string text) { var n = ChatRules.Norm(text); lock (_sent) { _sent.Add(new KeyValuePair<double, string>(now, n)); _sent.RemoveAll(e => now - e.Key > Window); } }      // chat arrives on the network thread, sends on the main thread
        public bool IsOurs(double now, string text) { var n = ChatRules.Norm(text); if (n.Length == 0) return false; lock (_sent) return _sent.Any(e => now - e.Key <= Window && e.Value == n); }
    }

    internal sealed class BreedingSpec
    {
        public double Interval = 10, TotalRange = 10, PartnerRange = 3, SkipChance = 0.5, PregnancyDuration = 10;
        public int MaxCreatures = 4, RequiredLove = 4; public bool SeparatePartner, NoPartnerOffspring;
    }

    internal sealed class BreedingState
    {
        public bool Tamed = true, Hungry, Alerted; public long PregnantTicks; public long NowTicks; public int Love, Population, ReadyPartners;
    }

    internal sealed class BreedingVerdict
    {
        public List<string> Blockers = new List<string>(); public bool Pregnant, Due; public double SecondsToBirth = double.NaN, ExpectedSeconds = double.NaN;
    }

    /// <summary>Procreation.Procreate (decompiled), every m_updateInterval on the owner: not tamed, hungry or alerted, too many of the kind close by, or too few ready partners, and it does nothing; otherwise love grows by one with probability 1 - m_pregnancyChance (the field is the chance to SKIP) until it reaches the required points, then pregnancy lasts m_pregnancyDuration.</summary>
    internal static class BreedingRules
    {
        public static BreedingVerdict Diagnose(BreedingSpec sp, BreedingState st)
        {
            var v = new BreedingVerdict();
            if (!st.Tamed) { v.Blockers.Add("not tamed"); return v; }
            if (st.PregnantTicks != 0)
            {
                v.Pregnant = true; var elapsed = (st.NowTicks - st.PregnantTicks) / (double)Husbandry.TicksPerSecond;
                v.Due = elapsed > sp.PregnancyDuration; v.SecondsToBirth = Math.Max(0, sp.PregnancyDuration - elapsed);
                return v;
            }
            if (st.Hungry) v.Blockers.Add("hungry (it must be fed)");
            if (st.Alerted) v.Blockers.Add("alerted (something is scaring it)");
            if (st.Population >= sp.MaxCreatures) v.Blockers.Add(st.Population + " of its kind and offspring within " + sp.TotalRange + " m, the cap is " + sp.MaxCreatures);
            var need = sp.NoPartnerOffspring ? 0 : (sp.SeparatePartner ? 1 : 2);
            if (st.ReadyPartners < need) v.Blockers.Add(st.ReadyPartners + " ready partners within " + sp.PartnerRange + " m (itself included), needs " + need + (sp.SeparatePartner ? " of the partner kind" : ""));
            if (v.Blockers.Count == 0 && sp.SkipChance < 1)
            {
                var ticks = Math.Max(0, sp.RequiredLove - st.Love) / (1 - sp.SkipChance);
                v.ExpectedSeconds = ticks * sp.Interval + sp.PregnancyDuration;
            }
            return v;
        }
    }

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
