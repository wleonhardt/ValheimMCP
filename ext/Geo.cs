// Batch geometry and world reads (client only): terrain profiles, surface probes, container contents, locations, creatures, terrain paint.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Geo
    {
        public static void Register(ToolRegistry r)
        {
            Reg.Add(r, "terrain_profile", "Ground height along a polyline in ONE call (terrain only, not pieces): points:[[x,z],...], step metres (default 2, max 4000 samples). Returns samples [x,z,h|null,water] plus min/max, max slope and the cumulative distance; null = zone not loaded. Replaces chunked terrain_grid loops for roads and paths.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"step\":{\"type\":\"number\"}},\"required\":[\"points\"]}", Profile);
            Reg.Add(r, "surface_probe", "First solid surface under many points in ONE call (terrainOnly:true = bare terrain; ignore:'Fir,Pine,Beech' = skip objects whose prefab starts with these, so trees and canopy do not count): points:[[x,z]|[x,y,z],...] (max 3000), fromY (start height, default 80 above y or 200), maxDist. Per point: hit y, prefab (or Terrain), collider, normal.y, and clear height above the hit (up to 3 m). Replaces raycast loops.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"fromY\":{\"type\":\"number\"},\"terrainOnly\":{\"type\":\"boolean\"},\"above\":{\"type\":\"number\"},\"ignore\":{\"type\":\"string\"},\"maxDist\":{\"type\":\"number\"},\"spread\":{\"type\":\"number\"}},\"required\":[\"points\"]}", Probe);
            Reg.Add(r, "room_view", "Inside-room camera: x,z,floorY, yaw (look direction, 0 = +z), pitch, eye height (1.6), fov (85), ceiling (3.2 m: every renderer whose centre is higher than floorY+ceiling is hidden, so floors above and the roof vanish), size. Returns an image of what a person standing there sees.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"floorY\":{\"type\":\"number\"},\"yaw\":{\"type\":\"number\"},\"pitch\":{\"type\":\"number\"},\"eye\":{\"type\":\"number\"},\"fov\":{\"type\":\"number\"},\"ceiling\":{\"type\":\"number\"},\"light\":{\"type\":\"number\"},\"size\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\",\"floorY\"]}", RoomView);
            Reg.Add(r, "containers", "Chests and other containers with their contents (ZDO inventory decoded by the game's own Inventory class): box x0,z0,x1,z1 or x,z,radius; optional item filter (substring of item name). Returns per container position, prefab, sign/label if any and an item->count summary.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"item\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"}}}", Containers);
            Reg.Add(r, "creatures", "Characters in the loaded area: name, position, level, tamed, health, faction; filter by name substring and radius around x,z.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"name\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"}}}", Creatures);
            Reg.Add(r, "sign_check", "Audit every loaded sign with text in a box (x0,z0,x1,z1 or x,z,radius): is its face buried (collider within 0.2 m in front), can a reader stand 1.5 / 3 / 6 m in front of it (stand point free, clear line of sight), and how big is the lettering (letterHeightM, estimated readable distance readM). Returns only problem signs unless verbose:true. Client only: needs the area loaded.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"verbose\":{\"type\":\"boolean\"},\"minRead\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}}}", SignCheck);
            Reg.Add(r, "bed_check", "Every bed in a box (x0,z0,x1,z1 or x,z,radius), judged by the game's own rules: cover percentage and roof from Cover.GetCoverForPoint at the sleeping spot (Bed.CheckExposure needs >= 0.8 cover and a roof), the rest comfort level from SE_Rested.CalculateComfortLevel and the comfort pieces within 10 m that give it. Client only (area loaded).",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"lift\":{\"type\":\"number\"}}}", BedCheck);
            Reg.Add(r, "headroom", "Clear height above a walking path in ONE call: points:[[x,y,z],...] (y = floor/foot height), step metres (default 0.5), min (flag threshold, default 2.6). Casts straight up from each sample; reports min clearance, the lowest points with the piece overhead, and every sample below min. Use on stairs, doorways and tunnels.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"step\":{\"type\":\"number\"},\"min\":{\"type\":\"number\"}},\"required\":[\"points\"]}", HeadroomTool);
            Reg.Add(r, "paint_at", "Terrain paint (dirt, cultivated, paved, vegetation weights 0..1) at points: points:[[x,z],...] (max 500). Read-only. null where the heightmap is not loaded.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}},\"required\":[\"points\"]}", Paint);
        }

        static List<float[]> Pts(Dictionary<string, object> a)
        {
            var res = new List<float[]>(); var l = McpJson.GetList(a, "points"); if (l == null) return res;
            foreach (var o in l) { var p = o as List<object>; if (p == null || p.Count < 2) continue; res.Add(p.Select(v => (float)(double)v).ToArray()); }
            return res;
        }

        static ToolOutput Profile(Dictionary<string, object> a)
        {
            var pts = Pts(a); if (pts.Count < 1) return ToolOutput.Err("points required");
            float step = Math.Max(0.5f, (float)McpJson.Get(a, "step", 2)); var zs = ZoneSystem.instance;
            var rows = new List<string>(); float dist = 0, min = 1e9f, max = -1e9f, maxSlope = 0, prevH = float.NaN; int nulls = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p0 = pts[i]; var p1 = i + 1 < pts.Count ? pts[i + 1] : null;
                float L = p1 == null ? 0 : Mathf.Sqrt((p1[0] - p0[0]) * (p1[0] - p0[0]) + (p1[1] - p0[1]) * (p1[1] - p0[1]));
                int n = p1 == null ? 1 : Math.Max(1, (int)Math.Ceiling(L / step));
                for (int k = 0; k < n; k++)
                {
                    if (rows.Count >= 4000) break;
                    float t = n == 0 ? 0 : (float)k / n; float x = p1 == null ? p0[0] : p0[0] + (p1[0] - p0[0]) * t, z = p1 == null ? p0[1] : p0[1] + (p1[1] - p0[1]) * t;
                    float h = 0; bool ok = zs != null && zs.GetGroundHeight(new Vector3(x, 5000f, z), out h);
                    if (ok) { min = Math.Min(min, h); max = Math.Max(max, h); if (!float.IsNaN(prevH)) maxSlope = Math.Max(maxSlope, Math.Abs(h - prevH) / step); prevH = h; } else nulls++;
                    rows.Add("[" + U.N(Math.Round(x, 2)) + "," + U.N(Math.Round(z, 2)) + "," + (ok ? U.N(Math.Round(h, 2)) : "null") + "," + (ok && h < ZoneSystem.instance.m_waterLevel ? "1" : "0") + "]");
                }
                dist += L;
            }
            return U.Json("{\"samples\":[" + string.Join(",", rows.ToArray()) + "],\"length\":" + U.N(Math.Round(dist, 1)) + ",\"min\":" + U.N(min) + ",\"max\":" + U.N(max) + ",\"maxSlope\":" + U.N(Math.Round(maxSlope, 3)) + ",\"unloaded\":" + nulls + "}");
        }

        // first hit along a downward ray, skipping objects whose prefab name starts with one of `ignore` (trees, bushes, ...)
        static bool CastDown(Vector3 o, float maxDist, int mask, string[] ignore, out RaycastHit best)
        {
            if (ignore == null || ignore.Length == 0) return Physics.Raycast(o, Vector3.down, out best, maxDist, mask, QueryTriggerInteraction.Ignore);
            best = default; var hits = Physics.RaycastAll(o, Vector3.down, maxDist, mask, QueryTriggerInteraction.Ignore); Array.Sort(hits, (p, q) => p.distance.CompareTo(q.distance));
            foreach (var h in hits)
            {
                var nv = h.collider.GetComponentInParent<ZNetView>(); var nm = nv != null ? U.PrefabName(nv) : "";
                bool skip = false; foreach (var ig in ignore) if (nm.StartsWith(ig, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                if (!skip) { best = h; return true; }
            }
            return false;
        }

        static ToolOutput Probe(Dictionary<string, object> a)
        {
            var pts = Pts(a); if (pts.Count < 1) return ToolOutput.Err("points required"); if (pts.Count > 3000) return ToolOutput.Err("max 3000 points");
            float maxDist = (float)McpJson.Get(a, "maxDist", 400), spread = (float)McpJson.Get(a, "spread", 0f); var mask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain", "Default_small", "vehicle");
            if (McpJson.GetBool(a, "terrainOnly", false)) mask = LayerMask.GetMask("terrain");
            var ignore = U.Split(McpJson.GetStr(a, "ignore"));
            var rows = new List<string>();
            var offs = spread > 0 ? new[] { Vector2.zero, new Vector2(spread, 0), new Vector2(-spread, 0), new Vector2(0, spread), new Vector2(0, -spread) } : new[] { Vector2.zero };
            foreach (var p in pts)
            {
                float x = p[0], z = p.Length > 2 ? p[2] : p[1]; float fromY = U.Has(a, "fromY") ? (float)McpJson.Get(a, "fromY", 200) : (p.Length > 2 ? p[1] + (float)McpJson.Get(a, "above", 80f) : 200f);
                float ymin = 1e9f, ymax = -1e9f; int miss = 0; RaycastHit hit0 = default; bool have0 = false;
                for (int i = 0; i < offs.Length; i++)
                {
                    RaycastHit h;
                    if (!CastDown(new Vector3(x + offs[i].x, fromY, z + offs[i].y), maxDist, mask, ignore, out h)) { miss++; continue; }
                    ymin = Math.Min(ymin, h.point.y); ymax = Math.Max(ymax, h.point.y);
                    if (i == 0 || !have0) { hit0 = h; have0 = true; }
                }
                if (!have0) { rows.Add("[" + U.N(x) + "," + U.N(z) + ",null]"); continue; }
                var nv = hit0.collider.GetComponentInParent<ZNetView>(); var name = nv != null ? U.PrefabName(nv) : (hit0.collider.GetComponent<Heightmap>() != null || hit0.collider.name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0 ? "Terrain" : hit0.collider.name);
                RaycastHit up; var clear = Physics.Raycast(hit0.point + Vector3.up * 0.05f, Vector3.up, out up, 3f, mask, QueryTriggerInteraction.Ignore) ? up.distance : 3f;
                rows.Add("[" + U.N(x) + "," + U.N(z) + "," + U.N(Math.Round(hit0.point.y, 3)) + "," + U.S(name) + "," + U.N(Math.Round(hit0.normal.y, 2)) + "," + U.N(Math.Round(clear, 2)) + "," + U.N(Math.Round(ymin, 3)) + "," + U.N(Math.Round(ymax, 3)) + "," + miss + "]");
            }
            return U.Json("{\"columns\":[\"x\",\"z\",\"y\",\"prefab\",\"normalY\",\"clearAbove\",\"yMin\",\"yMax\",\"misses\"],\"hits\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput RoomView(Dictionary<string, object> a)
        {
            // first-person camera inside a room with everything above the ceiling line hidden (by renderer centre height, so thick floors and roofs go too)
            float x = (float)U.D(a, "x", 0), z = (float)U.D(a, "z", 0), floorY = (float)U.D(a, "floorY", 48f), eye = (float)U.D(a, "eye", 1.6f), ceil = (float)U.D(a, "ceiling", 3.2f);
            float yl = (float)U.D(a, "yaw", 0), pl = (float)U.D(a, "pitch", -8f);
            var yr = yl * Mathf.Deg2Rad; var pr = pl * Mathf.Deg2Rad;
            var look = new Vector3(Mathf.Cos(pr) * Mathf.Sin(yr), Mathf.Sin(pr), Mathf.Cos(pr) * Mathf.Cos(yr));
            var e = new Vector3(x, floorY + eye, z); var target = e + look * 0.5f;
            var d = new Dictionary<string, object>
            {
                { "x", (double)target.x }, { "y", (double)target.y }, { "z", (double)target.z }, { "yaw", (double)(yl + 180f) }, { "pitch", (double)(-pl) }, { "dist", 0.5 },
                { "size", U.D(a, "size", 900) }, { "fov", U.D(a, "fov", 85) }, { "near", 0.05 }, { "hideCentreAboveY", (double)(floorY + ceil) }, { "cutRadius", 60.0 }
            };
            if (McpJson.GetBool(a, "hideTrees", true)) d["hideTrees"] = true;
            float lightI = (float)U.D(a, "light", 0);
            if (lightI > 0f)                                                           // night renders: a temporary point light at the camera, removed after the shot
            {
                var go = new GameObject("hubner_roomlight"); go.transform.position = e + Vector3.up * 0.2f;
                var li = go.AddComponent<Light>(); li.type = LightType.Point; li.range = 16f; li.intensity = lightI; li.color = new Color(1f, 0.93f, 0.8f); li.shadows = LightShadows.None;
                UnityEngine.Object.Destroy(go, 6f);
            }
            return Render.Ex(d);
        }

        static ToolOutput SignCheck(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1;
            if (U.Has(a, "x0")) { x0 = (float)U.D(a, "x0", 0); z0 = (float)U.D(a, "z0", 0); x1 = (float)U.D(a, "x1", 0); z1 = (float)U.D(a, "z1", 0); }
            else { var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0); var r = (float)U.D(a, "radius", 30); x0 = x - r; x1 = x + r; z0 = z - r; z1 = z + r; }
            bool verbose = McpJson.GetBool(a, "verbose", false); float minRead = (float)McpJson.Get(a, "minRead", 4f); int limit = (int)McpJson.Get(a, "limit", 400);
            int mask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain", "Default_small", "vehicle");          // solid things only: rugs and torches (piece_nonsolid) neither bury a sign nor block a reader
            var rows = new List<string>(); int total = 0, buried = 0, occluded = 0, noStand = 0, small = 0;
            foreach (var nv in U.Instances())
            {
                if (nv == null || U.PrefabName(nv) != "sign") continue;
                var p = nv.transform.position; if (p.x < x0 || p.x > x1 || p.z < z0 || p.z > z1) continue;
                var zdo = nv.GetZDO(); var text = zdo != null ? zdo.GetString("text", "") : ""; if (text.Length == 0) continue;
                total++;
                var col = nv.GetComponentInChildren<Collider>(); var centre = col != null ? col.bounds.center : p; var f = nv.transform.forward; f.y = 0f; f.Normalize();
                // buried: a collider of something else inside the volume directly in front of the face
                bool isBuried = false; string buriedBy = "";
                foreach (var c in Physics.OverlapBox(centre + f * 0.12f, new Vector3(0.45f, 0.2f, 0.07f), nv.transform.rotation, mask, QueryTriggerInteraction.Ignore))
                {
                    var o = c.GetComponentInParent<ZNetView>(); if (o == nv) continue;
                    isBuried = true; buriedBy = o != null ? U.PrefabName(o) : c.name; break;
                }
                // readers: three stand points in front of the face at sign height
                var reads = new List<string>(); bool anyStand = false, anyClear = false;
                foreach (var d in new[] { 0.9f, 1.5f, 3f, 6f })
                {
                    var probe = centre + f * d; string st;
                    float floorY = centre.y - 1.7f; RaycastHit fh;                                       // a reader stands on the floor under that spot, eyes 1.65 m up
                    if (Physics.Raycast(probe + Vector3.up * 0.2f, Vector3.down, out fh, 6f, mask, QueryTriggerInteraction.Ignore)) floorY = fh.point.y;          // start just above the sign's height: a ray from higher up would land on the storey above
                    var eye = new Vector3(probe.x, floorY + 1.65f, probe.z);
                    if (Physics.CheckCapsule(new Vector3(probe.x, floorY + 0.45f, probe.z), new Vector3(probe.x, floorY + 1.55f, probe.z), 0.3f, mask, QueryTriggerInteraction.Ignore)) st = "blocked";
                    else
                    {
                        anyStand = true; RaycastHit h; var dir = (centre - eye); var dist = dir.magnitude; dir /= dist;
                        if (Physics.Raycast(eye, dir, out h, dist + 0.3f, mask, QueryTriggerInteraction.Ignore) && h.collider.GetComponentInParent<ZNetView>() != nv) { var o = h.collider.GetComponentInParent<ZNetView>(); st = "occluded:" + (o != null ? U.PrefabName(o) : h.collider.name); }
                        else { st = "clear"; anyClear = true; }
                    }
                    reads.Add(U.S(d.ToString("0.#", CultureInfo.InvariantCulture) + "m " + st));
                }
                var lines = text.Split('\n'); int maxLen = 1; foreach (var l in lines) maxLen = Math.Max(maxLen, l.Length);
                float sw = col != null ? Math.Max(col.bounds.size.x, col.bounds.size.z) : 1.4f, sh = col != null ? col.bounds.size.y : 0.7f;
                float letterH = Math.Min(0.55f * sh / Math.Max(1, lines.Length), 1.8f * (0.9f * sw / maxLen));
                float readM = letterH * 164f;                                          // 0.35 degrees of arc per letter height is about the smallest legible size on screen
                bool tiny = readM < minRead; bool occ = anyStand && !anyClear;
                if (isBuried) buried++; if (occ) occluded++; if (!anyStand) noStand++; if (tiny) small++;
                if (verbose || isBuried || occ || !anyStand || tiny)
                    rows.Add("{\"text\":" + U.S(text.Replace("\n", " / ")) + ",\"pos\":" + U.V(p) + ",\"yaw\":" + U.N(Math.Round(nv.transform.eulerAngles.y, 1)) + ",\"buried\":" + (isBuried ? U.S(buriedBy) : "false") + ",\"readers\":[" + string.Join(",", reads.ToArray()) + "],\"letterHeightM\":" + U.N(Math.Round(letterH, 3)) + ",\"readM\":" + U.N(Math.Round(readM, 1)) + "}");
                if (rows.Count >= limit) break;
            }
            return U.Json("{\"signs\":" + total + ",\"buried\":" + buried + ",\"noReaderPosition\":" + noStand + ",\"occludedEverywhere\":" + occluded + ",\"tooSmall\":" + small + ",\"rows\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput BedCheck(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1;
            if (U.Has(a, "x0")) { x0 = (float)U.D(a, "x0", 0); z0 = (float)U.D(a, "z0", 0); x1 = (float)U.D(a, "x1", 0); z1 = (float)U.D(a, "z1", 0); }
            else { var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0); var r = (float)U.D(a, "radius", 40); x0 = x - r; x1 = x + r; z0 = z - r; z1 = z + r; }
            float lift = (float)U.D(a, "lift", 0.9f);                                   // sleeping spot above the bed origin (the player's centre point)
            var cover = typeof(Cover).GetMethod("GetCoverForPoint", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var comfort = typeof(SE_Rested).GetMethod("CalculateComfortLevel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool), typeof(Vector3) }, null);
            var nearby = typeof(SE_Rested).GetMethod("GetNearbyComfortPieces", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var rows = new List<string>();
            foreach (var nv in U.Instances())
            {
                if (nv == null) continue; var bed = nv.GetComponent<Bed>(); if (bed == null) continue;
                var p = nv.transform.position; if (p.x < x0 || p.x > x1 || p.z < z0 || p.z > z1) continue;
                var point = p + Vector3.up * lift; float pct = -1f; bool roof = false; string err = "";
                try { var args = new object[] { point, 0f, false, 0.5f }; cover.Invoke(null, args); pct = (float)args[1]; roof = (bool)args[2]; } catch (Exception ex) { err = ex.GetBaseException().Message; }
                int cl = -1; var names = new SortedDictionary<string, int>();
                try { cl = (int)comfort.Invoke(null, new object[] { true, point }); var lst = nearby.Invoke(null, new object[] { point }) as System.Collections.IEnumerable; if (lst != null) foreach (var o in lst) { var pc = o as Piece; if (pc != null) { var n = Utils.GetPrefabName(pc.gameObject); names[n] = (names.ContainsKey(n) ? names[n] : 0) + 1; } } } catch (Exception ex) { err += " " + ex.GetBaseException().Message; }
                bool ok = pct >= 0.8f && roof;
                rows.Add("{\"prefab\":" + U.S(U.PrefabName(nv)) + ",\"pos\":" + U.V(p) + ",\"cover\":" + U.N(Math.Round(pct, 2)) + ",\"roof\":" + (roof ? "true" : "false") + ",\"canSleep\":" + (ok ? "true" : "false") + ",\"comfort\":" + cl + ",\"comfortPieces\":{" + string.Join(",", names.Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "}" + (err.Length > 0 ? ",\"error\":" + U.S(err) : "") + "}");
            }
            return U.Json("{\"beds\":" + rows.Count + ",\"rows\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput HeadroomTool(Dictionary<string, object> a)
        {
            var pts = McpJson.GetList(a, "points"); if (pts == null || pts.Count < 1) return ToolOutput.Err("points required");
            float step = Mathf.Max(0.1f, (float)McpJson.Get(a, "step", 0.5)); float min = (float)McpJson.Get(a, "min", 2.6);
            var path = new List<Vector3>();
            foreach (var o in pts) { var l = o as List<object>; if (l == null || l.Count < 3) return ToolOutput.Err("points must be [x,y,z]"); path.Add(new Vector3((float)Convert.ToDouble(l[0], CultureInfo.InvariantCulture), (float)Convert.ToDouble(l[1], CultureInfo.InvariantCulture), (float)Convert.ToDouble(l[2], CultureInfo.InvariantCulture))); }
            var low = new List<string>(); float worst = 99f; string worstAt = ""; int n = 0;
            for (int i = 0; i < path.Count; i++)
            {
                var a0 = path[i]; var b0 = i + 1 < path.Count ? path[i + 1] : a0; float L = Vector3.Distance(a0, b0); int k = Mathf.Max(1, Mathf.CeilToInt(L / step));
                for (int j = 0; j < (i + 1 < path.Count ? k : 1); j++)
                {
                    var p = Vector3.Lerp(a0, b0, (float)j / k); n++;
                    float best = 99f; string what = "open"; Vector3 hp = p;
                    foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.05f, Vector3.up, 12f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider.GetComponentInParent<Player>() != null) continue;
                        if (h.distance < best) { best = h.distance; hp = h.point; var nv = h.collider.GetComponentInParent<ZNetView>(); what = nv != null ? U.PrefabName(nv) : h.collider.name; }
                    }
                    if (best < worst) { worst = best; worstAt = U.V(p) + " " + what; }
                    if (best < min && low.Count < 60) low.Add("{\"at\":" + U.V(p) + ",\"clear\":" + U.N(best) + ",\"above\":" + U.S(what) + "}");
                }
            }
            return U.Json("{\"samples\":" + n + ",\"minClear\":" + U.N(worst) + ",\"lowest\":" + U.S(worstAt) + ",\"belowMin\":" + low.Count + ",\"low\":[" + string.Join(",", low.ToArray()) + "]}");
        }

        static ToolOutput Containers(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            float x0, z0, x1, z1;
            if (U.Has(a, "x0")) { x0 = (float)U.D(a, "x0", 0); z0 = (float)U.D(a, "z0", 0); x1 = (float)U.D(a, "x1", 0); z1 = (float)U.D(a, "z1", 0); }
            else { var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0); var r = (float)U.D(a, "radius", 20); x0 = x - r; x1 = x + r; z0 = z - r; z1 = z + r; }
            var filter = McpJson.GetStr(a, "item"); int limit = (int)McpJson.Get(a, "limit", 200); var rows = new List<string>();
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var items = z.GetString("items", ""); if (string.IsNullOrEmpty(items)) continue;
                try
                {
                    var inv = new Inventory("x", null, 8, 4); inv.Load(new ZPackage(items));
                    var sum = new SortedDictionary<string, int>();
                    foreach (var it in inv.GetAllItems()) { var n = it.m_shared.m_name; sum[n] = (sum.ContainsKey(n) ? sum[n] : 0) + it.m_stack; }
                    if (filter != null && !sum.Keys.Any(k => k.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    var p = z.GetPosition();
                    rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Zdos.NameOf(z.GetPrefab())) + ",\"pos\":" + U.V(p) + ",\"items\":{" + string.Join(",", sum.Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "}}");
                    if (rows.Count >= limit) break;
                }
                catch { }
            }
            return U.Json("{\"count\":" + rows.Count + ",\"containers\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput Creatures(Dictionary<string, object> a)
        {
            var name = McpJson.GetStr(a, "name"); int limit = (int)McpJson.Get(a, "limit", 200); var rows = new List<string>(); float rad = (float)U.D(a, "radius", 1e9); bool hasC = U.Has(a, "x");
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer()) continue;
                var nv = c.GetComponent<ZNetView>(); var pf = nv != null ? U.PrefabName(nv) : c.name; var p = c.transform.position;
                if (name != null && pf.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (hasC) { var dx = p.x - (float)U.D(a, "x", 0); var dz = p.z - (float)U.D(a, "z", 0); if (dx * dx + dz * dz > rad * rad) continue; }
                rows.Add("{\"prefab\":" + U.S(pf) + ",\"pos\":" + U.V(p) + ",\"level\":" + c.GetLevel() + ",\"tamed\":" + (c.IsTamed() ? "true" : "false") + ",\"health\":" + U.N(Math.Round(c.GetHealth(), 1)) + ",\"faction\":" + U.S(c.GetFaction().ToString()) + "}");
                if (rows.Count >= limit) break;
            }
            return U.Json("{\"count\":" + rows.Count + ",\"creatures\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput Paint(Dictionary<string, object> a)
        {
            var pts = Pts(a); if (pts.Count > 500) return ToolOutput.Err("max 500 points"); var rows = new List<string>();
            var fh = typeof(Heightmap).GetMethod("FindHeightmap", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null);
            var pm = typeof(Heightmap).GetField("m_paintMask", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var w2n = typeof(Heightmap).GetMethod("WorldToNormalizedHM", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var p in pts)
            {
                try
                {
                    var pos = new Vector3(p[0], 0f, p[1]); var hm = fh == null ? null : fh.Invoke(null, new object[] { pos }) as Heightmap;
                    var tex = hm == null || pm == null ? null : pm.GetValue(hm) as Texture2D;
                    if (tex == null || w2n == null) { rows.Add("[" + U.N(p[0]) + "," + U.N(p[1]) + ",null]"); continue; }
                    var args = new object[] { pos, 0f, 0f }; w2n.Invoke(hm, args);
                    var c = tex.GetPixelBilinear((float)args[1], (float)args[2]);
                    rows.Add("[" + U.N(p[0]) + "," + U.N(p[1]) + "," + U.N(Math.Round(c.r, 2)) + "," + U.N(Math.Round(c.g, 2)) + "," + U.N(Math.Round(c.b, 2)) + "," + U.N(Math.Round(c.a, 2)) + "]");
                }
                catch { rows.Add("[" + U.N(p[0]) + "," + U.N(p[1]) + ",null]"); }
            }
            return U.Json("{\"columns\":[\"x\",\"z\",\"dirt\",\"cultivated\",\"paved\",\"vegetation\"],\"paint\":[" + string.Join(",", rows.ToArray()) + "]}");
        }
    }
}
