// Placement validation (client only): would these pieces collide with the world or sit buried / floating?
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Place
    {
        private sealed class Info { public Vector3 Center; public Vector3 Size; public bool Ok; }
        private static readonly Dictionary<string, Info> Cache = new Dictionary<string, Info>();

        public static void Register(ToolRegistry r)
        {
            r.Add("validate_placement",
                "Check planned pieces against the live world BEFORE placing them. items:[{prefab,x,y,z,yaw}] (pivot positions as spawned). Per piece: overlaps with existing solid objects (shrink 0.9 tolerates intended slight overlaps), how far the ground rises above the piece bottom (buried) or falls below it (floating), and unloaded zones. Returns counts and the worst offenders. Does not change anything.",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"shrink\":{\"type\":\"number\"},\"buriedTol\":{\"type\":\"number\"},\"examples\":{\"type\":\"number\"},\"ignore\":{\"type\":\"string\",\"description\":\"comma list of existing prefab substrings to ignore as overlaps (e.g. trees)\"}},\"required\":[\"items\"]}", Validate);
        }

        private static Info Get(string name)
        {
            if (Cache.TryGetValue(name, out var c)) return c;
            var info = new Info();
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (prefab != null)
            {
                GameObject go = null;
                try { ZNetView.m_forceDisableInit = true; go = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -4000f, 0f), Quaternion.identity); }
                finally { ZNetView.m_forceDisableInit = false; }
                try
                {
                    Bounds? b = null;
                    foreach (var col in go.GetComponentsInChildren<Collider>())
                    {
                        if (col.isTrigger) continue;
                        if (b == null) b = col.bounds; else { var bb = b.Value; bb.Encapsulate(col.bounds); b = bb; }
                    }
                    if (b != null) { info.Center = b.Value.center - go.transform.position; info.Size = b.Value.size; info.Ok = true; }
                }
                finally { UnityEngine.Object.Destroy(go); }
            }
            Cache[name] = info;
            return info;
        }

        private static ToolOutput Validate(Dictionary<string, object> a)
        {
            var items = McpJson.GetList(a, "items");
            if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            if (items.Count > 4000) return ToolOutput.Err("max 4000 items per call");
            var shrink = (float)McpJson.Get(a, "shrink", 0.9); var buriedTol = (float)McpJson.Get(a, "buriedTol", 0.3);
            var maxEx = (int)McpJson.Get(a, "examples", 12);
            var ignore = U.Split(McpJson.GetStr(a, "ignore"));
            var mask = LayerMask.GetMask("Default", "static_solid", "piece", "Default_small", "vehicle");
            var zs = ZoneSystem.instance;
            int ok = 0, overlap = 0, buried = 0, floating = 0, unloaded = 0, unknown = 0;
            var exO = new List<string>(); var exB = new List<string>(); var exF = new List<string>(); var exU = new List<string>();
            var idx = -1;
            foreach (var o in items)
            {
                idx++;
                var d = o as Dictionary<string, object>; if (d == null) continue;
                var name = McpJson.GetStr(d, "prefab"); var info = Get(name);
                if (!info.Ok) { unknown++; continue; }
                var pos = new Vector3((float)McpJson.Get(d, "x", 0), (float)McpJson.Get(d, "y", 0), (float)McpJson.Get(d, "z", 0));
                var rot = Quaternion.Euler(0f, (float)McpJson.Get(d, "yaw", 0), 0f);
                if (zs == null || !zs.IsZoneLoaded(pos)) { unloaded++; if (exU.Count < maxEx) exU.Add("{\"i\":" + idx + ",\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(pos) + "}"); continue; }
                var centre = pos + rot * info.Center;
                var half = info.Size * 0.5f;
                var bad = false;
                // overlap with existing solids
                var cs = Physics.OverlapBox(centre, half * shrink, rot, mask, QueryTriggerInteraction.Ignore);
                string hit = null; string hitId = null;
                foreach (var c in cs)
                {
                    if (c == null) continue;
                    var nv = c.GetComponentInParent<ZNetView>();
                    var pn = nv != null ? U.PrefabName(nv) : c.name;
                    if (ignore != null && U.MatchPrefab(pn, ignore)) continue;
                    hit = pn; hitId = nv != null && nv.GetZDO() != null ? U.Id(nv.GetZDO()) : null; break;
                }
                if (hit != null) { overlap++; bad = true; if (exO.Count < maxEx) exO.Add("{\"i\":" + idx + ",\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(pos) + ",\"hits\":" + U.S(hit) + (hitId != null ? ",\"hitId\":" + U.S(hitId) : "") + "}"); }
                // ground relation at the footprint (centre + 4 corners, inset 10%)
                var bottom = centre.y - half.y;
                float maxG = float.MinValue, minG = float.MaxValue;
                foreach (var off in new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(1, 0, -1), new Vector3(-1, 0, 1), new Vector3(-1, 0, -1) })
                {
                    var p = centre + rot * Vector3.Scale(off, new Vector3(half.x * 0.9f, 0, half.z * 0.9f));
                    float g; if (!zs.GetGroundHeight(new Vector3(p.x, 5000f, p.z), out g)) continue;
                    maxG = Math.Max(maxG, g); minG = Math.Min(minG, g);
                }
                if (maxG > float.MinValue)
                {
                    var bury = maxG - bottom;
                    if (bury > buriedTol && maxG - (centre.y + half.y) > 0.0f) { buried++; bad = true; if (exB.Count < maxEx) exB.Add("{\"i\":" + idx + ",\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(pos) + ",\"groundAboveBottom\":" + U.N(Math.Round(bury, 2)) + "}"); }
                    else if (bury > buriedTol && maxG > centre.y) { /* partly sunk into a slope: normal for foundations */ }
                    var gap = bottom - minG;
                    if (gap > 0.5f && bottom < minG + 3f && info.Size.y < 1.5f) { floating++; if (exF.Count < maxEx) exF.Add("{\"i\":" + idx + ",\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(pos) + ",\"gapAboveGround\":" + U.N(Math.Round(gap, 2)) + "}"); }
                }
                if (!bad) ok++;
            }
            return U.Json("{\"checked\":" + items.Count + ",\"clean\":" + ok + ",\"overlapping\":" + overlap + ",\"buriedUnderGround\":" + buried + ",\"thinPiecesFloating\":" + floating + ",\"zoneNotLoaded\":" + unloaded + ",\"unknownPrefab\":" + unknown +
                ",\"examples\":{\"overlap\":[" + string.Join(",", exO.ToArray()) + "],\"buried\":[" + string.Join(",", exB.ToArray()) + "],\"floating\":[" + string.Join(",", exF.ToArray()) + "],\"unloaded\":[" + string.Join(",", exU.ToArray()) + "]}}");
        }
    }
}
