// Recipe, item and material readers: what the game says it takes to make or build something, and what a base has in its chests. Work on the dedicated server (ObjectDB and
// the ZDO table need no player). The arithmetic is in Bill.cs and is unit-tested.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class RecipeTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("recipe_info", "What the game's recipes say (ObjectDB). item: every enabled recipe that makes it (not only the first: Bronze comes 1 or 5 at a time), with station, minimum station level, how many a craft makes, ingredients, whether it is an 'any one ingredient' recipe or only an upgrade, the upgrade cost per quality level and the station level each level needs. uses: every recipe that has this item as an ingredient. station: every recipe made at that station (prefab name, e.g. forge), optional level. limit (default 40).",
                "{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\"},\"uses\":{\"type\":\"string\"},\"station\":{\"type\":\"string\"},\"level\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}}}", RecipeInfo);
            r.Add("item_info", "One item from ObjectDB: type, weight, stack size, value, max quality, teleportable (false for ores, metals and scrap: they cannot go through a portal), tool tier, durability, food values, total damage, and the recipes that make it and use it. name = the item prefab name (Wood, Bronze, Club).",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}", ItemInfo);
            r.Add("missing_for", "What it takes to have count of an item from what the chests round a point hold: x,z,radius (default 40, max 150) or box x0,z0,x1,z1. Walks the recipe chain (depth 4): whole crafts only, what a craft makes over is reused, stock first. Returns the steps in order (ingredients before what they make), what is missing (nothing makes it: it has to be gathered), the stations and levels needed, and what stock was used. The world's NoCraftCost key is reported: with it the ingredients are not charged. Read-only.",
                "{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\"},\"count\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"}},\"required\":[\"item\"]}", MissingFor);
            r.Add("plan_materials", "The bill of materials of a plan: items:[{prefab,...}] as for plan_apply. Sums Piece.m_resources per item, the crafting stations the pieces need (by name, in range of whoever builds by hand), pieces the world makes free (NoBuildCost; items and feasts use NoCraftCost), unknown prefabs, and, with x,z,radius or a box, what the chests there hold and what is missing. standing:true leaves out items that already stand within 0.35 m. Read-only.",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"standing\":{\"type\":\"boolean\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"}},\"required\":[\"items\"]}", PlanMaterials);
        }

        // ------------------------------------------------------------------ the recipe book
        static List<RecipeRow> _rows; static Dictionary<string, List<RecipeRow>> _byItem; static int _builtFor = -1;

        static string ItemName(ItemDrop d) { return d != null ? d.gameObject.name : ""; }

        static void Build()
        {
            var odb = ObjectDB.instance; if (odb == null) { _rows = null; return; }
            if (_rows != null && _builtFor == odb.m_recipes.Count) return;
            _rows = new List<RecipeRow>(); _byItem = new Dictionary<string, List<RecipeRow>>();
            foreach (var rc in odb.m_recipes)
            {
                if (rc == null || rc.m_item == null || !rc.m_enabled) continue;
                var row = new RecipeRow { Item = ItemName(rc.m_item), Amount = rc.m_amount, Station = rc.m_craftingStation != null ? rc.m_craftingStation.gameObject.name : "", MinLevel = rc.m_minStationLevel, OnlyOne = rc.m_requireOnlyOneIngredient, UpgradeOnly = rc.m_noCraftOnlyUpgrade };
                foreach (var q in rc.m_resources) if (q.m_resItem != null) row.Ingredients.Add(new KeyValuePair<string, int>(ItemName(q.m_resItem), q.m_amount));
                _rows.Add(row); if (!_byItem.ContainsKey(row.Item)) _byItem[row.Item] = new List<RecipeRow>(); _byItem[row.Item].Add(row);
            }
            _builtFor = odb.m_recipes.Count;
        }

        static string Rows(IEnumerable<RecipeRow> rows, int limit)
        {
            return string.Join(",", rows.Take(limit).Select(r => "{\"item\":" + U.S(r.Item) + ",\"makes\":" + r.Amount + ",\"station\":" + U.S(r.Station) + ",\"stationLevel\":" + r.RequiredStationLevel + ",\"anyOneIngredient\":" + B(r.OnlyOne) + ",\"upgradeOnly\":" + B(r.UpgradeOnly) + ",\"ingredients\":[" + string.Join(",", r.Ingredients.Select(i => "{\"item\":" + U.S(i.Key) + ",\"amount\":" + i.Value + "}").ToArray()) + "]}").ToArray());
        }

        static string B(bool v) { return v ? "true" : "false"; }

        static ToolOutput RecipeInfo(Dictionary<string, object> a)
        {
            Build(); if (_rows == null) return ToolOutput.Err("ObjectDB not ready");
            var item = McpJson.GetStr(a, "item"); var uses = McpJson.GetStr(a, "uses"); var station = McpJson.GetStr(a, "station"); var level = (int)U.D(a, "level", 0); var limit = Math.Max(1, (int)U.D(a, "limit", 40));
            if (item == null && uses == null && station == null) return ToolOutput.Err("give item, uses or station");
            IEnumerable<RecipeRow> q = _rows;
            if (item != null) q = q.Where(r => string.Equals(r.Item, item, StringComparison.OrdinalIgnoreCase));
            if (uses != null) q = q.Where(r => r.Ingredients.Any(i => string.Equals(i.Key, uses, StringComparison.OrdinalIgnoreCase)));
            if (station != null) q = q.Where(r => string.Equals(r.Station, station, StringComparison.OrdinalIgnoreCase) && (level <= 0 || r.RequiredStationLevel <= level));
            var list = q.ToList();
            var upgrades = "";
            if (item != null && list.Count > 0)
            {
                var d = ObjectDB.instance.GetItemPrefab(item); var drop = d != null ? d.GetComponent<ItemDrop>() : null;
                var maxQ = drop != null ? drop.m_itemData.m_shared.m_maxQuality : 1;
                var rc = ObjectDB.instance.m_recipes.FirstOrDefault(x => x != null && x.m_item != null && ItemName(x.m_item) == list[0].Item && x.m_enabled && !x.m_noCraftOnlyUpgrade) ?? ObjectDB.instance.m_recipes.FirstOrDefault(x => x != null && x.m_item != null && ItemName(x.m_item) == list[0].Item && x.m_enabled);
                if (rc != null && maxQ > 1)
                {
                    var lv = new List<string>();
                    for (var qn = 2; qn <= Math.Min(maxQ, 10); qn++)
                        lv.Add("{\"quality\":" + qn + ",\"stationLevel\":" + rc.GetRequiredStationLevel(qn) + ",\"ingredients\":[" + string.Join(",", rc.m_resources.Where(x => x.m_resItem != null).Select(x => "{\"item\":" + U.S(ItemName(x.m_resItem)) + ",\"amount\":" + x.GetAmount(qn) + "}").ToArray()) + "]}");
                    upgrades = ",\"maxQuality\":" + maxQ + ",\"upgrades\":[" + string.Join(",", lv.ToArray()) + "]";
                }
            }
            return U.Json("{\"count\":" + list.Count + ",\"recipes\":[" + Rows(list, limit) + "]" + upgrades + "}");
        }

        static ToolOutput ItemInfo(Dictionary<string, object> a)
        {
            Build(); if (_rows == null) return ToolOutput.Err("ObjectDB not ready");
            var name = McpJson.GetStr(a, "name"); var go = name != null ? ObjectDB.instance.GetItemPrefab(name) : null; var drop = go != null ? go.GetComponent<ItemDrop>() : null;
            if (drop == null) return ToolOutput.Err("no item named " + name);
            var s = drop.m_itemData.m_shared;
            var makes = _rows.Where(r => r.Item == go.name).ToList(); var usedIn = _rows.Where(r => r.Ingredients.Any(i => i.Key == go.name)).Select(r => r.Item).Distinct().Take(30).ToList();
            double dmg = 0; try { dmg = s.m_damages.GetTotalDamage(); } catch (Exception ex) { U.Once("item_info.damage", ex); }
            return U.Json("{\"name\":" + U.S(go.name) + ",\"displayToken\":" + U.S(s.m_name) + ",\"type\":" + U.S(s.m_itemType.ToString()) + ",\"weight\":" + U.N(s.m_weight) + ",\"maxStack\":" + s.m_maxStackSize + ",\"value\":" + s.m_value + ",\"maxQuality\":" + s.m_maxQuality + ",\"teleportable\":" + B(s.m_teleportable) + ",\"toolTier\":" + s.m_toolTier + ",\"maxDurability\":" + U.N(s.m_maxDurability) +
                ",\"food\":" + U.N(s.m_food) + ",\"foodStamina\":" + U.N(s.m_foodStamina) + ",\"foodEitr\":" + U.N(s.m_foodEitr) + ",\"foodBurnTime\":" + U.N(s.m_foodBurnTime) + ",\"damage\":" + U.N(Math.Round(dmg, 1)) + ",\"madeBy\":[" + Rows(makes, 5) + "],\"usedIn\":[" + string.Join(",", usedIn.Select(U.S).ToArray()) + "]}");
        }

        // ------------------------------------------------------------------ stock
        static bool Box(Dictionary<string, object> a, out float x0, out float z0, out float x1, out float z1, out string err)
        {
            err = null; x0 = z0 = x1 = z1 = 0;
            if (U.Has(a, "x0")) { if (!(U.Has(a, "x1") && U.Has(a, "z0") && U.Has(a, "z1"))) { err = "a box needs x0,z0,x1,z1"; return false; } x0 = (float)U.D(a, "x0", 0); z0 = (float)U.D(a, "z0", 0); x1 = (float)U.D(a, "x1", 0); z1 = (float)U.D(a, "z1", 0); return true; }
            if (U.Has(a, "x")) { var r = (float)Math.Min(150, U.D(a, "radius", 40)); var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0); x0 = x - r; x1 = x + r; z0 = z - r; z1 = z + r; return true; }
            return false;
        }

        /// <summary>What the chests in a box hold, by item prefab name. Tombstones and carts are not storage.</summary>
        static Dictionary<string, int> Stock(float x0, float z0, float x1, float z1, out int chests)
        {
            var stock = new Dictionary<string, int>(); chests = 0;
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                if (!Neighbours.IsContainer(z.GetPrefab())) continue;
                var name = Zdos.NameOf(z.GetPrefab()); if (Ownership.IsTombstonePrefab(name) || name.IndexOf("cart", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var s = z.GetString("items", ""); if (s.Length == 0) continue;
                try
                {
                    var inv = new Inventory("x", null, 10, 10); inv.Load(new ZPackage(s)); chests++;
                    foreach (var it in inv.GetAllItems()) { var n = it.m_dropPrefab != null ? it.m_dropPrefab.name : it.m_shared.m_name; stock[n] = (stock.ContainsKey(n) ? stock[n] : 0) + it.m_stack; }
                }
                catch (Exception ex) { U.Once("Recipes.Stock", ex); }
                if (chests >= 400) break;
            }
            return stock;
        }

        static string CostKeys()
        {
            var zs = ZoneSystem.instance; if (zs == null) return "{}";
            return "{\"NoCraftCost\":" + B(zs.GetGlobalKey(GlobalKeys.NoCraftCost)) + ",\"NoBuildCost\":" + B(zs.GetGlobalKey(GlobalKeys.NoBuildCost)) + ",\"NoWorkbench\":" + B(zs.GetGlobalKey(GlobalKeys.NoWorkbench)) + "}";
        }

        static string Dict(IDictionary<string, int> d) { return "{" + string.Join(",", d.OrderByDescending(kv => kv.Value).Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "}"; }

        static ToolOutput MissingFor(Dictionary<string, object> a)
        {
            Build(); if (_rows == null) return ToolOutput.Err("ObjectDB not ready");
            var item = McpJson.GetStr(a, "item"); var go = item != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            if (go == null) return ToolOutput.Err("no item named " + item);
            float x0, z0, x1, z1; string err;
            if (!Box(a, out x0, out z0, out x1, out z1, out err)) return ToolOutput.Err(err ?? "give x,z[,radius] or x0,z0,x1,z1 (where the chests are)");
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            int chests; var stock = Stock(x0, z0, x1, z1, out chests);
            var count = Math.Max(1, (int)U.D(a, "count", 1));
            var b = Bill.Run(go.name, count, stock, _byItem);
            return U.Json("{\"item\":" + U.S(go.name) + ",\"count\":" + count + ",\"chests\":" + chests + ",\"complete\":" + B(b.Complete) + ",\"steps\":[" + string.Join(",", b.Steps.Select(s => U.S(Bill.Describe(s))).ToArray()) + "],\"missing\":" + Dict(b.Missing) + ",\"fromStock\":" + Dict(b.FromStock) + ",\"stations\":" + Dict(b.Stations) + ",\"notes\":[" + string.Join(",", b.Notes.Select(U.S).ToArray()) + "],\"costKeys\":" + CostKeys() + "}");
        }

        static ToolOutput PlanMaterials(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var items = McpJson.GetList(a, "items"); if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            if (items.Count > 6000) return ToolOutput.Err("max 6000 items");
            var standing = McpJson.GetBool(a, "standing", false);
            var counts = new Dictionary<string, int>(); var reqs = new Dictionary<string, List<KeyValuePair<string, int>>>(); var free = new HashSet<string>(); var stationsNeeded = new Dictionary<string, int>();
            var zs = ZoneSystem.instance;
            foreach (var o in items)
            {
                var it = o as Dictionary<string, object>; if (it == null) continue; var prefab = McpJson.GetStr(it, "prefab"); if (string.IsNullOrEmpty(prefab)) continue;
                if (standing && Zdos.Ready && U.Has(it, "x") && U.Has(it, "z"))
                {
                    var x = (float)U.D(it, "x", 0); var z = (float)U.D(it, "z", 0); var y = (float)U.D(it, "y", 0); var found = false;
                    foreach (var zd in Zdos.InBox(x - 1f, z - 1f, x + 1f, z + 1f)) if (Zdos.NameOf(zd.GetPrefab()) == prefab && Vector3.Distance(zd.GetPosition(), new Vector3(x, U.Has(it, "y") ? y : zd.GetPosition().y, z)) < 0.35f) { found = true; break; }
                    if (found) continue;
                }
                counts[prefab] = (counts.ContainsKey(prefab) ? counts[prefab] : 0) + 1;
                if (reqs.ContainsKey(prefab)) continue;
                var go = ZNetScene.instance.GetPrefab(prefab); var pc = go != null ? go.GetComponent<Piece>() : null; if (pc == null) continue;
                reqs[prefab] = pc.m_resources.Where(q => q.m_resItem != null && q.m_amount > 0).Select(q => new KeyValuePair<string, int>(ItemName(q.m_resItem), q.m_amount)).ToList();
                if (zs != null && zs.GetGlobalKey(pc.FreeBuildKey())) free.Add(prefab);
                if (pc.m_craftingStation != null) { var sn = pc.m_craftingStation.gameObject.name; stationsNeeded[sn] = (stationsNeeded.ContainsKey(sn) ? stationsNeeded[sn] : 0) + counts[prefab]; }
            }
            List<string> unknown; var total = Materials.Sum(counts, reqs, free, out unknown);
            var sb = new System.Text.StringBuilder("{\"pieces\":" + counts.Values.Sum() + ",\"kinds\":" + counts.Count + ",\"materials\":" + Dict(total) + ",\"free\":[" + string.Join(",", free.Select(U.S).ToArray()) + "],\"stationsNeeded\":" + Dict(stationsNeeded) + ",\"unknownPrefabs\":[" + string.Join(",", unknown.Select(U.S).ToArray()) + "],\"costKeys\":" + CostKeys());
            float x0, z0, x1, z1; string err;
            if (Box(a, out x0, out z0, out x1, out z1, out err) && Zdos.Ready)
            {
                int chests; var stock = Stock(x0, z0, x1, z1, out chests);
                sb.Append(",\"chests\":" + chests + ",\"compare\":[" + string.Join(",", Materials.Compare(total, stock).Select(c => "{\"item\":" + U.S(c.Key) + ",\"need\":" + c.Value[0] + ",\"have\":" + c.Value[1] + ",\"missing\":" + c.Value[2] + "}").ToArray()) + "]");
            }
            return U.Json(sb.Append('}').ToString());
        }
    }
}
