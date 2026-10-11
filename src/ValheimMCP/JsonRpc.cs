using System;
using System.Globalization;

namespace ValheimMCP
{
    /// <summary>JSON-RPC / MCP envelope helpers and protocol-version negotiation (pure, unit-tested).</summary>
    internal static class JsonRpc
    {
        /// <summary>Protocol revisions this server answers, newest first. It implements only tools (plus progress notifications), which every one of them shares.</summary>
        public static readonly string[] SupportedProtocols = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        /// <summary>The client's version when we support it, otherwise the newest one we do (spec: lifecycle / version negotiation).</summary>
        public static string Negotiate(string requested)
        {
            foreach (var v in SupportedProtocols) if (v == requested) return v;
            return SupportedProtocols[0];
        }

        /// <summary>The MCP-Protocol-Version header is optional for old clients; a present but unsupported value must be refused with 400.</summary>
        public static bool HeaderAcceptable(string header)
        {
            if (string.IsNullOrEmpty(header)) return true;
            return Array.IndexOf(SupportedProtocols, header.Trim()) >= 0;
        }

        public static string ToolText(string text, bool isError)
        {
            return "{\"content\":[{\"type\":\"text\",\"text\":" + Json.Str(text) + "}],\"isError\":" +
                   (isError ? "true" : "false") + "}";
        }

        public static string ToolImage(string base64, string mimeType, string caption = null)
        {
            var cap = string.IsNullOrEmpty(caption) ? "" : "{\"type\":\"text\",\"text\":" + Json.Str(caption) + "},";
            return "{\"content\":[" + cap + "{\"type\":\"image\",\"data\":" + Json.Str(base64) +
                   ",\"mimeType\":" + Json.Str(mimeType) + "}],\"isError\":false}";
        }

        public static string Result(object id, string resultJson)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + FormatId(id) + ",\"result\":" + resultJson + "}";
        }

        public static string ErrorResponse(object id, int code, string message)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + FormatId(id) +
                   ",\"error\":{\"code\":" + code.ToString(CultureInfo.InvariantCulture) +
                   ",\"message\":" + Json.Str(message) + "}}";
        }

        /// <summary>Echo the request id with its original JSON type (int vs string).</summary>
        public static string FormatId(object id)
        {
            if (id == null) return "null";
            if (id is string s) return Json.Str(s);
            if (id is bool b) return b ? "true" : "false";
            if (id is double d)
            {
                if (!double.IsInfinity(d) && !double.IsNaN(d) && Math.Abs(d - Math.Floor(d)) < double.Epsilon &&
                    Math.Abs(d) < 9.2e18)
                    return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }

            return Json.Str(id.ToString());
        }
    }
}
