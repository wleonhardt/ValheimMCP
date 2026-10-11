// Game-free rules behind the base-keeping readers (producers, floor items, pickables, active area, mine rocks, spawners, absences). Each follows the decompiled game: see the notes on the
// methods. Times are seconds; "now" is the game's world time (ZNet.GetTime), ticks are .NET ticks as the game stores them in ZDOs.
using System;
using System.Collections.Generic;

namespace HubnerExt
{
    internal static class ProducerRules
    {
        /// <summary>Fermenter.GetStatus: no Content is empty; more than m_fermentationDuration seconds since StartTime is ready; else fermenting.</summary>
        public static string FermenterStatus(bool hasContent, double secondsSinceStart, double duration)
        {
            if (!hasContent) return "empty";
            return secondsSinceStart > duration ? "ready" : "fermenting";
        }

        public static double FermenterSecondsLeft(double secondsSinceStart, double duration) { return Math.Max(0, duration - Math.Max(0, secondsSinceStart)); }

        /// <summary>
        ///     Beehive.UpdateBees and SapCollector.UpdateTick: while the producer works, the seconds since the last update pile up in the ZDO float "product"; when it passes secPerUnit, whole
        ///     units are added to "level" (at most max) and the remainder is dropped. Seconds until the level reaches <paramref name="target" />, at one unit per secPerUnit.
        /// </summary>
        public static double SecondsToLevel(int level, int target, double productSeconds, double secPerUnit)
        {
            if (level >= target) return 0;
            return Math.Max(0, (target - level) * secPerUnit - Math.Max(0, productSeconds));
        }

        /// <summary>SapCollector takes units from a ResourceRoot as it makes them (the root's level is a float, 100 when absent): at most floor(root level) more units.</summary>
        public static int SapUnitsAvailable(int level, int max, double rootLevel)
        {
            var room = Math.Max(0, max - level);
            if (double.IsNaN(rootLevel)) return room;
            return (int)Math.Min(room, Math.Max(0, Math.Floor(rootLevel)));
        }
    }

    internal static class DropRules
    {
        public const double DespawnSeconds = 3600, PlayerRange = 25, BaseMinY = 28;

        /// <summary>
        ///     ItemDrop.TimedDestruction (the owner, every SlowUpdate, with m_autoDestroy): a floor item older than an hour is destroyed unless it is inside a PlayerBase effect area ABOVE y 28,
        ///     a player is within 25 m, it lies in tar, or it is a piece. Returns why it is kept, or null when it will go.
        /// </summary>
        public static string KeptBy(bool autoDestroy, double ageSeconds, bool insidePlayerBase, double y, bool playerNear, bool inTar, bool isPiece)
        {
            if (!autoDestroy) return "does not despawn (m_autoDestroy off)";
            if (ageSeconds < DespawnSeconds) return null;       // still young: it goes when it turns an hour old, if nothing keeps it then
            if (insidePlayerBase && y > BaseMinY) return "inside a base (a PlayerBase area above y 28)";
            if (playerNear) return "a player is within 25 m";
            if (inTar) return "lies in tar";
            if (isPiece) return "is a piece";
            return null;
        }

        /// <summary>Seconds until the owner would remove it (0 when it is already past an hour and nothing keeps it; the owner's next check removes it).</summary>
        public static double SecondsLeft(double ageSeconds) { return Math.Max(0, DespawnSeconds - ageSeconds); }
    }

    internal static class PickRules
    {
        /// <summary>
        ///     Pickable.ShouldRespawn (owner, every 60 s while picked): with a respawn time, a picked plant comes back more than m_respawnTimeMinutes after "picked_time". A picked_time of 0 or 1
        ///     means the owner has not stamped it yet. Returns minutes left; 0 when due; NaN when it never respawns.
        /// </summary>
        public static double MinutesToRespawn(bool picked, bool enabled, double respawnMinutes, long pickedTicks, long nowTicks)
        {
            if (!picked) return 0;
            if (respawnMinutes <= 0) return double.NaN;
            if (pickedTicks <= 1) return respawnMinutes;           // not stamped yet: the owner stamps it with a random head start (m_respawnTimeInitMin/Max)
            var passed = (nowTicks - pickedTicks) / (double)TimeSpan.TicksPerMinute;
            return Math.Max(0, respawnMinutes - passed);
        }
    }

    internal static class ActiveArea
    {
        public const double ZoneSize = 64;

        /// <summary>The zone index of a coordinate (ZoneSystem.GetZone: floor((v + 32) / 64)) and the centre of that zone.</summary>
        public static int Zone(double v) { return (int)Math.Floor((v + ZoneSize / 2) / ZoneSize); }
        public static double ZoneCentre(int zone) { return zone * ZoneSize; }

        /// <summary>
        ///     ZNetScene.PointInsideActiveArea(zone, point): the Chebyshev distance from the zone centre is within 1.5 zones (1 zone when the near simulation distance is 1); with the near
        ///     distance 2 and not classic, the Euclidean distance must also be under 1.75 zones.
        /// </summary>
        public static bool Inside(double refX, double refZ, double px, double pz, int nearDistance, bool classic)
        {
            var cx = ZoneCentre(Zone(refX)); var cz = ZoneCentre(Zone(refZ));
            var reach = (nearDistance == 1 ? 1.0 : 1.5) * ZoneSize;
            var cheb = Math.Max(Math.Abs(cx - px), Math.Abs(cz - pz));
            if (cheb > reach) return false;
            if (nearDistance == 2 && !classic) { var r = ZoneSize * 1.75; return (cx - px) * (cx - px) + (cz - pz) * (cz - pz) < r * r; }
            return true;
        }
    }

    internal static class MineRules
    {
        /// <summary>MineRock5 keeps its hit areas in the ZDO string "health": a base64 ZPackage of an int count and one float per area (at or under 0 means mined out). Null when it is not that.</summary>
        public static float[] DecodeAreas(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            byte[] b; try { b = Convert.FromBase64String(base64); } catch (FormatException) { return null; }       // allow: not base64 means no readable health, and the caller says so
            if (b.Length < 4) return null;
            var n = BitConverter.ToInt32(b, 0);
            if (n < 0 || n > 4096 || b.Length < 4 + 4 * n) return null;
            var res = new float[n]; for (var i = 0; i < n; i++) res[i] = BitConverter.ToSingle(b, 4 + 4 * i);
            return res;
        }

        public static int Remaining(float[] areas) { var n = 0; if (areas != null) foreach (var a in areas) if (a > 0f) n++; return n; }

        /// <summary>HitData.CheckToolTier: a tool works when its tier is at least the rock's m_minToolTier.</summary>
        public static bool ToolWorks(int toolTier, int minToolTier) { return toolTier >= minToolTier; }
    }

    internal static class SpawnerRules
    {
        /// <summary>
        ///     CreatureSpawner.UpdateSpawner (owner, every m_spawnInterval s): no new spawn while the creature it made still exists (this refreshes "alive_time"), and only after
        ///     m_respawnTimeMinuts since "alive_time". Returns minutes until it may spawn again (0 when it may), NaN when it never respawns (respawn time 0 and it has spawned).
        /// </summary>
        public static double MinutesToRespawn(bool creatureAlive, bool hasSpawned, double respawnMinutes, long aliveTicks, long nowTicks)
        {
            if (creatureAlive) return double.NaN;
            if (respawnMinutes <= 0) return hasSpawned ? double.NaN : 0;
            var passed = (nowTicks - aliveTicks) / (double)TimeSpan.TicksPerMinute;
            return Math.Max(0, respawnMinutes - passed);
        }
    }

    internal static class WearRules
    {
        public const double RainPercent = 5, RainSeconds = 60, RainFloorHealth = 0.5, AshSeconds = 5, LavaSeconds = 2, LavaPercent = 70, LavaShieldedPercent = 30;

        /// <summary>
        ///     WearNTear.UpdateWear: rain takes 5 percent of the maximum health per 60 s from a piece with m_noRoofWear that has no roof, only while it is above half health (so rain alone never
        ///     destroys). Returns the seconds of rain it takes to reach half health from a health fraction, or 0 when it is already there.
        /// </summary>
        public static double SecondsOfRainToHalf(double healthFraction, double maxHealth)
        {
            if (healthFraction <= RainFloorHealth) return 0;
            var steps = Math.Ceiling((healthFraction - RainFloorHealth) / (RainPercent / 100.0) - 1e-9);
            return steps * RainSeconds;
        }

        /// <summary>Ash damage per 5 s in the Ashlands when Game.m_ashDamage is above 0, to a piece that is not immune, not under an ash roof and not inside a shield; a resistant piece stops at 10 percent health.</summary>
        public static bool AshApplies(bool ashlands, double ashDamage, bool shielded, bool immune, bool ashRoof, bool resist, double healthFraction)
        {
            return ashlands && ashDamage > 0 && !shielded && !immune && !ashRoof && (!resist || healthFraction > 0.1);
        }

        /// <summary>Damage percent per 2 s from lava paint above 0.2 within 1.5 m of the ground.</summary>
        public static double LavaPercentPerTick(double lavaValue, double groundDist, bool shielded, bool immune, bool resist)
        {
            if (immune || lavaValue <= 0.2 || groundDist >= 1.5) return 0;
            return (shielded ? LavaShieldedPercent : LavaPercent) * lavaValue * (resist ? 0.33 : 1.0);
        }

        /// <summary>A piece with m_requiredBiome outside that biome takes m_outsideRequiredBiomeDamage percent every update.</summary>
        public static bool OutsideRequiredBiome(int requiredMask, int biome) { return requiredMask != 0 && (requiredMask & biome) == 0; }
    }

    internal static class AbsenceRules
    {
        /// <summary>What an absence of the given length changes. Fire: uncapped catch-up (the fuel lasts fuel x secPerFuel s). Smelter: at most 3600 s of work. Pickable and fermenter: absolute timestamps.</summary>
        public static double FireBurnsFor(double fuel, double secPerFuel) { return Math.Max(0, fuel) * Math.Max(0, secPerFuel); }
        public static bool FireOutWithin(double fuel, double secPerFuel, double awaySeconds) { return FireBurnsFor(fuel, secPerFuel) <= awaySeconds; }
    }
}
