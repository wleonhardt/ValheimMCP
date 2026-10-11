using System;

namespace ValheimMCP
{
    /// <summary>
    ///     Pure request admission rules for the loopback HTTP endpoint (no HttpListener types, so it is unit-testable):
    ///     refuse cross-site browser requests (any Origin header), non-loopback Host names (DNS rebinding), bodies over the
    ///     cap and, when a token is configured, requests without it. Returns the rejection reason or null.
    /// </summary>
    internal static class RequestPolicy
    {
        public const long MaxBodyBytes = 8L * 1024 * 1024;

        public static string Reject(string origin, string host, string authorization, string tokenHeader, long contentLength, string configHost, string configToken)
        {
            if (!string.IsNullOrEmpty(origin)) return "cross-origin request refused (Origin header present)";
            var h = HostName(host ?? "");
            if (!(IsLoopbackName(h) || (!string.IsNullOrEmpty(configHost) && string.Equals(h, configHost, StringComparison.OrdinalIgnoreCase))))
                return "Host header refused (not a loopback name): " + h;
            if (!string.IsNullOrEmpty(configToken))
            {
                var auth = authorization ?? "";
                var tok = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim() : (tokenHeader ?? "");
                if (!FixedTimeEquals(tok, configToken)) return "missing or wrong token";
            }
            if (contentLength > MaxBodyBytes) return TooLarge;
            return null;
        }

        /// <summary>Host header without the port: "127.0.0.1:8731" -> "127.0.0.1", "[::1]:8731" -> "[::1]".</summary>
        public static string HostName(string host)
        {
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                var end = host.IndexOf(']');
                return end > 0 ? host.Substring(0, end + 1) : host;
            }
            var colon = host.IndexOf(':');
            return colon >= 0 ? host.Substring(0, colon) : host;
        }

        public static bool IsLoopbackName(string h)
        {
            return string.Equals(h, "127.0.0.1", StringComparison.Ordinal) || string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(h, "[::1]", StringComparison.Ordinal);
        }

        /// <summary>True for a bind address that only this machine can reach (127.0.0.0/8, localhost, ::1).</summary>
        public static bool IsLoopbackBind(string host)
        {
            var h = (host ?? "").Trim();
            return h.StartsWith("127.", StringComparison.Ordinal) || string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) || h == "::1" || h == "[::1]";
        }

        /// <summary>Compares in time that depends only on the length, not on where the strings differ.</summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public const string TooLarge = "request body too large (limit 8 MB)";

        public static int StatusFor(string reason)
        {
            if (reason == null) return 403;
            if (reason.StartsWith("missing", StringComparison.Ordinal)) return 401;
            if (reason.StartsWith("request body too large", StringComparison.Ordinal)) return 413;
            return 403;
        }
    }
}
