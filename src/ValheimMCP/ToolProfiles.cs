using System;
using System.Collections.Generic;

namespace ValheimMCP
{
    /// <summary>Pure rules behind tools.profile / tools.terse (unit-tested): which categories a profile advertises in tools/list, and the terse description cut.</summary>
    internal static class ToolProfiles
    {
        /// <summary>all | observe (read, ops) | build (read, ops, build) | control (read, ops, control). Unknown or empty = all (null).</summary>
        public static HashSet<string> Categories(string profile)
        {
            switch ((profile ?? "all").Trim().ToLowerInvariant())
            {
                case "observe": return new HashSet<string> { "read", "ops", "misc" };
                case "build": return new HashSet<string> { "read", "ops", "misc", "build" };
                case "control": return new HashSet<string> { "read", "ops", "misc", "control" };
                default: return null;
            }
        }

        /// <summary>First sentence of a description: the schema still carries the parameters, the README the details.</summary>
        public static string Terse(string d)
        {
            if (string.IsNullOrEmpty(d)) return d;
            var i = d.IndexOf(". ", StringComparison.Ordinal);
            if (i < 0) i = d.IndexOf(": ", StringComparison.Ordinal);
            return i > 20 ? d.Substring(0, i + 1) : d;
        }
    }
}
