// plan_check: the pure plan rules (PlanRules.cs) run on the prefabs' real collider boxes, for a plan that is not in the world yet. Works on the dedicated server: it needs only
// ZNetScene's prefabs (instantiated off-world to read their colliders, as prefab_info does) and the ZDO table; no player, no camera, no loaded zone.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class PlanCheck
    {
        static readonly Dictionary<string, PieceGeo> Cache = new Dictionary<string, PieceGeo>();
        const int MaxItems = 1500;

        public static void Register(ToolRegistry r)
        {
            r.Add("plan_check", "Check a plan BEFORE placing it, from the prefabs' real collider boxes and the game's own structure rules (works on the dedicated server): items:[{prefab,x,y,z,yaw|rot:[rx,ry,rz]}] (max 1500). Reports overlap (a piece that cannot clip is an error, building pieces a warning), unsupported (the structure model of WearNTear: contact by colliders grown 0.15 m, per-material loss, ground = full support; a piece under its material's minimum collapses), weak, tilt, water (sea level 30), yaw-grid (22.5 degrees), fire-clearance (1 m round a fire). Items with no y are checked at the ground but plan_apply refuses them unless they say seat:\"ground\" (missingY counts them; unknownKeys lists item keys the tools drop). Existing pieces in the area count as supporters and obstacles (those of plan:<name> are ignored: the plan replaces them). Ground: groundY (a flat pad) or, by default, the terrain collider's triangles (the game's own, edits included) where the zone is loaded and the world generator's height elsewhere (the result says how many vertex heights came from which; ground:\"generator\" forces the generator). Read-only. Arguments: plan, items, groundY, margin (default 8), examples (default 10).",
                "{\"type\":\"object\",\"properties\":{\"plan\":{\"type\":\"string\"},\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"groundY\":{\"type\":\"number\"},\"ground\":{\"type\":\"string\"},\"margin\":{\"type\":\"number\"},\"examples\":{\"type\":\"number\"}},\"required\":[\"items\"]}", Check);
            r.Add("plan_rules", "The rule book of plan_check: every rule with its origin (hard = the world enforces it after a spawn, ghost = only the hammer's placement check does, taste = our preference, never an error), the game's status code and the decompiled source it comes from. Read-only.", "{\"type\":\"object\",\"properties\":{}}", Rules);
        }

        static ToolOutput Rules(Dictionary<string, object> a)
        {
            return U.Json("{\"origins\":{\"hard\":\"the world enforces it after a ZDO spawn\",\"ghost\":\"only the hammer's placement ghost enforces it: a spawn skips it, a player cannot rebuild or repair it by hand\",\"taste\":\"our preference, never an error\"},\"rules\":[" +
                string.Join(",", PlanRuleBook.Rules.Select(r => "{\"id\":" + U.S(r.Id) + ",\"origin\":" + U.S(r.Origin) + ",\"code\":" + U.S(r.Code) + ",\"source\":" + U.S(r.Source) + "}").ToArray()) + "]}");
        }

        // ------------------------------------------------------------------ prefab geometry
        static Quat Q(Quaternion q) { return new Quat(q.x, q.y, q.z, q.w); }
        static bool IsLeaky(Collider c) { try { return c.gameObject.CompareTag("leaky"); } catch (Exception ex) { U.Once("Geo.leaky", ex); return false; } }
        static V3 ScaleOf(ZDO z) { var s = z.GetVec3("scale", Vector3.one); return new V3(s.x, s.y, s.z); }

        internal static PieceGeo GeoOf(string name) { return Geo(name); }

        static PieceGeo Geo(string name)
        {
            if (Cache.TryGetValue(name, out var c)) return c;
            if (ZNetScene.instance == null) return null;
            if (name == "Player" || U.IsLiving(name)) return null;                               // never instantiate a creature or a player to read its colliders
            var prefab = ZNetScene.instance.GetPrefab(name); if (prefab == null) return null;
            GameObject go = null;
            try { ZNetView.m_forceDisableInit = true; go = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -4000f, 0f), Quaternion.identity); }
            finally { ZNetView.m_forceDisableInit = false; }
            try
            {
                var root = go.transform.position; var g = new PieceGeo { Prefab = name };
                foreach (var col in go.GetComponentsInChildren<Collider>())
                {
                    if (col == null || col.isTrigger || col.attachedRigidbody != null) continue;
                    var layer = LayerMask.LayerToName(col.gameObject.layer);
                    if (!Layers.InPlace(layer)) continue;           // nav blockers, characters, ghosts: the game's checks never look at them
                    var box = col as BoxCollider;
                    if (box != null)
                    {
                        var ls = box.transform.lossyScale;
                        g.Colliders.Add(new ColliderBox { Centre = V(box.transform.TransformPoint(box.center) - root), Half = new V3(Math.Abs(box.size.x * ls.x) / 2, Math.Abs(box.size.y * ls.y) / 2, Math.Abs(box.size.z * ls.z) / 2), Rot = Q(box.transform.rotation), Layer = layer, Leaky = IsLeaky(col) });
                    }
                    else g.Colliders.Add(new ColliderBox { Centre = V(col.bounds.center - root), Half = V(col.bounds.extents), Rot = Quat.Identity, Layer = layer, Leaky = IsLeaky(col) });       // a bounds box for anything else (the game uses the real shape)
                }
                var wnt = go.GetComponent<WearNTear>();
                if (wnt != null) { g.Material = wnt.m_materialType.ToString(); g.Supports = wnt.m_supports; g.ComOffset = V(wnt.m_comOffset); g.WearsWithoutSupport = wnt.m_noSupportWear; g.ForceCorrectCom = wnt.m_forceCorrectCOMCalculation; }
                var piece = go.GetComponent<Piece>();
                if (piece != null) { g.NoClipping = piece.m_noClipping; g.NotOnTilting = piece.m_notOnTiltingSurface; g.NotOnWood = piece.m_notOnWood; g.NotOnFloor = piece.m_notOnFloor; g.NoInWater = piece.m_noInWater; g.GroundOnly = piece.m_groundOnly; }
                var bed = go.GetComponent<Bed>(); if (bed != null && bed.m_spawnPoint != null) g.CoverPoints.Add(new CoverPoint { Kind = "bed", Local = V(bed.m_spawnPoint.position - root), MinCover = 0.8 });
                var stn = go.GetComponent<CraftingStation>(); if (stn != null && stn.m_craftRequireRoof && stn.m_roofCheckPoint != null) g.CoverPoints.Add(new CoverPoint { Kind = "station", Local = V(stn.m_roofCheckPoint.position - root), MinCover = 0.7 });
                var sm = go.GetComponent<Smelter>(); if (sm != null && sm.m_requiresRoof && sm.m_roofCheckPoint != null) g.CoverPoints.Add(new CoverPoint { Kind = "smelter", Local = V(sm.m_roofCheckPoint.position - root), MinCover = 0 });
                var fer = go.GetComponent<Fermenter>(); if (fer != null && fer.m_roofCheckPoint != null) g.CoverPoints.Add(new CoverPoint { Kind = "fermenter", Local = V(fer.m_roofCheckPoint.position - root), MinCover = 0.7 });
                var hive = go.GetComponent<Beehive>(); if (hive != null && hive.m_coverPoint != null && hive.m_maxCover > 0) g.CoverPoints.Add(new CoverPoint { Kind = "beehive", Local = V(hive.m_coverPoint.position - root), MaxCover = hive.m_maxCover });
                foreach (Transform child in go.transform) { try { if (child.CompareTag("snappoint")) g.SnapPoints.Add(V(child.position - root)); } catch (Exception ex) { U.Once("Geo.snap", ex); } }
                var n = name.ToLowerInvariant();
                var cs = go.GetComponent<CraftingStation>(); var fp = go.GetComponent<Fireplace>(); var ext = go.GetComponent<StationExtension>();
                g.IsStation = cs != null;
                g.IsFireplace = fp != null;
                if (fp != null) { g.DisableCoverCheck = fp.m_disableCoverCheck; g.CoverCheckOffset = fp.m_coverCheckOffset; g.CheckTerrainOffset = fp.m_checkTerrainOffset; }
                if (cs != null) { g.ProvidesStation = cs.m_name; g.StationRangeBuild = cs.m_rangeBuild; g.StationExtraRange = cs.m_extraRangePerLevel; }
                if (ext != null && ext.m_craftingStation != null) { g.ExtensionOf = ext.m_craftingStation.m_name; g.ExtensionMaxDistance = ext.m_maxStationDistance; g.ExtensionStack = ext.m_stack; if (piece != null) g.ExtensionSpace = piece.m_spaceRequirement; }
                if (piece != null && piece.m_craftingStation != null) g.StationName = piece.m_craftingStation.m_name;
                g.IsFire = fp != null && (n.Contains("fire") || n.Contains("hearth")) && !n.Contains("torch");
                g.IsWall = wnt != null && (n.Contains("wall") || n.Contains("fence") || n.Contains("palisade"));
                var top = g.Colliders.Count == 0 ? 0 : g.Colliders.Max(b => b.Centre.Y + b.Half.Y);
                var flatName = n.Contains("floor") || n.Contains("rug") || n.Contains("carpet") || n.Contains("door") || n.Contains("roof");
                g.Occupies = top >= 0.5 && (fp != null || cs != null || !flatName);       // a brazier on a floor stand is not a floor
                Cache[name] = g; return g;
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        static V3 V(Vector3 v) { return new V3(v.x, v.y, v.z); }

        // ------------------------------------------------------------------ removal safety
        public const int CollapseCheckMax = 800;

        /// <summary>The pieces that would lose their support if these objects were removed. Null: not checked (too many objects, or none of them is a structure). The check runs on the world generator's ground, which does not know terrain edits; it only counts pieces that stand now and fall after, so a wrong ground cannot invent a collapse.</summary>
        public static List<string> CollapseOf(IList<ZDO> victims, out string note)
        {
            note = "";
            if (victims.Count == 0 || !Zdos.Ready) return null;
            if (victims.Count > CollapseCheckMax) { note = "collapse check skipped: more than " + CollapseCheckMax + " objects"; return null; }
            double x0 = 1e9, z0 = 1e9, x1 = -1e9, z1 = -1e9;
            foreach (var v in victims) { var p = v.GetPosition(); x0 = Math.Min(x0, p.x); x1 = Math.Max(x1, p.x); z0 = Math.Min(z0, p.z); z1 = Math.Max(z1, p.z); }
            var removed = new HashSet<string>(victims.Select(v => U.Id(v)));
            var all = new List<PlacedPiece>(); const double margin = 24;                                                       // wood carries about 20 m sideways before it reaches its minimum: look that far past the victims
            foreach (var z in Zdos.InBox((float)(x0 - margin), (float)(z0 - margin), (float)(x1 + margin), (float)(z1 + margin)))
            {
                var name = Zdos.NameOf(z.GetPrefab()); if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                var geo = Geo(name); if (geo == null || geo.Colliders.Count == 0) continue;
                var p = z.GetPosition();
                all.Add(new PlacedPiece { Id = U.Id(z), Geo = geo, Pos = new V3(p.x, p.y, p.z), Rot = Q(z.GetRotation()), Planned = false, Scale = ScaleOf(z) });
                if (all.Count > 4000) { note = "collapse check skipped: more than 4000 pieces around"; return null; }
            }
            if (!all.Any(p => removed.Contains(p.Id) && Material.Of(p.Geo.Material) != null)) return new List<string>();
            Func<double, double, double> ground, normalYAt; Func<string> groundNote;
            MakeGround(new Dictionary<string, object>(), out ground, out normalYAt, out groundNote);          // the terrain as it is now (edits included), not the generator's
            return RemovalImpact.Collapses(all, removed, ground).Select(p => p.Id + " " + p.Geo.Prefab + " " + U.V(new Vector3((float)p.Pos.X, (float)p.Pos.Y, (float)p.Pos.Z))).ToList();
        }

        /// <summary>The ground a structure check stands on: groundY flat, else the terrain triangles from the loaded terrain (edits included) with the generator behind it, else none. Shared by plan_check and the collapse check of a removal.</summary>
        static void MakeGround(Dictionary<string, object> a, out Func<double, double, double> ground, out Func<double, double, double> normalYAt, out Func<string> groundNote)
        {
            normalYAt = (x, z) => 1.0;
            var mode = McpJson.GetStr(a, "ground") ?? "auto";
            if (U.Has(a, "groundY")) { var gy = U.D(a, "groundY", 0); ground = (x, z) => gy; var note = "flat ground at y " + U.N(gy); groundNote = () => note; }
            else if (WorldGenerator.instance != null)
            {
                // The ground is the terrain collider: whole-metre vertices joined by triangles (TerrainRules). Where the zone is loaded the vertex heights are read from the game's own
                // terrain (edits included); elsewhere from the world generator, which does not know edits.
                var wg = WorldGenerator.instance; int fromWorld = 0, fromGenerator = 0; var cache = new Dictionary<long, double>(); var zsys = ZoneSystem.instance;
                Func<double, double, double> vertex = (vx, vz) =>
                {
                    var key = (long)(vx + 20000) * 100000L + (long)(vz + 20000); double h;
                    if (cache.TryGetValue(key, out h)) return h;
                    float gh;
                    if (mode != "generator" && zsys != null && zsys.GetGroundHeight(new Vector3((float)vx, 0f, (float)vz), out gh)) { h = gh; fromWorld++; }
                    else { h = wg.GetHeight((float)vx, (float)vz); fromGenerator++; }
                    cache[key] = h; return h;
                };
                ground = (x, z) => TerrainRules.Height(vertex, x, z); normalYAt = (x, z) => TerrainRules.NormalY(vertex, x, z);
                groundNote = () => "terrain triangles from " + fromWorld + " vertex heights of the loaded terrain (edits included) and " + fromGenerator + " from the world generator (which does not know edits)" + (mode == "generator" ? " [ground:generator forced]" : "");
            }
            else { ground = null; groundNote = () => "no ground (nothing is grounded)"; }
        }

        // ------------------------------------------------------------------ the tool
        static ToolOutput Check(Dictionary<string, object> a)
        {
            var items = McpJson.GetList(a, "items"); if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            if (items.Count > MaxItems) return ToolOutput.Err("max " + MaxItems + " items per call (the structure model is quadratic): split the plan by building");
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var plan = McpJson.GetStr(a, "plan"); var margin = Math.Max(1, Math.Min(30, U.D(a, "margin", 8))); var maxEx = Math.Max(1, (int)U.D(a, "examples", 10));
            Func<double, double, double> ground, normalYAt; Func<string> groundNote;
            MakeGround(a, out ground, out normalYAt, out groundNote);

            var planned = new List<PlacedPiece>(); var unknown = new List<string>(); var bad = new List<string>();
            double x0 = 1e9, z0 = 1e9, x1 = -1e9, z1 = -1e9; var idx = 0; var unknownKeys = new Dictionary<string, int>(); var missingY = 0;
            foreach (var o in items)
            {
                idx++; var it = o as Dictionary<string, object>; if (it == null) continue;
                foreach (var uk in PlanItemRules.Unknown(it.Keys)) unknownKeys[uk] = unknownKeys.ContainsKey(uk) ? unknownKeys[uk] + 1 : 1;
                if (PlanItemRules.MissingY(PlanItemRules.HasY(McpJson.Get(it, "y", double.NaN)), McpJson.GetStr(it, "seat"))) missingY++;
                var prefab = McpJson.GetStr(it, "prefab"); var geo = string.IsNullOrEmpty(prefab) ? null : Geo(prefab);
                if (geo == null) { if (unknown.Count < maxEx) unknown.Add("item " + idx + ": " + (prefab ?? "no prefab")); continue; }
                double x = McpJson.Get(it, "x", double.NaN), y = McpJson.Get(it, "y", double.NaN), z = McpJson.Get(it, "z", double.NaN);
                if (!WorldBounds.InWorld(x, double.IsNaN(y) ? 0 : y, z)) { if (bad.Count < maxEx) bad.Add("item " + idx + " (" + prefab + "): position not finite or outside the world"); continue; }
                if (double.IsNaN(y)) y = ground != null ? ground(x, z) : 0;
                var rl = McpJson.GetList(it, "rot");
                var rot = rl != null && rl.Count >= 3 ? Quat.Euler(U.At(rl, 0), U.At(rl, 1), U.At(rl, 2)) : Quat.Yaw(McpJson.Get(it, "yaw", 0));
                planned.Add(new PlacedPiece { Id = McpJson.GetStr(it, "key") ?? ("#" + idx), Geo = geo, Pos = new V3(x, y, z), Rot = rot });
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            if (planned.Count == 0) return ToolOutput.Err("no usable items" + (unknown.Count > 0 ? ": unknown prefabs " + string.Join(", ", unknown.ToArray()) : ""));

            // what is already there: supporters and obstacles, not reported on
            var all = new List<PlacedPiece>(planned); int world = 0;
            if (Zdos.Ready)
                foreach (var z in Zdos.InBox((float)(x0 - margin), (float)(z0 - margin), (float)(x1 + margin), (float)(z1 + margin)))
                {
                    if (plan != null && z.GetString(Plans.PlanHash, "") == plan) continue;
                    var name = Zdos.NameOf(z.GetPrefab()); if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                    var geo = Geo(name); if (geo == null || geo.Colliders.Count == 0) continue;
                    var p = z.GetPosition();
                    all.Add(new PlacedPiece { Id = U.Id(z), Geo = geo, Pos = new V3(p.x, p.y, p.z), Rot = Q(z.GetRotation()), Planned = false, Scale = ScaleOf(z) }); world++;
                }

            var problems = new List<PlanProblem>();
            problems.AddRange(PlanChecks.Overlaps(all));
            problems.AddRange(SnapSeams.Find(all));
            problems.AddRange(CoverChecks.Check(all, ground));
            foreach (var p in planned)
            {
                var gy = ground != null ? ground(p.Pos.X, p.Pos.Z) : double.NaN;
                if (ground != null)
                {
                    // the game takes the surface normal and the water from the first thing under the piece (Player.UpdatePlacementGhost): a piece standing on another piece is judged by that, not by the terrain
                    var bottoms = p.Boxes(Layers.InPlace); var lifted = bottoms.Count > 0 && bottoms.Min(b => b.MinY) > gy + 0.25;
                    if (!lifted)
                    {
                        var normalY = normalYAt(p.Pos.X, p.Pos.Z);
                        var t = PlanChecks.Tilt(p, normalY); if (t != null) problems.Add(t);
                        var w = PlanChecks.Water(p, gy); if (w != null) problems.Add(w);
                    }
                }
                var yawDeg = p.Rot.Rotate(new V3(0, 0, 1)); var g = PlanChecks.Grid(p, Math.Atan2(yawDeg.X, yawDeg.Z) * 180 / Math.PI); if (g != null) problems.Add(g);
            }
            problems.AddRange(PlanChecks.FireClearance(all));
            // Heightmap.GetHeight reads the nearest vertex, not the triangle surface (WorldToVertex: floor(v + 0.5))
            Func<double, double, double> vertexGround = ground == null ? null : (Func<double, double, double>)((x, z) => ground(TerrainRules.NearestVertex(x), TerrainRules.NearestVertex(z)));
            problems.AddRange(PlanChecks.FireBlocked(all, vertexGround));
            problems.AddRange(PlanChecks.Extensions(all));
            problems.AddRange(PlanChecks.StationsNeeded(all));
            problems.AddRange(PlanChecks.NotOnWood(all));
            foreach (var p in planned)
            {
                var pos = new Vector3((float)p.Pos.X, (float)p.Pos.Y, (float)p.Pos.Z);
                var loc = Neighbours.LocationNoBuildAt(pos);
                if (loc != null) problems.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "no-build-zone", Severity = "warning", Piece = p.Id, Detail = p.Geo.Prefab + " is inside " + loc + " (the hammer refuses to build there; a spawn does not)" }));
                if (Neighbours.InWard(pos, Neighbours.WardMargin(p.Geo.Prefab))) problems.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "private-zone", Severity = "error", Piece = p.Id, Detail = p.Geo.Prefab + " is inside an enabled guard stone's area; plan_apply refuses it unless forced" + (Neighbours.WardMargin(p.Geo.Prefab) > 0 ? " (a new ward also may not reach into another ward: its own radius counts too)" : "") }));
            }

            var support = SupportModel.Run(all, ground, null);
            var noFall = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBuildingFall);      // WearNTear skips the damage of a missing support when this world option is set
            int unsupported = 0, weak = 0; double minSupport = double.PositiveInfinity;
            foreach (var p in planned)
            {
                SupportResult s; if (!support.TryGetValue(p, out s)) continue;
                minSupport = Math.Min(minSupport, s.Support);
                if (!s.Holds) { unsupported++; problems.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "unsupported", Severity = noFall ? "warning" : "error", Piece = p.Id, Detail = p.Geo.Prefab + " at " + U.V(new Vector3((float)p.Pos.X, (float)p.Pos.Y, (float)p.Pos.Z)) + ": support " + U.N(Math.Round(s.Support, 1)) + " is under the " + p.Geo.Material + " minimum of " + U.N(s.Min) + (noFall ? ": it would collapse, but this world has NoBuildingFall" : ": it collapses") })); }
                else if (!s.Grounded)
                {
                    var weakBelow = s.Min * Material.Of(p.Geo.Material).WeakRatio;
                    if (s.Support < weakBelow) { weak++; problems.Add(PlanRuleBook.Stamp(new PlanProblem { Rule = "weak", Severity = "warning", Piece = p.Id, Detail = p.Geo.Prefab + ": support " + U.N(Math.Round(s.Support, 1)) + " is under " + U.N(Math.Round(weakBelow, 1)) + ", what one more 2 m module out would need (minimum " + U.N(s.Min) + ")" })); }
                }
            }

            var byRule = problems.GroupBy(p => p.Rule + "|" + p.Severity).OrderBy(g => g.Key).ToList();
            var sb = new StringBuilder("{\"planned\":" + planned.Count + ",\"skipped\":" + (items.Count - planned.Count) + ",\"worldPiecesConsidered\":" + world + ",\"ground\":" + U.S(groundNote()) + ",\"errors\":" + problems.Count(p => p.Severity == "error") + ",\"warnings\":" + problems.Count(p => p.Severity == "warning") +
                ",\"noBuildingFall\":" + (noFall ? "true" : "false") + ",\"unsupported\":" + unsupported + ",\"weak\":" + weak + ",\"minSupport\":" + (double.IsInfinity(minSupport) ? "null" : U.N(Math.Round(minSupport, 1))) + ",\"unknownPrefabs\":[" + string.Join(",", unknown.Select(U.S).ToArray()) + "],\"badItems\":[" + string.Join(",", bad.Select(U.S).ToArray()) + "],\"unknownKeys\":{" + string.Join(",", unknownKeys.Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "},\"missingY\":" + missingY + ",\"problems\":[");
            sb.Append(string.Join(",", byRule.Select(g => "{\"rule\":" + U.S(g.First().Rule) + ",\"severity\":" + U.S(g.First().Severity) + ",\"origin\":" + U.S(g.First().Origin) + ",\"code\":" + U.S(g.First().Code) + ",\"count\":" + g.Count() + ",\"examples\":[" + string.Join(",", g.Take(maxEx).Select(p => "{\"piece\":" + U.S(p.Piece) + ",\"detail\":" + U.S(p.Detail) + "}").ToArray()) + "]}").ToArray()));
            sb.Append("]}");
            return U.Json(sb.ToString());
        }
    }
}
