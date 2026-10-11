// Game-free terrain geometry, as the game builds it (Heightmap.RebuildCollisionMesh and WorldToVertex, decompiled). The terrain collider is a grid of vertices at whole metres; every
// cell is split into two triangles along the diagonal from (x+1, z) to (x, z+1): the corner (x, z) is in the first triangle (fx + fz <= 1), the corner (x+1, z+1) in the second.
// A height read by ray is the height on those triangles; a bilinear guess is up to 0.6 m off on a saddle. The slope a character slides on is the slope of the triangle under it.
using System;

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
}
