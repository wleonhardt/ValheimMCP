// Game-free rules shared by the tools (no Unity types, so tests/ links this file): the server write gate, name validation, world bounds.
using System;

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
