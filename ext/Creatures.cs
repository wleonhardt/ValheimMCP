// creature_info: the facts about a creature prefab that decide a fight, read from the prefab (no live instance, so it works on the dedicated server).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class CreatureTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("creature_info", "A creature prefab's facts (no live instance needed): faction, health per star, speeds, the damage modifiers it has (weak / resistant / immune per type), how it senses (view, hearing, fire fear, passive-aggressive), its attacks from its default and random weapons (damage by type, reach, interval, blockable, dodgeable), taming (time, fed duration, what it eats) and what it drops (amount, chance, expectation per kill). With armor and playerHp it also says what one of its hits does to such a player at 1, 2 and 3 stars (resistance first, then armour, as the game does) and how many hits kill. name = prefab (Troll, Boar, Wolf, Greydwarf_Elite).",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"armor\":{\"type\":\"number\"},\"playerHp\":{\"type\":\"number\"}},\"required\":[\"name\"]}", CreatureInfo);
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
