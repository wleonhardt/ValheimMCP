using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace ValheimMCP
{
    /// <summary>
    ///     Loads <c>valheimmcp.yml</c> from the BepInEx config directory (writing a
    ///     commented default if absent) and exposes the settings. Hand-parsed via
    ///     <see cref="MiniYaml" /> — no config-library dependency.
    /// </summary>
    internal static class ModConfig
    {
        public const string FileName = "valheimmcp.yml";

        /// <summary>True when the config file could not be read: the plugin then stays fail-closed (no listener) instead of running with open defaults.</summary>
        public static bool LoadFailed;

        public static string Host = "127.0.0.1";
        public static string Token = "";                      // optional bearer token (server.token); empty = no token check
        public static int Port = 8731;
        public static int CommandTimeoutMs = 15000;

        public static int RenderDefaultSize = 768;
        public static int RenderMinSize = 256;
        public static int RenderMaxSize = 1280;

        public static int WaitDefaultTimeoutMs = 120000;
        public static int WaitMaxTimeoutMs = 600000;
        public static int WaitHeartbeatMs = 5000;

        public static int LogBufferCapacity = 2000;
        public static int LogDefaultLines = 200;
        public static int LogMaxLines = 1000;

        public static bool ToolsWrite = false;
        public static string ToolsProfile = "all";       // tools/list filter: all | observe | build | control (every tool stays callable by name)
        public static bool ToolsTerse = false;          // tools/list: first sentence of each description only
        public static int ToolTimeoutMs = 30000;
        private static List<string> _toolDeny = new();
        public static bool IsToolDenied(string name) { return _toolDeny.Contains(name); }

        private static List<string> _allow = new();
        private static List<string> _deny = new();
        private static MiniYaml _y = MiniYaml.Parse("");

        /// <summary>Any scalar from valheimmcp.yml by dotted path (extensions read their own `hubner:` section through this).</summary>
        public static string Setting(string path, string dflt) { return _y.Get(path, dflt); }
        public static int SettingInt(string path, int dflt) { return _y.GetInt(path, dflt); }
        public static List<string> SettingList(string path) { return _y.GetList(path); }

        public static void Load()
        {
            try
            {
                var path = Path.Combine(Paths.ConfigPath, FileName);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, DefaultYaml);
                    Plugin.Log?.LogInfo($"[ValheimMCP] wrote default config: {path}");
                }

                var y = MiniYaml.Parse(File.ReadAllText(path)); _y = y;
                Host = y.Get("server.host", Host);
                Token = y.Get("server.token", Token) ?? "";
                Port = y.GetInt("server.port", Port);
                CommandTimeoutMs = y.GetInt("server.commandTimeoutMs", CommandTimeoutMs);
                RenderDefaultSize = y.GetInt("render.defaultSize", RenderDefaultSize);
                RenderMinSize = y.GetInt("render.minSize", RenderMinSize);
                RenderMaxSize = y.GetInt("render.maxSize", RenderMaxSize);
                WaitDefaultTimeoutMs = y.GetInt("wait.defaultTimeoutMs", WaitDefaultTimeoutMs);
                WaitMaxTimeoutMs = y.GetInt("wait.maxTimeoutMs", WaitMaxTimeoutMs);
                WaitHeartbeatMs = y.GetInt("wait.heartbeatMs", WaitHeartbeatMs);
                LogBufferCapacity = y.GetInt("log.bufferCapacity", LogBufferCapacity);
                LogDefaultLines = y.GetInt("log.defaultLines", LogDefaultLines);
                LogMaxLines = y.GetInt("log.maxLines", LogMaxLines);
                ToolsWrite = y.GetBool("tools.write", false);
                ToolTimeoutMs = y.GetInt("tools.timeoutMs", ToolTimeoutMs);
                _toolDeny = y.GetList("tools.deny");
                ToolsProfile = y.Get("tools.profile", ToolsProfile);
                ToolsTerse = y.GetBool("tools.terse", false);
                _allow = y.GetList("commands.allow");
                _deny = y.GetList("commands.deny");

                foreach (var w in y.Warnings) Plugin.Log?.LogWarning($"[ValheimMCP] config: {w}");
                Plugin.Log?.LogInfo(
                    $"[ValheimMCP] config: {Host}:{Port}, render {RenderMinSize}-{RenderMaxSize} " +
                    $"(default {RenderDefaultSize}), allow={_allow.Count} deny={_deny.Count}");
            }
            catch (Exception ex)
            {
                LoadFailed = true;
                ToolsWrite = false;
                _allow = new List<string>();
                _deny = new List<string> { "*" };
                Plugin.Log?.LogError($"[ValheimMCP] failed to load config, staying closed (no listener, writes off, all commands denied): {ex.Message}");
            }
        }

        /// <summary>Clamp a requested render size into the configured range (0 = use default).</summary>
        public static int ClampRenderSize(int requested)
        {
            if (requested <= 0) requested = RenderDefaultSize;
            var min = Math.Max(64, RenderMinSize);
            var max = Math.Max(min, RenderMaxSize);
            return Math.Min(max, Math.Max(min, requested));
        }

        /// <summary>Clamp a requested wait_for_log timeout (0 = use default) into [1s, max].</summary>
        public static int ClampWaitTimeout(int requested)
        {
            if (requested <= 0) requested = WaitDefaultTimeoutMs;
            return Math.Min(WaitMaxTimeoutMs, Math.Max(1000, requested));
        }

        /// <summary>Clamp a requested get_log line count (0 = use default) into [1, min(maxLines, capacity)].</summary>
        public static int ClampLogLines(int requested)
        {
            if (requested <= 0) requested = LogDefaultLines;
            var max = Math.Max(1, Math.Min(LogMaxLines, LogBufferCapacity));
            return Math.Min(max, Math.Max(1, requested));
        }

        /// <summary>Gate a console command line against the allow/deny lists (rules in <see cref="CommandPolicy" />).</summary>
        public static bool IsCommandAllowed(string commandLine, out string reason)
        {
            return CommandPolicy.IsAllowed(commandLine, _allow, _deny, out reason);
        }

        private const string DefaultYaml =
            @"# ValheimMCP configuration (YAML). Full-line comments (#) only.

server:
  host: 127.0.0.1          # loopback only. A non-loopback host is refused unless token is set
  token: ''                # optional shared secret; clients send 'Authorization: Bearer <token>'. Quote it if it contains ' #'
  port: 8731
  commandTimeoutMs: 15000  # max wait for a command to run on the main thread

render:
  defaultSize: 768         # render_view size (px, square) when 'size' is omitted
  minSize: 256
  maxSize: 1280

wait:
  defaultTimeoutMs: 120000 # wait_for_log timeout when 'timeoutMs' is omitted
  maxTimeoutMs: 600000     # hard cap on any wait_for_log request
  heartbeatMs: 5000        # SSE progress heartbeat interval while a wait is pending

log:
  bufferCapacity: 2000     # in-memory ring of recent log lines retained for get_log
  defaultLines: 200        # get_log lines returned when 'maxLines' is omitted
  maxLines: 1000           # hard cap on lines per get_log request

# Access control for run_command (and POST /command). 'deny' always wins. If
# 'allow' is non-empty, ONLY matching commands may run. Match is by command name;
# a trailing '*' is a prefix wildcard, e.g. ""spawn*"" matches every spawn command.
commands:
  allow: []
  deny: []

# 'write' enables tools that change the world, including run_command (console commands).
# 'deny' blocks individual tools by name (built-in tools too). 'profile' only trims tools/list, it is not access control.
tools:
  write: false
  timeoutMs: 30000
  deny: []
  profile: all             # what tools/list advertises: all | observe (reads, ops) | build (+ plans, zdo, undo, jobs) | control (+ teleport, walk, menu, env, chat)
  terse: false             # true = first sentence of each description only (smaller tool list for the model; the README has the details)

# Hubner extension settings (read by BepInEx/hubner-ext/HubnerExt.dll).
#   sandboxNames: character names that count as the sandbox; only they get god mode from teleport/walk. Empty = nobody
#   exposePeerHosts: true lets the players tool return each peer's IP / Steam id (default false)
#   captureWhispers: true lets chat_tail record whispers (default false)
#   trustedPlayers: platform ids (Steam_7656...) of players whose chat chat_tail does not mark untrusted; a name counts only while one player carries it
#   writeFlagMinutes: how long the server's ALLOW_SERVER_WRITES flag file stays valid after it was touched
#   jobBudgetMs: main-thread time a queued job may use per frame (plan_apply and friends are sliced)
hubner:
  sandboxNames: []
  exposePeerHosts: false
  captureWhispers: false
  trustedPlayers: []
  writeFlagMinutes: 30
  jobBudgetMs: 8
";
    }
}