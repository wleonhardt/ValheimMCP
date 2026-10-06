// Hubner extension for ValheimMCP: perception + (gated) control tools for building work.
// Loaded from BepInEx/hubner-ext/HubnerExt.dll and hot-reloaded on change. No terrain-editing tools, on purpose.
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class ClientTools
    {
        public static void Register(ToolRegistry r)
        {
            // ---- perception (read-only)
                        r.Add("terrain_info",
                "True terrain at (x,z) as the running game sees it (includes any terrain edits): ground height, solid height (top of terrain OR structures), what that solid is, surface normal, biome, water, and whether the zone is loaded. Unloaded zones return loaded=false (teleport near first).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Read.TerrainInfo);
            r.Add("terrain_grid",
                "Sample true ground height on a grid. Returns rows of heights (null where the zone is not loaded). Max 6000 samples.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"step\":{\"type\":\"number\",\"description\":\"metres between samples (default 4)\"},\"solid\":{\"type\":\"boolean\",\"description\":\"sample top of structures too (default false = terrain only)\"}},\"required\":[\"x0\",\"z0\",\"x1\",\"z1\"]}", Read.TerrainGrid);
            r.Add("raycast",
                "Cast a ray (origin x,y,z; direction dx,dy,dz) against solid world geometry. Returns hit point, normal, collider/prefab name, ZDO id.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"dx\":{\"type\":\"number\"},\"dy\":{\"type\":\"number\"},\"dz\":{\"type\":\"number\"},\"maxDist\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\",\"dx\",\"dy\",\"dz\"]}", Read.Raycast);
            r.Add("object_info",
                "Everything about one object by id (format 'ID:USERID' as RCON prints): prefab, transform, creator, health, support, sign text, bounds, components.",
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}", Read.ObjectInfo);
            r.Add("prefab_info",
                "Authoritative geometry of a prefab: pivot offset, size (local bounds), colliders, snap points, front direction, piece category/comfort/ground rules, structural material, key components. Replaces guessing from a catalogue.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}", Read.PrefabInfo);
                                    r.Add("zone_state",
                "Is the area around (x,z) loaded and populated? Poll after teleport. Returns zoneLoaded, ground height ok, instances within radius.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Read.ZoneState);
            r.Add("teleport",
                "Teleport the local player (the sandbox character) and switch on god mode so teleports and falls cannot hurt it. snap: solid (default, highest solid incl. roofs) | ground (terrain only) | none (keep altitude). y optional: defaults to solid ground + 1 when the zone is loaded, otherwise keeps the current altitude (groundSnapped=false: call again after zone_state reports loaded).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"snap\":{\"type\":\"string\"},\"distant\":{\"type\":\"boolean\"}},\"required\":[\"x\",\"z\"]}", Read.Teleport);
            r.Add("render_ex",
                "Off-screen render with cutaway controls: fov, near clip, orthographic top-down, hideAboveY (hides every object whose bounds are entirely above that height, to see into rooms through roofs), hidePrefabs (comma substrings to hide). Same yaw/pitch/dist convention as render_view (yaw = camera azimuth, 0 = camera north of target).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"yaw\":{\"type\":\"number\"},\"pitch\":{\"type\":\"number\"},\"dist\":{\"type\":\"number\"},\"size\":{\"type\":\"number\"},\"fov\":{\"type\":\"number\"},\"near\":{\"type\":\"number\"},\"ortho\":{\"type\":\"boolean\"},\"orthoSize\":{\"type\":\"number\"},\"hideAboveY\":{\"type\":\"number\"},\"hidePrefabs\":{\"type\":\"string\"},\"far\":{\"type\":\"number\"},\"hideCentreAboveY\":{\"type\":\"number\"},\"avoid\":{\"type\":\"boolean\"},\"hideTrees\":{\"type\":\"boolean\"},\"hidePlan\":{\"type\":\"string\"},\"cutRadius\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Render.Ex);
            r.Add("walk_check",
                "Simulate a player walking a polyline of waypoints [[x,y,z],...] with a capsule (radius, height, stepHeight, maxSlopeDeg). Closed doors are treated as passable (players open them). Reports the first blocking collider, headroom failures and unsupported drops. Use it to prove stairs, doors and corridors are walkable.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"radius\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"stepHeight\":{\"type\":\"number\"},\"maxSlopeDeg\":{\"type\":\"number\"}},\"required\":[\"points\"]}", Walk.Check);
            r.Add("stability_scan",
                "Structural support values (the game's own, from each piece) in a box: min/avg, list of pieces below a threshold (will collapse when NoBuildingFall is off).",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"threshold\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"x0\",\"z0\",\"x1\",\"z1\"]}", Read.Stability);
            r.Add("inspect_type", "Debug: list fields/methods of a game type by name (substring filter on members).",
                "{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\"},\"filter\":{\"type\":\"string\"}},\"required\":[\"type\"]}", Read.InspectType);
            
            // ---- control (gated by tools.write)
            r.Add("spawn",
                "Spawn pieces/objects. items:[{prefab,x,y,z,yaw|rot:[rx,ry,rz],scale,text,tag,creator,snap:'ground'}] (max 1000 per call). y is the prefab pivot height unless snap='ground'. 'text' sets sign text; 'tag' sets a portal's tag (required for portal prefabs); 'creator' (long) marks the piece player-built. Only loaded zones; set force=true to override. Returns ids; every spawn is journaled for undo.",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"force\":{\"type\":\"boolean\"}},\"required\":[\"items\"]}", Write.Spawn, true);
            r.Add("modify",
                "Modify objects by id: x,y,z, yaw|rot, scale, text, tag, health. Journaled.",
                "{\"type\":\"object\",\"properties\":{\"edits\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}}},\"required\":[\"edits\"]}", Write.Modify, true);
            r.Add("delete",
                "Delete objects by ids, or by box+prefab filter (requires confirm=true and a count <= max, default 500). dryRun=true only counts. Journaled for undo.",
                "{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"prefab\":{\"type\":\"string\"},\"built\":{\"type\":\"boolean\"},\"max\":{\"type\":\"number\"},\"confirm\":{\"type\":\"boolean\"},\"dryRun\":{\"type\":\"boolean\"}}}", Write.Delete, true);
            r.Add("undo", "Undo the last n journaled write operations (spawn->delete, delete->respawn, modify->restore).",
                "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"number\"}}}", Write.Undo, true);
            Place.Register(r);
            Control.Register(r);
            Geo.Register(r);
        }
    }

    // ------------------------------------------------------------------ read tools
    internal static class Read
    {
        public static ToolOutput Info(Dictionary<string, object> a)
        {
            return U.Json("{\"version\":" + U.S(Ext.Version) + ",\"writes\":" + (ToolRegistry.WritesEnabled ? "true" : "false") + ",\"note\":\"no terrain-edit tools by design\"}");
        }

        public static ToolOutput TerrainInfo(Dictionary<string, object> a)
        {
            var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0);
            var zs = ZoneSystem.instance;
            if (zs == null) return ToolOutput.Err("no world loaded");
            var p = new Vector3(x, 0f, z);
            var loaded = zs.IsZoneLoaded(p);
            var sb = new StringBuilder("{\"x\":" + U.N(x) + ",\"z\":" + U.N(z) + ",\"zoneLoaded\":" + (loaded ? "true" : "false"));
            if (loaded)
            {
                float g;
                if (zs.GetGroundHeight(new Vector3(x, 5000f, z), out g)) sb.Append(",\"ground\":" + U.N(g));
                float sh; Vector3 n; GameObject go;
                if (zs.GetSolidHeight(new Vector3(x, 5000f, z), out sh, out n, out go))
                {
                    sb.Append(",\"solid\":" + U.N(sh) + ",\"normal\":" + U.V(n));
                    if (go != null) { var nv = go.GetComponentInParent<ZNetView>(); sb.Append(",\"solidObject\":" + U.S(nv != null ? U.PrefabName(nv) : go.name)); }
                }
                try { sb.Append(",\"biome\":" + U.S(WorldGenerator.instance.GetBiome(x, z).ToString())); } catch { }
                sb.Append(",\"waterLevel\":" + U.N(zs.m_waterLevel));
            }
            sb.Append('}');
            return U.Json(sb.ToString());
        }

        public static ToolOutput TerrainGrid(Dictionary<string, object> a)
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return ToolOutput.Err("no world loaded");
            var x0 = (float)Math.Min(U.D(a, "x0", 0), U.D(a, "x1", 0)); var x1 = (float)Math.Max(U.D(a, "x0", 0), U.D(a, "x1", 0));
            var z0 = (float)Math.Min(U.D(a, "z0", 0), U.D(a, "z1", 0)); var z1 = (float)Math.Max(U.D(a, "z0", 0), U.D(a, "z1", 0));
            var step = (float)Math.Max(0.5, U.D(a, "step", 4));
            var solid = McpJson.GetBool(a, "solid", false);
            var nx = (int)Math.Floor((x1 - x0) / step) + 1; var nz = (int)Math.Floor((z1 - z0) / step) + 1;
            if (nx * nz > 6000) return ToolOutput.Err("too many samples (" + nx * nz + " > 6000); increase step");
            var sb = new StringBuilder("{\"x0\":" + U.N(x0) + ",\"z0\":" + U.N(z0) + ",\"step\":" + U.N(step) + ",\"nx\":" + nx + ",\"nz\":" + nz + ",\"rows\":[");
            var missing = 0;
            for (var j = 0; j < nz; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append('[');
                for (var i = 0; i < nx; i++)
                {
                    if (i > 0) sb.Append(',');
                    var p = new Vector3(x0 + i * step, 5000f, z0 + j * step);
                    float h; var ok = zs.IsZoneLoaded(p) && (solid ? zs.GetSolidHeight(p, out h) : zs.GetGroundHeight(p, out h));
                    if (ok) { if (solid) zs.GetSolidHeight(p, out h); else zs.GetGroundHeight(p, out h); sb.Append(U.N(Math.Round(h, 2))); }
                    else { sb.Append("null"); missing++; }
                }
                sb.Append(']');
            }
            sb.Append("],\"missing\":" + missing + "}");
            return U.Json(sb.ToString());
        }

        public static ToolOutput Raycast(Dictionary<string, object> a)
        {
            var o = new Vector3((float)U.D(a, "x", 0), (float)U.D(a, "y", 0), (float)U.D(a, "z", 0));
            var d = new Vector3((float)U.D(a, "dx", 0), (float)U.D(a, "dy", -1), (float)U.D(a, "dz", 0));
            if (d.sqrMagnitude < 1e-6f) return ToolOutput.Err("zero direction");
            RaycastHit hit;
            var maxd = (float)U.D(a, "maxDist", 500);
            if (!Physics.Raycast(o, d.normalized, out hit, maxd, ~0, QueryTriggerInteraction.Ignore))
                return U.Json("{\"hit\":false}");
            var nv = hit.collider.GetComponentInParent<ZNetView>();
            return U.Json("{\"hit\":true,\"point\":" + U.V(hit.point) + ",\"normal\":" + U.V(hit.normal) + ",\"distance\":" + U.N(hit.distance) +
                ",\"collider\":" + U.S(hit.collider.name) + ",\"layer\":" + U.S(LayerMask.LayerToName(hit.collider.gameObject.layer)) +
                (nv != null ? ",\"prefab\":" + U.S(U.PrefabName(nv)) + ",\"id\":" + U.S(U.Id(nv.GetZDO())) : "") + "}");
        }

        public static ToolOutput Objects(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var filters = U.Split(McpJson.GetStr(a, "prefab"));
            var limit = (int)Math.Min(2000, U.D(a, "limit", 300)); var offset = (int)U.D(a, "offset", 0);
            var builtFilter = U.Has(a, "built") ? (bool?)McpJson.GetBool(a, "built", false) : null;
            var bounds = McpJson.GetBool(a, "bounds", false);
            if (!(U.Has(a, "x0") || U.Has(a, "x"))) return ToolOutput.Err("give x,z[,radius] or x0,z0,x1,z1");
            var rows = new List<string>(); var total = 0;
            foreach (var nv in U.Instances())
            {
                if (!U.InBox(nv.transform.position, a)) continue;
                var name = U.PrefabName(nv);
                if (!U.MatchPrefab(name, filters)) continue;
                if (builtFilter != null && ((nv.GetZDO().GetLong("creator", 0L) != 0L) != builtFilter.Value)) continue;
                total++;
                if (total <= offset || rows.Count >= limit) continue;
                rows.Add(U.Row(nv, bounds));
            }
            return U.Json("{\"total\":" + total + ",\"returned\":" + rows.Count + ",\"offset\":" + offset + ",\"objects\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        public static ToolOutput ObjectInfo(Dictionary<string, object> a)
        {
            var z = U.FindZdo(McpJson.GetStr(a, "id"));
            if (z == null) return ToolOutput.Err("no such ZDO (not known to this client)");
            var nv = ZNetScene.instance.FindInstance(z);
            if (nv == null) return ToolOutput.Err("ZDO exists but is not instantiated here (too far away?)");
            var sb = new StringBuilder(U.Row(nv, true));
            sb.Length -= 1;
            sb.Append(",\"support\":" + U.N(z.GetFloat("support", -1f)) + ",\"health\":" + U.N(z.GetFloat("health", -1f)));
            sb.Append(",\"components\":[");
            var first = true;
            foreach (var c in nv.GetComponents<Component>()) { if (c == null) continue; if (!first) sb.Append(','); first = false; sb.Append(U.S(c.GetType().Name)); }
            sb.Append("]}");
            return U.Json(sb.ToString());
        }

        public static ToolOutput PrefabSearch(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var q = McpJson.GetStr(a, "contains", "");
            var lim = (int)U.D(a, "limit", 100);
            var res = new List<string>();
            foreach (var n in ZNetScene.instance.GetPrefabNames())
                if (n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { res.Add(U.S(n)); if (res.Count >= lim) break; }
            return U.Json("{\"count\":" + res.Count + ",\"names\":[" + string.Join(",", res.ToArray()) + "]}");
        }

        public static ToolOutput PrefabInfo(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var name = McpJson.GetStr(a, "name");
            var prefab = ZNetScene.instance.GetPrefab(name);
            if (prefab == null) return ToolOutput.Err("unknown prefab: " + name);
            GameObject go = null;
            try
            {
                ZNetView.m_forceDisableInit = true;
                go = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -4000f, 0f), Quaternion.identity);
            }
            finally { ZNetView.m_forceDisableInit = false; }
            try
            {
                var root = go.transform;
                var cols = go.GetComponentsInChildren<Collider>();
                Bounds? b = null;
                var csb = new StringBuilder("[");
                var n = 0;
                foreach (var c in cols)
                {
                    if (c.isTrigger) continue;
                    if (b == null) b = c.bounds; else { var bb = b.Value; bb.Encapsulate(c.bounds); b = bb; }
                    if (n < 12)
                    {
                        if (n > 0) csb.Append(',');
                        csb.Append("{\"type\":" + U.S(c.GetType().Name) + ",\"layer\":" + U.S(LayerMask.LayerToName(c.gameObject.layer)) + ",\"c\":" + U.V(c.bounds.center - root.position) + ",\"s\":" + U.V(c.bounds.size) + "}");
                    }
                    n++;
                }
                csb.Append(']');
                Bounds? rb = null;
                foreach (var rend in go.GetComponentsInChildren<Renderer>())
                { if (rb == null) rb = rend.bounds; else { var bb = rb.Value; bb.Encapsulate(rend.bounds); rb = bb; } }
                var sb = new StringBuilder("{\"name\":" + U.S(name));
                if (b != null) sb.Append(",\"colliderBounds\":{\"center\":" + U.V(b.Value.center - root.position) + ",\"size\":" + U.V(b.Value.size) + "}");
                if (rb != null) sb.Append(",\"renderBounds\":{\"center\":" + U.V(rb.Value.center - root.position) + ",\"size\":" + U.V(rb.Value.size) + "}");
                sb.Append(",\"colliders\":" + csb + ",\"colliderCount\":" + n);
                sb.Append(",\"forward\":" + U.V(root.forward) + ",\"right\":" + U.V(root.right));
                // snap points
                var sp = new StringBuilder("[");
                var k = 0;
                foreach (Transform t in go.GetComponentsInChildren<Transform>())
                {
                    var isSnap = false;
                    try { isSnap = t.CompareTag("snappoint"); } catch { }
                    if (!isSnap && t.name.IndexOf("snap", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (k >= 40) break;
                    if (k++ > 0) sp.Append(',');
                    sp.Append(U.V(t.position - root.position));
                }
                sp.Append(']');
                sb.Append(",\"snapPoints\":" + sp);
                var piece = go.GetComponent<Piece>();
                if (piece != null)
                    sb.Append(",\"piece\":{\"name\":" + U.S(piece.m_name) + ",\"category\":" + U.S(piece.m_category.ToString()) + ",\"comfort\":" + piece.m_comfort + ",\"groundPiece\":" + (piece.m_groundPiece ? "true" : "false") + ",\"groundOnly\":" + (piece.m_groundOnly ? "true" : "false") + ",\"clipEverything\":" + (piece.m_clipEverything ? "true" : "false") + "}");
                var wnt = go.GetComponent<WearNTear>();
                if (wnt != null) sb.Append(",\"wearNTear\":{\"health\":" + U.N(wnt.m_health) + ",\"supports\":" + (wnt.m_supports ? "true" : "false") + ",\"noSupportWear\":" + (wnt.m_noSupportWear ? "true" : "false") + "}");
                var comps = new StringBuilder("[");
                var ci = 0;
                foreach (var c in go.GetComponents<Component>()) { if (c == null) continue; if (ci++ > 0) comps.Append(','); comps.Append(U.S(c.GetType().Name)); }
                comps.Append(']');
                sb.Append(",\"components\":" + comps + "}");
                return U.Json(sb.ToString());
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        public static ToolOutput WorldState(Dictionary<string, object> a)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"world\":" + U.S(ZNet.instance != null ? ZNet.instance.GetWorldName() : null));
            if (EnvMan.instance != null)
            {
                sb.Append(",\"dayFraction\":" + U.N(EnvMan.instance.GetDayFraction()) + ",\"isDay\":" + (EnvMan.IsDay() ? "true" : "false"));
                try { sb.Append(",\"environment\":" + U.S(EnvMan.instance.GetCurrentEnvironment().m_name)); } catch { }
            }
            if (ZoneSystem.instance != null)
            {
                var keys = new List<string>();
                foreach (var k in ZoneSystem.instance.GetGlobalKeys()) keys.Add(U.S(k));
                sb.Append(",\"globalKeys\":[" + string.Join(",", keys.ToArray()) + "]");
            }
            if (Player.m_localPlayer != null)
                sb.Append(",\"localPlayer\":{\"name\":" + U.S(Player.m_localPlayer.GetPlayerName()) + ",\"pos\":" + U.V(Player.m_localPlayer.transform.position) + "}");
            if (ZNet.instance != null)
            {
                var ps = new List<string>();
                foreach (var p in ZNet.instance.GetPlayerList()) ps.Add("{\"name\":" + U.S(p.m_name) + ",\"pos\":" + U.V(p.m_position) + "}");
                sb.Append(",\"players\":[" + string.Join(",", ps.ToArray()) + "]");
            }
            if (ZDOMan.instance != null) sb.Append(",\"zdoCount\":" + ZDOMan.instance.NrOfObjects());
            if (ZNetScene.instance != null) sb.Append(",\"instances\":" + ZNetScene.instance.NrOfInstances());
            sb.Append(",\"fps\":" + U.N(Math.Round(1f / Math.Max(0.0001f, Time.smoothDeltaTime), 1)) + "}");
            return U.Json(sb.ToString());
        }

        public static ToolOutput ZoneState(Dictionary<string, object> a)
        {
            var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0); var r = (float)U.D(a, "radius", 30);
            var zs = ZoneSystem.instance;
            if (zs == null) return ToolOutput.Err("no world loaded");
            var p = new Vector3(x, 0, z);
            var loaded = zs.IsZoneLoaded(p);
            float g; var gok = loaded && zs.GetGroundHeight(new Vector3(x, 5000f, z), out g);
            var n = 0;
            foreach (var nv in U.Instances())
            { var d = nv.transform.position - p; if (d.x * d.x + d.z * d.z <= r * r) n++; }
            return U.Json("{\"zoneLoaded\":" + (loaded ? "true" : "false") + ",\"groundOk\":" + (gok ? "true" : "false") + ",\"instancesWithinRadius\":" + n + ",\"activeAreaLoaded\":" + (zs.IsActiveAreaLoaded() ? "true" : "false") + "}");
        }

        public static ToolOutput Teleport(Dictionary<string, object> a)
        {
            var pl = Player.m_localPlayer;
            if (pl == null) return ToolOutput.Err("no local player");
            // the sandbox character is a tool, not a player: never let a teleport or a fall hurt (or kill) whoever is watching
            pl.SetGodMode(true);
            var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0);
            float y; var snapped = true; var snap = McpJson.GetStr(a, "snap") ?? "solid";
            if (U.Has(a, "y")) y = (float)U.D(a, "y", 0);
            else
            {
                float h;
                // snap: 'solid' = highest solid (pieces, wall-walks: the old default), 'ground' = terrain only (never lands on a roof), 'none' = keep altitude
                bool loaded = ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(new Vector3(x, 0, z));
                if (snap == "none") { y = pl.transform.position.y; snapped = false; }
                else if (loaded && snap == "ground" && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 5000f, z), out h)) y = h + 1f;
                else if (loaded && snap != "ground" && ZoneSystem.instance.GetSolidHeight(new Vector3(x, 5000f, z), out h)) y = h + 1f;
                else { y = pl.transform.position.y; snapped = false; }   // unknown ground: keep altitude, call again once zone_state says loaded
            }
            // a distant teleport (8 s fade, waits for the new area) is only needed for long hops; a short hop inside the loaded area is much quicker. `distant` overrides the automatic choice.
            var here = pl.transform.position; bool dist = U.Has(a, "distant") ? McpJson.GetBool(a, "distant", true) : (new Vector2(here.x - x, here.z - z).magnitude > 80f);
            bool ok = pl.TeleportTo(new Vector3(x, y, z), pl.transform.rotation, dist);                // false: the game refused (already teleporting, or inside its ~2 s cooldown after the last one) - the old tool still answered 'teleporting:true'
            return U.Json("{\"teleporting\":" + (ok ? "true" : "false") + ",\"accepted\":" + (ok ? "true" : "false") + ",\"distant\":" + (dist ? "true" : "false") + ",\"to\":" + U.V(new Vector3(x, y, z)) + ",\"groundSnapped\":" + (snapped ? "true" : "false") + ",\"godMode\":true}");
        }

        public static ToolOutput Stability(Dictionary<string, object> a)
        {
            var thr = (float)U.D(a, "threshold", 20); var lim = (int)U.D(a, "limit", 100);
            var low = new List<string>(); var n = 0; double sum = 0; float min = float.MaxValue;
            foreach (var nv in U.Instances())
            {
                if (!U.InBox(nv.transform.position, a)) continue;
                if (nv.GetComponent<WearNTear>() == null) continue;
                var s = nv.GetZDO().GetFloat("support", -1f);
                if (s < 0) continue;
                n++; sum += s; if (s < min) min = s;
                if (s < thr && low.Count < lim) low.Add("{\"id\":" + U.S(U.Id(nv.GetZDO())) + ",\"prefab\":" + U.S(U.PrefabName(nv)) + ",\"pos\":" + U.V(nv.transform.position) + ",\"support\":" + U.N(s) + "}");
            }
            return U.Json("{\"pieces\":" + n + ",\"min\":" + (n > 0 ? U.N(min) : "null") + ",\"avg\":" + (n > 0 ? U.N(sum / n) : "null") + ",\"below\":[" + string.Join(",", low.ToArray()) + "]}");
        }

        public static ToolOutput InspectType(Dictionary<string, object> a)
        {
            var tn = McpJson.GetStr(a, "type"); var f = McpJson.GetStr(a, "filter", "");
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { t = asm.GetType(tn) ?? asm.GetType("UnityEngine." + tn); if (t != null) break; }
            if (t == null) return ToolOutput.Err("type not found");
            var rows = new List<string>();
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (f.Length > 0 && m.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(U.S(m.MemberType + " " + m.ToString()));
                if (rows.Count >= 200) break;
            }
            return U.Json("{\"type\":" + U.S(t.FullName) + ",\"members\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        public static ToolOutput JournalList(Dictionary<string, object> a)
        {
            var lim = (int)U.D(a, "limit", 30);
            var rows = new List<string>();
            for (var i = Journal.Entries.Count - 1; i >= 0 && rows.Count < lim; i--)
            { var e = Journal.Entries[i]; rows.Add("{\"n\":" + i + ",\"kind\":" + U.S(e.Kind) + ",\"id\":" + U.S(e.Id) + ",\"prefab\":" + U.S(e.Prefab) + ",\"pos\":" + U.V(e.Pos) + "}"); }
            return U.Json("{\"count\":" + Journal.Entries.Count + ",\"recent\":[" + string.Join(",", rows.ToArray()) + "]}");
        }
    }

    // ------------------------------------------------------------------ render with cutaways
    internal static class Render
    {
        private static Camera _cam;
        private static Camera Cam()
        {
            if (_cam != null) return _cam;
            var go = new GameObject("hubner_render_cam") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false; _cam.clearFlags = CameraClearFlags.Skybox; _cam.cullingMask = ~0;
            return _cam;
        }

        public static ToolOutput Ex(Dictionary<string, object> a)
        {
            var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0);
            float y;
            if (U.Has(a, "y")) y = (float)U.D(a, "y", 0);
            else { float h; y = (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 5000f, z), out h)) ? h : 0f; }
            var yaw = (float)U.D(a, "yaw", 45); var pitch = (float)U.D(a, "pitch", 35); var dist = (float)Math.Max(0.5, U.D(a, "dist", 12));
            var size = Math.Max(128, Math.Min(1600, (int)U.D(a, "size", 768)));
            var ortho = McpJson.GetBool(a, "ortho", false);
            var target = new Vector3(x, y, z);
            var pr = pitch * Mathf.Deg2Rad; var yr = yaw * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Cos(pr) * Mathf.Sin(yr), Mathf.Sin(pr), Mathf.Cos(pr) * Mathf.Cos(yr));
            var cp = target + dir * dist;
            if (McpJson.GetBool(a, "avoid", false))                        // camera collision: stop in front of the first solid between target and camera
            {
                RaycastHit hit;
                if (Physics.Raycast(target, dir, out hit, dist, LayerMask.GetMask("Default", "static_solid", "piece", "terrain"), QueryTriggerInteraction.Ignore)) cp = target + dir * Mathf.Max(0.5f, hit.distance - 0.3f);
            }
            var cam = Cam();
            cam.transform.position = cp;
            cam.transform.rotation = Quaternion.LookRotation(target - cp, Mathf.Abs(pitch) > 89f ? Vector3.forward : Vector3.up);
            cam.orthographic = ortho;
            if (ortho) cam.orthographicSize = (float)U.D(a, "orthoSize", 20);
            cam.fieldOfView = (float)U.D(a, "fov", 60);
            cam.nearClipPlane = (float)Math.Max(0.01, U.D(a, "near", 0.1));
            cam.farClipPlane = U.Has(a, "far") ? (float)U.D(a, "far", 100) : dist + 1500f;

            // cutaway: temporarily disable renderers
            var hidden = new List<Renderer>();
            var hideY = U.Has(a, "hideAboveY") ? (float?)U.D(a, "hideAboveY", 0) : null;
            var hideC = U.Has(a, "hideCentreAboveY") ? (float?)U.D(a, "hideCentreAboveY", 0) : null;    // hide any renderer whose CENTRE is above this height (floors and roofs that straddle a cut line)
            var hideNames = U.Split(McpJson.GetStr(a, "hidePrefabs"));
            if (McpJson.GetBool(a, "hideTrees", false)) hideNames = (hideNames ?? new string[0]).Concat(new[] { "Tree", "Beech", "Birch", "Oak", "Pine", "Fir", "Bush", "shrub", "Rock", "stubbe", "Stub" }).ToArray();
            var hidePlan = McpJson.GetStr(a, "hidePlan");
            var cut = (float)U.D(a, "cutRadius", dist + 40);
            if (hideY != null || hideC != null || hideNames != null || hidePlan != null)
            {
                foreach (var nv in U.Instances())
                {
                    var d = nv.transform.position - target;
                    if (d.x * d.x + d.z * d.z > cut * cut) continue;
                    var name = U.PrefabName(nv);
                    var byName = (hideNames != null && U.MatchPrefab(name, hideNames)) || (hidePlan != null && nv.GetZDO().GetString(Plans.PlanKey, "") == hidePlan);
                    foreach (var r in nv.GetComponentsInChildren<Renderer>())
                    {
                        if (r == null || !r.enabled) continue;
                        if (byName || (hideY != null && r.bounds.min.y > hideY.Value) || (hideC != null && r.bounds.center.y > hideC.Value)) { r.enabled = false; hidden.Add(r); }
                    }
                }
            }
            var rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active; Texture2D tex = null;
            try
            {
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(size, size, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0); tex.Apply();
                return ToolOutput.Image(tex.EncodeToPNG(), "hidden " + hidden.Count + " renderers");
            }
            finally
            {
                foreach (var r in hidden) if (r != null) r.enabled = true;
                cam.targetTexture = null; RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }
    }

    // ------------------------------------------------------------------ walkability simulation
    internal static class Walk
    {
        private static int Mask() { return LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "vehicle", "Default_small"); }

        public static ToolOutput Check(Dictionary<string, object> a)
        {
            var pts = McpJson.GetList(a, "points");
            if (pts == null || pts.Count < 2) return ToolOutput.Err("need >= 2 points");
            var radius = (float)U.D(a, "radius", 0.4); var height = (float)U.D(a, "height", 1.8);
            var stepH = (float)U.D(a, "stepHeight", 0.5); var maxSlope = (float)U.D(a, "maxSlopeDeg", 50); var wantTrace = McpJson.GetBool(a, "trace", false); var trace = new List<string>();
            var mask = Mask();
            if (mask == 0) return ToolOutput.Err("none of the expected physics layers exist (mask==0): walk_check would pass everything; refusing");
            var P = new List<Vector3>();
            foreach (var o in pts)
            { var l = o as List<object>; if (l == null || l.Count < 3) return ToolOutput.Err("point must be [x,y,z]"); P.Add(new Vector3((float)(double)l[0], (float)(double)l[1], (float)(double)l[2])); }
            var cur = P[0];
            const float ds = 0.2f;
            var steps = 0; var maxRise = 0f; var minHead = 99f;
            string blocked = null; Vector3 blockAt = Vector3.zero;
            for (var s = 1; s < P.Count && blocked == null; s++)
            {
                var dir = P[s] - new Vector3(cur.x, P[s].y, cur.z); dir.y = 0;
                var total = dir.magnitude; if (total < 0.01f) continue; dir /= total;
                for (var t = 0f; t < total - 0.001f; t += ds)
                {
                    var step = Math.Min(ds, total - t);
                    var next = cur + dir * step;
                    // can the capsule stand at 'next' lifted by the step allowance?
                    var lift = stepH;
                    Vector3 b = next + Vector3.up * (radius + lift), tp = next + Vector3.up * Math.Max(height - radius, radius + lift);   // lift only the feet end: a step is climbed, the head must still clear lintels
                    Collider hitc = FirstBlocker(b, tp, radius, mask);
                    if (hitc != null) { blocked = Describe(hitc); blockAt = cur; break; }
                    // settle onto the ground below
                    RaycastHit gh;
                    var from = next + Vector3.up * (lift + 0.05f);
                    if (GroundHit(from, lift + 3f, mask, out gh))
                    {
                        var rise = gh.point.y - cur.y;
                        if (rise > 0.3f && rise / step > Mathf.Tan(maxSlope * Mathf.Deg2Rad))      // small ledges are steps, only real rises count as slope { blocked = "slope too steep (" + U.N(Math.Round(Mathf.Atan2(rise, step) * Mathf.Rad2Deg, 1)) + " deg, rise " + U.N(Math.Round(rise, 2)) + " onto " + Describe(gh.collider) + ")"; blockAt = cur; break; }
                        maxRise = Math.Max(maxRise, rise);
                        next.y = gh.point.y;
                        // headroom at the settled position
                        // headroom is judged above step height: on a slope the capsule's lowest sphere touches the rising surface, which is not a ceiling
                        Vector3 b2 = next + Vector3.up * (radius + 0.4f), t2 = next + Vector3.up * (height - radius);
                        var head = FirstBlocker(b2, t2, radius * 0.9f, mask);
                        if (head != null) { blocked = "headroom: " + Describe(head); blockAt = next; break; }
                    }
                    else { blocked = "no ground within " + U.N(lift + 3f) + " m below (drop or hole)"; blockAt = next; break; }
                    cur = next; steps++;
                    if (wantTrace && steps % Math.Max(1, (int)U.D(a, "traceEvery", 6)) == 0) trace.Add(U.V(cur));
                }
            }
            return U.Json("{\"ok\":" + (blocked == null ? "true" : "false") + ",\"steps\":" + steps + ",\"end\":" + U.V(cur) + ",\"maxStepRise\":" + U.N(maxRise) +
                (blocked != null ? ",\"blocked\":" + U.S(blocked) + ",\"blockedAt\":" + U.V(blockAt) : "") + (wantTrace ? ",\"trace\":[" + string.Join(",", trace.ToArray()) + "]" : "") + "}");
        }

        private static bool IsWalkable(RaycastHit h) { return h.collider != null && h.collider.GetComponentInParent<Door>() == null; }

        /// <summary>nearest walkable surface below 'from' (doors and the player are not ground, even if the ray starts inside a door)</summary>
        private static bool GroundHit(Vector3 from, float maxDist, int mask, out RaycastHit best)
        {
            best = default(RaycastHit);
            // a sphere, not a ray: colliders that touch (stair low end against a landing tile) leave a zero-width seam a ray falls through, a body does not
            var hits = Physics.SphereCastAll(from + Vector3.up * 0.25f, 0.2f, Vector3.down, maxDist + 0.25f, mask, QueryTriggerInteraction.Ignore);
            var bd = float.MaxValue; var found = false;
            foreach (var h in hits)
            {
                if (!IsWalkable(h) || h.collider.GetComponentInParent<Player>() != null) continue;
                if (h.distance == 0f) continue;      // started inside this collider: no usable contact point
                if (h.distance < bd) { bd = h.distance; best = h; found = true; }
            }
            return found;
        }

        private static Collider FirstBlocker(Vector3 bottom, Vector3 top, float radius, int mask)
        {
            var cs = Physics.OverlapCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore);
            foreach (var c in cs)
            {
                if (c == null) continue;
                if (c.GetComponentInParent<Door>() != null) continue;      // closed doors: the player opens them
                if (c.GetComponentInParent<Player>() != null) continue;
                return c;
            }
            return null;
        }

        private static string Describe(Collider c)
        {
            var nv = c.GetComponentInParent<ZNetView>();
            return (nv != null ? U.PrefabName(nv) + " " + U.Id(nv.GetZDO()) : c.name) + " @" + U.V(c.bounds.center);
        }
    }

    // ------------------------------------------------------------------ write tools
    internal static class Write
    {
        private static Quaternion RotOf(Dictionary<string, object> d)
        {
            var l = McpJson.GetList(d, "rot");
            if (l != null && l.Count >= 3) return Quaternion.Euler((float)(double)l[0], (float)(double)l[1], (float)(double)l[2]);
            return Quaternion.Euler(0f, (float)McpJson.Get(d, "yaw", 0), 0f);
        }

        public static ToolOutput Spawn(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var items = McpJson.GetList(a, "items");
            if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            if (items.Count > 1000) return ToolOutput.Err("max 1000 items per call");
            var force = McpJson.GetBool(a, "force", false);
            var ids = new List<string>(); var errs = new List<string>();
            foreach (var o in items)
            {
                var it = o as Dictionary<string, object>;
                try
                {
                    var name = McpJson.GetStr(it, "prefab");
                    if (U.IsForbidden(name)) { errs.Add(name + ": forbidden prefab (terrain/internal)"); ids.Add(null); continue; }
                    var prefab = ZNetScene.instance.GetPrefab(name);
                    if (prefab == null) { errs.Add(name + ": unknown prefab"); ids.Add(null); continue; }
                    if (name.StartsWith("portal") && string.IsNullOrEmpty(McpJson.GetStr(it, "tag"))) { errs.Add(name + ": portals need a tag (untagged portals pair with every other untagged portal)"); ids.Add(null); continue; }
                    var pos = new Vector3((float)McpJson.Get(it, "x", 0), (float)McpJson.Get(it, "y", 0), (float)McpJson.Get(it, "z", 0));
                    if (McpJson.GetStr(it, "snap") == "ground") { float h; if (ZoneSystem.instance.GetGroundHeight(new Vector3(pos.x, 5000f, pos.z), out h)) pos.y = h; }
                    if (!force && ZoneSystem.instance != null && !ZoneSystem.instance.IsZoneLoaded(pos)) { errs.Add(name + ": zone not loaded at " + U.V(pos)); ids.Add(null); continue; }
                    var rot = RotOf(it);
                    var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                    if (McpJson.Get(it, "scale", 0) > 0) go.transform.localScale = Vector3.one * (float)McpJson.Get(it, "scale", 1);
                    var nv = go.GetComponent<ZNetView>();
                    if (nv == null || nv.GetZDO() == null) { errs.Add(name + ": no ZNetView/ZDO"); ids.Add(null); continue; }
                    var z = nv.GetZDO();
                    var text = McpJson.GetStr(it, "text");
                    if (text != null) z.Set("text", text);
                    var tag = McpJson.GetStr(it, "tag");
                    if (tag != null) z.Set("tag", tag);
                    if (U.Has(it, "creator")) z.Set("creator", (long)McpJson.Get(it, "creator", 0));
                    var id = U.Id(z);
                    ids.Add(id);
                    Journal.Add(new JEntry { Kind = "spawn", Id = id, Prefab = name, Pos = pos, Rot = rot, Scale = go.transform.localScale, Text = text });
                }
                catch (Exception ex) { errs.Add("exception: " + ex.Message); ids.Add(null); }
            }
            var idj = new List<string>(); foreach (var i in ids) idj.Add(i == null ? "null" : U.S(i));
            var ej = new List<string>(); foreach (var e in errs) ej.Add(U.S(e));
            return U.Json("{\"spawned\":" + (ids.Count - errs.Count) + ",\"ids\":[" + string.Join(",", idj.ToArray()) + "],\"errors\":[" + string.Join(",", ej.ToArray()) + "]}");
        }

        public static ToolOutput Modify(Dictionary<string, object> a)
        {
            var edits = McpJson.GetList(a, "edits");
            if (edits == null) return ToolOutput.Err("edits required");
            var done = 0; var errs = new List<string>();
            foreach (var o in edits)
            {
                var e = o as Dictionary<string, object>;
                var id = McpJson.GetStr(e, "id");
                var z = U.FindZdo(id);
                var nv = z == null ? null : ZNetScene.instance.FindInstance(z);
                if (nv == null) { errs.Add(id + ": not found/instantiated"); continue; }
                if (U.IsForbidden(U.PrefabName(nv))) { errs.Add(id + ": forbidden prefab"); continue; }
                nv.ClaimOwnership();
                var t = nv.transform;
                Journal.Add(new JEntry { Kind = "modify", Id = id, Prefab = U.PrefabName(nv), Pos = t.position, Rot = t.rotation, Scale = t.localScale, Text = z.GetString("text", "") });
                var pos = t.position;
                if (U.Has(e, "x")) pos.x = (float)McpJson.Get(e, "x", pos.x);
                if (U.Has(e, "y")) pos.y = (float)McpJson.Get(e, "y", pos.y);
                if (U.Has(e, "z")) pos.z = (float)McpJson.Get(e, "z", pos.z);
                var rot = (U.Has(e, "yaw") || U.Has(e, "rot")) ? RotOf(e) : t.rotation;
                t.position = pos; t.rotation = rot; z.SetPosition(pos); z.SetRotation(rot);
                if (McpJson.Get(e, "scale", 0) > 0) t.localScale = Vector3.one * (float)McpJson.Get(e, "scale", 1);
                var text = McpJson.GetStr(e, "text"); if (text != null) z.Set("text", text);
                var ptag = McpJson.GetStr(e, "tag"); if (ptag != null) z.Set("tag", ptag);
                done++;
            }
            var ej = new List<string>(); foreach (var er in errs) ej.Add(U.S(er));
            return U.Json("{\"modified\":" + done + ",\"errors\":[" + string.Join(",", ej.ToArray()) + "]}");
        }

        public static ToolOutput Delete(Dictionary<string, object> a)
        {
            var targets = new List<ZNetView>();
            var idl = McpJson.GetList(a, "ids");
            if (idl != null)
            {
                foreach (var o in idl) { var z = U.FindZdo(o as string); var nv = z == null ? null : ZNetScene.instance.FindInstance(z); if (nv != null && !U.IsForbidden(U.PrefabName(nv))) targets.Add(nv); }
            }
            else if (U.Has(a, "x0"))
            {
                var filters = U.Split(McpJson.GetStr(a, "prefab"));
                var bf = U.Has(a, "built") ? (bool?)McpJson.GetBool(a, "built", false) : null;
                foreach (var nv in U.Instances())
                {
                    if (!U.InBox(nv.transform.position, a)) continue;
                    if (U.IsForbidden(U.PrefabName(nv))) continue;
                    if (!U.MatchPrefab(U.PrefabName(nv), filters)) continue;
                    if (bf != null && ((nv.GetZDO().GetLong("creator", 0L) != 0L) != bf.Value)) continue;
                    targets.Add(nv);
                }
                if (filters == null) return ToolOutput.Err("box delete requires a prefab filter");
                if (!McpJson.GetBool(a, "dryRun", false) && !McpJson.GetBool(a, "confirm", false)) return ToolOutput.Err("box delete matched " + targets.Count + " objects; pass confirm=true (or dryRun=true)");
            }
            else return ToolOutput.Err("give ids[] or x0,z0,x1,z1 + prefab");
            var max = (int)McpJson.Get(a, "max", 500);
            if (targets.Count > max) return ToolOutput.Err("would delete " + targets.Count + " > max " + max + "; raise max or narrow the filter");
            if (McpJson.GetBool(a, "dryRun", false)) return U.Json("{\"wouldDelete\":" + targets.Count + "}");
            var n = 0; var skipped = 0;
            foreach (var nv in targets)
            {
                try
                {
                    if (nv == null || nv.GetZDO() == null) { skipped++; continue; }     // already destroyed (stale id)
                    var z = nv.GetZDO(); var t = nv.transform;
                    Journal.Add(new JEntry { Kind = "delete", Id = U.Id(z), Prefab = U.PrefabName(nv), Pos = t.position, Rot = t.rotation, Scale = t.localScale, Text = z.GetString("text", ""), Creator = z.GetLong("creator", 0L) });
                    nv.ClaimOwnership();
                    ZNetScene.instance.Destroy(nv.gameObject);
                    n++;
                }
                catch (Exception) { skipped++; }
            }
            return U.Json("{\"deleted\":" + n + ",\"skipped\":" + skipped + "}");
        }

        public static ToolOutput Undo(Dictionary<string, object> a)
        {
            var count = (int)McpJson.Get(a, "count", 1);
            var undone = 0; var errs = new List<string>();
            while (count-- > 0 && Journal.Entries.Count > 0)
            {
                var e = Journal.Entries[Journal.Entries.Count - 1];
                Journal.Entries.RemoveAt(Journal.Entries.Count - 1);
                try
                {
                    if (e.Kind == "spawn")
                    {
                        var z = U.FindZdo(e.Id); var nv = z == null ? null : ZNetScene.instance.FindInstance(z);
                        if (nv != null) { nv.ClaimOwnership(); ZNetScene.instance.Destroy(nv.gameObject); }
                    }
                    else if (e.Kind == "delete")
                    {
                        var prefab = ZNetScene.instance.GetPrefab(e.Prefab);
                        if (prefab != null)
                        {
                            var go = UnityEngine.Object.Instantiate(prefab, e.Pos, e.Rot); go.transform.localScale = e.Scale;
                            var nv = go.GetComponent<ZNetView>();
                            if (nv != null && nv.GetZDO() != null) { if (!string.IsNullOrEmpty(e.Text)) nv.GetZDO().Set("text", e.Text); if (e.Creator != 0) nv.GetZDO().Set("creator", e.Creator); }
                        }
                    }
                    else if (e.Kind == "modify")
                    {
                        var z = U.FindZdo(e.Id); var nv = z == null ? null : ZNetScene.instance.FindInstance(z);
                        if (nv != null) { nv.ClaimOwnership(); nv.transform.position = e.Pos; nv.transform.rotation = e.Rot; nv.transform.localScale = e.Scale; z.SetPosition(e.Pos); z.SetRotation(e.Rot); z.Set("text", e.Text ?? ""); }
                    }
                    undone++;
                }
                catch (Exception ex) { errs.Add(e.Kind + " " + e.Id + ": " + ex.Message); }
            }
            var ej = new List<string>(); foreach (var er in errs) ej.Add(U.S(er));
            return U.Json("{\"undone\":" + undone + ",\"errors\":[" + string.Join(",", ej.ToArray()) + "]}");
        }
    }
}
