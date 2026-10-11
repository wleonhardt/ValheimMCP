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
