// Game-free rules shared by the tools (no Unity types, so tests/ links this file): the server write gate, name validation, world bounds.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HubnerExt
{
    /// <summary>The decision behind the dedicated server's write gate (ALLOW_SERVER_WRITES flag file + confirm string).</summary>
    internal static class WriteGate
    {
        public const string Confirm = "server-write";

        /// <summary>
        ///     Null when the call may proceed, else the refusal. A dry run skips the gate only when the handler itself honours
        ///     <c>dry</c> (reports instead of writing): a tool that ignores the argument would otherwise write with the gate closed.
        /// </summary>
        public static string Check(bool dry, bool handlerHonoursDry, bool flagExists, bool flagFresh, int flagMinutes, string flagPath, string confirm)
        {
            if (dry && handlerHonoursDry) return null;
            if (!flagExists) return "server writes are off: create " + flagPath + " (and remove it when done)";
            if (!flagFresh) return "server write flag expired (older than " + flagMinutes + " min): touch " + flagPath + " again";
            if (confirm != Confirm) return "pass confirm:'" + Confirm + "' with every server write";
            return null;
        }
    }

    internal static class SafeNames
    {
        /// <summary>Snapshot names become file names: letters, digits, '_', '-' and '.', never a path and never '..'.</summary>
        public static bool IsFileStem(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name == "." || name.Contains("..")) return false;
            foreach (var c in name)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }
    }

    /// <summary>Valheim's world is a disc of about 10 km radius; tools that take coordinates refuse non-finite values and anything far outside it.</summary>
    internal static class WorldBounds
    {
        public const float Limit = 11000f;          // metres from the origin in x and z
        public const float HeightLimit = 5000f;

        public static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public static bool InWorld(double x, double y, double z)
        {
            return Finite(x) && Finite(y) && Finite(z) && Math.Abs(x) <= Limit && Math.Abs(z) <= Limit && Math.Abs(y) <= HeightLimit;
        }

        /// <summary>Clamp a box coordinate into the world so a typo such as 1e9 cannot make a zone scan loop for hours.</summary>
        public static float Clamp(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return Math.Max(-Limit, Math.Min(Limit, v));
        }
    }
}

namespace HubnerExt
{
    /// <summary>What the journal resolver knows about one object in the world (no Unity types).</summary>
    internal sealed class ObjFacts
    {
        public string Id, Prefab, Plan, Key;
        public double X, Y, Z;
        public object Handle;           // the caller's own reference (the ZDO)
    }

    internal enum Match { Found, NotFound, Ambiguous }

    /// <summary>
    ///     Finds the object a journal entry is about. ZDO ids are only valid within one world load: when the game loads a save it renumbers every ZDO
    ///     (ZDO.Load: m_uid.SetID(++m_loadID), user key 1). An entry written in an earlier run therefore cannot be found by id; it is found by what survives a
    ///     restart: the plan tag (hub_plan + hub_key) and the prefab, or for a plain spawn the prefab and the position (saved to 1 cm).
    /// </summary>
    internal static class Identity
    {
        public const double PositionTolerance = 0.05;      // metres: the save keeps positions to 1 cm
        public const double NearestWins = 0.5;             // duplicate plan keys: the one this close to the entry's position

        /// <param name="entryBoot">the world-load id stored with the entry ("" for old entries)</param>
        /// <param name="currentBoot">the id of the current world load</param>
        /// <param name="positionReliable">the entry's position is where the object stands now (a spawn), not where it stood before an edit</param>
        public static Match Find(string entryBoot, string currentBoot, string id, string prefab, string idPlan, string idKey, double x, double y, double z, bool positionReliable, IList<ObjFacts> candidates, out ObjFacts found)
        {
            found = null;
            if (candidates == null || candidates.Count == 0) return Match.NotFound;

            if (!string.IsNullOrEmpty(entryBoot) && entryBoot == currentBoot)
            {
                foreach (var c in candidates) if (c.Id == id && c.Prefab == prefab) { found = c; return Match.Found; }
                return Match.NotFound;                       // same load: the id is exact, and it names nothing (or something else) now
            }

            // a previous run: ids mean nothing
            var same = new List<ObjFacts>();
            if (!string.IsNullOrEmpty(idPlan) && !string.IsNullOrEmpty(idKey))
            {
                foreach (var c in candidates) if (c.Prefab == prefab && c.Plan == idPlan && c.Key == idKey) same.Add(c);
                if (same.Count == 0) return Match.NotFound;
                if (same.Count == 1) { found = same[0]; return Match.Found; }
                return Nearest(same, x, y, z, NearestWins, out found);
            }

            if (!positionReliable) return Match.NotFound;     // no tag and the position is from before the edit: nothing to go by
            foreach (var c in candidates) if (c.Prefab == prefab && Dist(c, x, y, z) <= PositionTolerance) same.Add(c);
            if (same.Count == 0) return Match.NotFound;
            if (same.Count == 1) { found = same[0]; return Match.Found; }
            return Match.Ambiguous;                           // two identical objects at one spot: do not guess which one to destroy
        }

        private static Match Nearest(List<ObjFacts> list, double x, double y, double z, double within, out ObjFacts found)
        {
            found = null;
            var ordered = list.OrderBy(c => Dist(c, x, y, z)).ToList();
            var best = Dist(ordered[0], x, y, z);
            if (best > within) return Match.Ambiguous;
            if (Dist(ordered[1], x, y, z) - best < 0.01) return Match.Ambiguous;      // a tie
            found = ordered[0]; return Match.Found;
        }

        private static double Dist(ObjFacts c, double x, double y, double z)
        {
            var dx = c.X - x; var dy = c.Y - y; var dz = c.Z - z;
            return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
