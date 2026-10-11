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
            r.Add("stations", "Crafting stations round a point or in a box (ZDO table, works on the server): prefab, position, level (1 + the extensions that count for it: pivot to pivot closer than the extension's own range, one per kind unless it stacks), the extensions with distances, the build range (m_rangeBuild + m_extraRangePerLevel per extension, measured flat from the player), whether it needs a roof or a fire to craft, and what the next extension would unlock (recipes of the next level). Also the extensions that count for no station here, with the reason. The roof and fire themselves need the physics and are judged by bed_check / plan_check style tools, not here.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"}}}", Stations);
            r.Add("smelters", "Smelters, blast furnaces, charcoal kilns, windmills, spinning wheels and eitr refineries round a point or in a box, from the ZDO table (works on the server): the ore queue (names, how many, room left), fuel and how many fuel presses the game still accepts (it refuses above max - 1), fuel needed for what is queued and seconds of fuel left, seconds to finish the queue (if the fuel lasts: see fuelShortfall), products held back as a stack (the smelter releases them itself when the queue empties, fuel runs out, the stack is full or the ore changes; 'heldBack.ore' is the ore that was smelted), whether the fuel covers the queue (fuelShortfall), what it accepts and what it makes, and why it stands still (no ore, out of fuel). A windmill-driven machine runs at the windmill's power, which is not stored: times assume power 1. A roof or blocked smoke needs the physics and is not judged here. The game catches up at most 3600 s of work after an absence (catchUpCapSeconds), and secondsToEmpty assumes full power: a windmill's real output is not read.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}}}", Smelters);
            r.Add("cookers", "Cooking stations (grills, ovens, cauldrons with slots, drying racks) round a point or in a box, from the ZDO table: each slot's item, its status (cooking, done, burnt), seconds until done and until it burns (twice the cook time, if the station can overcook), fuel for the ones that burn fuel and hours left. runsNow says whether it is cooking (fuel stations: from fuel and a slot that is not done; fire-lit stations: null, the fire is not stored, and the times then assume it is lit). x,z,radius (default 40, max 150) or box.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"}}}", Cookers);
            r.Add("fuel_forecast", "What a base burns and how long its chests cover it, per fuel item: every fireplace, smelter and fuel-burning cooker in the area with its fuel, the fuel units it burns per hour while it works, and the stock of that fuel item in the chests of the same area, so 'the hearth and two braziers need 14 wood an hour and the chests hold 60: four hours'. Hours are for the fuel already inside plus the chests. x,z,radius (default 40, max 150) or box. A fireplace that is switched off or empty burns nothing; a smelter burns only with ore queued.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"}}}", FuelForecast);
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
                var d = ObjectDB.instance.GetItemPrefab(list[0].Item); var drop = d != null ? d.GetComponent<ItemDrop>() : null;
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
                var pkgItems = ContainerData.Package(z); if (pkgItems == null) continue;
                try
                {
                    var inv = new Inventory("x", null, 10, 10); inv.Load(pkgItems); chests++;
                    foreach (var it in inv.GetAllItems()) { var n = it.m_dropPrefab != null ? it.m_dropPrefab.name : it.m_shared.m_name; stock[n] = (stock.ContainsKey(n) ? stock[n] : 0) + it.m_stack; }
                }
                catch (Exception ex) { U.Once("Recipes.Stock", ex); }
                if (chests >= 400) break;
            }
            return stock;
        }

        static ToolOutput Stations(Dictionary<string, object> a)
        {
            Build(); if (_rows == null) return ToolOutput.Err("ObjectDB not ready");
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            float x0, z0, x1, z1; string err;
            if (!Box(a, out x0, out z0, out x1, out z1, out err)) return ToolOutput.Err(err ?? "give x,z[,radius] or x0,z0,x1,z1");
            const float reach = 12f;                                                       // an extension up to this far outside the box can still belong to a station inside it
            var stations = new List<StationFact>(); var exts = new List<ExtensionFact>(); var cache = new Dictionary<int, GameObject>();
            foreach (var z in Zdos.InBox(x0 - reach, z0 - reach, x1 + reach, z1 + reach))
            {
                var h = z.GetPrefab(); GameObject go;
                if (!cache.TryGetValue(h, out go)) cache[h] = go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(h) : null;
                if (go == null) continue;
                var cs = go.GetComponent<CraftingStation>(); var se = go.GetComponent<StationExtension>();
                if (cs == null && se == null) continue;
                var p = z.GetPosition();
                if (cs != null) stations.Add(new StationFact { Id = U.Id(z), Name = cs.m_name, Prefab = go.name, X = p.x, Y = p.y, Z = p.z, RangeBuild = cs.m_rangeBuild, ExtraRange = cs.m_extraRangePerLevel, NeedsRoof = cs.m_craftRequireRoof, NeedsFire = cs.m_craftRequireFire });
                if (se != null && se.m_craftingStation != null) exts.Add(new ExtensionFact { Id = U.Id(z), Station = se.m_craftingStation.m_name, Prefab = go.name, X = p.x, Y = p.y, Z = p.z, MaxDistance = se.m_maxStationDistance, Stack = se.m_stack });
            }
            List<KeyValuePair<ExtensionFact, string>> unlinked;
            var links = StationMath.Link(stations, exts, out unlinked);
            var rows = links.Where(l => l.Station.X >= x0 && l.Station.X <= x1 && l.Station.Z >= z0 && l.Station.Z <= z1).Select(l =>
            {
                var s = l.Station;
                var next = _rows.Where(r => r.Station == s.Prefab && r.RequiredStationLevel == l.Level + 1 && !r.UpgradeOnly).Select(r => r.Item).Distinct().Take(8).ToList();
                return "{\"id\":" + U.S(s.Id) + ",\"prefab\":" + U.S(s.Prefab) + ",\"pos\":[" + U.N(Math.Round(s.X, 1)) + "," + U.N(Math.Round(s.Y, 1)) + "," + U.N(Math.Round(s.Z, 1)) + "],\"level\":" + l.Level + ",\"buildRange\":" + U.N(l.BuildRange) + ",\"needsRoof\":" + B(s.NeedsRoof) + ",\"needsFire\":" + B(s.NeedsFire) +
                    ",\"extensions\":[" + string.Join(",", l.Linked.Select(e => "{\"prefab\":" + U.S(e.Prefab) + ",\"id\":" + U.S(e.Id) + ",\"distance\":" + U.N(Math.Round(Math.Sqrt((e.X - s.X) * (e.X - s.X) + (e.Y - s.Y) * (e.Y - s.Y) + (e.Z - s.Z) * (e.Z - s.Z)), 1)) + "}").ToArray()) + "],\"nextLevelUnlocks\":[" + string.Join(",", next.Select(U.S).ToArray()) + "]}";
            }).ToList();
            var un = unlinked.Where(u => u.Key.X >= x0 && u.Key.X <= x1 && u.Key.Z >= z0 && u.Key.Z <= z1).Take(40).Select(u => "{\"prefab\":" + U.S(u.Key.Prefab) + ",\"id\":" + U.S(u.Key.Id) + ",\"why\":" + U.S(u.Value) + "}");
            return U.Json("{\"stations\":[" + string.Join(",", rows.ToArray()) + "],\"extensionsCountingForNothing\":[" + string.Join(",", un.ToArray()) + "]}");
        }

        static ToolOutput Smelters(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            float x0, z0, x1, z1; string err;
            if (!Box(a, out x0, out z0, out x1, out z1, out err)) return ToolOutput.Err(err ?? "give x,z[,radius] or x0,z0,x1,z1");
            var limit = Math.Max(1, (int)U.D(a, "limit", 100)); var rows = new List<string>(); var total = 0; var cache = new Dictionary<int, GameObject>();
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); GameObject go;
                if (!cache.TryGetValue(h, out go)) cache[h] = go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(h) : null;
                var sm = go != null ? go.GetComponent<Smelter>() : null; if (sm == null) continue;
                total++; if (rows.Count >= limit) continue;
                var spec = new SmelterSpec { MaxOre = sm.m_maxOre, MaxFuel = sm.m_maxFuel, FuelPerProduct = sm.m_fuelPerProduct, SecPerProduct = sm.m_secPerProduct, RequiresRoof = sm.m_requiresRoof };
                var queued = z.GetInt(ZDOVars.s_queued, 0); var fuel = z.GetFloat(ZDOVars.s_fuel, 0f); var bake = z.GetFloat(ZDOVars.s_bakeTimer, 0f);
                var ore = new List<string>(); for (var i = 0; i < queued && i < 40; i++) ore.Add(z.GetString("item" + i, ""));
                var held = z.GetString(ZDOVars.s_spawnOre, ""); var heldN = z.GetInt(ZDOVars.s_spawnAmount, 0);
                var accepts = sm.m_conversion.Where(c => c.m_from != null).Select(c => c.m_from.gameObject.name).Distinct().ToList();
                var makes = sm.m_conversion.Where(c => c.m_to != null).Select(c => c.m_to.gameObject.name).Distinct().ToList();
                var stalled = SmelterMath.Stalled(spec, queued, fuel, false, true);
                var p = z.GetPosition();
                rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(go.name) + ",\"pos\":" + U.V(p) + ",\"queued\":" + queued + ",\"queueRoom\":" + SmelterMath.QueueRoom(spec, queued) + ",\"ore\":[" + string.Join(",", ore.Select(U.S).ToArray()) + "],\"fuel\":" + U.N(Math.Round(fuel, 2)) + ",\"maxFuel\":" + spec.MaxFuel + ",\"fuelItem\":" + U.S(sm.m_fuelItem != null ? sm.m_fuelItem.gameObject.name : "") + ",\"fuelPressesAllowed\":" + SmelterMath.FuelPressesAllowed(spec, fuel) + ",\"fuelNeeded\":" + U.N(Math.Round(SmelterMath.FuelNeeded(spec, queued, bake), 2)) +
                    ",\"fuelSecondsLeft\":" + (spec.MaxFuel <= 0 ? "null" : U.N(Math.Round(SmelterMath.FuelSecondsLeft(spec, fuel, 1)))) + ",\"secondsToEmpty\":" + U.N(Math.Round(SmelterMath.SecondsToEmpty(spec, queued, bake, 1))) + ",\"catchUpCapSeconds\":" + SmelterMath.CatchUpCapSeconds + ",\"fuelShortfall\":" + (spec.MaxFuel <= 0 ? "0" : U.N(Math.Round(Math.Max(0, SmelterMath.FuelNeeded(spec, queued, bake) - fuel), 2))) + ",\"heldBack\":{\"ore\":" + U.S(held) + ",\"amount\":" + heldN + "},\"stalled\":" + U.S(stalled) + ",\"needsRoof\":" + B(spec.RequiresRoof) + ",\"windmill\":" + B(sm.m_windmill != null) + ",\"accepts\":[" + string.Join(",", accepts.Select(U.S).ToArray()) + "],\"makes\":[" + string.Join(",", makes.Select(U.S).ToArray()) + "]}");
            }
            return U.Json("{\"total\":" + total + ",\"returned\":" + rows.Count + ",\"smelters\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput Cookers(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            float x0, z0, x1, z1; string err;
            if (!Box(a, out x0, out z0, out x1, out z1, out err)) return ToolOutput.Err(err ?? "give x,z[,radius] or x0,z0,x1,z1");
            var rows = new List<string>(); var cache = new Dictionary<int, GameObject>();
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); GameObject go; if (!cache.TryGetValue(h, out go)) cache[h] = go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(h) : null;
                var cs = go != null ? go.GetComponent<CookingStation>() : null; if (cs == null) continue;
                var slots = new List<string>(); var fuel = z.GetFloat("fuel", 0f);
                var uncooked = Enumerable.Range(0, cs.m_slots.Length).Any(i => z.GetString("slot" + i, "").Length > 0 && z.GetInt("slotstatus" + i, 0) == 0);
                var fuelRuns = cs.m_useFuel && fuel > 0f && (cs.m_useFueldWhileEmpty || uncooked);          // CookingStation.UpdateCooking
                bool? runsNow = fuelRuns ? (bool?)true : (cs.m_requireFire ? (bool?)null : false);            // a fire-lit station: whether the fire is lit is not stored
                for (var i = 0; i < cs.m_slots.Length; i++)
                {
                    var item = z.GetString("slot" + i, ""); if (item.Length == 0) { slots.Add("{\"slot\":" + i + ",\"item\":null}"); continue; }
                    var cooked = z.GetFloat("slot" + i, 0f); var status = z.GetInt("slotstatus" + i, 0);
                    var conv = cs.m_conversion.FirstOrDefault(c => c.m_from != null && (c.m_from.gameObject.name == item || (c.m_to != null && c.m_to.gameObject.name == item)));
                    var ct = conv != null ? conv.m_cookTime : 0f; var times = runsNow != false;
                    slots.Add("{\"slot\":" + i + ",\"item\":" + U.S(item) + ",\"state\":" + U.S(CookRules.State(status)) + ",\"cookedSeconds\":" + U.N(Math.Round(cooked)) + ",\"secondsToDone\":" + (times && status == 0 && ct > 0 ? U.N(Math.Round(CookRules.SecondsToDone(cooked, ct))) : "null") + ",\"secondsToBurnt\":" + (times && status != 2 && ct > 0 && cs.m_canOvercookItems ? U.N(Math.Round(CookRules.SecondsToBurnt(cooked, ct))) : "null") + "}");
                }
                rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(go.name) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"needsFire\":" + B(cs.m_requireFire) + ",\"usesFuel\":" + B(cs.m_useFuel) + ",\"runsNow\":" + (runsNow.HasValue ? B(runsNow.Value) : "null") + (cs.m_useFuel ? ",\"fuel\":" + U.N(Math.Round(fuel, 2)) + ",\"maxFuel\":" + cs.m_maxFuel + ",\"fuelHoursLeft\":" + U.N(Math.Round(UpkeepRules.HoursOfCover(fuel, UpkeepRules.UnitsPerHour(cs.m_secPerFuel)), 1)) : "") + ",\"canOvercook\":" + B(cs.m_canOvercookItems) + ",\"slots\":[" + string.Join(",", slots.ToArray()) + "]}");
            }
            return U.Json("{\"count\":" + rows.Count + ",\"returned\":" + Math.Min(100, rows.Count) + ",\"cookers\":[" + string.Join(",", rows.Take(100).ToArray()) + "]}");
        }

        static ToolOutput FuelForecast(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            float x0, z0, x1, z1; string err;
            if (!Box(a, out x0, out z0, out x1, out z1, out err)) return ToolOutput.Err(err ?? "give x,z[,radius] or x0,z0,x1,z1");
            var cache = new Dictionary<int, GameObject>(); var rate = new Dictionary<string, double>(); var inside = new Dictionary<string, double>(); var users = new Dictionary<string, int>();
            Action<string, double, double> add = (item, perHour, fuelInside) => { rate[item] = (rate.ContainsKey(item) ? rate[item] : 0) + perHour; inside[item] = (inside.ContainsKey(item) ? inside[item] : 0) + fuelInside; users[item] = (users.ContainsKey(item) ? users[item] : 0) + 1; };
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); GameObject go; if (!cache.TryGetValue(h, out go)) cache[h] = go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(h) : null; if (go == null) continue;
                var fp = go.GetComponent<Fireplace>();
                if (fp != null && fp.m_fuelItem != null && !fp.m_infiniteFuel && fp.m_secPerFuel > 0)
                {
                    var fuel = z.GetFloat("fuel", 0f);
                    add(fp.m_fuelItem.gameObject.name, FireRules.Burning(z.GetInt("state", 1), fuel, false) ? UpkeepRules.UnitsPerHour(fp.m_secPerFuel) : 0, fuel);
                    continue;
                }
                var sm = go.GetComponent<Smelter>();
                if (sm != null && sm.m_fuelItem != null && sm.m_maxFuel > 0)
                {
                    var queued = z.GetInt(ZDOVars.s_queued, 0); var fuel = z.GetFloat(ZDOVars.s_fuel, 0f);
                    add(sm.m_fuelItem.gameObject.name, queued > 0 && fuel > 0 ? sm.m_fuelPerProduct * 3600.0 / sm.m_secPerProduct : 0, fuel);
                    continue;
                }
                var cs = go.GetComponent<CookingStation>();
                if (cs != null && cs.m_useFuel && cs.m_fuelItem != null)
                {
                    var fuel = z.GetFloat("fuel", 0f); var busy = Enumerable.Range(0, cs.m_slots.Length).Any(i => z.GetString("slot" + i, "").Length > 0 && z.GetInt("slotstatus" + i, 0) == 0);
                    add(cs.m_fuelItem.gameObject.name, fuel > 0 && (cs.m_useFueldWhileEmpty || busy) ? UpkeepRules.UnitsPerHour(cs.m_secPerFuel) : 0, fuel);
                }
            }
            int chests; var stock = Stock(x0, z0, x1, z1, out chests);
            var rows = rate.Keys.OrderByDescending(k => rate[k]).Select(k =>
            {
                var have = stock.ContainsKey(k) ? stock[k] : 0; var total = have + inside[k];
                var hours = UpkeepRules.HoursOfCover(total, rate[k]);
                return "{\"fuel\":" + U.S(k) + ",\"burners\":" + users[k] + ",\"burningPerHour\":" + U.N(Math.Round(rate[k], 2)) + ",\"insideThem\":" + U.N(Math.Round(inside[k], 1)) + ",\"inChests\":" + have + ",\"hoursOfCover\":" + (double.IsInfinity(hours) ? "null" : U.N(Math.Round(hours, 1))) + "}";
            });
            return U.Json("{\"chests\":" + chests + ",\"fuels\":[" + string.Join(",", rows.ToArray()) + "],\"note\":\"the hours count the fuel already inside the burners and the chests of the same area, at the rate they burn now\"}");
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
            var counts = new Dictionary<string, int>(); var reqs = new Dictionary<string, List<KeyValuePair<string, int>>>(); var free = new HashSet<string>(); var stationsNeeded = new Dictionary<string, int>(); var stationOf = new Dictionary<string, string>();
            var zs = ZoneSystem.instance;
            // standing:true — one pass over the plan's box, bucketed in 2 m cells (a lookup per item used to walk the zone every time)
            var near = new Dictionary<string, List<Vector3>>();
            if (standing && Zdos.Ready)
            {
                double bx0 = 1e9, bz0 = 1e9, bx1 = -1e9, bz1 = -1e9;
                foreach (var o in items) { var it = o as Dictionary<string, object>; if (it == null || !U.Has(it, "x") || !U.Has(it, "z")) continue; var px = U.D(it, "x", 0); var pz = U.D(it, "z", 0); bx0 = Math.Min(bx0, px); bx1 = Math.Max(bx1, px); bz0 = Math.Min(bz0, pz); bz1 = Math.Max(bz1, pz); }
                if (bx0 <= bx1) foreach (var zd in Zdos.InBox((float)bx0 - 1f, (float)bz0 - 1f, (float)bx1 + 1f, (float)bz1 + 1f))
                {
                    var pp = zd.GetPosition(); var key = Zdos.NameOf(zd.GetPrefab()) + "|" + (int)Math.Floor(pp.x / 2f) + "|" + (int)Math.Floor(pp.z / 2f);
                    List<Vector3> l; if (!near.TryGetValue(key, out l)) near[key] = l = new List<Vector3>(); l.Add(pp);
                }
            }
            foreach (var o in items)
            {
                var it = o as Dictionary<string, object>; if (it == null) continue; var prefab = McpJson.GetStr(it, "prefab"); if (string.IsNullOrEmpty(prefab)) continue;
                if (standing && near.Count > 0 && U.Has(it, "x") && U.Has(it, "z"))
                {
                    var x = (float)U.D(it, "x", 0); var z = (float)U.D(it, "z", 0); var y = (float)U.D(it, "y", 0); var found = false; int cx = (int)Math.Floor(x / 2f), cz = (int)Math.Floor(z / 2f);
                    for (var dx = -1; dx <= 1 && !found; dx++)
                        for (var dz = -1; dz <= 1 && !found; dz++)
                        {
                            List<Vector3> l; if (!near.TryGetValue(prefab + "|" + (cx + dx) + "|" + (cz + dz), out l)) continue;
                            foreach (var pp in l) if (Vector3.Distance(pp, new Vector3(x, U.Has(it, "y") ? y : pp.y, z)) < 0.35f) { found = true; break; }
                        }
                    if (found) continue;
                }
                counts[prefab] = (counts.ContainsKey(prefab) ? counts[prefab] : 0) + 1;
                if (reqs.ContainsKey(prefab)) continue;
                var go = ZNetScene.instance.GetPrefab(prefab); var pc = go != null ? go.GetComponent<Piece>() : null; if (pc == null) continue;
                reqs[prefab] = pc.m_resources.Where(q => q.m_resItem != null && q.m_amount > 0).Select(q => new KeyValuePair<string, int>(ItemName(q.m_resItem), q.m_amount)).ToList();
                if (zs != null && zs.GetGlobalKey(pc.FreeBuildKey())) free.Add(prefab);
                if (pc.m_craftingStation != null) stationOf[prefab] = pc.m_craftingStation.gameObject.name;
            }
            foreach (var kv in stationOf) stationsNeeded[kv.Value] = (stationsNeeded.ContainsKey(kv.Value) ? stationsNeeded[kv.Value] : 0) + counts[kv.Key];
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
