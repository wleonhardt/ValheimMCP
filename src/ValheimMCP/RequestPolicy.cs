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
            if (!(h == "127.0.0.1" || h == "localhost" || h == "[::1]" || string.Equals(h, configHost ?? "", StringComparison.OrdinalIgnoreCase)))
                return "Host header refused (not a loopback name): " + h;
            if (!string.IsNullOrEmpty(configToken))
            {
                var auth = authorization ?? "";
                var tok = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim() : (tokenHeader ?? "");
                if (!string.Equals(tok, configToken, StringComparison.Ordinal)) return "missing or wrong token";
            }
            if (contentLength > MaxBodyBytes) return "request body too large (limit 8 MB)";
            return null;
        }

        /// <summary>Host header without the port: "127.0.0.1:8731" -> "127.0.0.1", "[::1]:8731" -> "[::1]".</summary>
        public static string HostName(string host)
        {
            if (host.StartsWith("["))
            {
                var end = host.IndexOf(']');
                return end > 0 ? host.Substring(0, end + 1) : host;
            }
            var colon = host.IndexOf(':');
            return colon >= 0 ? host.Substring(0, colon) : host;
        }

        public static int StatusFor(string reason) { return reason != null && reason.StartsWith("missing") ? 401 : 403; }
    }
}
