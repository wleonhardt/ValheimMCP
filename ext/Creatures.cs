// creature_info: the facts about a creature prefab that decide a fight, read from the prefab (no live instance, so it works on the dedicated server).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class CreatureTools
    {
        public static void Register(ToolRegistry r)
        {
            RegisterBase(r);
            r.Add("raid_state", "Random events (raids) as the game runs them: the one running now (name, seconds left, where, range, start message), the roll timer (every interval x event rate seconds the game rolls chance / rate percent; seconds to the next roll), and every event with its duration, whether it needs a base (3 player-base areas, see base_value), biomes, the global keys it needs and forbids, whether those keys are set in this world now, and what it spawns. Read-only; works on the server.",
                "{\"type\":\"object\",\"properties\":{}}", RaidState);
            r.Add("threats_near", "What round a point could hurt a player of given armour and health, ranked ignore / watch / now: creatures from the ZDO table (works on the server) with distance, level, health, whether alert or after a target (the synced flags: another client's creature has no target stored), the reach and size of its hardest hit after resistance and armour, hits to kill, and seconds to contact at its run speed. Tamed animals and non-hostile ones are ignored; in a PassiveMobs world nothing is 'now' until it is alert and after a target. x,z,radius (default 40, max 120), armor (default 0), playerHp (default 100), limit.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"armor\":{\"type\":\"number\"},\"playerHp\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", ThreatsNear);
            r.Add("creature_info", "A creature prefab's facts (no live instance needed): faction, health per star, speeds, the damage modifiers it has (weak / resistant / immune per type), how it senses (view, hearing, fire fear, passive-aggressive), its attacks from its default and random weapons (damage by type, reach, interval, blockable, dodgeable), taming (time, fed duration, what it eats) and what it drops (amount, chance, expectation per kill). With armor and playerHp it also says what one of its hits does to such a player at 1, 2 and 3 stars (resistance first, then armour, as the game does) and how many hits kill. name = prefab (Troll, Boar, Wolf, Greydwarf_Elite).",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"armor\":{\"type\":\"number\"},\"playerHp\":{\"type\":\"number\"}},\"required\":[\"name\"]}", CreatureInfo);
        }

        public static void RegisterBase(ToolRegistry r)
        {
            r.Add("base_value", "The base value of a point as the game counts it for raids (Player.UpdateBaseValue): the PlayerBase areas (fires, workbenches, wards and so on) whose trigger reaches within 20 m, from the ZDO table and the prefabs' area radii. A near-base-only random event needs 3. Also the base value each online character reports (it is stored on the character), so the two can be compared. x,z (y optional: default the highest area nearby).",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", BaseValueTool);
        }

        static ToolOutput BaseValueTool(Dictionary<string, object> a)
        {
            if (!Zdos.Ready || ZNetScene.instance == null) return ToolOutput.Err("world not ready");
            double x = U.D(a, "x", 0), z = U.D(a, "z", 0); var cache = new Dictionary<int, float>(); var areas = new List<BaseArea>(); const float maxReach = 60f;
            foreach (var zd in Zdos.InBox((float)(x - 20 - maxReach), (float)(z - 20 - maxReach), (float)(x + 20 + maxReach), (float)(z + 20 + maxReach)))
            {
                var h = zd.GetPrefab(); float r;
                if (!cache.TryGetValue(h, out r))
                {
                    r = -1f; var go = ZNetScene.instance.GetPrefab(h);
                    if (go != null) foreach (var ea in go.GetComponentsInChildren<EffectArea>(true))
                        if ((ea.m_type & EffectArea.Type.PlayerBase) != 0) { var sc = ea.GetComponent<SphereCollider>(); var col = ea.GetComponent<Collider>(); r = sc != null ? sc.radius * Mathf.Max(ea.transform.lossyScale.x, Mathf.Max(ea.transform.lossyScale.y, ea.transform.lossyScale.z)) : (col != null ? col.bounds.extents.magnitude : 0f); break; }
                    cache[h] = r;
                }
                if (r < 0f) continue;
                var p = zd.GetPosition(); areas.Add(new BaseArea { Prefab = Zdos.NameOf(h), X = p.x, Y = p.y, Z = p.z, Radius = r });
            }
            var y = U.Has(a, "y") ? U.D(a, "y", 0) : (areas.Count > 0 ? areas.Max(q => q.Y) : 0);
            var inReach = BaseValue.InReach(x, y, z, areas);
            var players = new List<string>();
            try { foreach (var c in ZNet.instance.GetAllCharacterZDOS()) { var p = c.GetPosition(); if (Math.Sqrt((p.x - x) * (p.x - x) + (p.z - z) * (p.z - z)) < 40) players.Add("{\"name\":" + U.S(c.GetString(ZDOVars.s_playerName, "?")) + ",\"baseValue\":" + c.GetInt(ZDOVars.s_baseValue, 0) + "}"); } } catch (Exception ex) { U.Once("base_value.players", ex); }
            return U.Json("{\"point\":[" + U.N(x) + "," + U.N(Math.Round(y, 1)) + "," + U.N(z) + "],\"value\":" + inReach.Count + ",\"raidEligible\":" + B(BaseValue.RaidEligible(inReach.Count)) + ",\"areas\":[" + string.Join(",", inReach.OrderBy(q => q.X).Take(60).Select(q => "{\"prefab\":" + U.S(q.Prefab) + ",\"pos\":[" + U.N(Math.Round(q.X, 1)) + "," + U.N(Math.Round(q.Y, 1)) + "," + U.N(Math.Round(q.Z, 1)) + "],\"radius\":" + U.N(Math.Round(q.Radius, 1)) + "}").ToArray()) + "],\"charactersNearby\":[" + string.Join(",", players.ToArray()) + "],\"note\":\"radius is the sphere trigger of the prefab (a box or capsule area is approximated by its bounding sphere, which can count an area the game would not)\"}");
        }

        static ToolOutput RaidState(Dictionary<string, object> a)
        {
            var res = RandEventSystem.instance; if (res == null) return ToolOutput.Err("no RandEventSystem (not in a world)");
            var zs = ZoneSystem.instance;
            double timer = 0; try { var f = typeof(RandEventSystem).GetField("m_eventTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); if (f != null) timer = Convert.ToDouble(f.GetValue(res)); } catch (Exception ex) { U.Once("raid_state.timer", ex); }
            var rate = Game.m_eventRate;
            Func<string, bool> isSet = k => zs != null && zs.GetGlobalKey(k);
            var cur = res.GetCurrentRandomEvent();
            var evs = res.m_events.Select(e => "{\"name\":" + U.S(e.m_name) + ",\"enabled\":" + B(e.m_enabled) + ",\"random\":" + B(e.m_random) + ",\"durationSec\":" + U.N(e.m_duration) + ",\"nearBaseOnly\":" + B(e.m_nearBaseOnly) + ",\"eventRange\":" + U.N(e.m_eventRange) + ",\"biomes\":" + (int)e.m_biome + ",\"requiresKeys\":[" + string.Join(",", e.m_requiredGlobalKeys.Select(U.S).ToArray()) + "],\"forbidsKeys\":[" + string.Join(",", e.m_notRequiredGlobalKeys.Select(U.S).ToArray()) + "],\"keysOkNow\":" + B(RaidRules.KeysOk(e.m_requiredGlobalKeys, e.m_notRequiredGlobalKeys, isSet)) + ",\"startMessage\":" + U.S(e.m_startMessage) + ",\"spawns\":[" + string.Join(",", e.m_spawn.Where(sp => sp.m_prefab != null).Select(sp => U.S(sp.m_prefab.name)).Distinct().ToArray()) + "]}").ToArray();
            return U.Json("{\"current\":" + (cur == null ? "null" : "{\"name\":" + U.S(cur.m_name) + ",\"secondsLeft\":" + U.N(Math.Round(Math.Max(0, cur.m_duration - cur.m_time))) + ",\"pos\":" + U.V(cur.m_pos) + ",\"range\":" + U.N(cur.m_eventRange) + ",\"startMessage\":" + U.S(cur.m_startMessage) + "}") + ",\"eventRate\":" + U.N(rate) + ",\"rollIntervalSeconds\":" + U.N(Math.Round(RaidRules.RollIntervalSeconds(res.m_eventIntervalMin, rate))) + ",\"chancePercent\":" + U.N(Math.Round(RaidRules.ChancePercent(res.m_eventChance, rate), 1)) + ",\"secondsToNextRoll\":" + U.N(Math.Round(RaidRules.SecondsToNextRoll(timer, res.m_eventIntervalMin, rate))) + ",\"timerKnown\":" + B(timer > 0 || cur != null) + ",\"events\":[" + string.Join(",", evs) + "]}");
        }

        static List<KeyValuePair<Dictionary<string, double>, double>> Attacks(GameObject go)
        {
            var res = new List<KeyValuePair<Dictionary<string, double>, double>>(); var hum = go.GetComponent<Humanoid>(); if (hum == null) return res;
            var weapons = new List<GameObject>(); if (hum.m_defaultItems != null) weapons.AddRange(hum.m_defaultItems.Where(g => g != null)); if (hum.m_randomWeapon != null) weapons.AddRange(hum.m_randomWeapon.Where(g => g != null));
            foreach (var w in weapons.Distinct()) { var id = w.GetComponent<ItemDrop>(); if (id == null) continue; var dm = Dmg(id.m_itemData.m_shared.m_damages); if (dm.Count > 0) res.Add(new KeyValuePair<Dictionary<string, double>, double>(dm, id.m_itemData.m_shared.m_aiAttackRange)); }
            return res;
        }

        static ToolOutput ThreatsNear(Dictionary<string, object> a)
        {
            if (!Zdos.Ready || ZNetScene.instance == null) return ToolOutput.Err("world not ready");
            double cx = U.D(a, "x", 0), cz = U.D(a, "z", 0), rad = Math.Min(120, Math.Max(1, U.D(a, "radius", 40))), armor = U.D(a, "armor", 0), hp = U.D(a, "playerHp", 100); var limit = Math.Max(1, (int)U.D(a, "limit", 40));
            if (!WorldBounds.InWorld(cx, 0, cz)) return ToolOutput.Err("x and z must be finite and inside the world");
            var passive = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.PassiveMobs);
            var cache = new Dictionary<int, GameObject>(); var rows = new List<KeyValuePair<int, KeyValuePair<double, string>>>(); var total = 0;
            foreach (var z in Zdos.InBox((float)(cx - rad), (float)(cz - rad), (float)(cx + rad), (float)(cz + rad)))
            {
                var h = z.GetPrefab(); GameObject go; if (!cache.TryGetValue(h, out go)) cache[h] = go = ZNetScene.instance.GetPrefab(h);
                var ch = go != null ? go.GetComponent<Character>() : null; if (ch == null || ch.IsPlayer()) continue;
                var p = z.GetPosition(); var d = Math.Sqrt((p.x - cx) * (p.x - cx) + (p.z - cz) * (p.z - cz)); if (d > rad) continue;
                total++;
                var level = z.GetInt("level", 1); var tamed = z.GetBool("tamed", false); var alert = z.GetBool("alert", false); var have = z.GetBool("haveTarget", false);
                var hostile = ch.m_faction != Character.Faction.Players && ch.m_faction != Character.Faction.AnimalsVeg && go.GetComponent<MonsterAI>() != null;
                double best = 0, reach = 2; foreach (var at in Attacks(go)) { var hit = CombatRules.Hit(at.Key, null, armor, level); if (hit > best) { best = hit; reach = at.Value; } }
                var rank = ThreatRules.Rank(tamed, hostile, passive, alert, have, d, reach + 0.5, ch.m_runSpeed, best);
                var hp0 = z.GetFloat("health", (float)CombatRules.MaxHealth(ch.m_health, level));
                rows.Add(new KeyValuePair<int, KeyValuePair<double, string>>(-(int)rank, new KeyValuePair<double, string>(d, "{\"prefab\":" + U.S(go.name) + ",\"level\":" + level + ",\"threat\":" + U.S(rank.ToString().ToLowerInvariant()) + ",\"distance\":" + U.N(Math.Round(d, 1)) + ",\"pos\":" + U.V(p) + ",\"health\":" + U.N(Math.Round(hp0)) + ",\"alert\":" + B(alert) + ",\"haveTarget\":" + B(have) + ",\"tamed\":" + B(tamed) + ",\"hardestHit\":" + U.N(Math.Round(best, 1)) + ",\"reach\":" + U.N(reach) + ",\"hitsToKillPlayer\":" + (best > 0 ? CombatRules.HitsToDie(hp, best).ToString(CultureInfo.InvariantCulture) : "null") + ",\"secondsToContact\":" + (double.IsInfinity(CombatRules.SecondsToContact(d, reach + 0.5, ch.m_runSpeed)) ? "null" : U.N(Math.Round(CombatRules.SecondsToContact(d, reach + 0.5, ch.m_runSpeed), 1))) + "}")));
            }
            var ordered = rows.OrderBy(r => r.Key).ThenBy(r => r.Value.Key).Take(limit).Select(r => r.Value.Value).ToArray();
            return U.Json("{\"total\":" + total + ",\"returned\":" + ordered.Length + ",\"passiveMobs\":" + B(passive) + ",\"armor\":" + U.N(armor) + ",\"playerHp\":" + U.N(hp) + ",\"creatures\":[" + string.Join(",", ordered) + "]}");
        }

        static readonly string[] Types = { "blunt", "slash", "pierce", "chop", "pickaxe", "fire", "frost", "lightning", "poison", "spirit" };

        static Dictionary<string, double> Dmg(HitData.DamageTypes d)
        {
            var m = new Dictionary<string, double> { { "damage", d.m_damage }, { "blunt", d.m_blunt }, { "slash", d.m_slash }, { "pierce", d.m_pierce }, { "chop", d.m_chop }, { "pickaxe", d.m_pickaxe }, { "fire", d.m_fire }, { "frost", d.m_frost }, { "lightning", d.m_lightning }, { "poison", d.m_poison }, { "spirit", d.m_spirit } };
            return m.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        static Dictionary<string, Mod> Mods(HitData.DamageModifiers m)
        {
            var all = new Dictionary<string, HitData.DamageModifier> { { "blunt", m.m_blunt }, { "slash", m.m_slash }, { "pierce", m.m_pierce }, { "chop", m.m_chop }, { "pickaxe", m.m_pickaxe }, { "fire", m.m_fire }, { "frost", m.m_frost }, { "lightning", m.m_lightning }, { "poison", m.m_poison }, { "spirit", m.m_spirit } };
            return all.Where(kv => kv.Value != HitData.DamageModifier.Normal).ToDictionary(kv => kv.Key, kv => (Mod)(int)kv.Value);      // the two enums have the same members in the same order
        }

        static string Obj(IDictionary<string, double> d) { return "{" + string.Join(",", d.Select(kv => U.S(kv.Key) + ":" + U.N(Math.Round(kv.Value, 1))).ToArray()) + "}"; }
        static string B(bool v) { return v ? "true" : "false"; }

        static ToolOutput CreatureInfo(Dictionary<string, object> a)
        {
            if (ZNetScene.instance == null) return ToolOutput.Err("no world loaded");
            var name = McpJson.GetStr(a, "name"); var go = name != null ? ZNetScene.instance.GetPrefab(name) : null; var ch = go != null ? go.GetComponent<Character>() : null;
            if (ch == null) return ToolOutput.Err("no creature prefab named " + name);
            var armor = U.D(a, "armor", 0); var playerHp = U.D(a, "playerHp", 100);
            var ai = go.GetComponent<BaseAI>(); var mai = go.GetComponent<MonsterAI>(); var hum = go.GetComponent<Humanoid>(); var tame = go.GetComponent<Tameable>(); var drop = go.GetComponent<CharacterDrop>();
            var mods = Mods(ch.m_damageModifiers);

            var weapons = new List<GameObject>();
            if (hum != null) { if (hum.m_defaultItems != null) weapons.AddRange(hum.m_defaultItems.Where(g => g != null)); if (hum.m_randomWeapon != null) weapons.AddRange(hum.m_randomWeapon.Where(g => g != null)); }
            var attacks = new List<string>(); var hitsByStar = new List<string>();
            foreach (var w in weapons.Distinct())
            {
                var id = w.GetComponent<ItemDrop>(); if (id == null) continue; var sh = id.m_itemData.m_shared; var dm = Dmg(sh.m_damages);
                if (dm.Count == 0) continue;
                attacks.Add("{\"item\":" + U.S(w.name) + ",\"damage\":" + Obj(dm) + ",\"total\":" + U.N(Math.Round(dm.Values.Sum(), 1)) + ",\"aiAttackRange\":" + U.N(sh.m_aiAttackRange) + ",\"aiAttackRangeMin\":" + U.N(sh.m_aiAttackRangeMin) + ",\"interval\":" + U.N(sh.m_aiAttackInterval) + ",\"blockable\":" + B(sh.m_blockable) + ",\"dodgeable\":" + B(sh.m_dodgeable) + "}");
                if (U.Has(a, "armor") || U.Has(a, "playerHp"))
                    hitsByStar.Add("{\"item\":" + U.S(w.name) + ",\"stars\":[" + string.Join(",", Enumerable.Range(1, 3).Select(l => { var h = CombatRules.Hit(dm, null, armor, l); return "{\"level\":" + l + ",\"hit\":" + U.N(Math.Round(h, 1)) + ",\"hitsToKill\":" + CombatRules.HitsToDie(playerHp, h) + "}"; }).ToArray()) + "]}");
            }

            var drops = new List<string>();
            if (drop != null) foreach (var d in drop.m_drops) if (d.m_prefab != null) drops.Add("{\"item\":" + U.S(d.m_prefab.name) + ",\"min\":" + d.m_amountMin + ",\"max\":" + d.m_amountMax + ",\"chance\":" + U.N(d.m_chance) + ",\"levelMultiplier\":" + B(d.m_levelMultiplier) + ",\"expectedPerKill\":" + U.N(Math.Round(CombatRules.ExpectedDrop(d.m_amountMin, d.m_amountMax, d.m_chance, d.m_levelMultiplier, 1), 2)) + "}");

            var sb = new System.Text.StringBuilder("{\"prefab\":" + U.S(go.name) + ",\"nameToken\":" + U.S(ch.m_name) + ",\"faction\":" + U.S(ch.m_faction.ToString()) + ",\"boss\":" + B(ch.m_boss) + ",\"health\":" + U.N(ch.m_health) + ",\"healthByStar\":[" + string.Join(",", Enumerable.Range(1, 3).Select(l => U.N(CombatRules.MaxHealth(ch.m_health, l))).ToArray()) + "],\"damageFactorByStar\":[1,1.5,2],\"speed\":{\"walk\":" + U.N(ch.m_walkSpeed) + ",\"run\":" + U.N(ch.m_runSpeed) + ",\"swim\":" + U.N(ch.m_swimSpeed) + "},\"staggerFactor\":" + U.N(ch.m_staggerDamageFactor) +
                ",\"modifiers\":{" + string.Join(",", mods.Select(kv => U.S(kv.Key) + ":" + U.S(kv.Value.ToString())).ToArray()) + "},\"weakTo\":[" + string.Join(",", mods.Where(kv => CombatRules.Multiplier(kv.Value) > 1).Select(kv => U.S(kv.Key)).ToArray()) + "]");
            if (ai != null) sb.Append(",\"ai\":{\"kind\":" + U.S(mai != null ? "MonsterAI" : ai.GetType().Name) + ",\"viewRange\":" + U.N(ai.m_viewRange) + ",\"viewAngle\":" + U.N(ai.m_viewAngle) + ",\"hearRange\":" + U.N(ai.m_hearRange) + ",\"afraidOfFire\":" + B(ai.m_afraidOfFire) + ",\"avoidFire\":" + B(ai.m_avoidFire) + ",\"passiveAggressive\":" + B(ai.m_passiveAggresive) + (mai != null ? ",\"attacksPlayerBuildings\":" + B(mai.m_attackPlayerObjects) : "") + "}");
            if (tame != null) sb.Append(",\"tame\":{\"tamingTimeSec\":" + U.N(tame.m_tamingTime) + ",\"fedDurationSec\":" + U.N(tame.m_fedDuration) + ",\"commandable\":" + B(tame.m_commandable) + ",\"eats\":[" + string.Join(",", (mai != null && mai.m_consumeItems != null ? mai.m_consumeItems.Where(i => i != null).Select(i => U.S(i.gameObject.name)) : new string[0]).ToArray()) + "]}");
            sb.Append(",\"attacks\":[" + string.Join(",", attacks.ToArray()) + "]");
            if (hitsByStar.Count > 0) sb.Append(",\"againstPlayer\":{\"armor\":" + U.N(armor) + ",\"playerHp\":" + U.N(playerHp) + ",\"hits\":[" + string.Join(",", hitsByStar.ToArray()) + "]}");
            sb.Append(",\"drops\":[" + string.Join(",", drops.ToArray()) + "]}");
            return U.Json(sb.ToString());
        }
    }
}
