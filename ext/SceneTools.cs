// scene_capture / scene_get: write a piece of the world as a hubner.scene/1 fixture (see Scene.cs). Works on the dedicated server (ZDO table and prefabs) and on the client.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class SceneTools
    {
        const int MaxObjects = 4000;
        const double NoRecord = -12345;

        public static void Register(ToolRegistry r)
        {
            r.Add("scene_capture", "Capture a piece of the world as a test fixture (hubner.scene/1): x,z,radius (default 24, max 64) and a name (letters, digits, _ - .). Writes hubner-ext/scenes/<name>.scene.json with every object that has colliders (prefab geometry once per prefab, position, rotation), the support value and health the GAME recorded for each piece (support is session-only data, ZDOVars.s_sessionHashes: it is not in the world file and exists only for pieces whose owner has run its support update since the game started, so capture while a player or a virtual peer has the area loaded; withRecordedSupport says how many pieces had one), and the terrain vertex heights (the loaded terrain where the zone is loaded, else the world generator: terrain.source says which). A live bug becomes a file; python/live/capture_scene.py saves it into the unit-test fixtures, where the support model is checked against the recorded values. note = what went wrong. Read-only for the world.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"name\":{\"type\":\"string\"},\"note\":{\"type\":\"string\"}},\"required\":[\"x\",\"z\",\"name\"]}", Capture);
            r.Add("scene_get", "The text of a captured scene (hubner-ext/scenes/<name>.scene.json), up to 8 MB. Read-only.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}", Get);
        }

        static string Dir { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext", "scenes"); } }
        static string F(double v, int digits = 4) { return Math.Round(v, digits).ToString("0.####", CultureInfo.InvariantCulture); }
        static string V(V3 v) { return "[" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + "]"; }

        static ToolOutput Capture(Dictionary<string, object> a)
        {
            if (!Zdos.Ready || ZNetScene.instance == null) return ToolOutput.Err("world not ready");
            var name = McpJson.GetStr(a, "name"); if (!SafeNames.IsFileStem(name)) return ToolOutput.Err("name must be 1-64 characters of letters, digits, _ - . (it becomes a file name)");
            double cx = U.D(a, "x", double.NaN), cz = U.D(a, "z", double.NaN), rad = Math.Max(2, Math.Min(64, U.D(a, "radius", 24)));
            if (!WorldBounds.InWorld(cx, 0, cz)) return ToolOutput.Err("x and z must be finite and inside the world");
            var prefabs = new Dictionary<string, PieceGeo>(); var objects = new List<string>(); var recorded = 0; var truncated = false;
            foreach (var z in Zdos.InBox((float)(cx - rad), (float)(cz - rad), (float)(cx + rad), (float)(cz + rad)))
            {
                var pf = Zdos.NameOf(z.GetPrefab()); if (pf.StartsWith("_", StringComparison.Ordinal)) continue;
                PieceGeo g; if (!prefabs.TryGetValue(pf, out g)) { g = PlanCheck.GeoOf(pf); if (g == null || g.Colliders.Count == 0) { prefabs[pf] = null; continue; } prefabs[pf] = g; }
                if (g == null) continue;
                if (objects.Count >= MaxObjects) { truncated = true; break; }
                var p = z.GetPosition(); var q = z.GetRotation();
                var sb = new StringBuilder("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(pf) + ",\"pos\":[" + F(p.x) + "," + F(p.y) + "," + F(p.z) + "],\"rot\":[" + F(q.x, 5) + "," + F(q.y, 5) + "," + F(q.z, 5) + "," + F(q.w, 5) + "]");
                if (g.Material.Length > 0)
                {
                    var sup = z.GetFloat("support", (float)NoRecord); if (sup > NoRecord + 1) { sb.Append(",\"support\":" + F(sup, 2)); recorded++; }
                    var hp = z.GetFloat("health", (float)NoRecord); if (hp > NoRecord + 1) sb.Append(",\"health\":" + F(hp, 1));
                }
                objects.Add(sb.Append('}').ToString());
            }
            var x0 = (int)Math.Floor(cx - rad) - 1; var z0 = (int)Math.Floor(cz - rad) - 1; var nx = (int)(2 * rad) + 4; var nz = nx;
            int fromWorld = 0, fromGen = 0; var heights = new List<string>(nx * nz); var wg = WorldGenerator.instance; var zs = ZoneSystem.instance;
            for (var iz = 0; iz < nz; iz++)
                for (var ix = 0; ix < nx; ix++)
                {
                    float h;
                    if (zs != null && zs.GetGroundHeight(new Vector3(x0 + ix, 0f, z0 + iz), out h)) fromWorld++;
                    else { h = wg != null ? wg.GetHeight(x0 + ix, z0 + iz) : 0f; fromGen++; }
                    heights.Add(F(h, 3));
                }
            var json = new StringBuilder("{\"schema\":" + U.S(SceneLoader.Schema) + ",\"name\":" + U.S(name) + ",\"source\":\"captured\",\"note\":" + U.S(McpJson.GetStr(a, "note") ?? "") + ",\"captured\":" + U.S(DateTime.UtcNow.ToString("o")) + ",\"build\":" + U.S(Cfg.BuildStamp(typeof(SceneTools).Assembly)) + ",\"center\":[" + F(cx) + "," + F(cz) + "],\"radius\":" + F(rad) + ",\"truncated\":" + (truncated ? "true" : "false") +
                ",\"terrain\":{\"x0\":" + x0 + ",\"z0\":" + z0 + ",\"nx\":" + nx + ",\"nz\":" + nz + ",\"source\":" + U.S(fromWorld + " vertices from the loaded terrain, " + fromGen + " from the world generator") + ",\"h\":[" + string.Join(",", heights.ToArray()) + "]},\"prefabs\":{");
            json.Append(string.Join(",", prefabs.Where(kv => kv.Value != null && objects.Any(o => o.Contains("\"prefab\":" + U.S(kv.Key) + ","))).Select(kv =>
                U.S(kv.Key) + ":{\"material\":" + U.S(kv.Value.Material) + ",\"supports\":" + (kv.Value.Supports ? "true" : "false") + ",\"com\":" + V(kv.Value.ComOffset) + ",\"colliders\":[" + string.Join(",", kv.Value.Colliders.Select(c => "{\"c\":" + V(c.Centre) + ",\"h\":" + V(c.Half) + ",\"q\":[" + F(c.Rot.X, 5) + "," + F(c.Rot.Y, 5) + "," + F(c.Rot.Z, 5) + "," + F(c.Rot.W, 5) + "],\"layer\":" + U.S(c.Layer) + "}").ToArray()) + "]}").ToArray()));
            json.Append("},\"objects\":[" + string.Join(",", objects.ToArray()) + "]}");
            var text = json.ToString();
            try { Directory.CreateDirectory(Dir); AtomicFile.WriteAllText(Path.Combine(Dir, name + ".scene.json"), text); }
            catch (Exception ex) { U.Once("scene_capture.write", ex); return ToolOutput.Err("could not write the scene: " + ex.Message); }
            return U.Json("{\"name\":" + U.S(name) + ",\"file\":" + U.S("hubner-ext/scenes/" + name + ".scene.json") + ",\"objects\":" + objects.Count + ",\"withRecordedSupport\":" + recorded + ",\"prefabs\":" + prefabs.Count(kv => kv.Value != null) + ",\"terrainVertices\":" + nx * nz + ",\"fromLoadedTerrain\":" + fromWorld + ",\"truncated\":" + (truncated ? "true" : "false") + ",\"bytes\":" + text.Length + "}");
        }

        static ToolOutput Get(Dictionary<string, object> a)
        {
            var name = McpJson.GetStr(a, "name"); if (!SafeNames.IsFileStem(name)) return ToolOutput.Err("invalid name");
            var f = Path.Combine(Dir, name + ".scene.json"); if (!File.Exists(f)) return ToolOutput.Err("no scene named " + name);
            var fi = new FileInfo(f); if (fi.Length > 8L * 1024 * 1024) return ToolOutput.Err("scene is larger than 8 MB");
            return new ToolOutput { Text = File.ReadAllText(f) };
        }
    }
}
