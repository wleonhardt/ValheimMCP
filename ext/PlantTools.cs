// plant_check and farm_state: will a plant grow here, and how are the plants round a point doing (Plant.UpdateHealth rules in PlantRules.cs). Work on the server (ZDO table, prefabs,
// world generator); cultivated ground can only be read where the zone is loaded and is reported as unknown elsewhere.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class PlantTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("plant_check", "Will these plants grow where you put them? items:[{prefab,x,z,y?}] (max 400; saplings and crops: sapling_carrot, sapling_turnip, Birch_Sapling...). Judged as the game does every 10 s (Plant.UpdateHealth): wrong biome, not cultivated ground, too hot or too cold, no sun (any solid or piece in the 100 m straight above it; a wall beside it or the terrain is not shade), no space (any collider within the plant's grow radius; another plant only while healthy). Pieces and trees standing round it count, so do the other items of the request. Cultivated ground is read where the zone is loaded and reported as unknown elsewhere. Also the grow time range (the exact time is drawn from each plant's own seed). Read-only.",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}}},\"required\":[\"items\"]}", PlantCheck);
            r.Add("farm_state", "The plants round a point (ZDO table): prefab, position, age, the grow time range, ready from / by (seconds left), whether it already had time to grow, and the verdict the game's own rules give where it stands (biome, shade, space). x,z,radius (default 30, max 100), limit (default 100).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", FarmState);
        }

        static string B(bool v) { return v ? "true" : "false"; }

        static PlantFacts FactsOf(GameObject go)
        {
            var pl = go != null ? go.GetComponent<Plant>() : null; if (pl == null) return null;
            return new PlantFacts { Prefab = go.name, GrowRadius = pl.m_growRadius, GrowTime = pl.m_growTime, GrowTimeMax = pl.m_growTimeMax, BiomeMask = (int)pl.m_biome, NeedCultivated = pl.m_needCultivatedGround, TolerateHeat = pl.m_tolerateHeat, TolerateCold = pl.m_tolerateCold };
        }

        internal static int BiomeAt(double x, double z)
        {
            try { var wg = WorldGenerator.instance; if (wg != null) return (int)wg.GetBiome((float)x, (float)z); } catch (Exception ex) { U.Once("PlantTools.biome", ex); }
            return 0;
        }

        internal static bool? CultivatedAt(Vector3 p)
        {
            try { var hm = Heightmap.FindHeightmap(p); if (hm != null) return hm.IsCultivated(p); } catch (Exception ex) { U.Once("PlantTools.cultivated", ex); }
            return null;
        }

        static List<PlacedPiece> World(double x0, double z0, double x1, double z1, ISet<string> skipIds)
        {
            var res = new List<PlacedPiece>();
            foreach (var z in Zdos.InBox((float)x0, (float)z0, (float)x1, (float)z1))
            {
                var name = Zdos.NameOf(z.GetPrefab()); if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                var id = U.Id(z); if (skipIds != null && skipIds.Contains(id)) continue;
                var geo = PlanCheck.GeoOf(name); if (geo == null || geo.Colliders.Count == 0) continue;
                var p = z.GetPosition(); var q = z.GetRotation();
                res.Add(new PlacedPiece { Id = id, Geo = geo, Pos = new V3(p.x, p.y, p.z), Rot = new Quat(q.x, q.y, q.z, q.w), Planned = false });
            }
            return res;
        }

        static ToolOutput PlantCheck(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var items = McpJson.GetList(a, "items"); if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            if (items.Count > 400) return ToolOutput.Err("max 400 items");
            var plants = new List<KeyValuePair<PlacedPiece, PlantFacts>>(); var errors = new List<string>(); double x0 = 1e9, z0 = 1e9, x1 = -1e9, z1 = -1e9; var idx = 0;
            foreach (var o in items)
            {
                idx++; var it = o as Dictionary<string, object>; if (it == null) continue;
                var prefab = McpJson.GetStr(it, "prefab"); var go = prefab != null ? ZNetScene.instance.GetPrefab(prefab) : null; var f = FactsOf(go);
                if (f == null) { errors.Add("item " + idx + ": " + (prefab ?? "no prefab") + " is not a plant"); continue; }
                double x = McpJson.Get(it, "x", double.NaN), z = McpJson.Get(it, "z", double.NaN);
                if (!WorldBounds.InWorld(x, 0, z)) { errors.Add("item " + idx + ": position not finite or outside the world"); continue; }
                double y = McpJson.Get(it, "y", double.NaN);
                if (double.IsNaN(y)) { float gh; y = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3((float)x, 0f, (float)z), out gh) ? gh : (WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight((float)x, (float)z) : 0); }
                var geo = PlanCheck.GeoOf(prefab) ?? new PieceGeo { Prefab = prefab };
                plants.Add(new KeyValuePair<PlacedPiece, PlantFacts>(new PlacedPiece { Id = McpJson.GetStr(it, "key") ?? ("#" + idx), Geo = geo, Pos = new V3(x, y, z), Planned = true }, f));
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            if (plants.Count == 0) return ToolOutput.Err("no usable items: " + string.Join("; ", errors.Take(5).ToArray()));
            var world = World(x0 - 4, z0 - 4, x1 + 4, z1 + 4, null);
            var planned = plants.Select(p => p.Key).ToList(); var plannedSet = new HashSet<PlacedPiece>(planned); var all = new List<PlacedPiece>(world); all.AddRange(planned);
            var rows = new List<string>(); var byStatus = new Dictionary<string, int>(); var unknown = 0;
            var shields = ReaderTools.ShieldsNear(x0, z0, x1, z1);
            var judged = new Dictionary<PlacedPiece, string>();                       // Plant.HaveGrowSpace: another plant blocks only while it is Healthy, and the game updates plants one after the other: the first of a crowd is healthy, those after it have no room
            foreach (var kv in plants)
            {
                var p = kv.Key; var f = kv.Value; var pos = new Vector3((float)p.Pos.X, (float)p.Pos.Y, (float)p.Pos.Z);
                var others = all.Where(q => q != p && Math.Abs(q.Pos.X - p.Pos.X) < 40 && Math.Abs(q.Pos.Z - p.Pos.Z) < 40).ToList();
                var v = PlantRules.Judge(f, p.Pos, BiomeAt(p.Pos.X, p.Pos.Z), CultivatedAt(pos), others, q => plannedSet.Contains(q) || q.Geo.Prefab.StartsWith("sapling", StringComparison.OrdinalIgnoreCase) || q.Geo.Prefab.EndsWith("_sapling", StringComparison.OrdinalIgnoreCase), q => { string st; return !plannedSet.Contains(q) || (judged.TryGetValue(q, out st) && st == "Healthy"); }, ShieldRules.Inside(shields, p.Pos.X, p.Pos.Y, p.Pos.Z));
                judged[p] = v.Status; byStatus[v.Status] = (byStatus.ContainsKey(v.Status) ? byStatus[v.Status] : 0) + 1; if (v.Unknown) unknown++;
                rows.Add("{\"key\":" + U.S(p.Id) + ",\"prefab\":" + U.S(f.Prefab) + ",\"pos\":" + U.V(pos) + ",\"status\":" + U.S(v.Status) + ",\"why\":" + U.S(v.Why) + ",\"unknown\":" + B(v.Unknown) + ",\"growSeconds\":[" + U.N(f.GrowTime) + "," + U.N(f.GrowTimeMax) + "]}");
            }
            return U.Json("{\"checked\":" + plants.Count + ",\"byStatus\":{" + string.Join(",", byStatus.Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "},\"cultivationUnknown\":" + unknown + ",\"errors\":[" + string.Join(",", errors.Select(U.S).ToArray()) + "],\"plants\":[" + string.Join(",", rows.Take(200).ToArray()) + "]}");
        }

        /// <summary>The text the game shows for this plant (its own status: healthy, no sun, no space ...), when the plant is instantiated here (a client near it). null on the server.</summary>
        static string LiveHover(ZDO z)
        {
            try
            {
                var nv = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(z) : null; var pl = nv != null ? nv.GetComponent<Plant>() : null;
                return pl != null ? U.S(pl.GetHoverText().Replace('\n', ' ')) : "null";
            }
            catch (Exception ex) { U.Once("Plant.LiveHover", ex); return "null"; }
        }

        static ToolOutput FarmState(Dictionary<string, object> a)
        {
            if (!Zdos.Ready || ZNetScene.instance == null) return ToolOutput.Err("world not ready");
            double cx = U.D(a, "x", 0), cz = U.D(a, "z", 0), rad = Math.Min(100, Math.Max(1, U.D(a, "radius", 30))); var limit = Math.Max(1, (int)U.D(a, "limit", 100));
            if (!WorldBounds.InWorld(cx, 0, cz)) return ToolOutput.Err("x and z must be finite and inside the world");
            var world = World(cx - rad - 4, cz - rad - 4, cx + rad + 4, cz + rad + 4, null);
            var now = ZNet.instance != null ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks; var cache = new Dictionary<int, PlantFacts>(); var shields = ReaderTools.ShieldsNear(cx - rad, cz - rad, cx + rad, cz + rad);
            var rows = new List<string>(); var total = 0; var judgedIds = new Dictionary<string, string>();       // see plant_check: a plant blocks its neighbours only while it is Healthy, so the first of a crowd grows
            foreach (var z in Zdos.InBox((float)(cx - rad), (float)(cz - rad), (float)(cx + rad), (float)(cz + rad)))
            {
                var h = z.GetPrefab(); PlantFacts f;
                if (!cache.TryGetValue(h, out f)) cache[h] = f = FactsOf(ZNetScene.instance.GetPrefab(h));
                if (f == null) continue;
                var p = z.GetPosition(); if ((p.x - cx) * (p.x - cx) + (p.z - cz) * (p.z - cz) > rad * rad) continue;
                total++; if (rows.Count >= limit) continue;
                var planted = z.GetLong("plantTime", now); var age = Math.Max(0, (now - planted) / (double)Husbandry.TicksPerSecond);
                var me = U.Id(z); var pp = new V3(p.x, p.y, p.z);
                var others = world.Where(q => q.Id != me && Math.Abs(q.Pos.X - pp.X) < 40 && Math.Abs(q.Pos.Z - pp.Z) < 40).ToList();
                var v = PlantRules.Judge(f, pp, BiomeAt(p.x, p.z), CultivatedAt(p), others, q => q.Geo.Prefab.StartsWith("sapling", StringComparison.OrdinalIgnoreCase) || q.Geo.Prefab.EndsWith("_sapling", StringComparison.OrdinalIgnoreCase), q => { string st; return judgedIds.TryGetValue(q.Id, out st) && st == "Healthy"; }, ShieldRules.Inside(shields, pp.X, pp.Y, pp.Z));
                judgedIds[me] = v.Status;
                rows.Add("{\"id\":" + U.S(me) + ",\"live\":" + LiveHover(z) + ",\"prefab\":" + U.S(f.Prefab) + ",\"pos\":" + U.V(p) + ",\"ageSeconds\":" + U.N(Math.Round(age)) + ",\"growSeconds\":[" + U.N(f.GrowTime) + "," + U.N(f.GrowTimeMax) + "],\"readyFromSeconds\":" + U.N(Math.Round(Math.Max(0, f.GrowTime - age))) + ",\"readyBySeconds\":" + U.N(Math.Round(Math.Max(0, f.GrowTimeMax - age))) + ",\"hadTimeToGrow\":" + B(age >= f.GrowTimeMax) + ",\"status\":" + U.S(v.Status) + ",\"why\":" + U.S(v.Why) + "}");
            }
            return U.Json("{\"total\":" + total + ",\"returned\":" + rows.Count + ",\"plants\":[" + string.Join(",", rows.ToArray()) + "]}");
        }
    }
}
