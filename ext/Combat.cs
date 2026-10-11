// Game-free combat arithmetic, as the game does it (HitData, Character.RPC_Damage, Attack, decompiled): resistance first, then armour on the total of the armoured damage types,
// stars scale a creature's damage by 1 + 0.5 per extra level and its health by the level.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    internal enum Mod { Normal, Resistant, Weak, Immune, Ignore, VeryResistant, VeryWeak, SlightlyResistant, SlightlyWeak }

    internal static class CombatRules
    {
        /// <summary>HitData.DamageTypes.ApplyArmor is applied to these: not the generic damage, chop or pickaxe.</summary>
        public static readonly string[] Armoured = { "blunt", "slash", "pierce", "fire", "frost", "lightning", "poison", "spirit" };

        public static double Multiplier(Mod m)
        {
            switch (m)
            {
                case Mod.Resistant: return 0.5;
                case Mod.VeryResistant: return 0.25;
                case Mod.SlightlyResistant: return 0.75;
                case Mod.Weak: return 1.5;
                case Mod.VeryWeak: return 2;
                case Mod.SlightlyWeak: return 1.25;
                case Mod.Immune: case Mod.Ignore: return 0;
                default: return 1;
            }
        }

        /// <summary>HitData.DamageTypes.ApplyArmor(dmg, ac): armour below half the damage subtracts; above it the damage is cut by dmg / (4 x armour).</summary>
        public static double Armor(double dmg, double ac)
        {
            if (ac <= 0 || dmg <= 0) return dmg;
            if (ac < dmg / 2) return dmg - ac;
            return Math.Min(1, Math.Max(0, dmg / (ac * 4))) * dmg;
        }

        /// <summary>Attack.GetLevelDamageFactor: +50 % of the base damage for every star.</summary>
        public static double LevelFactor(int level) { return 1 + Math.Max(0, level - 1) * 0.5; }

        /// <summary>Character.GetMaxHealthBase x level; in a world level above 0 the base is multiplied by worldLevel x hpMultiplier (Game.m_worldLevelEnemyHPMultiplier).</summary>
        public static double MaxHealth(double baseHealth, int level, int worldLevel = 0, double worldHpMultiplier = 2) { return (worldLevel > 0 ? baseHealth * worldLevel * worldHpMultiplier : baseHealth) * Math.Max(1, level); }

        /// <summary>
        ///     What one hit does to the target: every damage type through its resistance, then armour on the sum of the armoured types, in the game's order.
        ///     <paramref name="damage" /> keys: damage (generic), blunt, slash, pierce, chop, pickaxe, fire, frost, lightning, poison, spirit.
        /// </summary>
        public static double Hit(IDictionary<string, double> damage, IDictionary<string, Mod> mods, double armor, int attackerLevel = 1)
        {
            var f = LevelFactor(attackerLevel); double armouredSum = 0, other = 0;
            foreach (var kv in damage)
            {
                Mod m = Mod.Normal; if (mods != null) mods.TryGetValue(kv.Key, out m);
                var v = kv.Value * f * Multiplier(m);
                if (Array.IndexOf(Armoured, kv.Key) >= 0) armouredSum += v; else other += v;
            }
            return other + Armor(armouredSum, armor);
        }

        public static int HitsToDie(double health, double hit) { return hit <= 0 ? int.MaxValue : (int)Math.Ceiling(health / hit - 1e-9); }

        /// <summary>Seconds before a creature closing in at <paramref name="closingSpeed" /> m/s can strike: (distance - reach) / speed; 0 when already in reach, infinity when not closing.</summary>
        public static double SecondsToContact(double distance, double reach, double closingSpeed)
        {
            if (distance <= reach) return 0;
            return closingSpeed <= 0 ? double.PositiveInfinity : (distance - reach) / closingSpeed;
        }

        /// <summary>CharacterDrop expectation per kill: chance x mean amount, x the level when the drop scales with it.</summary>
        public static double ExpectedDrop(int min, int max, double chance, bool levelMultiplier, int level) { return chance * (min + max) / 2.0 * (levelMultiplier ? Math.Max(1, level) : 1); }
    }

    /// <summary>One PlayerBase EffectArea: where it is and how far its trigger reaches.</summary>
    internal sealed class BaseArea { public string Prefab = ""; public double X, Y, Z, Radius; }

    /// <summary>
    ///     Player.UpdateBaseValue counts the PlayerBase EffectArea colliders within 20 m of the player (EffectArea.GetBaseValue: an overlap sphere, so an area counts when its edge is
    ///     within reach). A random event that is near-base-only needs a base value of 3. Fires, workbenches and wards carry such areas; the radius is a property of each prefab.
    /// </summary>
    internal static class BaseValue
    {
        public const double Reach = 20; public const int RaidThreshold = 3;

        public static List<BaseArea> InReach(double x, double y, double z, IEnumerable<BaseArea> areas)
        {
            return areas.Where(a => Math.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) + (a.Z - z) * (a.Z - z)) <= Reach + a.Radius).ToList();
        }

        public static bool RaidEligible(int value) { return value >= RaidThreshold; }
    }

    /// <summary>RandEventSystem.UpdateRandomEvent (decompiled): every m_eventIntervalMin x 60 x rate seconds the game rolls m_eventChance / rate percent for a random event; the rate is the world modifier (Game.m_eventRate).</summary>
    internal static class RaidRules
    {
        public static double RollIntervalSeconds(double intervalMin, double rate) { return intervalMin * 60 * rate; }
        public static double SecondsToNextRoll(double timer, double intervalMin, double rate) { return Math.Max(0, RollIntervalSeconds(intervalMin, rate) - timer); }
        public static double ChancePercent(double chance, double rate) { return rate <= 0 ? 0 : Math.Min(100, chance / rate); }

        /// <summary>RandEventSystem.HaveGlobalKeys: every required key set and no forbidden key set.</summary>
        public static bool KeysOk(IEnumerable<string> required, IEnumerable<string> forbidden, Func<string, bool> isSet)
        {
            return required.All(isSet) && !forbidden.Any(isSet);
        }
    }

    internal enum Threat { Ignore, Watch, Now }

    /// <summary>
    ///     A ranking of what is round a point, for someone deciding whether to act: ignore what is tamed or cannot hurt, now when it is aware and in reach or a couple of seconds from it,
    ///     watch otherwise. A creature owned by another client has no target stored but the synced "haveTarget" and "alert" flags, so those are all that is known.
    /// </summary>
    internal static class ThreatRules
    {
        public const double NowWithinSeconds = 3;

        public static Threat Rank(bool tamed, bool hostile, bool passiveMobsWorld, bool alert, bool haveTarget, double distance, double reach, double runSpeed, double hit)
        {
            if (tamed || !hostile || hit <= 0) return Threat.Ignore;
            var contact = CombatRules.SecondsToContact(distance, reach, runSpeed);
            if (passiveMobsWorld && !(alert && haveTarget)) return Threat.Watch;                  // PassiveMobs: nothing senses a player until it is hit
            if ((haveTarget || alert) && contact <= NowWithinSeconds) return Threat.Now;
            return Threat.Watch;
        }
    }
}
