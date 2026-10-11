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
    }

    internal sealed class ColliderBox
    {
        public V3 Centre, Half;          // in the prefab's own frame
        public Quat Rot = Quat.Identity;
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
    }

    internal sealed class PlacedPiece
    {
        public string Id = "";
        public PieceGeo Geo;
        public V3 Pos;
        public Quat Rot = Quat.Identity;
        public bool Planned = true;                 // false = already in the world (a supporter and an obstacle, never reported on)

        public List<Obb> Boxes()
        {
            var res = new List<Obb>();
            foreach (var c in Geo.Colliders) res.Add(Obb.Make(Pos + Rot.Rotate(c.Centre), c.Half, Rot * c.Rot));
            return res;
        }
        public V3 Com { get { return Pos + Rot.Rotate(Geo.ComOffset); } }
    }

    internal sealed class Material
    {
        public double Max, Min, Vertical, Horizontal;
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
        public bool Grounded;
        public bool Holds { get { return Support >= Min; } }
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
            foreach (var p in pieces)
            {
                var m = Material.Of(p.Geo.Material); mats[p] = m;
                if (m == null) continue;
                plain[p] = p.Boxes(); grown[p] = plain[p].Select(b => b.Grown(ContactGrow)).ToList();
                res[p] = new SupportResult { Max = m.Max, Min = m.Min };
            }
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
                    if (obstacles != null) foreach (var o in obstacles) if (Obb.Overlaps(g, o)) onSolid.Add(p);
                }
                var pa = BoundsOf(grown[p]);
                foreach (var q in res.Keys)
                {
                    if (q == p || !q.Geo.Supports) continue;
                    if (!Apart(pa, aabbs[q])) foreach (var g in grown[p]) foreach (var qb in plain[q]) if (Obb.Overlaps(g, qb)) { touching[p].Add(new KeyValuePair<PlacedPiece, Obb>(q, qb)); }
                }
            }
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
                        var d1 = V3.Dist(com, q.Com) + 0.1; var d2 = V3.Dist(com, q.Pos) + 0.1; var d = d2 < d1 ? d2 : d1;
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

        /// <summary>Does the terrain surface pass through this box? Sampled over the box's footprint.</summary>
        public static bool TouchesGround(Obb b, Func<double, double, double> ground)
        {
            var ex = b.Extent(new V3(1, 0, 0)); var ez = b.Extent(new V3(0, 0, 1));
            for (var i = 0; i <= 4; i++)
                for (var j = 0; j <= 4; j++)
                {
                    var x = b.C.X - ex + 2 * ex * i / 4.0; var z = b.C.Z - ez + 2 * ez * j / 4.0;
                    var g = ground(x, z);
                    if (g >= b.MinY && g <= b.MaxY) return true;
                }
            return false;
        }

        private static double AngleDeg(V3 a, V3 b)
        {
            var la = a.Len; var lb = b.Len; if (la < 1e-9 || lb < 1e-9) return 0;
            return Math.Acos(Math.Max(-1, Math.Min(1, a.Dot(b) / (la * lb)))) * 180 / Math.PI;
        }
    }

    internal sealed class PlanProblem
    {
        public string Rule, Severity, Piece, Detail;
    }

    /// <summary>The checks, each a function of geometry only.</summary>
    internal static class PlanChecks
    {
        public const double WaterLevel = 30.0;
        public const double PenetrationTolerance = 0.05;
        public const double TiltNormalY = 0.8;

        /// <summary>Colliders of two pieces that go into each other by more than the tolerance. An error when either piece cannot clip, else a warning (the game lets building pieces overlap).</summary>
        public static List<PlanProblem> Overlaps(IList<PlacedPiece> pieces)
        {
            var res = new List<PlanProblem>();
            var boxes = pieces.Select(p => p.Boxes()).ToList();
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
                    var hard = pieces[i].Geo.NoClipping || pieces[j].Geo.NoClipping;
                    res.Add(new PlanProblem { Rule = "overlap", Severity = hard ? "error" : "warning", Piece = pieces[i].Id, Detail = pieces[i].Geo.Prefab + " goes " + Math.Round(worst, 2) + " m into " + pieces[j].Geo.Prefab + " (" + pieces[j].Id + ")" + (hard ? ": a piece that cannot clip" : "") });
                }
            return res;
        }

        public static PlanProblem Tilt(PlacedPiece p, double groundNormalY)
        {
            return p.Geo.NotOnTilting && groundNormalY < TiltNormalY ? new PlanProblem { Rule = "tilt", Severity = "error", Piece = p.Id, Detail = p.Geo.Prefab + " cannot stand on a surface this steep (normal y " + Math.Round(groundNormalY, 2) + " < " + TiltNormalY + ")" } : null;
        }

        public static PlanProblem Water(PlacedPiece p, double groundY)
        {
            return p.Geo.NoInWater && groundY < WaterLevel ? new PlanProblem { Rule = "water", Severity = "error", Piece = p.Id, Detail = p.Geo.Prefab + " cannot be built in water (ground " + Math.Round(groundY, 2) + " m, sea level " + WaterLevel + ")" } : null;
        }

        public static PlanProblem Grid(PlacedPiece p, double yawDeg)
        {
            var m = ((yawDeg % 22.5) + 22.5) % 22.5; var off = Math.Min(m, 22.5 - m);
            return off > 0.5 ? new PlanProblem { Rule = "yaw-grid", Severity = "warning", Piece = p.Id, Detail = p.Geo.Prefab + " yaw " + Math.Round(yawDeg, 2) + " is " + Math.Round(off, 2) + " degrees off the 22.5 degree grid the game places on" } : null;
        }

        /// <summary>A fire needs a metre of free ground all round: an error beside a wall, a station or something closer than half a metre, otherwise a warning.</summary>
        public static List<PlanProblem> FireClearance(IList<PlacedPiece> pieces)
        {
            var res = new List<PlanProblem>();
            foreach (var f in pieces.Where(p => p.Planned && p.Geo.IsFire))
            {
                var fb = f.Boxes();
                foreach (var o in pieces)
                {
                    if (o == f || !o.Geo.Occupies) continue;
                    var gap = double.PositiveInfinity;
                    foreach (var x in fb) foreach (var y in o.Boxes()) { var d = Obb.Depth(x, y); gap = Math.Min(gap, d > 0 ? 0 : -d); }
                    if (gap >= 1.0) continue;
                    var hard = o.Geo.IsWall || o.Geo.IsStation || gap < 0.5;
                    res.Add(new PlanProblem { Rule = "fire-clearance", Severity = hard ? "error" : "warning", Piece = f.Id, Detail = f.Geo.Prefab + " has only " + Math.Round(gap, 2) + " m to " + o.Geo.Prefab + " (" + o.Id + "); a fire needs 1 m free all round" });
                }
            }
            return res;
        }
    }
}
