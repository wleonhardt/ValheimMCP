// Prefab discovery by name AND component (client and server): adopted from ValBridgeServer's prefab registry tools and Thrall's requirement tables.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class InsightShared
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("prefab_find", "Search the game's prefab registry (ZNetScene.m_prefabs) by name substring and/or component type name: name:'grausten', component:'Door'|'WearNTear'|'Container'|'Piece'|'Fireplace'..., category:'Building*|Furniture|Crafting|Misc' (Piece category), limit (default 100, max 500), requirements:true adds the build cost, components:true lists every component. Use it to validate prefab names and capabilities before plan_apply.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"component\":{\"type\":\"string\"},\"category\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"},\"requirements\":{\"type\":\"boolean\"},\"components\":{\"type\":\"boolean\"}}}", PrefabFind);
        }

        static ToolOutput PrefabFind(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var name = McpJson.GetStr(a, "name"); var comp = McpJson.GetStr(a, "component"); var cat = McpJson.GetStr(a, "category");
            int limit = Math.Max(1, Math.Min(500, (int)McpJson.Get(a, "limit", 100))); bool req = McpJson.GetBool(a, "requirements", false), allc = McpJson.GetBool(a, "components", false);
            var rows = new List<string>(); int total = 0;
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var dict = f != null ? f.GetValue(ZNetScene.instance) as System.Collections.Generic.Dictionary<int, GameObject> : null;
            if (dict == null) return ToolOutput.Err("prefab dictionary not found");
            foreach (var go in dict.Values.Where(g => g != null).OrderBy(g => g.name))
            {
                if (name != null && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var comps = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).Distinct().ToList();
                if (comp != null && !comps.Any(c => c.IndexOf(comp, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                var piece = go.GetComponent<Piece>();
                if (cat != null && (piece == null || !Wild(piece.m_category.ToString(), cat))) continue;
                total++; if (rows.Count >= limit) continue;
                var sb = new StringBuilder("{\"prefab\":" + U.S(go.name));
                if (piece != null)
                {
                    sb.Append(",\"category\":" + U.S(piece.m_category.ToString()) + ",\"comfort\":" + piece.m_comfort);
                    if (req && piece.m_resources != null)
                    {
                        var items = new List<string>();
                        foreach (var rq in piece.m_resources) if (rq != null && rq.m_resItem != null) items.Add("{\"item\":" + U.S(rq.m_resItem.name) + ",\"amount\":" + rq.m_amount + "}");
                        sb.Append(",\"requirements\":[" + string.Join(",", items.ToArray()) + "]");
                    }
                }
                var shown = allc ? comps : comps.Where(c => c != "Transform" && c != "ZNetView").Take(10).ToList();
                sb.Append(",\"components\":[" + string.Join(",", shown.Select(U.S).ToArray()) + "]}");
                rows.Add(sb.ToString());
            }
            return U.Json("{\"matches\":" + total + ",\"returned\":" + rows.Count + ",\"prefabs\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static bool Wild(string value, string pattern)
        {
            if (pattern.EndsWith("*")) return value.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
