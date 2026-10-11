// world_rules: the server options the world runs under, read from the global keys. Works on the dedicated server (reads ZoneSystem, not the local player).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ValheimMCP;

namespace HubnerExt
{
    internal static class WorldTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("world_rules", "The world's server options, read from the global keys (works on the dedicated server): every option that is set with its raw value, the rates as multipliers (a key stores a percentage; 150 is 1.5), what a death costs (tombstone, worn gear, bag, skills, per Player.CreateTombStone), whether portals are blocked now (NoPortals, or NoBossPortals while a boss event runs), and the preset. Use it before judging a base, a portal network or a death.",
                "{\"type\":\"object\",\"properties\":{}}", WorldRulesTool);
        }

        static readonly GlobalKeys[] Scalars = { GlobalKeys.PlayerDamage, GlobalKeys.EnemyDamage, GlobalKeys.EventRate, GlobalKeys.ResourceRate, GlobalKeys.StaminaRate, GlobalKeys.AdrenalineRate, GlobalKeys.EitrRate, GlobalKeys.DurabilityRate, GlobalKeys.FoodRate, GlobalKeys.MoveStaminaRate, GlobalKeys.StaminaRegenRate, GlobalKeys.SkillGainRate, GlobalKeys.SkillReductionRate, GlobalKeys.EnemySpeedSize, GlobalKeys.EnemyLevelUpRate, GlobalKeys.CarryWeightRate };

        static ToolOutput WorldRulesTool(Dictionary<string, object> a)
        {
            var zs = ZoneSystem.instance; if (zs == null) return ToolOutput.Err("no world loaded");
            var set = new List<string>();
            foreach (GlobalKeys k in Enum.GetValues(typeof(GlobalKeys)))
            {
                if ((int)k >= (int)GlobalKeys.NonServerOption) continue;                       // below the marker are the options a server owner can choose
                string raw; var has = zs.GetGlobalKey(k, out raw); var flag = zs.GetGlobalKey(k);
                if (!has && !flag) continue;
                set.Add("{\"key\":" + U.S(k.ToString()) + ",\"value\":" + (has ? U.S(raw) : "null") + "}");
            }
            var rates = new List<string>();
            foreach (var k in Scalars) { string raw; zs.GetGlobalKey(k, out raw); var f = WorldRules.Rate(raw); if (Math.Abs(f - 1.0) > 1e-9) rates.Add(U.S(k.ToString()) + ":" + U.N(Math.Round(f, 3))); }
            string wl; zs.GetGlobalKey(GlobalKeys.WorldLevel, out wl); string preset; zs.GetGlobalKey(GlobalKeys.Preset, out preset);
            var d = WorldRules.Death(zs.GetGlobalKey(GlobalKeys.DeathKeepInventory), zs.GetGlobalKey(GlobalKeys.DeathKeepEquip), zs.GetGlobalKey(GlobalKeys.DeathDeleteItems), zs.GetGlobalKey(GlobalKeys.DeathDeleteUnequipped), zs.GetGlobalKey(GlobalKeys.DeathSkillsReset));
            double sr; string srRaw; zs.GetGlobalKey(GlobalKeys.SkillReductionRate, out srRaw); sr = WorldRules.Rate(srRaw);
            float bosses; var bossRunning = zs.GetGlobalKey(GlobalKeys.activeBosses, out bosses) && bosses > 0;
            bool noPortals = zs.GetGlobalKey(GlobalKeys.NoPortals), noBoss = zs.GetGlobalKey(GlobalKeys.NoBossPortals);
            var sb = new StringBuilder("{\"set\":[" + string.Join(",", set.ToArray()) + "],\"rates\":{" + string.Join(",", rates.ToArray()) + "},\"worldLevel\":" + (wl != null ? U.S(wl) : "0") + ",\"preset\":" + (preset != null ? U.S(preset) : "null"));
            sb.Append(",\"death\":{\"tombstone\":" + (d.Tombstone ? "true" : "false") + ",\"wornGear\":" + U.S(d.Equipped) + ",\"bag\":" + U.S(d.Unequipped) + ",\"skills\":" + U.S(d.SkillsReset ? "all reset on every death" : "lowered on a hard death only (the game's m_DeathLowerFactor, 0.25 by default, times " + U.N(Math.Round(sr, 3)) + ")") + "}");
            sb.Append(",\"portals\":{\"blocked\":" + (WorldRules.PortalBlocked(noPortals, noBoss, bossRunning) ? "true" : "false") + ",\"noPortals\":" + (noPortals ? "true" : "false") + ",\"noBossPortals\":" + (noBoss ? "true" : "false") + ",\"bossRunning\":" + (bossRunning ? "true" : "false") + ",\"teleportAll\":" + (zs.GetGlobalKey(GlobalKeys.TeleportAll) ? "true" : "false") + "}}");
            return U.Json(sb.ToString());
        }
    }
}
