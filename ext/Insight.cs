// Client-side insight and interaction tools adopted from the community repo review (ValBridgeServer, bjorn, Thrall):
// visible_objects (frustum + occlusion + taxonomy), interact (press E), probe_fan (12-heading steering probe), base_survey, player_status, chat_send, chat_bubble.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Insight
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("visible_objects", "What the player camera can actually see: objects in a sphere (radius default 30, max 80) that are inside the view frustum and not occluded by something else (raycast from the camera). Per object: prefab, type taxonomy (Piece/Door/Container/Character/Tree/Pickable/ItemDrop/Rock/Destructible), distance, screen x,y (0..1), occluded flag. includeOccluded:true also lists hidden ones. limit default 60.",
                "{\"type\":\"object\",\"properties\":{\"radius\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"},\"includeOccluded\":{\"type\":\"boolean\"}}}", VisibleObjects);
            r.Add("interact", "Press E: interact with the object under the crosshair (hover) or, with prefab and radius (default 3), the nearest Interactable of that prefab name around the player (doors, signs, beds, chests, stations). Returns what was interacted with.",
                "{\"type\":\"object\",\"properties\":{\"prefab\":{\"type\":\"string\"},\"radius\":{\"type\":\"number\"},\"hold\":{\"type\":\"boolean\"}}}", Interact, true);
            r.Add("probe_fan", "Steering probe from a position (default the player): 12 headings, 30 degrees apart. Each is classified Clear/Wall/Chest/Slope/Drop/Void from a downward ground ray 1.2 m ahead and a chest-height ray (3 m): use it to explain why a walk is stuck. Args: x,y,z (optional), reach (default 3).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"reach\":{\"type\":\"number\"}}}", ProbeFan);
            r.Add("base_survey", "Understand a base: pieces within radius (default 60, max 150) of x,z (default the player): counts by category, notable stations/beds/fires/chests/doors, building clusters (pieces closer than 6 m link up) with centroid and extent, overall centroid and radius.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"}}}", BaseSurvey);
            r.Add("player_status", "The sandbox character's state: health, stamina, position, biome, equipped item, status effects with remaining time, foods eaten with time left.",
                "{\"type\":\"object\",\"properties\":{}}", PlayerStatus);
            r.Add("chat_send", "Say a line in game chat as the sandbox character: text (max 180 chars, longer is cut), type Normal (15 m) | Shout | Whisper | Ping.",
                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"type\":{\"type\":\"string\"}},\"required\":[\"text\"]}", ChatSend, true);
            r.Add("chat_bubble", "Show a speech bubble above a position as a named speaker without a player (Thrall's technique: Chat.AddInworldText with a synthetic sender): name, text, x,y,z (default above the sandbox character). Local to this client.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"text\"]}", ChatBubble, true);
        }

        static string Kind(GameObject go)
        {
            if (go.GetComponentInParent<Door>() != null) return "Door";
            if (go.GetComponentInParent<Container>() != null) return "Container";
            if (go.GetComponentInParent<Character>() != null) return "Character";
            if (go.GetComponentInParent<TreeBase>() != null || go.GetComponentInParent<TreeLog>() != null) return "Tree";
            if (go.GetComponentInParent<Pickable>() != null) return "Pickable";
            if (go.GetComponentInParent<ItemDrop>() != null) return "ItemDrop";
            if (go.GetComponentInParent<MineRock5>() != null || go.GetComponentInParent<MineRock>() != null) return "Rock";
            if (go.GetComponentInParent<Piece>() != null) return "Piece";
            if (go.GetComponentInParent<Destructible>() != null) return "Destructible";
            return "Other";
        }

        static string Prefab(GameObject go)
        {
            var nv = go.GetComponentInParent<ZNetView>(); return nv != null ? U.PrefabName(nv) : go.transform.root.name;
        }

        static ToolOutput VisibleObjects(Dictionary<string, object> a)
        {
            var cam = Camera.main; var pl = Player.m_localPlayer; if (cam == null || pl == null) return ToolOutput.Err("no camera or player (menu or loading)");
            float rad = (float)Math.Min(80, Math.Max(2, U.D(a, "radius", 30))); int limit = (int)McpJson.Get(a, "limit", 60); bool inc = McpJson.GetBool(a, "includeOccluded", false);
            var seen = new Dictionary<int, GameObject>(); var rows = new List<string>(); int hidden = 0;
            foreach (var c in Physics.OverlapSphere(cam.transform.position, rad))
            {
                if (c == null || c.isTrigger) continue; var go = c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.transform.root.gameObject;
                var nv = c.GetComponentInParent<ZNetView>(); if (nv != null) go = nv.gameObject; if (seen.ContainsKey(go.GetInstanceID())) continue; seen[go.GetInstanceID()] = go;
                var p = c.bounds.center; var vp = cam.WorldToViewportPoint(p);
                if (vp.z <= 0 || vp.x < 0 || vp.x > 1 || vp.y < 0 || vp.y > 1) continue;
                bool occ = false; Vector3 dir = p - cam.transform.position; RaycastHit hit;
                if (Physics.Raycast(cam.transform.position, dir.normalized, out hit, dir.magnitude + 0.5f) && hit.collider != null)
                {
                    var hr = hit.collider.GetComponentInParent<ZNetView>(); var hg = hr != null ? hr.gameObject : hit.collider.transform.root.gameObject;
                    occ = hg != go && hit.distance < dir.magnitude - 0.6f;
                }
                if (occ) { hidden++; if (!inc) continue; }
                rows.Add("{\"prefab\":" + U.S(Prefab(go)) + ",\"kind\":" + U.S(Kind(go)) + ",\"distance\":" + U.N(Math.Round(dir.magnitude, 1)) + ",\"screen\":[" + U.N(Math.Round(vp.x, 2)) + "," + U.N(Math.Round(vp.y, 2)) + "],\"occluded\":" + (occ ? "true" : "false") + ",\"pos\":" + U.V(p) + "}");
                if (rows.Count >= limit) break;
            }
            return U.Json("{\"count\":" + rows.Count + ",\"occludedHidden\":" + hidden + ",\"objects\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput Interact(Dictionary<string, object> a)
        {
            var pl = Player.m_localPlayer; if (pl == null) return ToolOutput.Err("no local player");
            Interactable it = null; GameObject src = null; var want = McpJson.GetStr(a, "prefab"); float rad = (float)U.D(a, "radius", 3);
            if (string.IsNullOrEmpty(want)) { var hov = pl.GetHoverObject(); if (hov != null) { it = hov.GetComponentInParent<Interactable>(); src = hov; } }
            else
            {
                float best = 1e9f;
                foreach (var c in Physics.OverlapSphere(pl.transform.position, rad))
                {
                    var i2 = c.GetComponentInParent<Interactable>(); if (i2 == null) continue; var go = ((Component)i2).gameObject; if (Prefab(go).IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var d = Vector3.Distance(pl.transform.position, c.transform.position); if (d < best) { best = d; it = i2; src = go; }
                }
            }
            if (it == null) return ToolOutput.Err("nothing to interact with (aim at it, or pass prefab and radius)");
            bool ok = it.Interact(pl, McpJson.GetBool(a, "hold", false), false);
            return U.Json("{\"interacted\":" + U.S(Prefab(src)) + ",\"accepted\":" + (ok ? "true" : "false") + ",\"pos\":" + U.V(src.transform.position) + "}");
        }

        static ToolOutput ProbeFan(Dictionary<string, object> a)
        {
            var pl = Player.m_localPlayer; Vector3 o;
            if (U.Has(a, "x")) o = new Vector3((float)U.D(a, "x", 0), (float)U.D(a, "y", 0), (float)U.D(a, "z", 0)); else if (pl != null) o = pl.transform.position; else return ToolOutput.Err("no player and no x,y,z");
            float reach = (float)U.D(a, "reach", 3); var rows = new List<string>(); var mask = ~0;
            for (int k = 0; k < 12; k++)
            {
                var dir = Quaternion.Euler(0, 30 * k, 0) * Vector3.forward; string cls = "Clear"; string what = ""; float rise = 0f;
                RaycastHit h; var chest = o + Vector3.up * 1.0f;
                if (Physics.Raycast(chest, dir, out h, reach, mask, QueryTriggerInteraction.Ignore)) { cls = h.collider.GetComponentInParent<Container>() != null ? "Chest" : "Wall"; what = Prefab(h.collider.gameObject) + "@" + U.N(Math.Round(h.distance, 1)); }
                else
                {
                    var ahead = o + dir * 1.2f + Vector3.up * 1.0f;
                    if (Physics.Raycast(ahead, Vector3.down, out h, 6f, mask, QueryTriggerInteraction.Ignore))
                    {
                        rise = h.point.y - o.y; float slope = Vector3.Angle(h.normal, Vector3.up);
                        if (rise < -0.8f) { cls = "Drop"; what = U.N(Math.Round(rise, 1)) + "m"; } else if (slope > 45f || rise > 0.6f) { cls = "Slope"; what = "rise " + U.N(Math.Round(rise, 2)) + " slope " + U.N(Math.Round(slope)); } else what = Prefab(h.collider.gameObject);
                    }
                    else { cls = "Void"; what = "no ground within 6 m below"; }
                }
                rows.Add("{\"heading\":" + 30 * k + ",\"class\":" + U.S(cls) + ",\"detail\":" + U.S(what) + "}");
            }
            return U.Json("{\"origin\":" + U.V(o) + ",\"fan\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput BaseSurvey(Dictionary<string, object> a)
        {
            var pl = Player.m_localPlayer; Vector3 c;
            if (U.Has(a, "x")) c = new Vector3((float)U.D(a, "x", 0), 0, (float)U.D(a, "z", 0)); else if (pl != null) c = pl.transform.position; else return ToolOutput.Err("no player and no x,z");
            float rad = (float)Math.Min(150, Math.Max(5, U.D(a, "radius", 60))); var list = new List<Piece>(); Piece.GetAllPiecesInRadius(c, rad, list);
            var cats = new Dictionary<string, int>(); var notable = new Dictionary<string, int>(); var pts = new List<Vector3>();
            foreach (var p in list)
            {
                if (p == null) continue; pts.Add(p.transform.position); var k = p.m_category.ToString(); cats[k] = (cats.ContainsKey(k) ? cats[k] : 0) + 1;
                var nm = Prefab(p.gameObject); if (p.GetComponent<CraftingStation>() != null || p.GetComponent<Bed>() != null || p.GetComponent<Fireplace>() != null || p.GetComponent<Container>() != null || p.GetComponent<Door>() != null) notable[nm] = (notable.ContainsKey(nm) ? notable[nm] : 0) + 1;
            }
            int n = pts.Count; var parent = new int[n]; for (int i = 0; i < n; i++) parent[i] = i;
            Func<int, int> find = null; find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
            var grid = new Dictionary<long, List<int>>(); Func<Vector3, long> key = v => ((long)Mathf.FloorToInt(v.x / 6f) << 32) ^ (uint)Mathf.FloorToInt(v.z / 6f);
            for (int i = 0; i < n; i++) { var kk = key(pts[i]); if (!grid.ContainsKey(kk)) grid[kk] = new List<int>(); grid[kk].Add(i); }
            for (int i = 0; i < n; i++) for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            {
                List<int> cell; var kk = ((long)(Mathf.FloorToInt(pts[i].x / 6f) + dx) << 32) ^ (uint)(Mathf.FloorToInt(pts[i].z / 6f) + dz);
                if (!grid.TryGetValue(kk, out cell)) continue;
                foreach (var j in cell) if (j > i && (pts[i] - pts[j]).sqrMagnitude < 36f) parent[find(i)] = find(j);
            }
            var cl = new Dictionary<int, List<Vector3>>(); for (int i = 0; i < n; i++) { var r0 = find(i); if (!cl.ContainsKey(r0)) cl[r0] = new List<Vector3>(); cl[r0].Add(pts[i]); }
            var rows = new List<string>(); foreach (var g in cl.Values.Where(g => g.Count >= 8).OrderByDescending(g => g.Count).Take(25))
            {
                var m = new Vector3(g.Average(v => v.x), g.Average(v => v.y), g.Average(v => v.z)); var mx = g.Max(v => Vector2.Distance(new Vector2(v.x, v.z), new Vector2(m.x, m.z)));
                rows.Add("{\"pieces\":" + g.Count + ",\"centre\":" + U.V(m) + ",\"extent\":" + U.N(Math.Round(mx, 1)) + ",\"yMin\":" + U.N(Math.Round(g.Min(v => v.y), 1)) + ",\"yMax\":" + U.N(Math.Round(g.Max(v => v.y), 1)) + "}");
            }
            string J(Dictionary<string, int> d) { return "{" + string.Join(",", d.OrderByDescending(kv => kv.Value).Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "}"; }
            return U.Json("{\"pieces\":" + n + ",\"categories\":" + J(cats) + ",\"notable\":" + J(notable) + ",\"clusters\":[" + string.Join(",", rows.ToArray()) + "],\"clusterCount\":" + cl.Values.Count(g => g.Count >= 8) + "}");
        }

        static ToolOutput PlayerStatus(Dictionary<string, object> a)
        {
            var pl = Player.m_localPlayer; if (pl == null) return ToolOutput.Err("no local player");
            var sb = new StringBuilder("{\"health\":" + U.N(Math.Round(pl.GetHealth(), 1)) + ",\"maxHealth\":" + U.N(Math.Round(pl.GetMaxHealth(), 1)) + ",\"stamina\":" + U.N(Math.Round(pl.GetStamina(), 1)) + ",\"pos\":" + U.V(pl.transform.position));
            try { sb.Append(",\"biome\":" + U.S(WorldGenerator.instance.GetBiome(pl.transform.position).ToString())); } catch { }
            try { var w = pl.GetCurrentWeapon(); sb.Append(",\"weapon\":" + U.S(w != null ? w.m_shared.m_name : "")); } catch { }
            var eff = new List<string>();
            try { foreach (var se in pl.GetSEMan().GetStatusEffects()) { float rem = 0; try { rem = se.GetRemaningTime(); } catch { } eff.Add("{\"name\":" + U.S(se.name) + ",\"remaining\":" + U.N(Math.Round(rem, 1)) + "}"); } } catch { }
            sb.Append(",\"statusEffects\":[" + string.Join(",", eff.ToArray()) + "]");
            var foods = new List<string>();
            try { foreach (var f in pl.GetFoods()) { var fi = f.GetType(); var item = fi.GetField("m_item")?.GetValue(f); var nm = item != null ? ((ItemDrop.ItemData)item).m_shared.m_name : "?"; var tm = fi.GetField("m_time")?.GetValue(f); foods.Add("{\"food\":" + U.S(nm) + ",\"timeLeft\":" + U.N(Math.Round(tm is float ft ? ft : 0f, 0)) + "}"); } } catch { }
            sb.Append(",\"foods\":[" + string.Join(",", foods.ToArray()) + "]}"); return U.Json(sb.ToString());
        }

        static ToolOutput ChatSend(Dictionary<string, object> a)
        {
            if (Chat.instance == null) return ToolOutput.Err("chat not available (menu or loading)");
            var t = McpJson.GetStr(a, "text") ?? ""; if (t.Length > 180) t = t.Substring(0, 180);
            Talker.Type ty = Talker.Type.Normal; Enum.TryParse(McpJson.GetStr(a, "type") ?? "Normal", true, out ty);
            Chat.instance.SendText(ty, t); return U.Json("{\"sent\":" + U.S(t) + ",\"type\":" + U.S(ty.ToString()) + "}");
        }

        static ToolOutput ChatBubble(Dictionary<string, object> a)
        {
            if (Chat.instance == null) return ToolOutput.Err("chat not available (menu or loading)");
            var m = typeof(Chat).GetMethod("AddInworldText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (m == null) return ToolOutput.Err("Chat.AddInworldText not found in this game version");
            var pl = Player.m_localPlayer; var pos = U.Has(a, "x") ? new Vector3((float)U.D(a, "x", 0), (float)U.D(a, "y", 0), (float)U.D(a, "z", 0)) : (pl != null ? pl.transform.position + Vector3.up * 2.2f : Vector3.zero);
            var ps = m.GetParameters(); var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt == typeof(GameObject)) args[i] = pl != null ? pl.gameObject : null; else if (pt == typeof(long)) args[i] = 99992L; else if (pt == typeof(Vector3)) args[i] = pos;
                else if (pt == typeof(Talker.Type)) args[i] = Talker.Type.Normal; else if (pt == typeof(string)) args[i] = McpJson.GetStr(a, "text") ?? "";
                else { var obj = Activator.CreateInstance(pt); var nf = pt.GetField("Name"); if (nf != null) { object boxed = obj; nf.SetValue(boxed, McpJson.GetStr(a, "name") ?? "Voice"); obj = boxed; } args[i] = obj; }
            }
            m.Invoke(Chat.instance, args); return U.Json("{\"shown\":true,\"speaker\":" + U.S(McpJson.GetStr(a, "name") ?? "Voice") + "}");
        }
    }
}
