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
}
