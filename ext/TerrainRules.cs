// Game-free terrain geometry, as the game builds it (Heightmap.RebuildCollisionMesh and WorldToVertex, decompiled). The terrain collider is a grid of vertices at whole metres; every
// cell is split into two triangles along the diagonal from (x+1, z) to (x, z+1): the corner (x, z) is in the first triangle (fx + fz <= 1), the corner (x+1, z+1) in the second.
// A height read by ray is the height on those triangles; a bilinear guess is up to 0.6 m off on a saddle. The slope a character slides on is the slope of the triangle under it.
using System;
using System.Collections.Generic;

namespace HubnerExt
{
    internal static class TerrainRules
    {
        public const double SlideDeg = 38;

        /// <summary>Height inside a cell at (fx, fz) in 0..1 from its corner (x, z), from the four vertex heights.</summary>
        public static double TriHeight(double h00, double h10, double h01, double h11, double fx, double fz)
        {
            if (fx + fz <= 1) return h00 + fx * (h10 - h00) + fz * (h01 - h00);
            return h11 + (1 - fx) * (h01 - h11) + (1 - fz) * (h10 - h11);
        }

        /// <summary>Upward unit normal of the triangle at (fx, fz).</summary>
        public static void TriNormal(double h00, double h10, double h01, double h11, double fx, double fz, out double nx, out double ny, out double nz)
        {
            double a, b;
            if (fx + fz <= 1) { a = h10 - h00; b = h01 - h00; }          // triangle (v00, v01, v10)
            else { a = h11 - h01; b = h11 - h10; }                         // triangle (v10, v01, v11)
            var len = Math.Sqrt(a * a + 1 + b * b);
            nx = -a / len; ny = 1 / len; nz = -b / len;
        }

        /// <summary>Terrain height at a world point from the vertex heights (a function of whole-metre x, z).</summary>
        public static double Height(Func<double, double, double> vertex, double x, double z)
        {
            double x0 = Math.Floor(x), z0 = Math.Floor(z);
            return TriHeight(vertex(x0, z0), vertex(x0 + 1, z0), vertex(x0, z0 + 1), vertex(x0 + 1, z0 + 1), x - x0, z - z0);
        }

        public static double NormalY(Func<double, double, double> vertex, double x, double z)
        {
            double x0 = Math.Floor(x), z0 = Math.Floor(z), nx, ny, nz;
            TriNormal(vertex(x0, z0), vertex(x0 + 1, z0), vertex(x0, z0 + 1), vertex(x0 + 1, z0 + 1), x - x0, z - z0, out nx, out ny, out nz);
            return ny;
        }

        public static double SlopeDeg(double normalY) { return Math.Acos(Math.Max(-1, Math.Min(1, normalY))) * 180 / Math.PI; }
        public static bool Slides(double normalY) { return SlopeDeg(normalY) > SlideDeg; }

        /// <summary>The vertex a point belongs to for the game's "nearest vertex" reads (Heightmap.WorldToVertex: floor(v + 0.5)).</summary>
        public static int NearestVertex(double v) { return (int)Math.Floor(v + 0.5); }
    }

    /// <summary>
    ///     The terrain tools' maths (TerrainComp.LevelTerrain / RaiseTerrain / SmoothTerrain, decompiled), per vertex: height = base + level delta + smooth delta. Level sets the vertex to the
    ///     target, but never further than 8 m from the base height; raise moves a vertex toward pos.y + delta x falloff; smooth pulls toward a height and keeps the smooth delta within 1 m.
    ///     Every operation first folds the smooth delta into the level delta, as the game does.
    /// </summary>
    internal sealed class TerrainGrid
    {
        public const double LevelMax = 8, SmoothMax = 1;
        readonly Func<int, int, double> _base; readonly Dictionary<long, double[]> _d = new Dictionary<long, double[]>();     // key -> [level, smooth]
        public TerrainGrid(Func<int, int, double> baseHeight) { _base = baseHeight; }

        static long Key(int x, int z) { return ((long)(x + 100000)) * 1000000L + (z + 100000); }
        double[] D(int x, int z) { double[] d; if (!_d.TryGetValue(Key(x, z), out d)) _d[Key(x, z)] = d = new double[2]; return d; }
        public double Base(int x, int z) { return _base(x, z); }
        public double Height(int x, int z) { double[] d; return _d.TryGetValue(Key(x, z), out d) ? _base(x, z) + Math.Max(-LevelMax, Math.Min(LevelMax, d[0] + d[1])) : _base(x, z); }          // TerrainComp.ApplyToHeightmap clamps the SUM to base +- 8

        /// <summary>Start from heights that already differ from the base (an edited terrain): the difference becomes the level delta.</summary>
        public void SetCurrent(int x, int z, double height) { var d = D(x, z); d[0] = Math.Max(-LevelMax, Math.Min(LevelMax, height - _base(x, z))); d[1] = 0; }

        public void Level(double cx, double cz, double targetY, double radius, bool square)
        {
            int vx = TerrainRules.NearestVertex(cx), vz = TerrainRules.NearestVertex(cz); var span = (int)Math.Ceiling(radius);
            for (var z = vz - span; z <= vz + span; z++)
                for (var x = vx - span; x <= vx + span; x++)
                {
                    if (!square && Math.Sqrt((x - vx) * (double)(x - vx) + (z - vz) * (double)(z - vz)) > radius) continue;
                    var d = D(x, z); var h = Height(x, z);
                    var delta = targetY - h + d[1]; d[1] = 0; d[0] = Math.Max(-LevelMax, Math.Min(LevelMax, d[0] + delta));
                }
        }

        public void Raise(double cx, double cz, double posY, double radius, double delta, bool square, double power)
        {
            int vx = TerrainRules.NearestVertex(cx), vz = TerrainRules.NearestVertex(cz); var span = (int)Math.Ceiling(radius);
            for (var z = vz - span; z <= vz + span; z++)
                for (var x = vx - span; x <= vx + span; x++)
                {
                    var f = 1.0;
                    if (!square)
                    {
                        var dist = Math.Sqrt((x - vx) * (double)(x - vx) + (z - vz) * (double)(z - vz)); if (dist > radius) continue;
                        if (power > 0) { f = 1 - dist / radius; if (power != 1) f = Math.Pow(f, power); }
                    }
                    var dd = D(x, z); var h = Height(x, z); var change = delta * f; var target = posY + change;
                    if (delta < 0 && target > h) continue;
                    if (delta > 0) { if (target < h) continue; if (target > h + change) target = h + change; }
                    var nd = target - h + dd[1]; dd[1] = 0; dd[0] = Math.Max(-LevelMax, Math.Min(LevelMax, dd[0] + nd));
                }
        }

        public void Smooth(double cx, double cz, double toY, double radius, double power)
        {
            int vx = TerrainRules.NearestVertex(cx), vz = TerrainRules.NearestVertex(cz); var span = (int)Math.Ceiling(radius);
            for (var z = vz - span; z <= vz + span; z++)
                for (var x = vx - span; x <= vx + span; x++)
                {
                    var dist = Math.Sqrt((x - vx) * (double)(x - vx) + (z - vz) * (double)(z - vz)); if (dist > radius) continue;
                    var f = dist / radius; f = power == 3 ? f * f * f : Math.Pow(f, power);
                    var d = D(x, z); var h = Height(x, z); var change = (toY - h) * (1 - f);
                    d[1] = Math.Max(-SmoothMax, Math.Min(SmoothMax, d[1] + change));
                }
        }
    }

    internal sealed class PadReport
    {
        public int Vertices, Unreachable, Raised, Lowered; public double MaxShortfall, FillM3, CutM3; public int Strokes, Spacing;
    }

    internal static class PadPlanner
    {
        /// <summary>
        ///     Can level strokes of this radius bring the vertices x0..x1, z0..z1 to <paramref name="targetY" />? A vertex more than 8 m from its base height cannot get there; volumes are
        ///     the difference to the current heights (1 m x 1 m per vertex); strokes is the fewest on a square lattice that leaves no reachable vertex off target (a simulation, not a formula).
        /// </summary>
        public static PadReport Level(Func<int, int, double> baseHeight, Func<int, int, double> current, int x0, int z0, int x1, int z1, double targetY, double radius, bool square)
        {
            var rep = new PadReport();
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    rep.Vertices++; var b = baseHeight(x, z); var reach = b + Math.Max(-TerrainGrid.LevelMax, Math.Min(TerrainGrid.LevelMax, targetY - b));
                    var shortfall = Math.Abs(targetY - reach); if (shortfall > 1e-9) { rep.Unreachable++; rep.MaxShortfall = Math.Max(rep.MaxShortfall, shortfall); }
                    var c = current(x, z); if (reach > c) { rep.Raised++; rep.FillM3 += reach - c; } else if (reach < c) { rep.Lowered++; rep.CutM3 += c - reach; }          // volumes to the height a stroke can actually reach
                }
            for (var spacing = Math.Max(1, (int)Math.Floor(radius * (square ? 2 : 1.4))); spacing >= 1; spacing--)
            {
                var g = new TerrainGrid(baseHeight); var n = 0;
                for (var z = z0; z <= z1 + spacing - 1; z += spacing)
                    for (var x = x0; x <= x1 + spacing - 1; x += spacing) { g.Level(Math.Min(x, x1), Math.Min(z, z1), targetY, radius, square); n++; }
                var ok = true;
                for (var z = z0; z <= z1 && ok; z++) for (var x = x0; x <= x1; x++) { var b = baseHeight(x, z); var reach = b + Math.Max(-TerrainGrid.LevelMax, Math.Min(TerrainGrid.LevelMax, targetY - b)); if (Math.Abs(g.Height(x, z) - reach) > 1e-6) { ok = false; break; } }
                if (ok) { rep.Strokes = n; rep.Spacing = spacing; break; }
            }
            return rep;
        }
    }
}
