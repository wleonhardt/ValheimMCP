// World modifier rules (game-free): what the server options mean, from ZoneSystem global keys, Game.UpdateWorldRates, Player.CreateTombStone and TeleportWorld.Teleport (decompiled).
using System;
using System.Globalization;

namespace HubnerExt
{
    /// <summary>What happens to a dying player's things under the world's death options.</summary>
    internal sealed class DeathRule
    {
        public bool Tombstone;                      // a grave object is made (when there is anything for it)
        public string Equipped, Unequipped;         // "stays on the player", "goes into the tombstone" or "deleted"
        public bool SkillsReset;
    }

    internal static class WorldRules
    {
        /// <summary>A scalar option is stored as a percentage in the key's value ("resourcerate 150") and divided by 100 (Game.UpdateWorldRates); absent or unreadable is 1.</summary>
        public static double Rate(string value)
        {
            double v;
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v)) return v / 100.0;
            return 1.0;
        }

        /// <summary>
        ///     Player.CreateTombStone: DeathKeepInventory makes no grave. Otherwise, unless DeathKeepEquip or DeathDeleteUnequipped is set, everything is unequipped first; then DeathDeleteItems or
        ///     DeathDeleteUnequipped deletes what is not equipped; then, for DeathDeleteUnequipped without DeathKeepEquip, the equipped items are unequipped; the grave takes what is not equipped.
        ///     So DeathDeleteItems alone deletes everything, and DeathDeleteUnequipped alone deletes the bag but graves the worn gear.
        /// </summary>
        public static DeathRule Death(bool keepInventory, bool keepEquip, bool deleteItems, bool deleteUnequipped, bool skillsReset)
        {
            var d = new DeathRule { SkillsReset = skillsReset };
            if (keepInventory) { d.Equipped = d.Unequipped = "stays on the player"; return d; }
            d.Tombstone = true;
            var deleting = deleteItems || deleteUnequipped;
            d.Unequipped = deleting ? "deleted" : "goes into the tombstone";
            if (keepEquip) d.Equipped = "stays on the player";
            else if (deleteUnequipped) d.Equipped = "goes into the tombstone";
            else d.Equipped = deleteItems ? "deleted" : "goes into the tombstone";
            if (d.Equipped == "deleted" && d.Unequipped == "deleted") d.Tombstone = false;     // nothing is left to put in a grave
            return d;
        }

        /// <summary>TeleportWorld.Teleport: NoPortals always blocks; NoBossPortals blocks while a boss event or the activeBosses key is running.</summary>
        public static bool PortalBlocked(bool noPortals, bool noBossPortals, bool bossRunning) { return noPortals || (noBossPortals && bossRunning); }
    }
}
