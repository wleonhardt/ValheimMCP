// Who owns what, and server-side readers built on the ZDO table (they work on the dedicated server with no player and no camera).
// The rules are in Rules.cs (pure, tested); this file reads the game for their facts.
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
    internal static class Neighbours
    {
        static HashSet<int> _wardH, _containerH, _pieceH, _fireH;
        static readonly Dictionary<int, GameObject> _prefabs = new Dictionary<int, GameObject>();

        static void EnsurePrefabSets()
        {
            if (_wardH != null || ZNetScene.instance == null) return;
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var dict = f != null ? f.GetValue(ZNetScene.instance) as Dictionary<int, GameObject> : null;
            if (dict == null || dict.Count == 0) return;
            var w = new HashSet<int>(); var c = new HashSet<int>(); var p = new HashSet<int>(); var fi = new HashSet<int>();
            foreach (var kv in dict)
            {
                var go = kv.Value; if (go == null) continue;
                if (go.GetComponent<PrivateArea>() != null) w.Add(kv.Key);
                if (go.GetComponent<Container>() != null) c.Add(kv.Key);
                if (go.GetComponent<Piece>() != null) p.Add(kv.Key);
                if (go.GetComponent<Fireplace>() != null) fi.Add(kv.Key);
                _prefabs[kv.Key] = go;
            }
            _containerH = c; _pieceH = p; _fireH = fi; _wardH = w;
        }

        static GameObject Prefab(int hash) { EnsurePrefabSets(); return _prefabs.TryGetValue(hash, out var go) ? go : null; }

        // ------------------------------------------------------------------ wards
        static List<WardFact> _wards = new List<WardFact>(); static float _wardsAt = -100f;
        static readonly int EnabledHash = "enabled".GetStableHashCode(), PermittedHash = "permitted".GetStableHashCode(), CreatorHash = "creator".GetStableHashCode();

        /// <summary>Every guard stone the table knows (cached 10 s: a world scan).</summary>
        public static List<WardFact> Wards()
        {
            if (Time.realtimeSinceStartup - _wardsAt < 10f) return _wards;
            EnsurePrefabSets();
            var list = new List<WardFact>();
            if (_wardH != null)
                foreach (var chunk in Zdos.InBoxChunks(-WorldBounds.Limit, -WorldBounds.Limit, WorldBounds.Limit, WorldBounds.Limit))
                    foreach (var z in chunk)
                    {
                        var h = z.GetPrefab(); if (!_wardH.Contains(h)) continue;
                        var pa = Prefab(h) != null ? Prefab(h).GetComponent<PrivateArea>() : null;
                        var p = z.GetPosition();
                        var w = new WardFact { X = p.x, Z = p.z, Radius = pa != null ? pa.m_radius : 32f, Enabled = z.GetBool(EnabledHash, false), Creator = z.GetLong(CreatorHash, 0L), Name = Zdos.NameOf(h) };
                        var n = z.GetInt(PermittedHash, 0);
                        for (var i = 0; i < n; i++) w.Permitted.Add(z.GetLong("pu_id" + i, 0L));
                        list.Add(w);
                    }
            _wards = list; _wardsAt = Time.realtimeSinceStartup;
            return _wards;
        }

        public static bool InWard(Vector3 p, double margin) { return Ownership.WardCovers(Wards(), p.x, p.z, margin); }

        // ------------------------------------------------------------------ locations (no-build advice)
        /// <summary>The location (if any) whose largest radius covers the point. The server knows every location; whether it forbids building is the Location component of its prefab, which a headless server may not have loaded, so this is advice.</summary>
        public static string LocationAt(Vector3 p)
        {
            var zs = ZoneSystem.instance; if (zs == null) return null;
            foreach (var inst in zs.m_locationInstances.Values)
            {
                var loc = inst.m_location; if (loc == null) continue;
                var dx = inst.m_position.x - p.x; var dz = inst.m_position.z - p.z;
                if (dx * dx + dz * dz > 400f * 400f) continue;
                var r = LocationRules.MaxRadius(loc.m_interiorRadius > 0f, loc.m_exteriorRadius, loc.m_interiorRadius);
                if (LocationRules.NoBuildInside(Math.Sqrt(dx * dx + dz * dz), r, 0, true)) return loc.m_prefabName;
            }
            return null;
        }

        // ------------------------------------------------------------------ the Protected extension
        /// <summary>The ownership rules for one object: null = fair game, else why not. Called from Plans.Protected after the creator and living-thing rules.</summary>
        public static string Judge(ZDO z, string prefab)
        {
            EnsurePrefabSets();
            var h = z.GetPrefab(); var p = z.GetPosition();
            var f = new OwnFacts
            {
                PlanOwned = !string.IsNullOrEmpty(z.GetString(Plans.PlanHash, "")),
                Tombstone = Ownership.IsTombstonePrefab(prefab),
                InWard = InWard(p, 0),
                ContainerWithItems = _containerH != null && _containerH.Contains(h) && z.GetString("items", "").Length > 0
            };
            if (_pieceH != null && _pieceH.Contains(h) && z.GetLong(CreatorHash, 0L) == 0L && !f.PlanOwned)
            {
                var n = 0;
                foreach (var o in Zdos.InBox(p.x - (float)Ownership.ClusterMetres, p.z - (float)Ownership.ClusterMetres, p.x + (float)Ownership.ClusterMetres, p.z + (float)Ownership.ClusterMetres))
                {
                    if (o == z || !_pieceH.Contains(o.GetPrefab()) || o.GetLong(CreatorHash, 0L) != 0L || !string.IsNullOrEmpty(o.GetString(Plans.PlanHash, ""))) continue;
                    var q = o.GetPosition(); var dx = q.x - p.x; var dz = q.z - p.z;
                    if (dx * dx + dz * dz <= Ownership.ClusterMetres * Ownership.ClusterMetres) n++;
                }
                f.Creator0Neighbours = n;
            }
            return Ownership.Why(f);
        }

        // ------------------------------------------------------------------ tools
        public static void Register(ToolRegistry r)
        {
            r.Add("neighbours", "Who owns what round a point, from the ZDO table: built pieces by creator (id and name), guard stones (radius, enabled, creator, permitted count) covering or near it, tombstones, containers that hold items, claimed beds, the locations around. Arguments x,z,radius (default 40, max 150).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", NeighboursTool);
            r.Add("locations_near", "Locations the world knows (the server knows all of them): name, position, whether it has been placed, largest radius, distance. Arguments x,z,radius (default 500, max 3000), name filter, limit (default 50).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"name\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", LocationsNear);
            r.Add("biome_at", "Biome and the generator's own ground height at points (no zone needs to be loaded): points:[[x,z],...] (max 500). The height is the world generator's, before any terrain edit.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}}},\"required\":[\"points\"]}", BiomeAt);
            r.Add("fires", "Fireplaces (fires, torches, braziers, hearths) in a box or round a point, from the ZDO table: prefab, position, fuel, max fuel, seconds of burning left, whether it burns. Arguments x,z,radius (default 40) or x0,z0,x1,z1; limit (default 200).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}}}", Fires);
            r.Add("death_state", "Tombstones in the world: position, owner name and id, time of death, how many item stacks they hold. Optional name filter. The harness never touches them.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"limit\":{\"type\":\"number\"}}}", DeathState);
            r.Add("cost_mode", "What the world really enforces for building and crafting, from the global keys: NoBuildCost, NoCraftCost, NoWorkbench, AllPiecesUnlocked, AllRecipesUnlocked, DungeonBuild, NoBuildingFall, plus the resource and event rates.",
                "{\"type\":\"object\",\"properties\":{}}", CostMode);
        }

        static string Row(string key, string json) { return U.S(key) + ":" + json; }

        static ToolOutput NeighboursTool(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            EnsurePrefabSets();
            double x = U.D(a, "x", 0), zc = U.D(a, "z", 0), rad = Math.Min(150, Math.Max(1, U.D(a, "radius", 40)));
            if (!WorldBounds.InWorld(x, 0, zc)) return ToolOutput.Err("x and z must be finite and inside the world");
            var byCreator = new Dictionary<long, int>(); var names = new Dictionary<long, string>();
            var tombs = new List<string>(); var chests = new List<string>(); var beds = new List<string>(); int pieces = 0;
            foreach (var z in Zdos.InBox((float)(x - rad), (float)(zc - rad), (float)(x + rad), (float)(zc + rad)))
            {
                var p = z.GetPosition(); var dx = p.x - x; var dz = p.z - zc; if (dx * dx + dz * dz > rad * rad) continue;
                var h = z.GetPrefab(); var name = Zdos.NameOf(h);
                if (Ownership.IsTombstonePrefab(name)) { tombs.Add("{\"id\":" + U.S(U.Id(z)) + ",\"pos\":" + U.V(p) + ",\"owner\":" + U.S(z.GetString("ownerName", "")) + "}"); continue; }
                if (_containerH != null && _containerH.Contains(h) && z.GetString("items", "").Length > 0) chests.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(name) + ",\"pos\":" + U.V(p) + ",\"creator\":" + z.GetLong(CreatorHash, 0L).ToString(CultureInfo.InvariantCulture) + "}");
                if (name.IndexOf("bed", StringComparison.OrdinalIgnoreCase) >= 0 && z.GetLong("owner", 0L) != 0L) beds.Add("{\"id\":" + U.S(U.Id(z)) + ",\"pos\":" + U.V(p) + ",\"owner\":" + U.S(z.GetString("ownerName", "")) + "}");
                if (_pieceH == null || !_pieceH.Contains(h)) continue;
                pieces++; var cr = z.GetLong(CreatorHash, 0L);
                byCreator[cr] = byCreator.ContainsKey(cr) ? byCreator[cr] + 1 : 1;
                if (cr != 0 && !names.ContainsKey(cr)) names[cr] = z.GetString("creatorName", "");
            }
            var wards = Wards().Where(w => { var dx = w.X - x; var dz = w.Z - zc; return Math.Sqrt(dx * dx + dz * dz) <= rad + w.Radius; })
                .Select(w => "{\"prefab\":" + U.S(w.Name) + ",\"pos\":[" + U.N(Math.Round(w.X, 1)) + "," + U.N(Math.Round(w.Z, 1)) + "],\"radius\":" + U.N(w.Radius) + ",\"enabled\":" + (w.Enabled ? "true" : "false") + ",\"creator\":" + w.Creator.ToString(CultureInfo.InvariantCulture) + ",\"permitted\":" + w.Permitted.Count + ",\"coversCentre\":" + (Ownership.WardCovers(new[] { w }, x, zc, 0) ? "true" : "false") + "}").ToArray();
            var by = byCreator.OrderByDescending(kv => kv.Value).Select(kv => "{\"creator\":" + kv.Key.ToString(CultureInfo.InvariantCulture) + ",\"name\":" + U.S(names.ContainsKey(kv.Key) ? names[kv.Key] : (kv.Key == 0 ? "(creator 0: world-generated, spawned by a tool, or an old build)" : "")) + ",\"pieces\":" + kv.Value + "}").ToArray();
            var loc = LocationAt(new Vector3((float)x, 0, (float)zc));
            return U.Json("{\"centre\":[" + U.N(x) + "," + U.N(zc) + "],\"radius\":" + U.N(rad) + ",\"pieces\":" + pieces + ",\"byCreator\":[" + string.Join(",", by) + "],\"wards\":[" + string.Join(",", wards) + "],\"tombstones\":[" + string.Join(",", tombs.ToArray()) + "],\"containersWithItems\":[" + string.Join(",", chests.Take(50).ToArray()) + "],\"claimedBeds\":[" + string.Join(",", beds.ToArray()) + "],\"locationHere\":" + U.S(loc) + "}");
        }

        static ToolOutput LocationsNear(Dictionary<string, object> a)
        {
            var zs = ZoneSystem.instance; if (zs == null) return ToolOutput.Err("no ZoneSystem");
            double x = U.D(a, "x", 0), z = U.D(a, "z", 0), rad = Math.Min(3000, Math.Max(1, U.D(a, "radius", 500))); var name = McpJson.GetStr(a, "name"); var limit = Math.Max(1, (int)U.D(a, "limit", 50));
            var rows = new List<KeyValuePair<double, string>>();
            foreach (var inst in zs.m_locationInstances.Values)
            {
                var loc = inst.m_location; if (loc == null) continue;
                if (name != null && loc.m_prefabName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var dx = inst.m_position.x - x; var dz = inst.m_position.z - z; var d = Math.Sqrt(dx * dx + dz * dz); if (d > rad) continue;
                rows.Add(new KeyValuePair<double, string>(d, "{\"name\":" + U.S(loc.m_prefabName) + ",\"pos\":" + U.V(inst.m_position) + ",\"placed\":" + (inst.m_placed ? "true" : "false") + ",\"radius\":" + U.N(LocationRules.MaxRadius(loc.m_interiorRadius > 0f, loc.m_exteriorRadius, loc.m_interiorRadius)) + ",\"distance\":" + U.N(Math.Round(d, 1)) + "}"));
            }
            var ordered = rows.OrderBy(r => r.Key).Take(limit).Select(r => r.Value).ToArray();
            return U.Json("{\"total\":" + rows.Count + ",\"returned\":" + ordered.Length + ",\"locations\":[" + string.Join(",", ordered) + "]}");
        }

        static ToolOutput BiomeAt(Dictionary<string, object> a)
        {
            var wg = WorldGenerator.instance; if (wg == null) return ToolOutput.Err("no WorldGenerator (no world loaded)");
            var l = McpJson.GetList(a, "points"); if (l == null || l.Count == 0) return ToolOutput.Err("points required");
            if (l.Count > 500) return ToolOutput.Err("max 500 points");
            var rows = new List<string>();
            foreach (var o in l)
            {
                var p = o as List<object>; var x = U.At(p, 0); var z = U.At(p, 1);
                if (double.IsNaN(x) || double.IsNaN(z)) { rows.Add("null"); continue; }
                rows.Add("[" + U.N(x) + "," + U.N(z) + "," + U.S(wg.GetBiome((float)x, (float)z).ToString()) + "," + U.N(Math.Round(wg.GetHeight((float)x, (float)z), 2)) + "]");
            }
            return U.Json("{\"columns\":[\"x\",\"z\",\"biome\",\"generatorHeight\"],\"points\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput Fires(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready"); EnsurePrefabSets();
            if (_fireH == null) return ToolOutput.Err("prefab table not ready");
            float x0, z0, x1, z1;
            if (U.Has(a, "x0")) { if (!(U.Has(a, "x1") && U.Has(a, "z0") && U.Has(a, "z1"))) return ToolOutput.Err("a box needs x0,z0,x1,z1"); x0 = (float)U.D(a, "x0", 0); z0 = (float)U.D(a, "z0", 0); x1 = (float)U.D(a, "x1", 0); z1 = (float)U.D(a, "z1", 0); }
            else { var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0); var r = (float)Math.Min(300, U.D(a, "radius", 40)); x0 = x - r; x1 = x + r; z0 = z - r; z1 = z + r; }
            var limit = Math.Max(1, (int)U.D(a, "limit", 200)); var rows = new List<string>(); int total = 0;
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); if (!_fireH.Contains(h)) continue;
                var fp = Prefab(h) != null ? Prefab(h).GetComponent<Fireplace>() : null; if (fp == null) continue;
                total++; if (rows.Count >= limit) continue;
                var fuel = z.GetFloat("fuel", 0f);
                rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Zdos.NameOf(h)) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"fuel\":" + U.N(Math.Round(fuel, 2)) + ",\"maxFuel\":" + U.N(fp.m_maxFuel) + ",\"infinite\":" + (fp.m_infiniteFuel ? "true" : "false") + ",\"secondsLeft\":" + (fp.m_infiniteFuel ? "null" : U.N(Math.Round(fuel * fp.m_secPerFuel))) + ",\"burning\":" + (fp.m_infiniteFuel || fuel > 0f ? "true" : "false") + "}");
            }
            return U.Json("{\"total\":" + total + ",\"returned\":" + rows.Count + ",\"fires\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput DeathState(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var filter = McpJson.GetStr(a, "name"); var limit = Math.Max(1, (int)U.D(a, "limit", 50)); var rows = new List<string>(); int total = 0;
            foreach (var chunk in Zdos.InBoxChunks(-WorldBounds.Limit, -WorldBounds.Limit, WorldBounds.Limit, WorldBounds.Limit))
                foreach (var z in chunk)
                {
                    var name = Zdos.NameOf(z.GetPrefab()); if (!Ownership.IsTombstonePrefab(name)) continue;
                    var owner = z.GetString("ownerName", ""); if (filter != null && owner.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    total++; if (rows.Count >= limit) continue;
                    var stacks = 0; try { var s = z.GetString("items", ""); if (s.Length > 0) { var inv = new Inventory("x", null, 8, 8); inv.Load(new ZPackage(s)); stacks = inv.GetAllItems().Count; } } catch (Exception ex) { U.Once("death_state items", ex); }
                    rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"owner\":" + U.S(owner) + ",\"ownerId\":" + z.GetLong("owner", 0L).ToString(CultureInfo.InvariantCulture) + ",\"timeOfDeath\":" + z.GetLong("timeOfDeath", 0L).ToString(CultureInfo.InvariantCulture) + ",\"itemStacks\":" + stacks + "}");
                }
            return U.Json("{\"total\":" + total + ",\"tombstones\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput CostMode(Dictionary<string, object> a)
        {
            var zs = ZoneSystem.instance; if (zs == null) return ToolOutput.Err("no ZoneSystem");
            var sb = new StringBuilder("{");
            var first = true;
            foreach (var k in new[] { GlobalKeys.NoBuildCost, GlobalKeys.NoCraftCost, GlobalKeys.NoWorkbench, GlobalKeys.AllPiecesUnlocked, GlobalKeys.AllRecipesUnlocked, GlobalKeys.DungeonBuild, GlobalKeys.NoBuildingFall, GlobalKeys.PassiveMobs })
            { if (!first) sb.Append(','); first = false; sb.Append(U.S(k.ToString()) + ":" + (zs.GetGlobalKey(k) ? "true" : "false")); }
            foreach (var k in new[] { GlobalKeys.ResourceRate, GlobalKeys.EventRate, GlobalKeys.DurabilityRate, GlobalKeys.WorldLevel })
            { float v; sb.Append(',' + U.S(k.ToString()) + ":" + (zs.GetGlobalKey(k, out v) ? U.N(v) : "null")); }
            return U.Json(sb.Append('}').ToString());
        }
    }
}
