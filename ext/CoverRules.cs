// Game-free port of Cover.GetCoverForPoint / Cover.IsUnderRoof (assembly_utils, decompiled) and the checks that use it: a bed (cover >= 0.8 and a roof, Bed.CheckExposure),
// a crafting station that needs a roof (cover >= 0.7, CraftingStation.CheckUsable) and a smelter that needs one (Smelter.UpdateRoof: a roof only).
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    internal sealed class CoverSolid
    {
        public Obb Box; public bool Leaky;         // Leaky: the collider is tagged "leaky" and does not count as a roof
    }

    internal sealed class CoverResult
    {
        public double Percent; public bool UnderRoof; public int Hits, Rays;
    }

    /// <summary>A point on a prefab where the game tests cover (a bed's spawn point, a station's roof check point), in the prefab's own frame.</summary>
    internal sealed class CoverPoint
    {
        public string Kind = "";        // bed | station | smelter | fermenter | beehive
        public V3 Local;
        public double MinCover;         // 0.8 bed, 0.7 station and fermenter, 0 = a roof is enough (smelter)
        public double MaxCover;         // a beehive wants LESS than this (m_maxCover, 0.25); 0 = no maximum
    }

    internal static class CoverModel
    {
        public const double RayLength = 30, MinDistance = 0.5, RoofCastLength = 100, RoofRadius = 0.1;

        // Cover.CreateCoverRays: 17 vectors. The game calls Normalize on a struct copy, so the vectors stay unnormalised: a diagonal starts MinDistance x |v| out from the point.
        public static readonly V3[] Rays =
        {
            new V3(0, 1, 0), new V3(1, 0, 0), new V3(-1, 0, 0), new V3(0, 0, 1), new V3(0, 0, -1), new V3(-1, 0, -1), new V3(1, 0, -1), new V3(-1, 0, 1), new V3(1, 0, 1),
            new V3(-1, 1, 0), new V3(1, 1, 0), new V3(0, 1, 1), new V3(0, 1, -1), new V3(-1, 1, -1), new V3(1, 1, -1), new V3(-1, 1, 1), new V3(1, 1, 1)
        };

        /// <param name="ground">terrain height (null: no terrain); a ray ends at the surface when it goes under it. A start under the surface sees no terrain (the collider is a one-sided mesh).</param>
        public static CoverResult At(V3 point, IList<CoverSolid> solids, Func<double, double, double> ground, double minDistance = MinDistance)
        {
            var res = new CoverResult { Rays = Rays.Length };
            foreach (var v in Rays)
            {
                var origin = point + v * minDistance; var dir = v.Norm(); var len = RayLength - minDistance;
                var hit = false;
                foreach (var s in solids) if (s.Box.RayHits(origin, dir, len)) { hit = true; break; }
                if (!hit && ground != null && HitsTerrain(origin, dir, len, ground)) hit = true;
                if (hit) res.Hits++;
            }
            res.Percent = res.Hits / (double)res.Rays;
            res.UnderRoof = IsUnderRoof(point, solids, ground);
            return res;
        }

        /// <summary>A 0.1 m sphere cast up 100 m: any collider that is not tagged "leaky" (or the terrain above the point) is a roof.</summary>
        public static bool IsUnderRoof(V3 point, IList<CoverSolid> solids, Func<double, double, double> ground)
        {
            var up = new V3(0, 1, 0);
            foreach (var s in solids) if (!s.Leaky && s.Box.Grown(RoofRadius).RayHits(point, up, RoofCastLength)) return true;
            return ground != null && ground(point.X, point.Z) > point.Y;
        }

        static bool HitsTerrain(V3 origin, V3 dir, double len, Func<double, double, double> ground)
        {
            if (ground(origin.X, origin.Z) > origin.Y) return false;              // starts under the surface
            for (var t = 1.0; ; t += 1.0)
            {
                var d = Math.Min(t, len); var x = origin.X + dir.X * d; var z = origin.Z + dir.Z * d; var y = origin.Y + dir.Y * d;
                if (ground(x, z) >= y) return true;
                if (d >= len) return false;
            }
        }

        /// <summary>What the game says about a point that needs <paramref name="minCover"/>: null when it passes, else the game's own message token.</summary>
        public static string Verdict(string kind, CoverResult c, double minCover, double maxCover = 0)
        {
            if (kind == "beehive") return maxCover > 0 && c.Percent >= maxCover ? "$piece_beehive_freespace" : null;      // Beehive.HaveFreeSpace: cover under m_maxCover, no roof needed
            if (!c.UnderRoof) return kind == "bed" ? "$msg_bedneedroof" : kind == "fermenter" ? "$piece_fermenter_needroof" : "$msg_stationneedroof";
            if (minCover > 0 && c.Percent < minCover) return kind == "bed" ? "$msg_bedtooexposed" : kind == "fermenter" ? "$piece_fermenter_exposed" : "$msg_stationtooexposed";
            return null;
        }
    }

    internal static class CoverChecks
    {
        public const double Reach = 32;     // rays are 30 m long: nothing farther away can matter

        /// <summary>Beds, roofed stations and smelters among the planned pieces, judged against everything within reach (planned and already built).</summary>
        public static List<PlanProblem> Check(IList<PlacedPiece> pieces, Func<double, double, double> ground, IList<string> measured = null)
        {
            var res = new List<PlanProblem>();
            foreach (var p in pieces)
            {
                if (!p.Planned || p.Geo.CoverPoints.Count == 0) continue;
                foreach (var cp in p.Geo.CoverPoints)
                {
                    var point = p.Pos + p.Rot.Rotate(new V3(cp.Local.X * p.Scale.X, cp.Local.Y * p.Scale.Y, cp.Local.Z * p.Scale.Z));
                    var solids = new List<CoverSolid>();
                    foreach (var o in pieces)
                    {
                        if (Math.Abs(o.Pos.X - point.X) > Reach + 4 || Math.Abs(o.Pos.Z - point.Z) > Reach + 4 || Math.Abs(o.Pos.Y - point.Y) > 120) continue;
                        foreach (var c in o.Geo.Colliders.Where(c => Layers.InCover(c.Layer)))
                            foreach (var b in o.BoxesOf(new[] { c })) solids.Add(new CoverSolid { Box = b, Leaky = c.Leaky });
                    }
                    var cover = CoverModel.At(point, solids, ground);
                    if (measured != null) measured.Add(p.Id + "|" + cp.Kind + "|" + Math.Round(cover.Percent, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (cover.UnderRoof ? "1" : "0"));
                    var bad = CoverModel.Verdict(cp.Kind, cover, cp.MinCover, cp.MaxCover);
                    if (bad == null) continue;
                    var near = cp.Kind == "beehive" ? cover.Percent < cp.MaxCover + 0.1 : cp.MinCover > 0 && cover.UnderRoof && cover.Percent >= cp.MinCover - 0.1;
                    var rule = cp.Kind == "bed" ? "bed-cover" : cp.Kind == "smelter" ? "smelter-roof" : cp.Kind == "fermenter" ? "fermenter-cover" : cp.Kind == "beehive" ? "beehive-cover" : "station-cover";
                    res.Add(PlanRuleBook.Stamp(new PlanProblem
                    {
                        Rule = rule, Severity = near ? "warning" : "error", Piece = p.Id,
                        Detail = p.Geo.Prefab + ": " + bad + " (" + (cover.UnderRoof ? "roof, " : "no roof, ") + Math.Round(cover.Percent * 100) + "% cover of " + (cp.Kind == "beehive" ? "under " + Math.Round(cp.MaxCover * 100) + "% wanted" : cp.MinCover > 0 ? Math.Round(cp.MinCover * 100) + "% needed" : "no minimum") + "; a model of the game's 17 cover rays on the pieces' boxes, so near the limit it can be a little off)"
                    }));
                }
            }
            return res;
        }
    }
}
