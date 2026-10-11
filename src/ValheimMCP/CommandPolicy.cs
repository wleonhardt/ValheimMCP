using System;
using System.Collections.Generic;

namespace ValheimMCP
{
    /// <summary>
    ///     Pure allow/deny rules for console command lines (no BepInEx types, so it is unit-tested). Matches the command
    ///     name (first whitespace-delimited token, case-insensitive); a trailing <c>*</c> is a prefix wildcard.
    ///     Deny wins; a non-empty allow list is exclusive.
    /// </summary>
    internal static class CommandPolicy
    {
        public static string NameOf(string commandLine)
        {
            var s = (commandLine ?? "").Trim();
            var end = 0;
            while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
            return s.Substring(0, end).ToLowerInvariant();
        }

        public static bool IsAllowed(string commandLine, IList<string> allow, IList<string> deny, out string reason)
        {
            reason = null;
            var name = NameOf(commandLine);

            if (Matches(deny, name))
            {
                reason = $"command '{name}' is denied by config (commands.deny)";
                return false;
            }

            if (allow != null && allow.Count > 0 && !Matches(allow, name))
            {
                reason = $"command '{name}' is not in the config allowlist (commands.allow)";
                return false;
            }

            return true;
        }

        public static bool Matches(IList<string> patterns, string name)
        {
            if (patterns == null) return false;
            foreach (var raw in patterns)
            {
                var p = (raw ?? "").ToLowerInvariant();
                if (p.EndsWith("*", StringComparison.Ordinal))
                {
                    if (name.StartsWith(p.Substring(0, p.Length - 1), StringComparison.Ordinal)) return true;
                }
                else if (name == p)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
