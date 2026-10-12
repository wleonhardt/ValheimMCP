// Terrain editing (decision 0006): terrain_paint and terrain_level apply the game's own registered terrain ops (the ones a hoe stroke instantiates) at points this file computes, after saving
// the compressed terrain data of every zone the edit can reach, so undo / undo_group put it back byte for byte. Needs a heightmap loaded for the point (a client near it, or a virtual peer on the
// server). Never writes height or paint by hand.
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
    internal static class TerrainEdit
    {
        const int MaxStrokes = 300; const double MaxRadius = 40;
        static readonly int CompilerHash = "_TerrainCompiler".GetStableHashCode();

        static string Dir { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext", "terrain"); } }

        public static void Register(ToolRegistry r)
        {
            r.Add("terrain_paint", "Paint the ground with the game's own hoe ops: type cultivated | paved | dirt, over a disc (x, z, radius up to 40 m) or a box (x0,z0,x1,z1). The ops are registered terrain-op prefabs (cultivate_v2, paved_road_v2, mud_road_v2), so, as with the hoe, they also smooth the terrain a little (heightChanged tells how much). At most 300 strokes per call. Refused inside an enabled guard stone's area (force:true + forceConfirm:'player-built'), inside a known no-build location, and where no heightmap is loaded (a client near it, or a virtual peer on the server). The terrain data of every zone the edit can reach is saved first: undo / undo_group restore it byte for byte. dry=true reports strokes, zones and refusals only. Journaled.",
                "{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"dry\":{\"type\":\"boolean\"},\"group\":{\"type\":\"string\"},\"force\":{\"type\":\"boolean\"},\"forceConfirm\":{\"type\":\"string\"},\"confirm\":{\"type\":\"string\"}},\"required\":[\"type\"]}",
                a => Plans.GuardPublic(a, Paint, true), Plans.WriteFlag);
            r.Add("terrain_level", "Level a rectangular pad (x0,z0,x1,z1 or x,z,half; up to 40 x 40 m) to targetY with the game's own level op (the hoe/digging strokes), on the lattice level_check plans. A stroke never moves a vertex more than 8 m from its generated height, so an unreachable pad is refused (use level_check first). Refused when a built piece stands on the pad or within the stroke radius of it (allowPieces:true overrides), inside an enabled guard stone's area, inside a no-build location, and where no heightmap is loaded. tool picks the op prefab (default: the level op that needs fewest strokes, one that does not paint if the game has any; alsoPaints says). This game has no terrain op that levels, so the strokes are the older level prefabs (digg, digg_v2): each leaves a persistent object in the world, journaled, and undo destroys exactly those. Reports the heights read back after the strokes. dry=true plans only. Journaled.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"half\":{\"type\":\"number\"},\"targetY\":{\"type\":\"number\"},\"tool\":{\"type\":\"string\"},\"allowPieces\":{\"type\":\"boolean\"},\"dry\":{\"type\":\"boolean\"},\"group\":{\"type\":\"string\"},\"force\":{\"type\":\"boolean\"},\"forceConfirm\":{\"type\":\"string\"},\"confirm\":{\"type\":\"string\"}},\"required\":[\"targetY\"]}",
                a => Plans.GuardPublic(a, Level, true), Plans.WriteFlag);
        }

        // ------------------------------------------------------------------ the compilers
        static ZDO FindCompiler(int zx, int zz)
        {
            double cx = zx * ActiveArea.ZoneSize, cz = zz * ActiveArea.ZoneSize;
            foreach (var z in Zdos.InBox((float)(cx - 2), (float)(cz - 2), (float)(cx + 2), (float)(cz + 2)))
            {
                if (z.GetPrefab() != CompilerHash) continue;
                var p = z.GetPosition(); if (Math.Abs(p.x - cx) < 2 && Math.Abs(p.z - cz) < 2) return z;
            }
            return null;
        }

        static string Key(int zx, int zz) { return "z:" + zx + "," + zz; }

        /// <summary>Saves the compressed terrain data of each zone ("none" when the zone has no data) and returns zone key -> file name.</summary>
        static Dictionary<string, string> Snapshot(IEnumerable<int[]> zones, string tag)
        {
            Directory.CreateDirectory(Dir);
            var res = new Dictionary<string, string>();
            foreach (var zn in zones)
            {
                var z = FindCompiler(zn[0], zn[1]); var bytes = z != null ? z.GetByteArray("TCData") : null;
                if (bytes == null) { res[Key(zn[0], zn[1])] = "none"; continue; }
                var file = tag + "_" + zn[0] + "_" + zn[1] + ".tc"; File.WriteAllBytes(Path.Combine(Dir, file), bytes); res[Key(zn[0], zn[1])] = file;
            }
            return res;
        }

        /// <summary>Journal undo of a kind "terrain" entry: write each saved zone back (a zone that had no data gets the "no edits" state).</summary>
        internal static string Undo(JEntry e)
        {
            if (e.Strings == null || e.Strings.Count == 0) return "terrain: the entry names no zone";
            var errs = new List<string>(); int gone = 0;
            foreach (var kv in e.Strings)
            {
                if (kv.Key.StartsWith("p:", StringComparison.Ordinal)) { if (!DestroyModifier(kv.Key.Substring(2))) gone++; continue; }
                if (!kv.Key.StartsWith("z:", StringComparison.Ordinal)) continue;
                var parts = kv.Key.Substring(2).Split(','); int zx, zz;
                if (parts.Length != 2 || !int.TryParse(parts[0], out zx) || !int.TryParse(parts[1], out zz)) { errs.Add(kv.Key + ": bad zone"); continue; }
                var z = FindCompiler(zx, zz);
                byte[] bytes;
                if (kv.Value == "none") bytes = Utils.Compress(TcData.Empty().Encode());
                else
                {
                    var path = Path.Combine(Dir, Path.GetFileName(kv.Value));                    // a file name only: nothing in the journal can point outside the folder
                    if (!File.Exists(path)) { errs.Add(kv.Key + ": the saved terrain file " + kv.Value + " is gone"); continue; }
                    bytes = File.ReadAllBytes(path);
                }
                if (z == null) { if (kv.Value == "none") continue; errs.Add(kv.Key + ": the zone has no terrain compiler now"); continue; }
                try { z.SetOwner(ZDOMan.GetSessionID()); z.Set("TCData", bytes); }
                catch (Exception ex) { errs.Add(kv.Key + ": " + ex.Message); }   // allow: the message goes back to the caller
            }
            if (gone > 0) errs.Add(gone + " terrain modifier objects were already gone");
            return errs.Count == 0 ? null : "terrain: " + string.Join("; ", errs.ToArray());
        }

        static string PKey(string prefab, Vector3 p) { return "p:" + prefab + "@" + p.x.ToString("R", CultureInfo.InvariantCulture) + "," + p.y.ToString("R", CultureInfo.InvariantCulture) + "," + p.z.ToString("R", CultureInfo.InvariantCulture); }

        /// <summary>Destroys the persistent terrain modifier object that a stroke left at the position ("prefab@x,y,z"); false when it is not there.</summary>
        static bool DestroyModifier(string spec)
        {
            var at = spec.IndexOf('@'); if (at <= 0) return false;
            var c = spec.Substring(at + 1).Split(','); float x, y, z;
            if (c.Length != 3 || !float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) || !float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            var h = spec.Substring(0, at).GetStableHashCode();
            foreach (var zdo in Zdos.InBox(x - 0.5f, z - 0.5f, x + 0.5f, z + 0.5f))
            {
                if (zdo.GetPrefab() != h) continue;
                var p = zdo.GetPosition(); if (Math.Abs(p.x - x) > 0.02 || Math.Abs(p.y - y) > 0.02 || Math.Abs(p.z - z) > 0.02) continue;
                var nv = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                if (nv != null) { nv.ClaimOwnership(); ZNetScene.instance.Destroy(nv.gameObject); }
                else { zdo.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(zdo); }
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ ops
        static TerrainOp PaintOp(string type)
        {
            if (ObjectDB.instance == null) return null;
            TerrainModifier.PaintType want;
            switch ((type ?? "").ToLowerInvariant())
            {
                case "cultivated": case "cultivate": want = TerrainModifier.PaintType.Cultivate; break;
                case "paved": case "pave": want = TerrainModifier.PaintType.Paved; break;
                case "dirt": case "path": want = TerrainModifier.PaintType.Dirt; break;
                default: return null;
            }
            TerrainOp best = null;
            foreach (var op in ObjectDB.instance.m_terrainOps)
            {
                if (op == null || op.m_settings == null || !op.m_settings.m_paintCleared || op.m_settings.m_paintType != want || op.m_settings.m_level || op.m_settings.m_raise) continue;
                if (best == null || (op.name.EndsWith("_v2", StringComparison.Ordinal) && !best.name.EndsWith("_v2", StringComparison.Ordinal))) best = op;
            }
            return best;
        }

        static bool Ground(double x, double z, out float y)
        {
            y = 0f; var p = new Vector3((float)x, 0f, (float)z);
            if (Heightmap.FindHeightmap(p) == null) return false;
            return ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3((float)x, 5000f, (float)z), out y);
        }

        sealed class Plan
        {
            public List<Vector3> Points = new List<Vector3>(); public int Unloaded, Warded, NoBuild; public List<string> Examples = new List<string>();
        }

        static Plan Strokes(IEnumerable<double[]> pts, double reach, bool forced)
        {
            var pl = new Plan();
            foreach (var q in pts)
            {
                float y; if (!Ground(q[0], q[1], out y)) { pl.Unloaded++; continue; }
                var pos = new Vector3((float)q[0], y, (float)q[1]);
                if (!forced && Neighbours.InWard(pos, reach)) { pl.Warded++; if (pl.Examples.Count < 5) pl.Examples.Add(U.V(pos) + ": inside an enabled guard stone's area"); continue; }
                var loc = Neighbours.LocationNoBuildAt(pos); if (loc != null) { pl.NoBuild++; if (pl.Examples.Count < 5) pl.Examples.Add(U.V(pos) + ": inside " + loc + " (no building there)"); continue; }
                pl.Points.Add(pos);
            }
            return pl;
        }

        // ------------------------------------------------------------------ terrain_paint
        static ToolOutput Paint(Dictionary<string, object> a)
        {
            if (!Zdos.Ready || ZNetScene.instance == null) return ToolOutput.Err("world not ready");
            var type = McpJson.GetStr(a, "type"); var op = PaintOp(type);
            if (op == null) return ToolOutput.Err("type must be cultivated, paved or dirt (and the game must have its terrain op for it)");
            var dry = McpJson.GetBool(a, "dry", false); var group = McpJson.GetStr(a, "group") ?? "terrain_paint";
            var forced = McpJson.GetBool(a, "force", false) && McpJson.GetStr(a, "forceConfirm") == Plans.ForceConfirm;
            if (McpJson.GetBool(a, "force", false) && !forced) return ToolOutput.Err("force needs forceConfirm:'" + Plans.ForceConfirm + "'");
            var reach = op.m_settings.GetRadius(); var spacing = Math.Max(1.0, op.m_settings.m_paintRadius * 0.9);
            List<double[]> pts; double bx0, bz0, bx1, bz1;
            if (U.Has(a, "x0"))
            {
                bx0 = U.D(a, "x0", 0); bz0 = U.D(a, "z0", 0); bx1 = U.D(a, "x1", 0); bz1 = U.D(a, "z1", 0);
                if (!WorldBounds.InWorld(bx0, 0, bz0) || !WorldBounds.InWorld(bx1, 0, bz1)) return ToolOutput.Err("the box must be finite and inside the world");
                if (Math.Abs(bx1 - bx0) > 2 * MaxRadius || Math.Abs(bz1 - bz0) > 2 * MaxRadius) return ToolOutput.Err("the box is wider than " + 2 * MaxRadius + " m");
                pts = StrokePlan.Box(bx0, bz0, bx1, bz1, spacing, MaxStrokes + 1);
            }
            else if (U.Has(a, "x"))
            {
                var cx = U.D(a, "x", 0); var cz = U.D(a, "z", 0); var rad = U.D(a, "radius", 4);
                if (!WorldBounds.InWorld(cx, 0, cz) || !WorldBounds.Finite(rad) || rad <= 0 || rad > MaxRadius) return ToolOutput.Err("x and z must be inside the world and radius between 0 and " + MaxRadius + " m");
                pts = StrokePlan.Disc(cx, cz, rad, spacing, MaxStrokes + 1); bx0 = cx - rad; bz0 = cz - rad; bx1 = cx + rad; bz1 = cz + rad;
            }
            else return ToolOutput.Err("give x, z, radius or x0, z0, x1, z1");
            if (pts.Count > MaxStrokes) return ToolOutput.Err("more than " + MaxStrokes + " strokes: a smaller area (the stroke spacing is " + U.N(Math.Round(spacing, 1)) + " m)");
            var plan = Strokes(pts, reach, forced);
            var zones = StrokePlan.Zones(bx0, bz0, bx1, bz1, reach + 4);
            var head = "\"type\":" + U.S(type) + ",\"op\":" + U.S(op.name) + ",\"strokes\":" + plan.Points.Count + ",\"requested\":" + pts.Count + ",\"zones\":" + zones.Count + ",\"noHeightmap\":" + plan.Unloaded + ",\"wardRefused\":" + plan.Warded + ",\"noBuildRefused\":" + plan.NoBuild + ",\"refusals\":[" + string.Join(",", plan.Examples.Select(U.S).ToArray()) + "]";
            if (plan.Points.Count == 0) return ToolOutput.Err("nothing to do: " + plan.Unloaded + " points without a loaded heightmap, " + plan.Warded + " inside a guard stone's area, " + plan.NoBuild + " in a no-build location");
            if (dry) return U.Json("{\"dry\":true," + head + "}");
            return Apply(op.gameObject, plan.Points, zones, group, "paint " + type + " x" + plan.Points.Count, head);
        }

        /// <summary>Snapshot, instantiate the op at every point (TerrainOp.Awake applies it and destroys itself), journal, and report what the terrain data says changed.</summary>
        static ToolOutput Apply(GameObject prefab, IList<Vector3> points, IList<int[]> zones, string group, string note, string head, Func<string> readBack = null)
        {
            var tag = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + (Journal.Entries.Count + 1);
            var legacy = prefab.GetComponent<TerrainOp>() == null;                                   // the older TerrainModifier prefabs (digg, raise ...) stay in the world as objects and change nothing in the terrain data
            var snap = legacy ? new Dictionary<string, string>() : Snapshot(zones, tag);
            if (legacy) foreach (var p in points) snap[PKey(prefab.name, p)] = "1";
            var before = new Dictionary<string, TcData>();
            foreach (var zn in zones) { var z = FindCompiler(zn[0], zn[1]); var b = z != null ? z.GetByteArray("TCData") : null; before[Key(zn[0], zn[1])] = b != null ? TryDecode(b) : TcData.Empty(); }
            Journal.Add(new JEntry { Kind = "terrain", Id = "", Prefab = "_TerrainCompiler", Pos = points[0], Rot = Quaternion.identity, Group = group, Note = note, Strings = snap });
            var applied = 0;
            foreach (var p in points) { UnityEngine.Object.Instantiate(prefab, p, Quaternion.identity); applied++; }
            if (legacy) return U.Json("{\"dry\":false," + head + ",\"applied\":" + applied + ",\"persistentObjects\":" + applied + (readBack != null ? readBack() : "") + ",\"group\":" + U.S(group) + ",\"undo\":\"undo_group group=" + group + " (destroys the " + applied + " " + prefab.name + " objects)\"}");
            int hc = 0, pc = 0; double mx = 0; var pending = 0; var rows = new List<string>();
            foreach (var zn in zones)
            {
                var z = FindCompiler(zn[0], zn[1]); var b = z != null ? z.GetByteArray("TCData") : null;
                var after = b != null ? TryDecode(b) : null; var was = before[Key(zn[0], zn[1])];
                rows.Add("{\"zone\":[" + zn[0] + "," + zn[1] + "],\"compiler\":" + (z != null ? "true" : "false") + ",\"opsBefore\":" + (was != null ? was.Operations : -1) + ",\"opsAfter\":" + (after != null ? after.Operations : -1) + "}");
                if (after == null || was == null) continue;
                int h, pa; double d; after.Diff(was, out h, out pa, out d); hc += h; pc += pa; mx = Math.Max(mx, d);
                if (after.Operations == was.Operations) pending++;
            }
            return U.Json("{\"dry\":false," + head + ",\"applied\":" + applied + ",\"heightChanged\":" + hc + ",\"paintChanged\":" + pc + ",\"maxHeightChange\":" + U.N(Math.Round(mx, 3)) + ",\"zonesUnchanged\":" + pending + ",\"zoneData\":[" + string.Join(",", rows.ToArray()) + "]" + (readBack != null ? readBack() : "") + ",\"group\":" + U.S(group) + ",\"undo\":\"undo_group group=" + group + " (or undo)\"}");
        }

        // ------------------------------------------------------------------ terrain_level
        const int MaxPad = 40;

        static ToolOutput Level(Dictionary<string, object> a)
        {
            if (!Zdos.Ready || ZNetScene.instance == null || WorldGenerator.instance == null) return ToolOutput.Err("world not ready");
            if (!U.Has(a, "targetY")) return ToolOutput.Err("targetY required");
            var target = U.D(a, "targetY", 0); int x0, z0, x1, z1;
            if (U.Has(a, "x0")) { x0 = (int)Math.Floor(U.D(a, "x0", 0)); z0 = (int)Math.Floor(U.D(a, "z0", 0)); x1 = (int)Math.Ceiling(U.D(a, "x1", 0)); z1 = (int)Math.Ceiling(U.D(a, "z1", 0)); }
            else if (U.Has(a, "x")) { var h = U.D(a, "half", 8); x0 = (int)Math.Floor(U.D(a, "x", 0) - h); x1 = (int)Math.Ceiling(U.D(a, "x", 0) + h); z0 = (int)Math.Floor(U.D(a, "z", 0) - h); z1 = (int)Math.Ceiling(U.D(a, "z", 0) + h); }
            else return ToolOutput.Err("give x,z[,half] or x0,z0,x1,z1");
            if (x1 < x0 || z1 < z0) return ToolOutput.Err("the pad is empty");
            if (!WorldBounds.InWorld(x0, 0, z0) || !WorldBounds.InWorld(x1, 0, z1) || !WorldBounds.Finite(target)) return ToolOutput.Err("the pad and targetY must be finite and inside the world");
            if (x1 - x0 > MaxPad || z1 - z0 > MaxPad) return ToolOutput.Err("the pad is larger than " + MaxPad + " x " + MaxPad + " m: split it");
            if (target < 30.4) return ToolOutput.Err("targetY under 30.4 m is washed by the sea (sea level 30)");
            var dry = McpJson.GetBool(a, "dry", false); var group = McpJson.GetStr(a, "group") ?? "terrain_level";
            var forced = McpJson.GetBool(a, "force", false) && McpJson.GetStr(a, "forceConfirm") == Plans.ForceConfirm;
            if (McpJson.GetBool(a, "force", false) && !forced) return ToolOutput.Err("force needs forceConfirm:'" + Plans.ForceConfirm + "'");

            var wg = WorldGenerator.instance; var zs = ZoneSystem.instance;
            Func<int, int, double> baseH = (x, z) => wg.GetHeight(x, z);
            var loadedAll = true;
            Func<int, int, double> current = (x, z) => { float gh; if (zs.GetGroundHeight(new Vector3(x, 0f, z), out gh) && Heightmap.FindHeightmap(new Vector3(x, 0f, z)) != null) return gh; loadedAll = false; return baseH(x, z); };
            var wanted = (McpJson.GetStr(a, "tool") ?? "").Trim();
            TerrainTools.Op op = null; PadReport rep = null;
            var levelOps = TerrainTools.Ops().Where(o => o.Level && (wanted.Length == 0 || o.Prefab == wanted)).ToList();
            if (wanted.Length == 0 && levelOps.Any(o => !o.Paints)) levelOps = levelOps.Where(o => !o.Paints).ToList();            // the game's dig ops paint the ground too: only used when nothing else levels
            foreach (var o in levelOps)
            {
                var r = PadPlanner.Level(baseH, current, x0, z0, x1, z1, target, o.LevelRadius, o.Square);
                if (r.Strokes > 0 && (rep == null || r.Strokes < rep.Strokes)) { rep = r; op = o; }
            }
            if (op == null) return ToolOutput.Err(wanted.Length == 0 ? "no level op found that the pad can use" : "no level op named " + wanted + " (level_check lists them), or its strokes cannot reach the pad");
            if (rep.Unreachable > 0) return ToolOutput.Err(rep.Unreachable + " vertices are more than 8 m from their generated height (shortfall up to " + Math.Round(rep.MaxShortfall, 1) + " m): lower the target or shrink the pad");
            if (!loadedAll) return ToolOutput.Err("part of the pad has no loaded heightmap: stand near it (client) or add a virtual peer (server) and repeat");

            var pts = new List<double[]>();
            for (var z = z0; z <= z1 + rep.Spacing - 1; z += rep.Spacing) for (var x = x0; x <= x1 + rep.Spacing - 1; x += rep.Spacing) { var qx = Math.Min(x, x1); var qz = Math.Min(z, z1); if (!pts.Any(q => q[0] == qx && q[1] == qz)) pts.Add(new[] { (double)qx, (double)qz }); }
            if (pts.Count > MaxStrokes) return ToolOutput.Err(pts.Count + " strokes are more than " + MaxStrokes + ": a smaller pad");
            var reach = op.LevelRadius + 2;
            var strokes = new List<Vector3>(); var refusals = new List<string>();
            foreach (var q in pts)
            {
                var pos = new Vector3((float)q[0], (float)(target - op.LevelOffset), (float)q[1]);
                if (!forced && Neighbours.InWard(pos, reach)) { refusals.Add(U.V(pos) + ": inside an enabled guard stone's area"); continue; }
                var loc = Neighbours.LocationNoBuildAt(pos); if (loc != null) { refusals.Add(U.V(pos) + ": inside " + loc + " (no building there)"); continue; }
                strokes.Add(pos);
            }
            if (refusals.Count > 0) return ToolOutput.Err(refusals.Count + " strokes refused, none applied: " + string.Join("; ", refusals.Take(3).ToArray()));

            int pieces = 0; string example = null;
            foreach (var z in Zdos.InBox(x0 - (float)reach, z0 - (float)reach, x1 + (float)reach, z1 + (float)reach))
            {
                if (!Neighbours.IsPiece(z.GetPrefab())) continue;
                pieces++; if (example == null) example = Zdos.NameOf(z.GetPrefab()) + " at " + U.V(z.GetPosition());
            }
            var legacyOp = TerrainTools.Prefab(op.Prefab); var isLegacy = legacyOp != null && legacyOp.GetComponent<TerrainOp>() == null;
            if (isLegacy)                                                                                        // a new modifier removes the older ones around it, and undo cannot bring those back
                foreach (var z in Zdos.InBox(x0 - (float)reach, z0 - (float)reach, x1 + (float)reach, z1 + (float)reach))
                {
                    if (z.GetPrefab() == CompilerHash) continue; var nm = Zdos.NameOf(z.GetPrefab());
                    if (U.IsForbidden(nm) && nm.Length > 0 && nm[0] != '_') return ToolOutput.Err("an older terrain modifier object (" + nm + " at " + U.V(z.GetPosition()) + ") stands on the pad: a new stroke would remove it and undo cannot bring it back");
                }
            var allow = McpJson.GetBool(a, "allowPieces", false);
            var zones = StrokePlan.Zones(x0, z0, x1, z1, reach + 4);
            var head = "\"pad\":[" + x0 + "," + z0 + "," + x1 + "," + z1 + "],\"targetY\":" + U.N(target) + ",\"op\":" + U.S(op.Prefab) + ",\"radius\":" + U.N(op.LevelRadius) + ",\"alsoSmooths\":" + (op.Smooth ? "true" : "false") + ",\"alsoPaints\":" + (op.Paints ? "true" : "false") + ",\"strokes\":" + strokes.Count + ",\"spacing\":" + rep.Spacing + ",\"zones\":" + zones.Count + ",\"fillM3\":" + U.N(Math.Round(rep.FillM3, 1)) + ",\"cutM3\":" + U.N(Math.Round(rep.CutM3, 1)) + ",\"piecesNear\":" + pieces;
            if (pieces > 0 && !allow) return ToolOutput.Err(pieces + " built pieces stand on or within " + U.N(reach) + " m of the pad (" + example + "): they would float or sink. Move them or pass allowPieces:true");
            if (dry) return U.Json("{\"dry\":true," + head + "}");

            var prefab = TerrainTools.Prefab(op.Prefab); if (prefab == null) return ToolOutput.Err("the op prefab " + op.Prefab + " is not available");
            return Apply(prefab, strokes, zones, group, "level " + x0 + "," + z0 + ".." + x1 + "," + z1 + " to " + U.N(target), head, () => ",\"settle\":\"the heightmap rebuilds over the next second or two: read it back with terrain_grid then\"");
        }

        static TcData TryDecode(byte[] compressed)
        {
            try { return TcData.Decode(Utils.Decompress(compressed)); }
            catch (Exception ex) { U.Once("TerrainEdit.decode", ex); return null; }
        }
    }
}
