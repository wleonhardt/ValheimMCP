using System;
using System.Text.RegularExpressions;

namespace ValheimMCP
{
    /// <summary>
    ///     Regexes that come from a tool argument run against log lines on the HTTP and log threads, so a pattern with
    ///     catastrophic backtracking must not be able to hang them: every match has a short timeout and a timeout counts as no match.
    /// </summary>
    internal static class SafeRegex
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

        public static Regex Create(string pattern) { return new Regex(pattern, RegexOptions.None, Timeout); }

        public static bool IsMatch(Regex rx, string line)
        {
            try { return rx.IsMatch(line); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
