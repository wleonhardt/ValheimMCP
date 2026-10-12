// The terrain compiler's saved data ("TCData" in a _TerrainCompiler ZDO), game-free: TerrainComp.Save / Load, decompiled. The bytes here are the DECOMPRESSED package (the game
// compresses it with Utils.Compress). Used to check what an edit changed and to build the "no edits" state an undo needs for a zone that had no compiler before.
using System;
using System.Collections.Generic;
using System.IO;

namespace HubnerExt
{
    internal sealed class TcData
    {
        public int Version = 1, Operations;
        public float Lx, Ly, Lz, LastRadius;            // the last operation's point and radius
        public bool[] ModHeight = new bool[0]; public float[] Level = new float[0], Smooth = new float[0];
        public bool[] ModPaint = new bool[0]; public float[][] Paint = new float[0][];       // r,g,b,a per modified node

        /// <summary>A zone without edits: (width + 1)^2 nodes, nothing modified (width 64 in the game).</summary>
        public static TcData Empty(int width = 64)
        {
            var n = (width + 1) * (width + 1);
            return new TcData { ModHeight = new bool[n], Level = new float[n], Smooth = new float[n], ModPaint = new bool[n], Paint = new float[n][] };
        }

        public static TcData Decode(byte[] raw)
        {
            using (var ms = new MemoryStream(raw)) using (var r = new BinaryReader(ms))
            {
                var d = new TcData { Version = r.ReadInt32(), Operations = r.ReadInt32(), Lx = r.ReadSingle(), Ly = r.ReadSingle(), Lz = r.ReadSingle(), LastRadius = r.ReadSingle() };
                var n = r.ReadInt32(); if (n < 0 || n > 1 << 20) throw new InvalidDataException("terrain data: bad height node count " + n);
                d.ModHeight = new bool[n]; d.Level = new float[n]; d.Smooth = new float[n];
                for (var i = 0; i < n; i++) { d.ModHeight[i] = r.ReadBoolean(); if (d.ModHeight[i]) { d.Level[i] = r.ReadSingle(); d.Smooth[i] = r.ReadSingle(); } }
                var m = r.ReadInt32(); if (m < 0 || m > 1 << 20) throw new InvalidDataException("terrain data: bad paint node count " + m);
                d.ModPaint = new bool[m]; d.Paint = new float[m][];
                for (var j = 0; j < m; j++) { d.ModPaint[j] = r.ReadBoolean(); if (d.ModPaint[j]) d.Paint[j] = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() }; }
                return d;
            }
        }

        public byte[] Encode()
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write(Version); w.Write(Operations); w.Write(Lx); w.Write(Ly); w.Write(Lz); w.Write(LastRadius);
                w.Write(ModHeight.Length);
                for (var i = 0; i < ModHeight.Length; i++) { w.Write(ModHeight[i]); if (ModHeight[i]) { w.Write(Level[i]); w.Write(Smooth[i]); } }
                w.Write(ModPaint.Length);
                for (var j = 0; j < ModPaint.Length; j++) { w.Write(ModPaint[j]); if (ModPaint[j]) { var p = Paint[j]; w.Write(p[0]); w.Write(p[1]); w.Write(p[2]); w.Write(p[3]); } }
                w.Flush(); return ms.ToArray();
            }
        }

        public int HeightNodes { get { var n = 0; foreach (var b in ModHeight) if (b) n++; return n; } }
        public int PaintNodes { get { var n = 0; foreach (var b in ModPaint) if (b) n++; return n; } }

        /// <summary>Height nodes whose edit differs from <paramref name="other" /> (level + smooth, to a millimetre), and paint nodes whose colour differs.</summary>
        public void Diff(TcData other, out int heightChanged, out int paintChanged, out double maxHeightDelta)
        {
            heightChanged = 0; paintChanged = 0; maxHeightDelta = 0;
            var n = Math.Max(ModHeight.Length, other.ModHeight.Length);
            for (var i = 0; i < n; i++)
            {
                double a = i < ModHeight.Length && ModHeight[i] ? Level[i] + Smooth[i] : 0, b = i < other.ModHeight.Length && other.ModHeight[i] ? other.Level[i] + other.Smooth[i] : 0;
                if (Math.Abs(a - b) > 0.001) { heightChanged++; maxHeightDelta = Math.Max(maxHeightDelta, Math.Abs(a - b)); }
            }
            var m = Math.Max(ModPaint.Length, other.ModPaint.Length);
            for (var j = 0; j < m; j++)
            {
                var pa = j < ModPaint.Length && ModPaint[j] ? Paint[j] : null; var pb = j < other.ModPaint.Length && other.ModPaint[j] ? other.Paint[j] : null;
                if (pa == null && pb == null) continue;
                if (pa == null || pb == null) { paintChanged++; continue; }
                for (var k = 0; k < 4; k++) if (Math.Abs(pa[k] - pb[k]) > 0.002) { paintChanged++; break; }
            }
        }
    }

    /// <summary>The points a paint or level tool strokes: a grid over a disc or a box, spaced by the tool's radius (a stroke covers a disc of that radius), never more than a cap.</summary>
    internal static class StrokePlan
    {
        public static List<double[]> Disc(double cx, double cz, double radius, double spacing, int cap)
        {
            var res = new List<double[]>(); spacing = Math.Max(0.5, spacing);
            for (var dz = -radius; dz <= radius + 1e-9; dz += spacing)
                for (var dx = -radius; dx <= radius + 1e-9; dx += spacing)
                    if (dx * dx + dz * dz <= radius * radius + 1e-9) { if (res.Count >= cap) return res; res.Add(new[] { cx + dx, cz + dz }); }
            return res;
        }

        public static List<double[]> Box(double x0, double z0, double x1, double z1, double spacing, int cap)
        {
            var res = new List<double[]>(); spacing = Math.Max(0.5, spacing);
            for (var z = Math.Min(z0, z1); z <= Math.Max(z0, z1) + 1e-9; z += spacing)
                for (var x = Math.Min(x0, x1); x <= Math.Max(x0, x1) + 1e-9; x += spacing) { if (res.Count >= cap) return res; res.Add(new[] { x, z }); }
            return res;
        }

        /// <summary>Terrain zones (64 m, centred on multiples of 64) a rectangle plus a margin can reach.</summary>
        public static List<int[]> Zones(double x0, double z0, double x1, double z1, double margin)
        {
            var res = new List<int[]>();
            int zx0 = ActiveArea.Zone(Math.Min(x0, x1) - margin), zx1 = ActiveArea.Zone(Math.Max(x0, x1) + margin), zz0 = ActiveArea.Zone(Math.Min(z0, z1) - margin), zz1 = ActiveArea.Zone(Math.Max(z0, z1) + margin);
            for (var zx = zx0; zx <= zx1; zx++) for (var zz = zz0; zz <= zz1; zz++) res.Add(new[] { zx, zz });
            return res;
        }
    }

    /// <summary>The one console line terrain_wec may run: built only from validated numbers and a fixed paint list, so nothing a caller types can reach the console (decision 0006, option C).</summary>
    internal sealed class WecLine
    {
        public const double MaxRadius = 40;
        public static readonly string[] Paints = { "cultivated", "dirt", "grass", "grass_dark", "patches", "paved", "paved_dark", "paved_dirt", "paved_moss" };

        public string Error, Line; public double HalfX, HalfZ; public bool HeightOp;

        static string F(double v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
        static bool Fin(double? v) { return v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value); }

        /// <param name="smoothMetres">the outer band of the shape that fades from no change to full change; the command takes a fraction of the shape</param>
        public static WecLine Build(double cx, double cz, double? radius, double? width, double? depth, double? level, double? raise, string paint, double smoothMetres)
        {
            var r = new WecLine();
            if (!WorldBounds.InWorld(cx, 0, cz)) { r.Error = "x and z must be finite and inside the world"; return r; }
            var circle = radius.HasValue; var rect = width.HasValue || depth.HasValue;
            if (circle == rect) { r.Error = "give radius (circle) or width and depth (rectangle)"; return r; }
            if (circle)
            {
                if (!Fin(radius) || radius.Value <= 0 || radius.Value > MaxRadius) { r.Error = "radius must be between 0 and " + MaxRadius + " m"; return r; }
                r.HalfX = r.HalfZ = radius.Value;
            }
            else
            {
                if (!Fin(width) || !Fin(depth) || width.Value <= 0 || depth.Value <= 0 || width.Value > 2 * MaxRadius || depth.Value > 2 * MaxRadius) { r.Error = "width and depth must be between 0 and " + 2 * MaxRadius + " m"; return r; }
                r.HalfX = width.Value / 2; r.HalfZ = depth.Value / 2;
            }
            if (!WorldBounds.Finite(smoothMetres) || smoothMetres < 0 || smoothMetres > 20) { r.Error = "smooth must be between 0 and 20 m"; return r; }
            var parts = new System.Collections.Generic.List<string>();
            if (level.HasValue)
            {
                if (!Fin(level) || level.Value < 30.4 || level.Value > 2000) { r.Error = "level must be between 30.4 m (the sea washes lower ground) and 2000 m"; return r; }
                parts.Add("level=" + F(level.Value)); r.HeightOp = true;
            }
            if (raise.HasValue)
            {
                if (!Fin(raise) || Math.Abs(raise.Value) > 50) { r.Error = "raise must be within 50 m either way"; return r; }
                if (r.HeightOp) { r.Error = "give level or raise, not both"; return r; }
                parts.Add("raise=" + F(raise.Value)); r.HeightOp = true;
            }
            if (!string.IsNullOrEmpty(paint))
            {
                if (Array.IndexOf(Paints, paint) < 0) { r.Error = "paint must be one of " + string.Join(", ", Paints); return r; }
                parts.Add("paint=" + paint);
            }
            if (parts.Count == 0) { r.Error = "give level, raise or paint"; return r; }
            if (smoothMetres > 0 && r.HeightOp) parts.Add("smooth=" + F(Math.Min(1.0, smoothMetres / Math.Min(r.HalfX, r.HalfZ))));
            r.Line = "terrain from=" + F(cx) + "," + F(cz) + (circle ? " circle=" + F(r.HalfX) : " rect=" + F(r.HalfX) + "," + F(r.HalfZ) + " angle=0") + " " + string.Join(" ", parts.ToArray());
            return r;
        }
    }
}
