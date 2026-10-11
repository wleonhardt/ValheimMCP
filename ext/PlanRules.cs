// Game-free geometry and structure rules for checking a plan before it is placed (no Unity types, so tests/ links this file).
// The support model is a port of WearNTear.UpdateSupport (decompiled from the game): contact by an OverlapBox of every collider grown by 0.15 m on each side, the
// per-material loss table, the closest-point test for what lies below the centre of mass, and the rule that two support points 100 degrees apart average.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    internal struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static V3 operator *(V3 a, double s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
        public double Dot(V3 b) { return X * b.X + Y * b.Y + Z * b.Z; }
        public V3 Cross(V3 b) { return new V3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X); }
        public double Len { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public V3 Norm() { var l = Len; return l < 1e-12 ? new V3(0, 0, 0) : new V3(X / l, Y / l, Z / l); }
        public static double Dist(V3 a, V3 b) { return (a - b).Len; }
    }

    /// <summary>A rotation (x, y, z, w) with Unity's conventions.</summary>
    internal struct Quat
    {
        public double X, Y, Z, W;
        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        public static Quat Identity { get { return new Quat(0, 0, 0, 1); } }
        public static Quat Yaw(double deg) { var h = deg * Math.PI / 360.0; return new Quat(0, Math.Sin(h), 0, Math.Cos(h)); }
        public static Quat AxisAngle(V3 axis, double deg) { var a = axis.Norm(); var h = deg * Math.PI / 360.0; var s = Math.Sin(h); return new Quat(a.X * s, a.Y * s, a.Z * s, Math.Cos(h)); }
        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y, a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X, a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W, a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }
        /// <summary>Unity's Quaternion.Euler: rotate about z, then x, then y.</summary>
        public static Quat Euler(double xDeg, double yDeg, double zDeg)
        {
            return AxisAngle(new V3(0, 1, 0), yDeg) * AxisAngle(new V3(1, 0, 0), xDeg) * AxisAngle(new V3(0, 0, 1), zDeg);
        }
        public V3 Rotate(V3 v)
        {
            var u = new V3(X, Y, Z); var s = W;
            return u * (2 * u.Dot(v)) + v * (s * s - u.Dot(u)) + u.Cross(v) * (2 * s);
        }
    }

    /// <summary>An oriented box.</summary>
    internal sealed class Obb
    {
        public V3 C, Half;
        public V3[] Axes = new V3[3];

        public static Obb Make(V3 centre, V3 half, Quat rot)
        {
            var o = new Obb { C = centre, Half = half };
            o.Axes[0] = rot.Rotate(new V3(1, 0, 0)); o.Axes[1] = rot.Rotate(new V3(0, 1, 0)); o.Axes[2] = rot.Rotate(new V3(0, 0, 1));
            return o;
        }

        public Obb Grown(double by) { return new Obb { C = C, Half = new V3(Half.X + by, Half.Y + by, Half.Z + by), Axes = Axes }; }

        public V3 ClosestPoint(V3 p)
        {
            var d = p - C; var r = C;
            for (var i = 0; i < 3; i++) { var t = Math.Max(-HalfOf(i), Math.Min(HalfOf(i), d.Dot(Axes[i]))); r = r + Axes[i] * t; }
            return r;
        }

        public double HalfOf(int i) { return i == 0 ? Half.X : (i == 1 ? Half.Y : Half.Z); }

        /// <summary>Highest and lowest world y of the box.</summary>
        public double MaxY { get { return C.Y + Extent(new V3(0, 1, 0)); } }
        public double MinY { get { return C.Y - Extent(new V3(0, 1, 0)); } }
        public double Extent(V3 axis) { return Half.X * Math.Abs(axis.Dot(Axes[0])) + Half.Y * Math.Abs(axis.Dot(Axes[1])) + Half.Z * Math.Abs(axis.Dot(Axes[2])); }

        /// <summary>
        ///     Separating axis test. Positive: the boxes overlap and this is the smallest overlap over all axes (the depth to push one out). Zero or negative: they are apart and
        ///     the number is minus the largest separation found along an axis (a lower bound of the true gap, exact for axis-aligned boxes).
        /// </summary>
        public static double Depth(Obb a, Obb b)
        {
            var t = b.C - a.C; var best = double.PositiveInfinity;
            var axes = new List<V3>(15);
            axes.AddRange(a.Axes); axes.AddRange(b.Axes);
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) { var c = a.Axes[i].Cross(b.Axes[j]); if (c.Len > 1e-6) axes.Add(c.Norm()); }
            foreach (var ax in axes)
            {
                var depth = a.Extent(ax) + b.Extent(ax) - Math.Abs(t.Dot(ax));
                if (depth < best) best = depth;
            }
            return best;
        }

        public static bool Overlaps(Obb a, Obb b) { return Depth(a, b) > 0; }

        /// <summary>First hit of a ray within <paramref name="maxLen"/> (slab test in the box's own frame). A ray that starts inside the box does not hit it, like a Unity raycast.</summary>
        public bool RayHits(V3 origin, V3 dir, double maxLen)
        {
            var d = origin - C; var tmin = 0.0; var tmax = maxLen;
            for (var i = 0; i < 3; i++)
            {
                var o = d.Dot(Axes[i]); var v = dir.Dot(Axes[i]); var h = HalfOf(i);
                if (Math.Abs(v) < 1e-12) { if (o < -h || o > h) return false; continue; }
                var t1 = (-h - o) / v; var t2 = (h - o) / v; if (t1 > t2) { var t = t1; t1 = t2; t2 = t; }
                tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2); if (tmin > tmax) return false;
            }
            var inside = true;
            for (var i = 0; i < 3; i++) { var o = d.Dot(Axes[i]); if (o < -HalfOf(i) || o > HalfOf(i)) { inside = false; break; } }
            return !inside;
        }
    }

    internal sealed class ColliderBox
    {
        public V3 Centre, Half;          // in the prefab's own frame
        public Quat Rot = Quat.Identity;
        public bool Leaky;               // tagged "leaky": Cover.IsUnderRoof ignores it
        public string Layer = "Default"; // Unity layer name of the collider (the game's checks each look at their own set of layers)
    }

    /// <summary>The layer sets the game's own checks use (decompiled): WearNTear.s_rayMask, Player.m_placeRayMask, Cover.m_coverRayMask, Fireplace.m_solidRayMask.</summary>
    internal static class Layers
    {
        static readonly string[] Support = { "piece", "Default", "static_solid", "Default_small", "terrain" };
        static readonly string[] Place = { "Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle" };
        static readonly string[] Cover = { "Default", "static_solid", "Default_small", "piece", "terrain", "vehicle" };
        static readonly string[] Solid = { "Default", "static_solid", "Default_small", "piece", "terrain" };
        public static bool InSupport(string layer) { return Array.IndexOf(Support, layer) >= 0; }
        public static bool InPlace(string layer) { return Array.IndexOf(Place, layer) >= 0; }
        public static bool InCover(string layer) { return Array.IndexOf(Cover, layer) >= 0; }
        public static bool InSolid(string layer) { return Array.IndexOf(Solid, layer) >= 0; }
    }

    /// <summary>What the checks need to know about a prefab.</summary>
    internal sealed class PieceGeo
    {
        public string Prefab = "";
        public List<ColliderBox> Colliders = new List<ColliderBox>();
        public string Material = "";                // WearNTear.MaterialType name; "" = not a WearNTear (cannot carry or be carried)
        public bool Supports;                       // WearNTear.m_supports
        public V3 ComOffset;
        public bool NoClipping, NotOnTilting, NotOnWood, NotOnFloor, NoInWater, GroundOnly, IsFire, IsWall, IsStation, Occupies = true;
        public bool ForceCorrectCom;                // WearNTear.m_forceCorrectCOMCalculation: the support distance may not use the nearer pivot distance
        public bool WearsWithoutSupport = true;    // WearNTear.m_noSupportWear (the name is the opposite of what it says): false = the piece never takes damage from lack of support
        public bool IsFireplace;                    // has a Fireplace component (torches and sconces too, not only the big fires IsFire names)
        public bool DisableCoverCheck;              // Fireplace.m_disableCoverCheck
        public double CoverCheckOffset = 0.5, CheckTerrainOffset = 0.2;       // Fireplace.m_coverCheckOffset / m_checkTerrainOffset
        public string StationName = "";             // Piece.m_craftingStation name: the station that has to be in range of the builder ("" = none)
        public string ExtensionOf = "";             // StationExtension.m_craftingStation name ("" = not an extension)
        public double ExtensionMaxDistance = 5, ExtensionSpace = 0;       // StationExtension.m_maxStationDistance / Piece.m_spaceRequirement
        public bool ExtensionStack;                 // StationExtension.m_stack
        public string ProvidesStation = "";         // CraftingStation.m_name when this piece is a station
        public double StationRangeBuild = 10, StationExtraRange = 0;      // CraftingStation.m_rangeBuild / m_extraRangePerLevel

        public List<CoverPoint> CoverPoints = new List<CoverPoint>();   // bed spawn point, roofed station / smelter check point
        public List<V3> SnapPoints = new List<V3>();    // direct children of the prefab tagged "snappoint" (Piece.GetSnapPoints), in the piece's own space

        public bool IsWood { get { return Material == "Wood" || Material == "HardWood"; } }
    }

    internal sealed class PlacedPiece
    {
        public string Id = "";
        public PieceGeo Geo;
        public V3 Pos;
        public Quat Rot = Quat.Identity;
        public bool Planned = true;                 // false = already in the world (a supporter and an obstacle, never reported on)
        public V3 Scale = new V3(1, 1, 1);          // the ZDO's scale vector (trees, rocks and logs are placed scaled)

        public List<Obb> Boxes() { return Boxes(null); }

        /// <summary>The collider boxes in the world; <paramref name="layer"/> keeps only the colliders on the layers a given check looks at.</summary>
        public List<Obb> Boxes(Func<string, bool> layer)
        {
            return BoxesOf(Geo.Colliders.Where(c => layer == null || layer(c.Layer)));
        }

        /// <summary>The world boxes of some of this piece's colliders.</summary>
        public List<Obb> BoxesOf(IEnumerable<ColliderBox> colliders)
        {
            var res = new List<Obb>();
            foreach (var c in colliders)
                    res.Add(Obb.Make(Pos + Rot.Rotate(new V3(c.Centre.X * Scale.X, c.Centre.Y * Scale.Y, c.Centre.Z * Scale.Z)), new V3(c.Half.X * Math.Abs(Scale.X), c.Half.Y * Math.Abs(Scale.Y), c.Half.Z * Math.Abs(Scale.Z)), Rot * c.Rot));
            return res;
        }
        public V3 Com { get { return Pos + Rot.Rotate(Geo.ComOffset); } }
    }

    internal sealed class Material
    {
        public double Max, Min, Vertical, Horizontal;

        /// <summary>
        ///     How far above the minimum a piece's support has to be for one more 2 m module out (sideways where the material spans, up where it does not) to leave it standing:
        ///     1 / (1 - 2 x loss). Wood 1.67, core wood 1.5, iron 1.18, grausten 3.0, stone and marble 1.33.
        /// </summary>
        public double WeakRatio { get { var loss = Horizontal < 0.5 ? Horizontal : Vertical; return 1.0 / (1.0 - 2.0 * loss); } }

        /// <summary>WearNTear.GetMaterialProperties, as decompiled.</summary>
        public static Material Of(string name)
        {
            switch (name)
            {
                case "Wood": return new Material { Max = 100, Min = 10, Vertical = 0.125, Horizontal = 0.2 };
                case "HardWood": return new Material { Max = 140, Min = 10, Vertical = 0.1, Horizontal = 1.0 / 6 };
                case "Stone": return new Material { Max = 1000, Min = 100, Vertical = 0.125, Horizontal = 1.0 };
                case "Iron": return new Material { Max = 1500, Min = 20, Vertical = 1.0 / 13, Horizontal = 1.0 / 13 };
                case "Marble": return new Material { Max = 1500, Min = 100, Vertical = 0.125, Horizontal = 0.5 };
                case "Ashstone": return new Material { Max = 2000, Min = 100, Vertical = 0.1, Horizontal = 1.0 / 3 };
                case "Ancient": return new Material { Max = 5000, Min = 100, Vertical = 1.0 / 15, Horizontal = 0.25 };
                case "Ice": return new Material { Max = 1000, Min = 100, Vertical = 0.125, Horizontal = 1.0 / 3 };
                case "Timberwood": return new Material { Max = 200, Min = 10, Vertical = 1.0 / 13, Horizontal = 0.2 };
                default: return null;
            }
        }
    }

    internal sealed class SupportResult
    {
        public double Support, Min, Max;
        public bool Grounded, NeverFalls;
        public bool Holds { get { return NeverFalls || Support >= Min; } }
    }

    internal static class SupportModel
    {
        public const double ContactGrow = 0.15;          // the game grows every collider box by 0.3 in total before looking for neighbours
        public const double GroundSampleTolerance = 0.0;

        /// <param name="ground">terrain height at x,z (null: no terrain, nothing is grounded)</param>
        /// <param name="obstacles">solid things that are not pieces (rocks, trees): touching one gives full support</param>
        public static Dictionary<PlacedPiece, SupportResult> Run(IList<PlacedPiece> pieces, Func<double, double, double> ground, IList<Obb> obstacles, int maxRounds = 80)
        {
            var res = new Dictionary<PlacedPiece, SupportResult>();
            var grown = new Dictionary<PlacedPiece, List<Obb>>(); var plain = new Dictionary<PlacedPiece, List<Obb>>();
            var mats = new Dictionary<PlacedPiece, Material>();
            // A collider without a WearNTear that a piece touches gives it full support at once (WearNTear.UpdateSupport): a rock, a tree, a bush, a post that is not a structure.
            var solids = new List<Obb>(); if (obstacles != null) solids.AddRange(obstacles);
            foreach (var p in pieces)
            {
                var m = Material.Of(p.Geo.Material); mats[p] = m;
                if (m == null) { solids.AddRange(p.Boxes(Layers.InSupport)); continue; }
                plain[p] = p.Boxes(Layers.InSupport); grown[p] = plain[p].Select(b => b.Grown(ContactGrow)).ToList();
                res[p] = new SupportResult { Max = m.Max, Min = m.Min, NeverFalls = !p.Geo.WearsWithoutSupport };
            }
            obstacles = solids;
            // contacts that do not change between rounds
            var aabbs = res.Keys.ToDictionary(k => k, k => BoundsOf(plain[k]));          // a cheap box test first: most pairs are metres apart
            var onGround = new HashSet<PlacedPiece>(); var onSolid = new HashSet<PlacedPiece>();
            var touching = new Dictionary<PlacedPiece, List<KeyValuePair<PlacedPiece, Obb>>>();
            foreach (var p in res.Keys)
            {
                touching[p] = new List<KeyValuePair<PlacedPiece, Obb>>();
                foreach (var g in grown[p])
                {
                    if (ground != null && TouchesGround(g, ground)) onGround.Add(p);
                    foreach (var o in obstacles) { if ((g.C - o.C).Len > g.Half.Len + o.Half.Len) continue; if (Obb.Overlaps(g, o)) { onSolid.Add(p); break; } }
                }
                var pa = BoundsOf(grown[p]);
                foreach (var q in res.Keys)
                {
                    if (q == p || !q.Geo.Supports) continue;
                    if (!Apart(pa, aabbs[q])) foreach (var g in grown[p]) foreach (var qb in plain[q]) if (Obb.Overlaps(g, qb)) { touching[p].Add(new KeyValuePair<PlacedPiece, Obb>(q, qb)); }
                }
            }
            // WearNTear sets m_support to the maximum in Awake and calls UpdateSupport only when m_noSupportWear is set: a piece without it keeps full support and carries others like an anchor
            foreach (var p in res.Keys) if (res[p].NeverFalls) onSolid.Add(p);
            foreach (var p in res.Keys) { res[p].Grounded = onGround.Contains(p) || onSolid.Contains(p); res[p].Support = res[p].Grounded ? res[p].Max : 0; }

            for (var round = 0; round < maxRounds; round++)
            {
                var changed = false; var next = new Dictionary<PlacedPiece, double>();
                foreach (var p in res.Keys)
                {
                    if (res[p].Grounded) { next[p] = res[p].Max; continue; }
                    var m = mats[p]; var com = p.Com; var best = 0.0; var points = new List<V3>(); var values = new List<double>();
                    foreach (var kv in touching[p])
                    {
                        var q = kv.Key; var s = res[q].Support;
                        var d1 = V3.Dist(com, q.Com) + 0.1; var d2 = V3.Dist(com, q.Pos) + 0.1; var d = d2 < d1 && !p.Geo.ForceCorrectCom ? d2 : d1;
                        best = Math.Max(best, s - m.Horizontal * d * s);
                        var sp = kv.Value.ClosestPoint(com);
                        if (sp.Y < com.Y + 0.05)
                        {
                            var n = (sp - com).Norm();
                            if (n.Y < 0)
                            {
                                var t = Math.Acos(1 - Math.Abs(n.Y)) / (Math.PI / 2);
                                var loss = m.Horizontal + (m.Vertical - m.Horizontal) * t;
                                best = Math.Max(best, s - loss * d * s);
                            }
                            points.Add(sp); values.Add(s - m.Vertical * d * s);
                        }
                    }
                    for (var i = 0; i < points.Count - 1; i++)
                    {
                        var a = points[i] - com; a.Y = 0;
                        for (var j = i + 1; j < points.Count; j++)
                        {
                            var avg = (values[i] + values[j]) * 0.5;
                            if (avg <= best) continue;
                            var b = points[j] - com; b.Y = 0;
                            if (AngleDeg(a, b) >= 100) best = avg;
                        }
                    }
                    next[p] = Math.Min(best, m.Max);
                }
                foreach (var p in res.Keys) if (Math.Abs(next[p] - res[p].Support) > 1e-4) { res[p].Support = next[p]; changed = true; }
                if (!changed) break;
            }
            return res;
        }

        private static double[] BoundsOf(IList<Obb> boxes)
        {
            if (boxes.Count == 0) return new double[] { 0, 0, 0, 0, 0, 0 };
            return new[] { boxes.Min(b => b.C.X - b.Extent(new V3(1, 0, 0))), boxes.Max(b => b.C.X + b.Extent(new V3(1, 0, 0))), boxes.Min(b => b.MinY), boxes.Max(b => b.MaxY), boxes.Min(b => b.C.Z - b.Extent(new V3(0, 0, 1))), boxes.Max(b => b.C.Z + b.Extent(new V3(0, 0, 1))) };
        }
        private static bool Apart(double[] a, double[] b) { return a[1] < b[0] || b[1] < a[0] || a[3] < b[2] || b[3] < a[2] || a[5] < b[4] || b[5] < a[4]; }

        /// <summary>
        ///     Does the terrain surface pass through this box (WearNTear overlaps the box with the terrain collider)? The surface is continuous, so it does when the lowest ground under
        ///     the box's footprint is not above the box and the highest is not below it. The footprint is sampled in the box's own axes, so a rotated box is not sampled outside itself.
        /// </summary>
        public static bool TouchesGround(Obb b, Func<double, double, double> ground)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (var s = -1; s <= 1; s += 2)
                for (var i = 0; i <= 4; i++)
                    for (var j = 0; j <= 4; j++)
                    {
                        var u = -1 + 2 * i / 4.0; var w = -1 + 2 * j / 4.0;
                        var p = b.C + b.Axes[0] * (u * b.Half.X) + b.Axes[2] * (w * b.Half.Z) + b.Axes[1] * (s * b.Half.Y);
                        var g = ground(p.X, p.Z); if (g < lo) lo = g; if (g > hi) hi = g;
                    }
            return lo <= b.MaxY && hi >= b.MinY;
        }

        private static double AngleDeg(V3 a, V3 b)
        {
            var la = a.Len; var lb = b.Len; if (la < 1e-9 || lb < 1e-9) return 0;
            return Math.Acos(Math.Max(-1, Math.Min(1, a.Dot(b) / (la * lb)))) * 180 / Math.PI;
        }
    }

    /// <summary>What a removal brings down: the pieces that hold now and fall once the removed ones are gone (WearNTear destroys a piece whose support drops under its material minimum).</summary>
    internal static class RemovalImpact
    {
        public static List<PlacedPiece> Collapses(IList<PlacedPiece> all, ISet<string> removedIds, Func<double, double, double> ground)
        {
            var before = SupportModel.Run(all, ground, null);
            var rest = all.Where(p => !removedIds.Contains(p.Id)).ToList();
            var after = SupportModel.Run(rest, ground, null);
            var res = new List<PlacedPiece>();
            foreach (var p in rest)
            {
                SupportResult b, a;
                if (before.TryGetValue(p, out b) && b.Holds && after.TryGetValue(p, out a) && !a.Holds) res.Add(p);
            }
            return res;
        }
    }

    internal sealed class PlanProblem
    {
        public string Rule, Severity, Piece, Detail;
        public string Code = "", Origin = "";       // filled by PlanRuleBook.Stamp: the game's own status name, and where the rule comes from
    }

    /// <summary>One entry of the rule book: where a plan check comes from. hard = the world enforces it after a ZDO spawn; ghost = only the hammer's placement ghost enforces it; taste = our preference.</summary>
    internal sealed class PlanRule
    {
        public string Id, Origin, Code, Source;
    }

    internal static class PlanRuleBook
    {
        public const string Hard = "hard", Ghost = "ghost", Taste = "taste";

        public static readonly IList<PlanRule> Rules = new List<PlanRule>
        {
            new PlanRule { Id = "overlap", Origin = Ghost, Code = "Invalid", Source = "Player.TestGhostClipping(ghost, 0.2): checked only for the piece being placed, and only when that piece has m_noClipping; penetration over 0.2 m. Everything else is a warning" },
            new PlanRule { Id = "unsupported", Origin = Hard, Code = "", Source = "WearNTear.UpdateSupport: a piece under its material minimum is destroyed by its owner" },
            new PlanRule { Id = "weak", Origin = Hard, Code = "", Source = "derived from the same loss table: one more 2 m module (sideways where the material spans, else up) would break it" },
            new PlanRule { Id = "tilt", Origin = Ghost, Code = "Invalid", Source = "Piece.m_notOnTiltingSurface: surface normal y under 0.8" },
            new PlanRule { Id = "water", Origin = Ghost, Code = "Invalid", Source = "Piece.m_noInWater" },
            new PlanRule { Id = "snap-seam", Origin = Ghost, Code = "", Source = "Player.FindClosestSnapPoints: the hammer moves a ghost until its nearest snap point meets the nearest snap point of a piece within 10 m, when they are under 0.5 m apart. A ZDO spawn never snaps, so a plan that leaves them 0.02 to 0.5 m apart has a seam or an overlap the hammer would have closed" },
            new PlanRule { Id = "bed-cover", Origin = Hard, Code = "", Source = "Bed.CheckExposure: Cover.GetCoverForPoint at the spawn point needs a roof ($msg_bedneedroof) and cover of 0.8 ($msg_bedtooexposed), else the bed cannot be claimed. A model of the 17 cover rays, not the game's physics" },
            new PlanRule { Id = "station-cover", Origin = Hard, Code = "", Source = "CraftingStation.CheckUsable: with m_craftRequireRoof, Cover at m_roofCheckPoint needs a roof and cover of 0.7, else the station cannot be used" },
            new PlanRule { Id = "fermenter-cover", Origin = Hard, Code = "", Source = "Fermenter.UpdateCover: no roof or under 70% cover at m_roofCheckPoint restarts the fermentation timer every 10 s" },
            new PlanRule { Id = "beehive-cover", Origin = Hard, Code = "", Source = "Beehive.HaveFreeSpace: cover at m_coverPoint must be under m_maxCover (0.25), else the hive makes no honey ($piece_beehive_freespace)" },
            new PlanRule { Id = "smelter-roof", Origin = Hard, Code = "", Source = "Smelter.UpdateRoof: with m_requiresRoof, Cover.IsUnderRoof at m_roofCheckPoint, else the smelter does not work" },
            new PlanRule { Id = "yaw-grid", Origin = Ghost, Code = "", Source = "the hammer rotates in steps of 22.5 degrees; a ZDO spawn can be anything" },
            new PlanRule { Id = "fire-clearance", Origin = Taste, Code = "", Source = "our preference: a metre of free ground round a fire (vanilla has no such placement rule; the real failures are fire-blocked and smoke)" },
            new PlanRule { Id = "fire-blocked", Origin = Hard, Code = "", Source = "Fireplace.CheckUnderTerrain: terrain above y + m_checkTerrainOffset, or a solid within 0.5 m straight up from y + m_coverCheckOffset, and the fire does not burn" },
            new PlanRule { Id = "extension-no-station", Origin = Hard, Code = "ExtensionMissingStation", Source = "StationExtension.FindExtensions: pivot distance to a station of its kind under m_maxStationDistance, else it never counts" },
            new PlanRule { Id = "extension-spacing", Origin = Ghost, Code = "MoreSpace", Source = "StationExtension.OtherExtensionInRange(Piece.m_spaceRequirement)" },
            new PlanRule { Id = "station-out-of-range", Origin = Ghost, Code = "", Source = "Player.HaveRequirements: the builder must stand within m_rangeBuild + extensions x m_extraRangePerLevel (horizontal) of a station of that name" },
            new PlanRule { Id = "not-on-wood", Origin = Ghost, Code = "Invalid", Source = "Piece.m_notOnWood: refused on a Wood or HardWood piece" },
            new PlanRule { Id = "no-build-zone", Origin = Ghost, Code = "NoBuildZone", Source = "Location.IsInsideNoBuildLocation: m_noBuild and DistanceXZ under the override radius or the location radius" },
            new PlanRule { Id = "private-zone", Origin = Hard, Code = "PrivateZone", Source = "PrivateArea.CheckAccess: an enabled ward without access (plan_apply refuses unless forced)" },
        };

        public static PlanRule Get(string id) { foreach (var r in Rules) if (r.Id == id) return r; return null; }

        /// <summary>Fill Origin and Code from the book, and never let a taste rule be an error.</summary>
        public static PlanProblem Stamp(PlanProblem p)
        {
            if (p == null) return null;
            var r = Get(p.Rule);
            if (r != null) { p.Origin = r.Origin; p.Code = r.Code; if (r.Origin == Taste && p.Severity == "error") p.Severity = "warning"; }
            return p;
        }
    }

    /// <summary>The hammer's snapping, as a check: where a planned piece's snap point is near, but not at, another piece's.</summary>
    internal static class SnapSeams
    {
        public const double Reach = 0.5, Exact = 0.02;

        public static List<PlanProblem> Find(IList<PlacedPiece> pieces)
        {
            var res = new List<PlanProblem>();
            var cells = new Dictionary<long, List<KeyValuePair<int, V3>>>();
            var pts = new List<List<V3>>();
            for (var i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i]; var l = new List<V3>();
                foreach (var s in p.Geo.SnapPoints) l.Add(p.Pos + p.Rot.Rotate(new V3(s.X * p.Scale.X, s.Y * p.Scale.Y, s.Z * p.Scale.Z)));
                pts.Add(l);
            }
            // 0.5 m cells keyed by their integer index; neighbours come from the 27 cells round a point and the real distance decides
            var grid = new Dictionary<string, List<KeyValuePair<int, V3>>>();
            Func<long, long, long, string> ck = (a, b, c) => a + "," + b + "," + c;
            for (var i = 0; i < pts.Count; i++)
                foreach (var w in pts[i])
                {
                    var k = ck((long)Math.Floor(w.X / Reach), (long)Math.Floor(w.Y / Reach), (long)Math.Floor(w.Z / Reach));
                    List<KeyValuePair<int, V3>> l; if (!grid.TryGetValue(k, out l)) grid[k] = l = new List<KeyValuePair<int, V3>>();
                    l.Add(new KeyValuePair<int, V3>(i, w));
                }
            for (var i = 0; i < pieces.Count; i++)
            {
                if (!pieces[i].Planned || pts[i].Count == 0) continue;
                var best = double.PositiveInfinity; var bestJ = -1;
                foreach (var w in pts[i])
                {
                    long cx = (long)Math.Floor(w.X / Reach), cy = (long)Math.Floor(w.Y / Reach), cz = (long)Math.Floor(w.Z / Reach);
                    for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
                    {
                        List<KeyValuePair<int, V3>> l; if (!grid.TryGetValue(ck(cx + dx, cy + dy, cz + dz), out l)) continue;
                        foreach (var kv in l) { if (kv.Key == i) continue; var d = V3.Dist(w, kv.Value); if (d <= Reach && d < best) { best = d; bestJ = kv.Key; } }
                    }
                }
                if (bestJ < 0 || best <= Exact) continue;
                res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "snap-seam", Severity = "warning", Piece = pieces[i].Id, Detail = pieces[i].Geo.Prefab + " has a snap point " + Math.Round(best, 2) + " m from one of " + pieces[bestJ].Geo.Prefab + " (" + pieces[bestJ].Id + "): the hammer would have moved it to meet, a spawn leaves the seam" }));
            }
            return res;
        }
    }

    /// <summary>The checks, each a function of geometry only.</summary>
    internal static class PlanChecks
    {
        public const double WaterLevel = 30.0;
        public const double PenetrationTolerance = 0.05;      // below this an overlap is a rounding error
        public const double ClipError = 0.2;                  // Player.TestGhostClipping(ghost, 0.2f)
        public const double TiltNormalY = 0.8;

        /// <summary>Colliders of two pieces that go into each other. An error when a piece that cannot clip (m_noClipping) goes in by more than the game's 0.2 m, else a warning above 0.05 m (the game lets building pieces overlap).</summary>
        public static List<PlanProblem> Overlaps(IList<PlacedPiece> pieces)
        {
            var res = new List<PlanProblem>();
            var boxes = pieces.Select(p => p.Boxes(Layers.InPlace)).ToList();
            var aabb = boxes.Select(bl => bl.Count == 0 ? null : new[] { bl.Min(b => b.C.X - b.Extent(new V3(1, 0, 0))), bl.Max(b => b.C.X + b.Extent(new V3(1, 0, 0))), bl.Min(b => b.MinY), bl.Max(b => b.MaxY), bl.Min(b => b.C.Z - b.Extent(new V3(0, 0, 1))), bl.Max(b => b.C.Z + b.Extent(new V3(0, 0, 1))) }).ToList();
            for (var i = 0; i < pieces.Count; i++)
                for (var j = i + 1; j < pieces.Count; j++)
                {
                    if (!pieces[i].Planned && !pieces[j].Planned) continue;
                    var a = aabb[i]; var b = aabb[j]; if (a == null || b == null) continue;
                    if (a[1] < b[0] || b[1] < a[0] || a[3] < b[2] || b[3] < a[2] || a[5] < b[4] || b[5] < a[4]) continue;
                    var worst = 0.0;
                    foreach (var x in boxes[i]) foreach (var y in boxes[j]) worst = Math.Max(worst, Obb.Depth(x, y));
                    if (worst <= PenetrationTolerance) continue;
                    var cannotClip = (pieces[i].Planned && pieces[i].Geo.NoClipping) || (pieces[j].Planned && pieces[j].Geo.NoClipping);       // the game tests only the piece being placed (Player.UpdatePlacementGhost: component.m_noClipping)
                    var hard = cannotClip && worst > ClipError;
                    res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "overlap", Severity = hard ? "error" : "warning", Piece = pieces[i].Id, Detail = pieces[i].Geo.Prefab + " goes " + Math.Round(worst, 2) + " m into " + pieces[j].Geo.Prefab + " (" + pieces[j].Id + ")" + (hard ? ": a piece that cannot clip (the game allows " + ClipError + " m)" : "") }));
                }
            return res;
        }

        public static PlanProblem Tilt(PlacedPiece p, double groundNormalY)
        {
            return PlanRuleBook.Stamp(p.Geo.NotOnTilting && groundNormalY < TiltNormalY ? new PlanProblem { Rule = "tilt", Severity = "error", Piece = p.Id, Detail = p.Geo.Prefab + " cannot stand on a surface this steep (normal y " + Math.Round(groundNormalY, 2) + " < " + TiltNormalY + ")" } : null);
        }

        public static PlanProblem Water(PlacedPiece p, double groundY)
        {
            return PlanRuleBook.Stamp(p.Geo.NoInWater && groundY < WaterLevel ? new PlanProblem { Rule = "water", Severity = "error", Piece = p.Id, Detail = p.Geo.Prefab + " cannot be built in water (ground " + Math.Round(groundY, 2) + " m, sea level " + WaterLevel + ")" } : null);
        }

        public static PlanProblem Grid(PlacedPiece p, double yawDeg)
        {
            var m = ((yawDeg % 22.5) + 22.5) % 22.5; var off = Math.Min(m, 22.5 - m);
            return PlanRuleBook.Stamp(off > 0.5 ? new PlanProblem { Rule = "yaw-grid", Severity = "warning", Piece = p.Id, Detail = p.Geo.Prefab + " yaw " + Math.Round(yawDeg, 2) + " is " + Math.Round(off, 2) + " degrees off the 22.5 degree grid the game places on" } : null);
        }

        /// <summary>A piece standing on top of another (a cauldron on a hearth, a pot on a fire pit): its centre is over the host's footprint and its bottom is at the host's top.</summary>
        public static bool RidesOn(PlacedPiece rider, PlacedPiece host)
        {
            var rb = rider.Boxes(); var hb = host.Boxes(); if (rb.Count == 0 || hb.Count == 0) return false;
            var rider0 = rb.Min(b => b.MinY); var hostTop = hb.Max(b => b.MaxY);
            if (rider0 < hostTop - 0.25) return false;
            foreach (var h in hb)
            {
                var cp = h.ClosestPoint(new V3(rider.Pos.X, h.C.Y, rider.Pos.Z));
                if (Math.Sqrt((cp.X - rider.Pos.X) * (cp.X - rider.Pos.X) + (cp.Z - rider.Pos.Z) * (cp.Z - rider.Pos.Z)) < 0.05) return true;
            }
            return false;
        }

        /// <summary>Taste: a fire wants a metre of free ground all round. Always a warning; a piece that stands on the fire (a cauldron on a hearth) does not count.</summary>
        public static List<PlanProblem> FireClearance(IList<PlacedPiece> pieces)
        {
            var res = new List<PlanProblem>();
            foreach (var f in pieces.Where(p => p.Planned && p.Geo.IsFire))
            {
                var fb = f.Boxes();
                foreach (var o in pieces)
                {
                    if (o == f || !o.Geo.Occupies || RidesOn(o, f) || RidesOn(f, o)) continue;
                    var gap = double.PositiveInfinity;
                    foreach (var x in fb) foreach (var y in o.Boxes()) { var d = Obb.Depth(x, y); gap = Math.Min(gap, d > 0 ? 0 : -d); }
                    if (gap >= 1.0) continue;
                    res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "fire-clearance", Severity = "warning", Piece = f.Id, Detail = f.Geo.Prefab + " has only " + Math.Round(gap, 2) + " m to " + o.Geo.Prefab + " (" + o.Id + "); a fire wants 1 m free all round (our preference, not a game rule)" }));
                }
            }
            return res;
        }

        /// <summary>Fireplace.CheckUnderTerrain: terrain above the fire, or a solid within 0.5 m straight up, and the fire does not burn (its fuel is not spent either).</summary>
        public static List<PlanProblem> FireBlocked(IList<PlacedPiece> pieces, Func<double, double, double> ground)
        {
            var res = new List<PlanProblem>();
            foreach (var f in pieces.Where(p => p.Planned && p.Geo.IsFireplace && !p.Geo.DisableCoverCheck))
            {
                if (ground != null && ground(f.Pos.X, f.Pos.Z) > f.Pos.Y + f.Geo.CheckTerrainOffset)
                { res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "fire-blocked", Severity = "error", Piece = f.Id, Detail = f.Geo.Prefab + " is under the terrain (ground " + Math.Round(ground(f.Pos.X, f.Pos.Z), 2) + " above its origin " + Math.Round(f.Pos.Y, 2) + "): it does not burn" })); continue; }
                var origin = new V3(f.Pos.X, f.Pos.Y + f.Geo.CoverCheckOffset, f.Pos.Z); var up = new V3(0, 1, 0);
                foreach (var o in pieces)
                {
                    if (o == f || Math.Abs(o.Pos.X - f.Pos.X) > 8 || Math.Abs(o.Pos.Z - f.Pos.Z) > 8 || Math.Abs(o.Pos.Y - f.Pos.Y) > 12) continue;          // only what can be within half a metre above it
                    foreach (var b in o.Boxes(Layers.InSolid))
                        if (b.RayHits(origin, up, 0.5))
                        { res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "fire-blocked", Severity = "error", Piece = f.Id, Detail = f.Geo.Prefab + " has " + o.Geo.Prefab + " (" + o.Id + ") within 0.5 m straight above it: the game puts the fire out" })); goto next; }
                }
            next:;
            }
            return res;
        }

        /// <summary>Station extensions: an extension counts only with a station of its kind within its range pivot to pivot (else it does nothing), and two extensions closer than the spacing are refused by the hammer.</summary>
        public static List<PlanProblem> Extensions(IList<PlacedPiece> pieces)
        {
            var res = new List<PlanProblem>();
            var stations = pieces.Where(p => p.Geo.ProvidesStation.Length > 0).ToList();
            var exts = pieces.Where(p => p.Geo.ExtensionOf.Length > 0).ToList();
            foreach (var e in exts.Where(x => x.Planned))
            {
                if (!stations.Any(s => s.Geo.ProvidesStation == e.Geo.ExtensionOf && V3.Dist(s.Pos, e.Pos) < e.Geo.ExtensionMaxDistance))
                    res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "extension-no-station", Severity = "error", Piece = e.Id, Detail = e.Geo.Prefab + " has no " + e.Geo.ExtensionOf + " within " + e.Geo.ExtensionMaxDistance + " m (pivot to pivot): it adds no station level" }));
                foreach (var o in exts)
                {
                    if (o == e || (o.Planned && string.CompareOrdinal(o.Id, e.Id) < 0)) continue;
                    if (e.Geo.ExtensionSpace > 0 && V3.Dist(o.Pos, e.Pos) < e.Geo.ExtensionSpace)
                        res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "extension-spacing", Severity = "warning", Piece = e.Id, Detail = e.Geo.Prefab + " is " + Math.Round(V3.Dist(o.Pos, e.Pos), 2) + " m from " + o.Geo.Prefab + " (" + o.Id + "); the hammer needs " + e.Geo.ExtensionSpace + " m between extensions" }));
                }
            }
            return res;
        }

        /// <summary>The build range of a station: m_rangeBuild plus m_extraRangePerLevel for every linked extension, measured flat (the game sets the point's y to the station's).</summary>
        public static double StationBuildRange(PlacedPiece station, IList<PlacedPiece> pieces)
        {
            var n = 0; var seen = new HashSet<string>();
            foreach (var e in pieces)
                if (e.Geo.ExtensionOf == station.Geo.ProvidesStation && V3.Dist(e.Pos, station.Pos) < e.Geo.ExtensionMaxDistance && (e.Geo.ExtensionStack || seen.Add(e.Geo.Prefab))) n++;
            return station.Geo.StationRangeBuild + n * station.Geo.StationExtraRange;
        }

        /// <summary>A piece that needs a station (Piece.m_craftingStation) can only be built by hand while the builder stands within that station's range. The builder can stand up to the placement distance from the piece.</summary>
        public static List<PlanProblem> StationsNeeded(IList<PlacedPiece> pieces, double placeReach = 8)
        {
            var res = new List<PlanProblem>();
            var stations = pieces.Where(p => p.Geo.ProvidesStation.Length > 0).ToList();
            foreach (var p in pieces.Where(x => x.Planned && x.Geo.StationName.Length > 0))
            {
                var ok = stations.Any(s => s.Geo.ProvidesStation == p.Geo.StationName && Math.Sqrt((s.Pos.X - p.Pos.X) * (s.Pos.X - p.Pos.X) + (s.Pos.Z - p.Pos.Z) * (s.Pos.Z - p.Pos.Z)) < StationBuildRange(s, pieces) + placeReach);
                if (!ok) res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "station-out-of-range", Severity = "warning", Piece = p.Id, Detail = p.Geo.Prefab + " needs a " + p.Geo.StationName + " within its build range of whoever builds it by hand; none is in or near the plan" }));
            }
            return res;
        }

        /// <summary>m_notOnWood: the hammer refuses a campfire, kiln or smelter on a Wood or HardWood piece.</summary>
        public static List<PlanProblem> NotOnWood(IList<PlacedPiece> pieces)
        {
            var res = new List<PlanProblem>();
            foreach (var p in pieces.Where(x => x.Planned && x.Geo.NotOnWood))
            {
                var bottom = p.Boxes().Count == 0 ? p.Pos.Y : p.Boxes().Min(b => b.MinY);
                foreach (var q in pieces)
                {
                    if (q == p || !q.Geo.IsWood) continue;
                    var under = false;
                    foreach (var b in q.Boxes(Layers.InSupport))
                    {
                        if (Math.Abs(b.MaxY - bottom) > 0.25) continue;
                        var cp = b.ClosestPoint(new V3(p.Pos.X, b.C.Y, p.Pos.Z));
                        if (Math.Sqrt((cp.X - p.Pos.X) * (cp.X - p.Pos.X) + (cp.Z - p.Pos.Z) * (cp.Z - p.Pos.Z)) < 0.1) { under = true; break; }
                    }
                    if (under) { res.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "not-on-wood", Severity = "warning", Piece = p.Id, Detail = p.Geo.Prefab + " stands on " + q.Geo.Prefab + " (" + q.Id + ", " + q.Geo.Material + "); the hammer refuses it there" })); break; }
                }
            }
            return res;
        }
    }
}
