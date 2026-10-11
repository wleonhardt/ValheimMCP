// Scene fixtures (hubner.scene/1): a captured piece of the world (prefab geometry, objects, terrain heights, and what the game itself recorded for each piece) that the pure rules can
// be run on without the game. A live bug or a surprise becomes a file plus a test; the support model is checked against the support values the game stored.
// This file is game-free (tests link it); the capture tool that writes scenes is in SceneTools.cs.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    internal sealed class SceneObject
    {
        public string Id = "", Prefab = ""; public V3 Pos; public Quat Rot = Quat.Identity;
        public double? Support, Health;               // what the game recorded in the ZDO (WearNTear owner's cached support); null = none
    }

    internal sealed class SceneData
    {
        public string Name = "", Note = "", Source = ""; public double CenterX, CenterZ, Radius;       // Radius 0 = hand-built, no edge
        public Dictionary<string, PieceGeo> Prefabs = new Dictionary<string, PieceGeo>();
        public List<SceneObject> Objects = new List<SceneObject>();
        public double TerrainX0, TerrainZ0; public int TerrainNx, TerrainNz; public double[] Heights = new double[0];

        /// <summary>The terrain vertex height at whole-metre (x, z); NaN outside the captured grid.</summary>
        public double Vertex(double x, double z)
        {
            var ix = (int)Math.Round(x - TerrainX0); var iz = (int)Math.Round(z - TerrainZ0);
            if (ix < 0 || iz < 0 || ix >= TerrainNx || iz >= TerrainNz) return double.NaN;
            return Heights[iz * TerrainNx + ix];
        }

        /// <summary>The ground as the game's collider has it (triangles between vertices); a very low value outside the grid, so nothing is grounded there.</summary>
        public double Ground(double x, double z)
        {
            var h = TerrainRules.Height(Vertex, x, z);
            return double.IsNaN(h) ? -1000 : h;
        }

        public List<PlacedPiece> Pieces()
        {
            var res = new List<PlacedPiece>();
            foreach (var o in Objects)
            {
                PieceGeo g; if (!Prefabs.TryGetValue(o.Prefab, out g)) continue;
                res.Add(new PlacedPiece { Id = o.Id, Geo = g, Pos = o.Pos, Rot = o.Rot, Planned = false });
            }
            return res;
        }
    }

    internal static class SceneLoader
    {
        public const string Schema = "hubner.scene/1";

        /// <summary>Builds a scene from a parsed JSON tree (Dictionary / List / double / string / bool, as MiniJson gives it).</summary>
        public static SceneData FromTree(object tree)
        {
            var d = tree as Dictionary<string, object>; if (d == null) throw new FormatException("a scene is a JSON object");
            if (Str(d, "schema") != Schema) throw new FormatException("schema is " + Str(d, "schema") + ", expected " + Schema);
            var s = new SceneData { Name = Str(d, "name"), Note = Str(d, "note"), Source = Str(d, "source") };
            var ctr = Arr(d, "center"); if (ctr.Length >= 2) { s.CenterX = ctr[0]; s.CenterZ = ctr[1]; s.Radius = Num(d, "radius"); }
            var t = Obj(d, "terrain");
            if (t != null)
            {
                s.TerrainX0 = Num(t, "x0"); s.TerrainZ0 = Num(t, "z0"); s.TerrainNx = (int)Num(t, "nx"); s.TerrainNz = (int)Num(t, "nz");
                var h = List(t, "h"); s.Heights = h.Select(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (s.Heights.Length != s.TerrainNx * s.TerrainNz) throw new FormatException("terrain.h has " + s.Heights.Length + " heights, nx*nz is " + s.TerrainNx * s.TerrainNz);
            }
            var prefabs = Obj(d, "prefabs");
            if (prefabs != null) foreach (var kv in prefabs)
            {
                var p = kv.Value as Dictionary<string, object>; if (p == null) continue;
                var g = new PieceGeo { Prefab = kv.Key, Material = Str(p, "material"), Supports = Bool(p, "supports") };
                if (p.ContainsKey("wears")) g.WearsWithoutSupport = Bool(p, "wears");
                var com = Vec(p, "com"); g.ComOffset = com;
                foreach (var co in List(p, "colliders"))
                {
                    var c = co as Dictionary<string, object>; if (c == null) continue;
                    var q = Arr(c, "q");
                    g.Colliders.Add(new ColliderBox { Centre = Vec(c, "c"), Half = Vec(c, "h"), Rot = q.Length >= 4 ? new Quat(q[0], q[1], q[2], q[3]) : Quat.Identity, Layer = Str(c, "layer") == "" ? "Default" : Str(c, "layer") });
                }
                s.Prefabs[kv.Key] = g;
            }
            foreach (var oo in List(d, "objects"))
            {
                var o = oo as Dictionary<string, object>; if (o == null) continue;
                var q = Arr(o, "rot");
                var so = new SceneObject { Id = Str(o, "id"), Prefab = Str(o, "prefab"), Pos = Vec(o, "pos"), Rot = q.Length >= 4 ? new Quat(q[0], q[1], q[2], q[3]) : Quat.Identity };
                if (o.ContainsKey("support")) so.Support = Num(o, "support");
                if (o.ContainsKey("health")) so.Health = Num(o, "health");
                s.Objects.Add(so);
            }
            return s;
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is string ? (string)v : ""; }
        static double Num(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is double ? (double)v : 0; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static List<object> List(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is List<object> ? (List<object>)v : new List<object>(); }
        static double[] Arr(Dictionary<string, object> d, string k) { return List(d, k).Select(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); }
        static V3 Vec(Dictionary<string, object> d, string k) { var a = Arr(d, k); return a.Length >= 3 ? new V3(a[0], a[1], a[2]) : new V3(0, 0, 0); }
    }

    /// <summary>The differential test: run our support model on a captured scene and compare it with what the game recorded.</summary>
    internal static class SceneCheck
    {
        public sealed class Diff { public string Id = "", Prefab = "", Kind = ""; public double Ours, Recorded; public override string ToString() { return Kind + " " + Prefab + " " + Id + ": ours " + Math.Round(Ours, 1) + ", game " + Math.Round(Recorded, 1); } }

        /// <param name="relTol">relative tolerance on a recorded support value of a piece that is not on the ground</param>
        /// <param name="edge">pieces within this many metres of the capture's edge are skipped: their supporters may lie outside it</param>
        public static List<Diff> Support(SceneData scene, double relTol = 0.15, double absTol = 5, double edge = 12)
        {
            var pieces = scene.Pieces(); var res = SupportModel.Run(pieces, scene.Ground, null);
            var diffs = new List<Diff>();
            foreach (var p in pieces)
            {
                var o = scene.Objects.First(x => x.Id == p.Id); SupportResult r;
                if (!o.Support.HasValue || !res.TryGetValue(p, out r)) continue;
                if (scene.Radius > 0 && Math.Sqrt((o.Pos.X - scene.CenterX) * (o.Pos.X - scene.CenterX) + (o.Pos.Z - scene.CenterZ) * (o.Pos.Z - scene.CenterZ)) > scene.Radius - edge) continue;
                var recordedHolds = o.Support.Value >= r.Min;                // the game destroys a piece under its minimum, so a standing piece with a recorded value holds
                if (r.Holds != recordedHolds) diffs.Add(new Diff { Id = p.Id, Prefab = p.Geo.Prefab, Kind = r.Holds ? "we say it holds, the game says it does not" : "we say it falls, the game says it holds", Ours = r.Support, Recorded = o.Support.Value });
                else if (Math.Abs(r.Support - o.Support.Value) > Math.Max(absTol, relTol * o.Support.Value)) diffs.Add(new Diff { Id = p.Id, Prefab = p.Geo.Prefab, Kind = "support differs", Ours = r.Support, Recorded = o.Support.Value });
            }
            return diffs;
        }
    }
}
