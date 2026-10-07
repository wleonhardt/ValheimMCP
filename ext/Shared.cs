// Shared tools: ZDO-record based, work on the game client AND the dedicated server (no physics, no renderers).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    public sealed class Ext : IMcpExtension
    {
        public string Name { get { return "hubner-ext"; } }
        /// <summary>From the assembly version (ext/HubnerExt.csproj &lt;Version&gt;): one place to bump.</summary>
        public static readonly string Version = typeof(Ext).Assembly.GetName().Version.ToString(3);
        public void Unload()
        {
            Audit.Uninstall();
#if !SERVER
            Control.Uninstall();
#endif
        }

        public void Register(ToolRegistry r)
        {
            SharedTools.Register(r);
            Plans.Register(r);
            Audit.Register(r);
            InsightShared.Register(r);
            VirtualPeers.Register(r);
            Journal.Load();
            ClientTools.Register(r);                                    // on the server only the whitelisted physics/read tools register (see Reg.cs)
#if !SERVER
            Env.Register(r);
#endif
        }
    }

    /// <summary>Lightweight view of one ZDO (works without any instantiated GameObject).</summary>
    internal sealed class Rec
    {
        public ZDO Z; public string Id; public string Prefab; public Vector3 Pos; public float Yaw; public Vector3 Rot; public Vector3 Scale; public long Creator; public string Text;
    }

    internal static class Zdos
    {
        private static FieldInfo _bySector;
        private static Dictionary<int, string> _names; private static bool _namesComplete;
        private static readonly int TerrainCompHash = "_TerrainCompiler".GetStableHashCode();

        public static string NameOf(int hash)
        {
            if (!_namesComplete)                                                          // a call before ZNetScene exists must not freeze a one-entry table forever
            {
                _names = new Dictionary<int, string>();
                if (ZNetScene.instance != null) { foreach (var n in ZNetScene.instance.GetPrefabNames()) _names[n.GetStableHashCode()] = n; _namesComplete = _names.Count > 0; }
                _names[TerrainCompHash] = "_TerrainCompiler";
            }
            return _names.TryGetValue(hash, out var s) ? s : "#" + hash;
        }

        public static bool Ready { get { return ZDOMan.instance != null && ZoneSystem.instance != null; } }

        /// <summary>All ZDOs this process knows whose position is inside the box (the server knows every zone, a client only the area it has loaded).</summary>
        public static List<ZDO> InBox(float x0, float z0, float x1, float z1)
        {
            var res = new List<ZDO>();
            if (!Ready) return res;
            if (_bySector == null) _bySector = typeof(ZDOMan).GetField("m_objectsBySector", BindingFlags.NonPublic | BindingFlags.Instance);
            var arr = _bySector.GetValue(ZDOMan.instance) as List<ZDO>[];
            if (arr == null) return res;
            var a = ZoneSystem.GetZone(new Vector3(Math.Min(x0, x1), 0, Math.Min(z0, z1)));
            var b = ZoneSystem.GetZone(new Vector3(Math.Max(x0, x1), 0, Math.Max(z0, z1)));
            float lx = Math.Min(x0, x1), hx = Math.Max(x0, x1), lz = Math.Min(z0, z1), hz = Math.Max(z0, z1);
            for (int sx = a.x; sx <= b.x; sx++)
                for (int sy = a.y; sy <= b.y; sy++)
                {
                    var idx = ZoneSystem.SectorToIndex(sx, sy).Sector;
                    if (idx >= arr.Length) continue;
                    var list = arr[idx];
                    if (list == null) continue;
                    foreach (var z in list)
                    {
                        var p = z.GetPosition();
                        if (p.x >= lx && p.x <= hx && p.z >= lz && p.z <= hz) res.Add(z);
                    }
                }
            return res;
        }

        public static Rec ToRec(ZDO z)
        {
            var q = z.GetRotation();
            var e = q.eulerAngles;
            return new Rec
            {
                Z = z, Id = U.Id(z), Prefab = NameOf(z.GetPrefab()), Pos = z.GetPosition(), Rot = e, Yaw = e.y,
                Scale = z.GetVec3("scale", Vector3.one), Creator = z.GetLong("creator", 0L), Text = z.GetString("text", "")
            };
        }

        public static string Json(Rec r)
        {
            var sb = new StringBuilder("{\"id\":" + U.S(r.Id) + ",\"prefab\":" + U.S(r.Prefab) + ",\"pos\":" + U.V(r.Pos) + ",\"rot\":" + U.V(r.Rot) + ",\"creator\":" + r.Creator.ToString(CultureInfo.InvariantCulture));
            if (r.Scale != Vector3.one) sb.Append(",\"scale\":" + U.V(r.Scale));
            if (!string.IsNullOrEmpty(r.Text)) sb.Append(",\"text\":" + U.S(r.Text));
            return sb.Append('}').ToString();
        }

        public static float AngDiff(float a, float b) { var d = Mathf.Abs(Mathf.DeltaAngle(a, b)); return d; }
    }

    internal static class SharedTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("ext_info", "Extension version, role (client or server) and whether writes are enabled.", "{\"type\":\"object\",\"properties\":{}}", Info);
            r.Add("objects",
                "List world objects near a point or in a box from the ZDO table (every object this process knows; on the dedicated server that is the whole world, on the client the loaded area). Filters: prefab substring(s) comma separated, built=true (creator!=0) / false, limit (default 300, max 3000), offset. Fast: indexed by zone.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"y0\":{\"type\":\"number\"},\"y1\":{\"type\":\"number\"},\"prefab\":{\"type\":\"string\"},\"built\":{\"type\":\"boolean\"},\"limit\":{\"type\":\"number\"},\"offset\":{\"type\":\"number\"}}}", Objects);
            r.Add("portals",
                "Every portal in the world from the game's portal list (the generic objects query does not see portals): id, prefab, position, tag, and the tag's partner count so unpaired or ambiguous tags stand out. Use before spawning a tagged portal.",
                "{\"type\":\"object\",\"properties\":{}}", Portals);
            r.Add("terraform_map",
                "Where has the terrain been edited? Decodes every zone's terrain-compiler data (no heightmap needed). Returns per-zone edit counts, a coarse raster of the largest height change per cell (metres, + = raised), and clustered edited regions with bounding boxes. Use it to know what you must not touch.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"cell\":{\"type\":\"number\",\"description\":\"raster cell size in metres (default 4)\"},\"minDelta\":{\"type\":\"number\",\"description\":\"ignore changes smaller than this (default 0.15)\"},\"raster\":{\"type\":\"boolean\",\"description\":\"include the raster (default true)\"}},\"required\":[\"x0\",\"z0\",\"x1\",\"z1\"]}", Terraform.Map);
            r.Add("verify_plan",
                "Compare a build plan with the world. items:[{prefab,x,y,z,yaw,text?}] (positions are prefab pivots, as spawned). Reports ok / moved / missing / duplicate / wrong-text counts with examples, and 'extra' objects of plan prefabs inside the plan's box that the plan does not contain. tol (default 0.06 m) is the position match, yawTol 2 degrees.",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"alsoPrefabs\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"tol\":{\"type\":\"number\"},\"matchRadius\":{\"type\":\"number\"},\"examples\":{\"type\":\"number\"},\"margin\":{\"type\":\"number\"}},\"required\":[\"items\"]}", Verify.Plan);
            r.Add("snapshot", "Record every object in a box under a name (kept in memory and on disk) so a later snapshot_diff can show exactly what changed.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"}},\"required\":[\"name\",\"x0\",\"z0\",\"x1\",\"z1\"]}", Snap.Take);
            r.Add("snapshot_diff", "Diff the current world against a named snapshot: added, removed, moved/changed objects (limit examples).",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"name\"]}", Snap.Diff);
            r.Add("prefab_search", "Search prefab names (substring, case-insensitive).",
                "{\"type\":\"object\",\"properties\":{\"contains\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"contains\"]}", PrefabSearch);
            r.Add("world_state", "World name, global keys, time of day (client), object counts, players, FPS.", "{\"type\":\"object\",\"properties\":{}}", WorldState);
            r.Add("journal", "Recent write operations made through this extension (undo history).",
                "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"number\"}}}", JournalList);
        }

        static ToolOutput Info(Dictionary<string, object> a)
        {
#if SERVER
            const string role = "server";
#else
            const string role = "client";
#endif
            return U.Json("{\"version\":" + U.S(Ext.Version) + ",\"role\":" + U.S(role) + ",\"writes\":" + (ToolRegistry.WritesEnabled ? "true" : "false") + ",\"zdoReady\":" + (Zdos.Ready ? "true" : "false") + ",\"note\":\"no terrain-edit tools by design\"}");
        }

        static ToolOutput Portals(Dictionary<string, object> a)
        {
            if (ZDOMan.instance == null) return ToolOutput.Err("no ZDOMan");
            var list = ZDOMan.instance.GetPortalList();
            var byTag = new Dictionary<string, int>();
            foreach (var z in list) { var tg = z.GetString("tag", ""); byTag[tg] = byTag.ContainsKey(tg) ? byTag[tg] + 1 : 1; }
            var rows = new List<string>();
            foreach (var z in list)
            {
                var tg = z.GetString("tag", ""); var p = z.GetPosition();
                rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Zdos.NameOf(z.GetPrefab())) + ",\"pos\":[" + U.N(Math.Round(p.x, 1)) + "," + U.N(Math.Round(p.y, 1)) + "," + U.N(Math.Round(p.z, 1)) + "],\"tag\":" + U.S(tg) + ",\"sameTag\":" + byTag[tg] + "}");
            }
            return U.Json("{\"count\":" + rows.Count + ",\"portals\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput Objects(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("no world loaded");
            float x0, z0, x1, z1;
            if (U.Has(a, "x0")) { x0 = (float)U.D(a, "x0", 0); x1 = (float)U.D(a, "x1", 0); z0 = (float)U.D(a, "z0", 0); z1 = (float)U.D(a, "z1", 0); }
            else if (U.Has(a, "x")) { var r = (float)U.D(a, "radius", 20); x0 = (float)U.D(a, "x", 0) - r; x1 = x0 + 2 * r; z0 = (float)U.D(a, "z", 0) - r; z1 = z0 + 2 * r; }
            else return ToolOutput.Err("give x,z[,radius] or x0,z0,x1,z1");
            var filters = U.Split(McpJson.GetStr(a, "prefab"));
            var limit = (int)Math.Min(3000, U.D(a, "limit", 300)); var offset = (int)U.D(a, "offset", 0);
            var bf = U.Has(a, "built") ? (bool?)McpJson.GetBool(a, "built", false) : null;
            var rows = new List<string>(); var total = 0;
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var rec = Zdos.ToRec(z);
                if (rec.Prefab.StartsWith("_")) continue;
                if (U.Has(a, "x") && !U.Has(a, "x0") && Vector2.Distance(new Vector2(rec.Pos.x, rec.Pos.z), new Vector2((float)U.D(a, "x", 0), (float)U.D(a, "z", 0))) > U.D(a, "radius", 20)) continue;
                if (U.Has(a, "y0") && rec.Pos.y < U.D(a, "y0", -1e9)) continue;
                if (U.Has(a, "y1") && rec.Pos.y > U.D(a, "y1", 1e9)) continue;
                if (!U.MatchPrefab(rec.Prefab, filters)) continue;
                if (bf != null && ((rec.Creator != 0L) != bf.Value)) continue;
                total++;
                if (total <= offset || rows.Count >= limit) continue;
                rows.Add(Zdos.Json(rec));
            }
            return U.Json("{\"total\":" + total + ",\"returned\":" + rows.Count + ",\"offset\":" + offset + ",\"objects\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput PrefabSearch(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var q = McpJson.GetStr(a, "contains", ""); var lim = (int)U.D(a, "limit", 100);
            var res = new List<string>();
            foreach (var n in ZNetScene.instance.GetPrefabNames())
                if (n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { res.Add(U.S(n)); if (res.Count >= lim) break; }
            return U.Json("{\"count\":" + res.Count + ",\"names\":[" + string.Join(",", res.ToArray()) + "]}");
        }

        static ToolOutput WorldState(Dictionary<string, object> a)
        {
            var sb = new StringBuilder("{\"world\":" + U.S(ZNet.instance != null ? ZNet.instance.GetWorldName() : null));
#if !SERVER
            if (EnvMan.instance != null)
            {
                sb.Append(",\"dayFraction\":" + U.N(EnvMan.instance.GetDayFraction()) + ",\"isDay\":" + (EnvMan.IsDay() ? "true" : "false"));
                try { sb.Append(",\"environment\":" + U.S(EnvMan.instance.GetCurrentEnvironment().m_name)); } catch { }
            }
            if (Player.m_localPlayer != null)
                sb.Append(",\"localPlayer\":{\"name\":" + U.S(Player.m_localPlayer.GetPlayerName()) + ",\"pos\":" + U.V(Player.m_localPlayer.transform.position) + "}");
#endif
            if (ZoneSystem.instance != null)
            {
                var keys = new List<string>(); foreach (var k in ZoneSystem.instance.GetGlobalKeys()) keys.Add(U.S(k));
                sb.Append(",\"globalKeys\":[" + string.Join(",", keys.ToArray()) + "]");
            }
            if (ZNet.instance != null)
            {
                var ps = new List<string>(); foreach (var p in ZNet.instance.GetPlayerList()) ps.Add("{\"name\":" + U.S(p.m_name) + ",\"pos\":" + U.V(p.m_position) + "}");
                sb.Append(",\"players\":[" + string.Join(",", ps.ToArray()) + "]");
            }
            if (ZDOMan.instance != null) sb.Append(",\"zdoCount\":" + ZDOMan.instance.NrOfObjects());
            if (ZNetScene.instance != null) sb.Append(",\"instances\":" + ZNetScene.instance.NrOfInstances());
            sb.Append(",\"fps\":" + U.N(Math.Round(1f / Math.Max(0.0001f, Time.smoothDeltaTime), 1)) + "}");
            return U.Json(sb.ToString());
        }

        static ToolOutput JournalList(Dictionary<string, object> a)
        {
            var lim = (int)U.D(a, "limit", 30); var rows = new List<string>();
            for (var i = Journal.Entries.Count - 1; i >= 0 && rows.Count < lim; i--)
            { var e = Journal.Entries[i]; rows.Add("{\"n\":" + i + ",\"kind\":" + U.S(e.Kind) + ",\"id\":" + U.S(e.Id) + ",\"prefab\":" + U.S(e.Prefab) + ",\"pos\":" + U.V(e.Pos) + "}"); }
            return U.Json("{\"count\":" + Journal.Entries.Count + ",\"recent\":[" + string.Join(",", rows.ToArray()) + "]}");
        }
    }

    // ------------------------------------------------------------------ terraform map
    internal static class Terraform
    {
        public static ToolOutput Map(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("no world loaded");
            float x0 = (float)Math.Min(U.D(a, "x0", 0), U.D(a, "x1", 0)), x1 = (float)Math.Max(U.D(a, "x0", 0), U.D(a, "x1", 0));
            float z0 = (float)Math.Min(U.D(a, "z0", 0), U.D(a, "z1", 0)), z1 = (float)Math.Max(U.D(a, "z0", 0), U.D(a, "z1", 0));
            var cell = (float)Math.Max(1, U.D(a, "cell", 4)); var minD = (float)U.D(a, "minDelta", 0.15);
            var wantRaster = McpJson.GetBool(a, "raster", true);
            var nx = (int)Math.Ceiling((x1 - x0) / cell); var nz = (int)Math.Ceiling((z1 - z0) / cell);
            if (nx * nz > 20000) return ToolOutput.Err("raster too large; increase cell");
            var grid = new float[nx, nz]; var hit = new bool[nx, nz];
            var hash = "_TerrainCompiler".GetStableHashCode();
            var zones = new List<string>(); long modified = 0, paint = 0; var tcCount = 0;
            // terrain compilers sit at zone centres; widen the search by one zone
            foreach (var z in Zdos.InBox(x0 - 64, z0 - 64, x1 + 64, z1 + 64))
            {
                if (z.GetPrefab() != hash) continue;
                tcCount++;
                var bytes = z.GetByteArray("TCData");
                if (bytes == null) continue;
                try
                {
                    var pkg = new ZPackage(Utils.Decompress(bytes));
                    pkg.ReadInt(); var ops = pkg.ReadInt(); pkg.ReadVector3(); pkg.ReadSingle();
                    var n = pkg.ReadInt();
                    var pitch = (int)Math.Round(Math.Sqrt(n)); var width = pitch - 1;
                    var c = z.GetPosition(); var half = width * 0.5f;
                    int zMod = 0; float zMax = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (!pkg.ReadBool()) continue;
                        var lvl = pkg.ReadSingle(); var smo = pkg.ReadSingle();
                        var d = lvl + smo;
                        zMod++; if (Math.Abs(d) > Math.Abs(zMax)) zMax = d;
                        if (Math.Abs(d) < minD) continue;
                        var ix = i % pitch; var iy = i / pitch;
                        var wx = c.x - half + ix; var wz = c.z - half + iy;
                        if (wx < x0 || wx >= x1 || wz < z0 || wz >= z1) continue;
                        var gx = (int)((wx - x0) / cell); var gz = (int)((wz - z0) / cell);
                        if (gx >= nx || gz >= nz) continue;
                        if (!hit[gx, gz] || Math.Abs(d) > Math.Abs(grid[gx, gz])) { grid[gx, gz] = d; hit[gx, gz] = true; }
                    }
                    var np = pkg.ReadInt(); int pm = 0;
                    for (int i = 0; i < np; i++) { if (pkg.ReadBool()) { pm++; pkg.ReadSingle(); pkg.ReadSingle(); pkg.ReadSingle(); pkg.ReadSingle(); } }
                    modified += zMod; paint += pm;
                    if (zMod > 0 || pm > 0) zones.Add("{\"zoneCentre\":" + U.V(c) + ",\"ops\":" + ops + ",\"heightNodes\":" + zMod + ",\"paintNodes\":" + pm + ",\"maxDelta\":" + U.N(Math.Round(zMax, 2)) + "}");
                }
                catch (Exception ex) { zones.Add("{\"error\":" + U.S(ex.Message) + "}"); }
            }
            // cluster hit cells (8-neighbourhood)
            var seen = new bool[nx, nz]; var clusters = new List<string>();
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    if (!hit[i, j] || seen[i, j]) continue;
                    var st = new Stack<int[]>(); st.Push(new[] { i, j }); seen[i, j] = true;
                    int minx = i, maxx = i, minz = j, maxz = j, cnt = 0; double sum = 0; float mx = 0;
                    while (st.Count > 0)
                    {
                        var p = st.Pop(); cnt++; sum += grid[p[0], p[1]]; if (Math.Abs(grid[p[0], p[1]]) > Math.Abs(mx)) mx = grid[p[0], p[1]];
                        minx = Math.Min(minx, p[0]); maxx = Math.Max(maxx, p[0]); minz = Math.Min(minz, p[1]); maxz = Math.Max(maxz, p[1]);
                        for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
                        { int qx = p[0] + dx, qz = p[1] + dz; if (qx < 0 || qz < 0 || qx >= nx || qz >= nz || seen[qx, qz] || !hit[qx, qz]) continue; seen[qx, qz] = true; st.Push(new[] { qx, qz }); }
                    }
                    clusters.Add("{\"box\":[" + U.N(x0 + minx * cell) + "," + U.N(z0 + minz * cell) + "," + U.N(x0 + (maxx + 1) * cell) + "," + U.N(z0 + (maxz + 1) * cell) + "],\"cells\":" + cnt + ",\"meanDelta\":" + U.N(Math.Round(sum / cnt, 2)) + ",\"maxDelta\":" + U.N(Math.Round(mx, 2)) + "}");
                }
            var sb = new StringBuilder("{\"box\":[" + U.N(x0) + "," + U.N(z0) + "," + U.N(x1) + "," + U.N(z1) + "],\"cell\":" + U.N(cell) + ",\"compilersSeen\":" + tcCount + ",\"modifiedHeightNodes\":" + modified + ",\"paintNodes\":" + paint);
            sb.Append(",\"zones\":[" + string.Join(",", zones.ToArray()) + "],\"clusters\":[" + string.Join(",", clusters.ToArray()) + "]");
            if (wantRaster)
            {
                sb.Append(",\"nx\":" + nx + ",\"nz\":" + nz + ",\"rows\":[");
                for (int j = 0; j < nz; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append('[');
                    for (int i = 0; i < nx; i++) { if (i > 0) sb.Append(','); sb.Append(hit[i, j] ? U.N(Math.Round(grid[i, j], 1)) : "0"); }
                    sb.Append(']');
                }
                sb.Append(']');
            }
            sb.Append('}');
            return U.Json(sb.ToString());
        }
    }

    // ------------------------------------------------------------------ plan verification
    internal static class Verify
    {
        public static ToolOutput Plan(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("no world loaded");
            var items = McpJson.GetList(a, "items");
            if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            var also = McpJson.GetList(a, "alsoPrefabs");     // extra prefab names to consider when looking for stray objects (e.g. pieces removed from the plan)
            var tol = (float)U.D(a, "tol", 0.06); var mr = (float)U.D(a, "matchRadius", 1.0); var maxEx = (int)U.D(a, "examples", 8);
            var margin = (float)U.D(a, "margin", 1.0);
            float lx = 1e9f, hx = -1e9f, lz = 1e9f, hz = -1e9f;
            var plan = new List<KeyValuePair<string, KeyValuePair<Vector3, KeyValuePair<float, string>>>>();
            var prefabs = new HashSet<string>();
            foreach (var o in items)
            {
                var d = o as Dictionary<string, object>; if (d == null) continue;
                var name = McpJson.GetStr(d, "prefab"); var p = new Vector3((float)McpJson.Get(d, "x", 0), (float)McpJson.Get(d, "y", 0), (float)McpJson.Get(d, "z", 0));
                plan.Add(new KeyValuePair<string, KeyValuePair<Vector3, KeyValuePair<float, string>>>(name, new KeyValuePair<Vector3, KeyValuePair<float, string>>(p, new KeyValuePair<float, string>((float)McpJson.Get(d, "yaw", 0), McpJson.GetStr(d, "text")))));
                prefabs.Add(name);
                lx = Math.Min(lx, p.x); hx = Math.Max(hx, p.x); lz = Math.Min(lz, p.z); hz = Math.Max(hz, p.z);
            }
            if (also != null) foreach (var o in also) { var nm = o as string; if (nm != null) prefabs.Add(nm); }
            var recs = new List<Rec>();
            foreach (var z in Zdos.InBox(lx - margin, lz - margin, hx + margin, hz + margin)) { var r = Zdos.ToRec(z); if (prefabs.Contains(r.Prefab)) recs.Add(r); }
            // spatial hash (1 m cells)
            var cells = new Dictionary<long, List<int>>();
            Func<float, float, long> key = (x, z) => ((long)Math.Floor(x) << 32) ^ (uint)(int)Math.Floor(z);
            for (int i = 0; i < recs.Count; i++) { var k = key(recs[i].Pos.x, recs[i].Pos.z); if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>(); l.Add(i); }
            var used = new bool[recs.Count];
            int ok = 0, moved = 0, missing = 0, dup = 0, badText = 0, badYaw = 0;
            var exMoved = new List<string>(); var exMiss = new List<string>(); var exDup = new List<string>(); var exText = new List<string>(); var exYaw = new List<string>();
            Func<int, Vector3, List<int>> candidates = (pi_, pos_) =>
            {
                var res = new List<int>();
                int cx0 = (int)Math.Floor(pos_.x - mr), cx1 = (int)Math.Floor(pos_.x + mr), cz0 = (int)Math.Floor(pos_.z - mr), cz1 = (int)Math.Floor(pos_.z + mr);
                for (int cxi = cx0; cxi <= cx1; cxi++) for (int czi = cz0; czi <= cz1; czi++)
                    if (cells.TryGetValue(key(cxi, czi), out var lst)) foreach (var i in lst) if (recs[i].Prefab == plan[pi_].Key && Vector3.Distance(recs[i].Pos, pos_) <= mr) res.Add(i);
                return res;
            };
            // pass 1: exact matches claim their world pieces
            var exactOf = new List<int>[plan.Count];
            for (int pi_ = 0; pi_ < plan.Count; pi_++)
            {
                var pos = plan[pi_].Value.Key; var ex = new List<int>();
                foreach (var i in candidates(pi_, pos)) if (Vector3.Distance(recs[i].Pos, pos) <= tol) ex.Add(i);
                exactOf[pi_] = ex; foreach (var i in ex) used[i] = true;
            }
            // pass 2: everything else
            for (int pi_ = 0; pi_ < plan.Count; pi_++)
            {
                var name = plan[pi_].Key; var pos = plan[pi_].Value.Key; var yaw = plan[pi_].Value.Value.Key; var text = plan[pi_].Value.Value.Value;
                var exact = exactOf[pi_];
                if (exact.Count == 0)
                {
                    int best = -1; float bd = 1e9f;
                    foreach (var i in candidates(pi_, pos)) { if (used[i]) continue; var d = Vector3.Distance(recs[i].Pos, pos); if (d < bd) { bd = d; best = i; } }
                    if (best < 0) { missing++; if (exMiss.Count < maxEx) exMiss.Add("{\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(pos) + "}"); continue; }
                    moved++; used[best] = true;
                    if (exMoved.Count < maxEx) exMoved.Add("{\"prefab\":" + U.S(name) + ",\"plan\":" + U.V(pos) + ",\"world\":" + U.V(recs[best].Pos) + ",\"id\":" + U.S(recs[best].Id) + ",\"dist\":" + U.N(Math.Round(bd, 3)) + "}");
                    continue;
                }
                if (exact.Count > 1) { dup++; if (exDup.Count < maxEx) { var ids = new List<string>(); foreach (var i in exact) ids.Add(U.S(recs[i].Id)); exDup.Add("{\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(pos) + ",\"ids\":[" + string.Join(",", ids.ToArray()) + "]}"); } }
                var r0 = recs[exact[0]];
                if (Zdos.AngDiff(r0.Yaw, yaw) > 2f && Math.Abs(Mathf.DeltaAngle(r0.Yaw, yaw + 180f)) > 2f) { badYaw++; if (exYaw.Count < maxEx) exYaw.Add("{\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(pos) + ",\"planYaw\":" + U.N(yaw) + ",\"worldYaw\":" + U.N(Math.Round(r0.Yaw, 1)) + ",\"id\":" + U.S(r0.Id) + "}"); }
                if (text != null && r0.Text != text) { badText++; if (exText.Count < maxEx) exText.Add("{\"id\":" + U.S(r0.Id) + ",\"want\":" + U.S(text) + ",\"have\":" + U.S(r0.Text) + "}"); }
                ok++;
            }
            var extra = 0; var exExtra = new List<string>();
            for (int i = 0; i < recs.Count; i++)
                if (!used[i] && recs[i].Creator == 0 && recs[i].Pos.x >= lx - 0.5f && recs[i].Pos.x <= hx + 0.5f && recs[i].Pos.z >= lz - 0.5f && recs[i].Pos.z <= hz + 0.5f)
                { extra++; if (exExtra.Count < maxEx) exExtra.Add(Zdos.Json(recs[i])); }
            return U.Json("{\"planned\":" + plan.Count + ",\"ok\":" + ok + ",\"moved\":" + moved + ",\"missing\":" + missing + ",\"duplicates\":" + dup + ",\"wrongYaw\":" + badYaw + ",\"wrongText\":" + badText + ",\"extraOfPlanPrefabs\":" + extra +
                ",\"examples\":{\"missing\":[" + string.Join(",", exMiss.ToArray()) + "],\"moved\":[" + string.Join(",", exMoved.ToArray()) + "],\"duplicates\":[" + string.Join(",", exDup.ToArray()) + "],\"wrongYaw\":[" + string.Join(",", exYaw.ToArray()) + "],\"wrongText\":[" + string.Join(",", exText.ToArray()) + "],\"extra\":[" + string.Join(",", exExtra.ToArray()) + "]}}");
        }
    }

    // ------------------------------------------------------------------ snapshots
    internal static class Snap
    {
        private sealed class S { public float[] Box; public Dictionary<string, Rec> Items = new Dictionary<string, Rec>(); }
        private static readonly Dictionary<string, S> All = new Dictionary<string, S>();

        public static ToolOutput Take(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("no world loaded");
            var name = McpJson.GetStr(a, "name");
            var s = new S { Box = new[] { (float)U.D(a, "x0", 0), (float)U.D(a, "z0", 0), (float)U.D(a, "x1", 0), (float)U.D(a, "z1", 0) } };
            foreach (var z in Zdos.InBox(s.Box[0], s.Box[1], s.Box[2], s.Box[3])) { var r = Zdos.ToRec(z); if (!r.Prefab.StartsWith("_")) s.Items[r.Id] = r; }
            All[name] = s;
            try
            {
                var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext", "snapshots"); Directory.CreateDirectory(dir);
                var sb = new StringBuilder(); foreach (var r in s.Items.Values) sb.AppendLine(Zdos.Json(r));
                File.WriteAllText(Path.Combine(dir, name + ".jsonl"), sb.ToString());
            }
            catch { }
            return U.Json("{\"snapshot\":" + U.S(name) + ",\"objects\":" + s.Items.Count + "}");
        }

        public static ToolOutput Diff(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("no world loaded");
            var name = McpJson.GetStr(a, "name"); var lim = (int)U.D(a, "limit", 15);
            if (!All.TryGetValue(name, out var s)) return ToolOutput.Err("no such snapshot in memory (extension reload clears memory; take it again): " + name);
            var now = new Dictionary<string, Rec>();
            foreach (var z in Zdos.InBox(s.Box[0], s.Box[1], s.Box[2], s.Box[3])) { var r = Zdos.ToRec(z); if (!r.Prefab.StartsWith("_")) now[r.Id] = r; }
            var added = new List<string>(); var removed = new List<string>(); var changed = new List<string>(); int na = 0, nr = 0, nc = 0;
            foreach (var kv in now) if (!s.Items.ContainsKey(kv.Key)) { na++; if (added.Count < lim) added.Add(Zdos.Json(kv.Value)); }
            foreach (var kv in s.Items)
            {
                if (!now.TryGetValue(kv.Key, out var cur)) { nr++; if (removed.Count < lim) removed.Add(Zdos.Json(kv.Value)); continue; }
                var o = kv.Value;
                if (Vector3.Distance(o.Pos, cur.Pos) > 0.01f || Zdos.AngDiff(o.Yaw, cur.Yaw) > 0.5f || o.Text != cur.Text || o.Scale != cur.Scale)
                { nc++; if (changed.Count < lim) changed.Add("{\"id\":" + U.S(o.Id) + ",\"prefab\":" + U.S(o.Prefab) + ",\"from\":" + U.V(o.Pos) + ",\"to\":" + U.V(cur.Pos) + "}"); }
            }
            return U.Json("{\"snapshot\":" + U.S(name) + ",\"added\":" + na + ",\"removed\":" + nr + ",\"changed\":" + nc + ",\"examples\":{\"added\":[" + string.Join(",", added.ToArray()) + "],\"removed\":[" + string.Join(",", removed.ToArray()) + "],\"changed\":[" + string.Join(",", changed.ToArray()) + "]}}");
        }
    }
}
