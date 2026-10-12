// level_check: can the terrain tools bring a pad to a height, and what would it cost? The strokes are simulated with the game's own terrain maths (TerrainGrid in TerrainRules.cs, ported
// from TerrainComp), the stroke shapes are read from the prefabs of the tools that carry a TerrainOp. Read-only; works on the server (the world generator gives the base height).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class TerrainTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("level_check", "Can the terrain tools bring a pad to a height, and what would it cost? Pad x0,z0,x1,z1 (or x,z,half) and targetY (max 4096 vertices). Per vertex the base height is the world generator's and the current height the loaded terrain's (else the base). A level stroke sets a vertex to the target but never further than 8 m from its base height, so vertices beyond that are reported as unreachable with the shortfall. Also: how many vertices have to be raised or lowered, the fill and cut in cubic metres, the step at the pad's edge (a cliff the walker slides on), the sea (30; 30.4 is dry), and, for every terrain tool prefab the game has (level, raise, smooth with their radii), the fewest strokes on a lattice that bring the whole reachable pad to the target (simulated). Read-only. The base used for the 8 m limit is the generator's own, the game blends the four corner biomes of a zone, so near a biome border the real limit can differ by metres.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"half\":{\"type\":\"number\"},\"targetY\":{\"type\":\"number\"}},\"required\":[\"targetY\"]}", LevelCheck);
        }

        static string B(bool v) { return v ? "true" : "false"; }

        sealed class Op { public string Prefab = ""; public bool Level, Raise, Smooth, Square; public double LevelRadius, RaiseRadius, RaiseDelta, RaisePower, SmoothRadius, SmoothPower; }
        static List<Op> _ops;

        static List<Op> Ops()
        {
            if (_ops != null || ZNetScene.instance == null) return _ops ?? new List<Op>();
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var dict = f != null ? f.GetValue(ZNetScene.instance) as Dictionary<int, GameObject> : null; if (dict == null) return new List<Op>();
            var list = new List<Op>(); var seen = new HashSet<string>();
            // the terrain tools' prefabs: current games keep them in ObjectDB.m_terrainOps (they are not in ZNetScene's table), older ones in the scene table
            var sources = new List<GameObject>(dict.Values);
            if (ObjectDB.instance != null) foreach (var t in ObjectDB.instance.m_terrainOps) if (t != null) sources.Add(t.gameObject);
            foreach (var go in sources)
            {
                if (go == null || !seen.Add(go.name)) continue; var top = go.GetComponent<TerrainOp>(); if (top == null || top.m_settings == null) continue; var s = top.m_settings;
                if (!s.m_level && !s.m_raise && !s.m_smooth) continue;
                list.Add(new Op { Prefab = go.name, Level = s.m_level, Raise = s.m_raise, Smooth = s.m_smooth, Square = s.m_square, LevelRadius = s.m_levelRadius, RaiseRadius = s.m_raiseRadius, RaiseDelta = s.m_raiseDelta, RaisePower = s.m_raisePower, SmoothRadius = s.m_smoothRadius, SmoothPower = s.m_smoothPower });
            }
            _ops = list.OrderBy(o => o.Prefab, StringComparer.Ordinal).ToList(); return _ops;
        }

        static ToolOutput LevelCheck(Dictionary<string, object> a)
        {
            if (WorldGenerator.instance == null) return ToolOutput.Err("no world generator (not in a world)");
            if (!U.Has(a, "targetY")) return ToolOutput.Err("targetY required");
            var target = U.D(a, "targetY", 0); int x0, z0, x1, z1;
            if (U.Has(a, "x0")) { x0 = (int)Math.Floor(U.D(a, "x0", 0)); z0 = (int)Math.Floor(U.D(a, "z0", 0)); x1 = (int)Math.Ceiling(U.D(a, "x1", 0)); z1 = (int)Math.Ceiling(U.D(a, "z1", 0)); }
            else if (U.Has(a, "x")) { var h = U.D(a, "half", 8); x0 = (int)Math.Floor(U.D(a, "x", 0) - h); x1 = (int)Math.Ceiling(U.D(a, "x", 0) + h); z0 = (int)Math.Floor(U.D(a, "z", 0) - h); z1 = (int)Math.Ceiling(U.D(a, "z", 0) + h); }
            else return ToolOutput.Err("give x,z[,half] or x0,z0,x1,z1");
            if (x1 < x0 || z1 < z0) return ToolOutput.Err("the pad is empty");
            if ((x1 - x0 + 1) * (z1 - z0 + 1) > 4096) return ToolOutput.Err("the pad has more than 4096 vertices: split it");
            if (!WorldBounds.InWorld(x0, 0, z0) || !WorldBounds.InWorld(x1, 0, z1) || double.IsNaN(target)) return ToolOutput.Err("the pad must be finite and inside the world");
            var wg = WorldGenerator.instance; var zs = ZoneSystem.instance; var cache = new Dictionary<long, double>(); int loaded = 0, generated = 0;
            Func<int, int, double> baseH = (x, z) => wg.GetHeight(x, z);
            Func<int, int, double> current = (x, z) =>
            {
                var key = (long)(x + 20000) * 100000L + (z + 20000); double h; if (cache.TryGetValue(key, out h)) return h;
                float gh;
                if (zs != null && zs.GetGroundHeight(new Vector3(x, 0f, z), out gh)) { h = gh; loaded++; } else { h = baseH(x, z); generated++; }
                cache[key] = h; return h;
            };
            var ops = Ops(); var rows = new List<string>(); PadReport first = null;
            foreach (var op in ops.Where(o => o.Level))
            {
                var rep = PadPlanner.Level(baseH, current, x0, z0, x1, z1, target, op.LevelRadius, op.Square);
                if (first == null) first = rep;
                rows.Add("{\"tool\":" + U.S(op.Prefab) + ",\"radius\":" + U.N(op.LevelRadius) + ",\"square\":" + B(op.Square) + ",\"alsoRaises\":" + B(op.Raise) + ",\"alsoSmooths\":" + B(op.Smooth) + ",\"strokes\":" + rep.Strokes + "}");
            }
            if (first == null) first = PadPlanner.Level(baseH, current, x0, z0, x1, z1, target, 3, true);
            double edge = 0;                                                                              // the biggest step between the pad and the ground one metre outside it
            for (var x = x0 - 1; x <= x1 + 1; x++) { edge = Math.Max(edge, Math.Abs(target - current(x, z0 - 1))); edge = Math.Max(edge, Math.Abs(target - current(x, z1 + 1))); }
            for (var z = z0 - 1; z <= z1 + 1; z++) { edge = Math.Max(edge, Math.Abs(target - current(x0 - 1, z))); edge = Math.Max(edge, Math.Abs(target - current(x1 + 1, z))); }
            var notes = new List<string>();
            if (target < 30.4) notes.Add("the target is under 30.4 m: the sea washes it (sea level 30)");
            if (first.Unreachable > 0) notes.Add(first.Unreachable + " vertices are more than 8 m from their base height: a level stroke cannot reach them (shortfall up to " + Math.Round(first.MaxShortfall, 1) + " m)");
            if (edge > 0.8) notes.Add("the pad edge is a " + Math.Round(edge, 1) + " m step: a character slides on a face steeper than 0.78 m per metre (38 degrees); grade the apron");
            return U.Json("{\"pad\":[" + x0 + "," + z0 + "," + x1 + "," + z1 + "],\"targetY\":" + U.N(target) + ",\"vertices\":" + first.Vertices + ",\"unreachable\":" + first.Unreachable + ",\"maxShortfall\":" + U.N(Math.Round(first.MaxShortfall, 2)) + ",\"raised\":" + first.Raised + ",\"lowered\":" + first.Lowered + ",\"fillM3\":" + U.N(Math.Round(first.FillM3, 1)) + ",\"cutM3\":" + U.N(Math.Round(first.CutM3, 1)) + ",\"edgeStep\":" + U.N(Math.Round(edge, 2)) + ",\"heights\":\"" + loaded + " vertices from the loaded terrain, " + generated + " from the world generator\",\"notes\":[" + string.Join(",", notes.Select(U.S).ToArray()) + "],\"levelTools\":[" + string.Join(",", rows.ToArray()) + "],\"otherTools\":[" + string.Join(",", ops.Where(o => !o.Level).Select(o => "{\"tool\":" + U.S(o.Prefab) + ",\"raise\":" + B(o.Raise) + ",\"raiseRadius\":" + U.N(o.RaiseRadius) + ",\"raiseDelta\":" + U.N(o.RaiseDelta) + ",\"smooth\":" + B(o.Smooth) + ",\"smoothRadius\":" + U.N(o.SmoothRadius) + "}").ToArray()) + "]}");
        }
    }
}
