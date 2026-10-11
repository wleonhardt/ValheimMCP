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
                        Reg.Add(r, "terrain_info",
                "True terrain at (x,z) as the running game sees it (includes any terrain edits): ground height, solid height (top of terrain OR structures), what that solid is, surface normal, biome, water, and whether the zone is loaded. Unloaded zones return loaded=false (teleport near first).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Read.TerrainInfo);
            Reg.Add(r, "terrain_grid",
                "Sample true ground height on a grid. Returns rows of heights (null where the zone is not loaded). Max 6000 samples.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"step\":{\"type\":\"number\",\"description\":\"metres between samples (default 4)\"},\"solid\":{\"type\":\"boolean\",\"description\":\"sample top of structures too (default false = terrain only)\"}},\"required\":[\"x0\",\"z0\",\"x1\",\"z1\"]}", Read.TerrainGrid);
            Reg.Add(r, "raycast",
                "Cast a ray (origin x,y,z; direction dx,dy,dz) against solid world geometry. Returns hit point, normal, collider/prefab name, ZDO id.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"dx\":{\"type\":\"number\"},\"dy\":{\"type\":\"number\"},\"dz\":{\"type\":\"number\"},\"maxDist\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\",\"dx\",\"dy\",\"dz\"]}", Read.Raycast);
            Reg.Add(r, "object_info",
                "Everything about one object by id (format 'ID:USERID' as RCON prints): prefab, transform, creator, health, support, sign text, bounds, components.",
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}", Read.ObjectInfo);
            Reg.Add(r, "prefab_info",
                "Authoritative geometry of a prefab: pivot offset, size (local bounds), colliders, snap points, front direction, piece category/comfort/ground rules, structural material, key components. Replaces guessing from a catalogue.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}", Read.PrefabInfo);
                                    Reg.Add(r, "zone_state",
                "Is the area around (x,z) loaded and populated? Poll after teleport. Returns zoneLoaded, ground height ok, instances within radius.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Read.ZoneState);
            Reg.Add(r, "teleport",
                "Teleport the local player (the sandbox character). Characters named in hubner.sandboxNames also get god mode so teleports and falls cannot hurt them; any other character is left mortal. snap: solid (default, highest solid incl. roofs) | ground (terrain only) | none (keep altitude). y optional: defaults to solid ground + 1 when the zone is loaded, otherwise keeps the current altitude (groundSnapped=false: call again after zone_state reports loaded).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"snap\":{\"type\":\"string\"},\"distant\":{\"type\":\"boolean\"}},\"required\":[\"x\",\"z\"]}", Read.Teleport, true);
            Reg.Add(r, "render_ex",
                "Off-screen render with cutaway controls: fov, near clip, orthographic top-down, hideAboveY (hides every object whose bounds are entirely above that height, to see into rooms through roofs), hidePrefabs (comma substrings to hide). Same yaw/pitch/dist convention as render_view (yaw = camera azimuth, 0 = camera north of target).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"yaw\":{\"type\":\"number\"},\"pitch\":{\"type\":\"number\"},\"dist\":{\"type\":\"number\"},\"size\":{\"type\":\"number\"},\"fov\":{\"type\":\"number\"},\"near\":{\"type\":\"number\"},\"ortho\":{\"type\":\"boolean\"},\"orthoSize\":{\"type\":\"number\"},\"hideAboveY\":{\"type\":\"number\"},\"hidePrefabs\":{\"type\":\"string\"},\"far\":{\"type\":\"number\"},\"hideCentreAboveY\":{\"type\":\"number\"},\"avoid\":{\"type\":\"boolean\"},\"hideTrees\":{\"type\":\"boolean\"},\"hidePlan\":{\"type\":\"string\"},\"cutRadius\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Render.Ex);
            Reg.Add(r, "walk_check",
                "Simulate a player walking a polyline of waypoints [[x,y,z],...] (max 500 points, 1500 m) with the player's capsule (radius 0.49, height 1.85, stepHeight 0.45, maxSlopeDeg 38; run:true allows 60 because a runner climbs steep banks; trace:true, traceEvery). Closed doors are treated as passable (players open them). Reports the first blocking collider, headroom failures and unsupported drops. Use it to prove stairs, doors and corridors are walkable.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"radius\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"stepHeight\":{\"type\":\"number\"},\"maxSlopeDeg\":{\"type\":\"number\"},\"run\":{\"type\":\"boolean\"},\"trace\":{\"type\":\"boolean\"},\"traceEvery\":{\"type\":\"number\"}},\"required\":[\"points\"]}", Walk.Check);
            Reg.Add(r, "stability_scan",
                "Structural support of the supporting pieces (WearNTear with m_supports, so furniture and torches are excluded) in a box, from the game's own cached values: pieces, min/avg, atRisk = pieces whose support is below their material's own GetMinSupport (what falls when NoBuildingFall is off), and the list below `threshold` (default: each piece's minSupport). Values are a converging iteration seeded from ground contact: scan a freshly spawned plan only after ~90 s with the area instantiated.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"threshold\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"x0\",\"z0\",\"x1\",\"z1\"]}", Read.Stability);
            Reg.Add(r, "inspect_type", "Debug: list fields/methods of a game type by name (substring filter on members).",
                "{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\"},\"filter\":{\"type\":\"string\"}},\"required\":[\"type\"]}", Read.InspectType);
            
            // ---- control (gated by tools.write)
            Reg.Add(r, "spawn",
                "Spawn pieces/objects. items:[{prefab,x,y,z,yaw|rot:[rx,ry,rz],scale,text,tag,creator,snap:'ground'}] (max 1000 per call). y is the prefab pivot height unless snap='ground'. 'text' sets sign text; 'tag' sets a portal's tag (required for portal prefabs); 'creator' (long) marks the piece player-built. Only loaded zones; set force=true to override. Creatures, ships, carts and dropped items need forceConfirm:'player-built'. Returns ids; every spawn is journaled for undo.",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"force\":{\"type\":\"boolean\"},\"forceConfirm\":{\"type\":\"string\"}},\"required\":[\"items\"]}", Write.Spawn, true);
            Reg.Add(r, "modify",
                "Modify objects by id: x,y,z, yaw|rot, scale, text, tag. Journaled. Refuses players, creatures, ships, carts, dropped items and player-built objects (creator!=0) unless force=true AND forceConfirm:'player-built'.",
                "{\"type\":\"object\",\"properties\":{\"edits\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"force\":{\"type\":\"boolean\"},\"forceConfirm\":{\"type\":\"string\"}},\"required\":[\"edits\"]}", Write.Modify, true);
            Reg.Add(r, "delete",
                "Delete objects by ids, or by box+prefab filter (requires confirm=true and a count <= max, default 500). dryRun=true only counts. Journaled for undo. Refuses players, creatures, ships, carts, dropped items and player-built objects (creator!=0) unless force=true AND forceConfirm:'player-built'.",
                "{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"prefab\":{\"type\":\"string\"},\"built\":{\"type\":\"boolean\"},\"max\":{\"type\":\"number\"},\"confirm\":{\"type\":\"boolean\"},\"dryRun\":{\"type\":\"boolean\"},\"force\":{\"type\":\"boolean\"},\"forceConfirm\":{\"type\":\"string\"}}}", Write.Delete, true);
            Reg.Add(r, "undo", "Undo the last n journaled write operations (spawn->delete, delete->respawn, modify->restore).",
                "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"number\"}}}", Write.Undo, true);
#if !SERVER
            Place.Register(r);
            Control.Register(r);
#endif
            Geo.Register(r);
#if !SERVER
            Insight.Register(r);
#endif
        }
    }

    // ------------------------------------------------------------------ read tools
    internal static class Read
    {
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
            double fx = Math.Floor((x1 - x0) / step) + 1, fz = Math.Floor((z1 - z0) / step) + 1;
            if (double.IsNaN(fx) || double.IsNaN(fz) || fx * fz > 6000) return ToolOutput.Err("too many samples (" + U.N(fx * fz) + " > 6000); increase step");       // in double: int * int wraps for a huge box
            var nx = (int)fx; var nz = (int)fz;
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
                    float h = 0f; var ok = zs.IsZoneLoaded(p) && (solid ? zs.GetSolidHeight(p, out h) : zs.GetGroundHeight(p, out h));
                    if (ok) sb.Append(U.N(Math.Round(h, 2)));
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

        public static ToolOutput PrefabInfo(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var name = McpJson.GetStr(a, "name") ?? McpJson.GetStr(a, "prefab");
            if (string.IsNullOrEmpty(name)) return ToolOutput.Err("name required (the prefab name)");
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
            var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0);
            if (!WorldBounds.InWorld(x, U.Has(a, "y") ? U.D(a, "y", 0) : 0, z)) return ToolOutput.Err("x, y and z must be finite numbers inside the world");
            // the sandbox character is a tool, not a player: never let a teleport or a fall hurt (or kill) whoever is watching; anyone else stays mortal
            var god = U.IsSandbox(pl); if (god) pl.SetGodMode(true);
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
            return U.Json("{\"teleporting\":" + (ok ? "true" : "false") + ",\"accepted\":" + (ok ? "true" : "false") + ",\"distant\":" + (dist ? "true" : "false") + ",\"to\":" + U.V(new Vector3(x, y, z)) + ",\"groundSnapped\":" + (snapped ? "true" : "false") + ",\"godMode\":" + (god ? "true" : "false") + "}");
        }

        static readonly MethodInfo _minSup = typeof(WearNTear).GetMethod("GetMinSupport", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static float MinSupport(WearNTear w) { try { return _minSup != null ? (float)_minSup.Invoke(w, null) : 0f; } catch (Exception ex) { U.Once("GetMinSupport", ex); return 0f; } }

        public static ToolOutput Stability(Dictionary<string, object> a)
        {
            if (!U.Has(a, "x0")) return ToolOutput.Err("x0,z0,x1,z1 required");
            bool hasThr = U.Has(a, "threshold"); var thr = (float)U.D(a, "threshold", 0); var lim = (int)U.D(a, "limit", 100);
            var low = new List<string>(); var n = 0; double sum = 0; float min = float.MaxValue; int atRisk = 0, skipped = 0;
            foreach (var nv in U.Instances())
            {
                if (!U.InBox(nv.transform.position, a)) continue;
                var w = nv.GetComponent<WearNTear>(); if (w == null) continue;
                if (!w.m_supports) { skipped++; continue; }                                  // furniture, torches, banners: carry no support, cannot collapse
                var s = nv.GetZDO().GetFloat("support", -1f);
                if (s < 0) continue;
                float minSup = MinSupport(w);
                n++; sum += s; if (s < min) min = s;
                bool risk = s < minSup; if (risk) atRisk++;
                if ((hasThr ? s < thr : risk) && low.Count < lim) low.Add("{\"id\":" + U.S(U.Id(nv.GetZDO())) + ",\"prefab\":" + U.S(U.PrefabName(nv)) + ",\"pos\":" + U.V(nv.transform.position) + ",\"support\":" + U.N(s) + ",\"minSupport\":" + U.N(minSup) + ",\"atRisk\":" + (risk ? "true" : "false") + "}");
            }
            return U.Json("{\"pieces\":" + n + ",\"nonSupporting\":" + skipped + ",\"min\":" + (n > 0 ? U.N(min) : "null") + ",\"avg\":" + (n > 0 ? U.N(sum / n) : "null") + ",\"atRisk\":" + atRisk + ",\"below\":[" + string.Join(",", low.ToArray()) + "]}");
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
            int minSize = Math.Max(64, Cfg.Int("render.minSize", 256)), maxSize = Math.Max(minSize, Cfg.Int("render.maxSize", 1280));     // same limits as render_view
            var size = Math.Max(minSize, Math.Min(maxSize, (int)U.D(a, "size", Cfg.Int("render.defaultSize", 768))));
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
            cam.fieldOfView = Mathf.Clamp((float)U.D(a, "fov", 60), 5f, 150f);
            cam.nearClipPlane = (float)Math.Max(0.01, U.D(a, "near", 0.1));
            cam.farClipPlane = U.Has(a, "far") ? (float)U.D(a, "far", 100) : dist + 1500f;

            var hidden = new List<Renderer>();
            var rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active; Texture2D tex = null;
            try
            {
                // cutaway inside the try: whatever fails while renderers are being switched off, the finally switches them back on
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
                        var byName = (hideNames != null && U.MatchPrefab(name, hideNames)) || (hidePlan != null && nv.GetZDO().GetString(Plans.PlanHash, "") == hidePlan);
                        foreach (var r in nv.GetComponentsInChildren<Renderer>())
                        {
                            if (r == null || !r.enabled) continue;
                            if (byName || (hideY != null && r.bounds.min.y > hideY.Value) || (hideC != null && r.bounds.center.y > hideC.Value)) { r.enabled = false; hidden.Add(r); }
                        }
                    }
                }
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
            var radius = (float)U.D(a, "radius", WalkFacts.CapsuleRadius); var height = (float)U.D(a, "height", WalkFacts.CapsuleHeight);
            var stepH = (float)U.D(a, "stepHeight", WalkFacts.StepUp);
            var maxSlope = (float)U.D(a, "maxSlopeDeg", McpJson.GetBool(a, "run", false) ? WalkFacts.RunClimbMaxDeg : WalkFacts.SlideDeg);       // a walker slips on terrain over 38 degrees; at a run banks up to 60 are climbed var wantTrace = McpJson.GetBool(a, "trace", false); var trace = new List<string>();
            var mask = Mask();
            if (mask == 0) return ToolOutput.Err("none of the expected physics layers exist (mask==0): walk_check would pass everything; refusing");
            var P = new List<Vector3>();
            foreach (var o in pts)
            { var l = o as List<object>; if (l == null || l.Count < 3) return ToolOutput.Err("point must be [x,y,z]"); var v = U.Vec(l); if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return ToolOutput.Err("point must be [x,y,z] numbers"); P.Add(v); }
            if (P.Count > 500) return ToolOutput.Err("max 500 points");
            float pathLen = 0f; for (var q = 1; q < P.Count; q++) pathLen += Vector3.Distance(P[q - 1], P[q]);
            if (pathLen > 1500f) return ToolOutput.Err("path is " + U.N(Math.Round(pathLen)) + " m long (max 1500 m per call: it is checked every 0.2 m on the main thread); split it");
            var cur = P[0];
            const float ds = 0.2f;
            var steps = 0; var maxRise = 0f;
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
                        if (rise > 0.3f && rise / step > Mathf.Tan(maxSlope * Mathf.Deg2Rad))      // small ledges are steps, only real rises count as slope
                        { blocked = "slope too steep (" + U.N(Math.Round(Mathf.Atan2(rise, step) * Mathf.Rad2Deg, 1)) + " deg, rise " + U.N(Math.Round(rise, 2)) + " onto " + Describe(gh.collider) + ")"; blockAt = cur; break; }
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
            if (l != null && l.Count >= 3) return Quaternion.Euler((float)U.At(l, 0), (float)U.At(l, 1), (float)U.At(l, 2));
            return Quaternion.Euler(0f, (float)McpJson.Get(d, "yaw", 0), 0f);
        }

        public static ToolOutput Spawn(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var items = McpJson.GetList(a, "items");
            if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            if (items.Count > 1000) return ToolOutput.Err("max 1000 items per call");
            var force = McpJson.GetBool(a, "force", false);
            bool livingOk = McpJson.GetStr(a, "forceConfirm") == Plans.ForceConfirm;
            var ids = new List<string>(); var errs = new List<string>();
            Journal.Begin();
            try
            {
                foreach (var o in items)
                {
                    var it = o as Dictionary<string, object>;
                    GameObject go = null; var registered = false;
                    try
                    {
                        var name = McpJson.GetStr(it, "prefab");
                        if (string.IsNullOrEmpty(name)) { errs.Add("item without a prefab"); ids.Add(null); continue; }
                        if (U.IsForbidden(name) || name == "Player" || name.StartsWith("Player", StringComparison.Ordinal)) { errs.Add(name + ": forbidden prefab (terrain/internal/player)"); ids.Add(null); continue; }
                        var prefab = ZNetScene.instance.GetPrefab(name);
                        if (prefab == null) { errs.Add(name + ": unknown prefab"); ids.Add(null); continue; }
                        if (!livingOk && U.IsLiving(name)) { errs.Add(name + ": creatures, ships, carts and dropped items need forceConfirm:'" + Plans.ForceConfirm + "'"); ids.Add(null); continue; }
                        if (name.StartsWith("portal", StringComparison.Ordinal) && string.IsNullOrEmpty(McpJson.GetStr(it, "tag"))) { errs.Add(name + ": portals need a tag (untagged portals pair with every other untagged portal)"); ids.Add(null); continue; }
                        var pos = new Vector3((float)McpJson.Get(it, "x", 0), (float)McpJson.Get(it, "y", 0), (float)McpJson.Get(it, "z", 0));
                        if (McpJson.GetStr(it, "snap") == "ground") { float h; if (ZoneSystem.instance.GetGroundHeight(new Vector3(pos.x, 5000f, pos.z), out h)) pos.y = h; }
                        if (!WorldBounds.InWorld(pos.x, pos.y, pos.z)) { errs.Add(name + ": position not finite or outside the world"); ids.Add(null); continue; }
                        if (!force && ZoneSystem.instance != null && !ZoneSystem.instance.IsZoneLoaded(pos)) { errs.Add(name + ": zone not loaded at " + U.V(pos)); ids.Add(null); continue; }
                        var rot = RotOf(it);
                        go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                        var scale = McpJson.Get(it, "scale", 0);
                        if (scale > 0 && scale <= 20) go.transform.localScale = Vector3.one * (float)scale;
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
                        registered = true;
                    }
                    catch (Exception ex) { errs.Add("exception: " + ex.Message); ids.Add(null); }
                    finally
                    {
                        // an object that was instantiated but not registered in the journal would be an orphan nobody can undo
                        if (!registered && go != null) { try { UnityEngine.Object.Destroy(go); } catch (Exception ex) { U.Once("Spawn.cleanup", ex); } }
                    }
                }
            }
            finally { Journal.End(); }
            var idj = new List<string>(); foreach (var i in ids) idj.Add(i == null ? "null" : U.S(i));
            var ej = new List<string>(); foreach (var e in errs) ej.Add(U.S(e));
            return U.Json("{\"spawned\":" + (ids.Count - errs.Count) + ",\"ids\":[" + string.Join(",", idj.ToArray()) + "],\"errors\":[" + string.Join(",", ej.ToArray()) + "]}");
        }

        public static ToolOutput Modify(Dictionary<string, object> a)
        {
            var edits = McpJson.GetList(a, "edits");
            if (edits == null) return ToolOutput.Err("edits required");
            var done = 0; var errs = new List<string>();
            Journal.Begin();
            try
            {
                foreach (var o in edits)
                {
                    var e = o as Dictionary<string, object>;
                    var id = McpJson.GetStr(e, "id");
                    var z = U.FindZdo(id);
                    var nv = z == null ? null : ZNetScene.instance.FindInstance(z);
                    if (nv == null) { errs.Add(id + ": not found/instantiated"); continue; }
                    var prefabName = U.PrefabName(nv);
                    if (U.IsForbidden(prefabName)) { errs.Add(id + ": forbidden prefab"); continue; }
                    if (Plans.Protected(z, prefabName, a, out var why)) { errs.Add(id + ": " + why); continue; }       // same rules as zdo_set: no players, creatures or player-built objects without force + forceConfirm
                    var pos = nv.transform.position;
                    if (U.Has(e, "x")) pos.x = (float)McpJson.Get(e, "x", pos.x);
                    if (U.Has(e, "y")) pos.y = (float)McpJson.Get(e, "y", pos.y);
                    if (U.Has(e, "z")) pos.z = (float)McpJson.Get(e, "z", pos.z);
                    if (!WorldBounds.InWorld(pos.x, pos.y, pos.z)) { errs.Add(id + ": position not finite or outside the world"); continue; }
                    nv.ClaimOwnership();
                    var t = nv.transform;
                    Journal.Add(new JEntry { Kind = "modify", Id = id, Prefab = prefabName, Pos = t.position, Rot = t.rotation, Scale = t.localScale, Text = z.GetString("text", ""), Tag = z.GetString("tag", ""), Plan = z.GetString(Plans.PlanHash, ""), Key = z.GetString(Plans.ItemHash, ""), Creator = z.GetLong("creator", 0L) });
                    var rot = (U.Has(e, "yaw") || U.Has(e, "rot")) ? RotOf(e) : t.rotation;
                    t.position = pos; t.rotation = rot; z.SetPosition(pos); z.SetRotation(rot);
                    var scale = McpJson.Get(e, "scale", 0);
                    if (scale > 0 && scale <= 20) t.localScale = Vector3.one * (float)scale;
                    var text = McpJson.GetStr(e, "text"); if (text != null) z.Set("text", text);
                    var ptag = McpJson.GetStr(e, "tag"); if (ptag != null) z.Set("tag", ptag);
                    done++;
                }
            }
            finally { Journal.End(); }
            var ej = new List<string>(); foreach (var er in errs) ej.Add(U.S(er));
            return U.Json("{\"modified\":" + done + ",\"errors\":[" + string.Join(",", ej.ToArray()) + "]}");
        }

        public static ToolOutput Delete(Dictionary<string, object> a)
        {
            var targets = new List<ZNetView>(); var errs = new List<string>();
            var idl = McpJson.GetList(a, "ids");
            if (idl != null)
            {
                foreach (var o in idl)
                {
                    var id = o as string; var z = U.FindZdo(id); var nv = z == null ? null : ZNetScene.instance.FindInstance(z);
                    if (nv == null) { errs.Add(id + ": not found/instantiated"); continue; }
                    var pn = U.PrefabName(nv);
                    if (U.IsForbidden(pn)) { errs.Add(id + ": forbidden prefab"); continue; }
                    if (Plans.Protected(z, pn, a, out var why)) { errs.Add(id + ": " + why); continue; }       // players, creatures, ships and player-built objects need force + forceConfirm
                    targets.Add(nv);
                }
            }
            else if (U.Has(a, "x0"))
            {
                var filters = U.Split(McpJson.GetStr(a, "prefab"));
                if (filters == null) return ToolOutput.Err("box delete requires a prefab filter");
                var bf = U.Has(a, "built") ? (bool?)McpJson.GetBool(a, "built", false) : null;
                foreach (var nv in U.Instances())
                {
                    if (!U.InBox(nv.transform.position, a)) continue;
                    var pn = U.PrefabName(nv);
                    if (U.IsForbidden(pn)) continue;
                    if (!U.MatchPrefab(pn, filters)) continue;
                    if (bf != null && ((nv.GetZDO().GetLong("creator", 0L) != 0L) != bf.Value)) continue;
                    if (Plans.Protected(nv.GetZDO(), pn, a, out _)) continue;                       // a box sweep silently skips what an id delete would refuse
                    targets.Add(nv);
                }
                if (!McpJson.GetBool(a, "dryRun", false) && !McpJson.GetBool(a, "confirm", false)) return ToolOutput.Err("box delete matched " + targets.Count + " objects; pass confirm=true (or dryRun=true)");
            }
            else return ToolOutput.Err("give ids[] or x0,z0,x1,z1 + prefab");
            var max = (int)McpJson.Get(a, "max", 500);
            if (targets.Count > max) return ToolOutput.Err("would delete " + targets.Count + " > max " + max + "; raise max or narrow the filter");
            if (McpJson.GetBool(a, "dryRun", false)) return U.Json("{\"wouldDelete\":" + targets.Count + ",\"errors\":[" + string.Join(",", errs.Select(U.S).ToArray()) + "]}");
            var n = 0; var skipped = 0;
            Journal.Begin();
            try
            {
                foreach (var nv in targets)
                {
                    try
                    {
                        if (nv == null || nv.GetZDO() == null) { skipped++; continue; }     // already destroyed (stale id)
                        var z = nv.GetZDO(); var t = nv.transform;
                        var entry = new JEntry { Kind = "delete", Id = U.Id(z), Prefab = U.PrefabName(nv), Pos = t.position, Rot = t.rotation, Scale = t.localScale, Text = z.GetString("text", ""), Tag = z.GetString("tag", ""), Plan = z.GetString(Plans.PlanHash, ""), Key = z.GetString(Plans.ItemHash, ""), Creator = z.GetLong("creator", 0L) };
                        var items = z.GetString("items", ""); if (items.Length > 0) entry.Strings = new Dictionary<string, string> { { "items", items } };
                        nv.ClaimOwnership();
                        ZNetScene.instance.Destroy(nv.gameObject);
                        Journal.Add(entry);                                                  // after the destroy: a failed destroy leaves no entry that "undo" would turn into a duplicate
                        n++;
                    }
                    catch (Exception ex) { skipped++; U.Once("Delete", ex); }
                }
            }
            finally { Journal.End(); }
            return U.Json("{\"deleted\":" + n + ",\"skipped\":" + skipped + ",\"errors\":[" + string.Join(",", errs.Select(U.S).ToArray()) + "]}");
        }

        /// <summary>Undo the last n entries through the shared journal path (marks them undone on disk, so a restart does not replay them twice). An entry that cannot be undone stays in the journal.</summary>
        public static ToolOutput Undo(Dictionary<string, object> a)
        {
            var count = (int)McpJson.Get(a, "count", 1);
            var take = new List<JEntry>();
            for (var i = Journal.Entries.Count - 1; i >= 0 && take.Count < count; i--) take.Add(Journal.Entries[i]);
            var undone = 0; var errs = new List<string>();
            Journal.Begin();
            try
            {
                foreach (var e in take)
                {
                    try
                    {
                        var err = Plans.UndoEntry(e);
                        if (err != null) { errs.Add(err); continue; }
                        Journal.Remove(e); undone++;
                    }
                    catch (Exception ex) { errs.Add(e.Kind + " " + e.Id + ": " + ex.Message); }
                }
            }
            finally { Journal.End(); }
            var ej = new List<string>(); foreach (var er in errs) ej.Add(U.S(er));
            return U.Json("{\"undone\":" + undone + ",\"errors\":[" + string.Join(",", ej.ToArray()) + "]}");
        }
    }
}
