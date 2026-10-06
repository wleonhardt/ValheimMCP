// Tool registration filter: the same source files build into the client and the server extension; on the server only tools that make sense without a camera or local player register.
using System;
using System.Collections.Generic;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Reg
    {
#if SERVER
        static readonly HashSet<string> ServerTools = new HashSet<string> { "terrain_info", "terrain_profile", "raycast", "surface_probe", "headroom", "stability_scan", "walk_check", "sign_check", "bed_check", "creatures", "object_info", "prefab_info" };
#endif
        public static void Add(ToolRegistry r, string name, string description, string schemaJson, Func<Dictionary<string, object>, ToolOutput> handler, bool write = false)
        {
#if SERVER
            if (!ServerTools.Contains(name)) return;
#endif
            r.Add(name, description, schemaJson, handler, write);
        }
    }
}
