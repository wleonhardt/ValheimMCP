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
