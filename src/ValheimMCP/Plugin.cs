using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace ValheimMCP
{
    /// <summary>
    ///     Standalone BepInEx plugin that exposes a localhost HTTP endpoint for
    ///     driving Valheim's in-game console remotely and returning the console
    ///     output directly in the response. Intended to live in <c>plugins/</c>
    ///     (loaded once, never hot-reloaded) so the listener stays up across F6
    ///     reloads of other mods.
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        // Guid/Name/Version live in PluginInfo, generated from ValheimMCP.csproj.

        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;
        private HttpServer _server;

        private void Awake()
        {
            Log = Logger;
            MainThreadDispatcher.Warn = s => Log.LogWarning(s);
            MainWatch.Log = s => Log.LogWarning(s);
            MainWatch.Start();

            // Settings come from BepInEx/config/valheimmcp.yml (written with defaults
            // on first run). See ModConfig / MiniYaml.
            ModConfig.Load();
            if (ModConfig.LoadFailed)
            {
                Log.LogError($"{PluginInfo.Name}: config could not be read, listener not started");
                return;
            }

            if (!RequestPolicy.IsLoopbackBind(ModConfig.Host) && string.IsNullOrEmpty(ModConfig.Token))
            {
                Log.LogError($"{PluginInfo.Name}: server.host '{ModConfig.Host}' is not loopback and server.token is empty, listener not started (set a token or bind 127.0.0.1)");
                return;
            }

            // Harmony postfix on Terminal.AddString tees console output into the
            // capture buffer while a command runs (see ConsoleOutputCapture).
            _harmony = new Harmony(PluginInfo.Guid);
            _harmony.PatchAll(typeof(ConsoleOutputCapture));

            // Tap the BepInEx log stream so the wait_for_log MCP tool can block until a
            // matching line appears (e.g. another mod's hot-reload finishing).
            LogWatch.Install();
            ExtensionHost.Start();

            var prefix = $"http://{ModConfig.Host}:{ModConfig.Port}/";
            try
            {
                _server = new HttpServer(prefix, ModConfig.CommandTimeoutMs);
                _server.Start();
                Log.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} listening on {prefix}");
            }
            catch (Exception ex)
            {
                Log.LogError($"{PluginInfo.Name} failed to start on {prefix}: {ex}");
            }
        }

        // Drains main-thread work queued by the HTTP listener's background thread.
        private void Update()
        {
            MainWatch.Frame();
            MainThreadDispatcher.Pump();
        }

        private void OnDestroy()
        {
            MainWatch.Stop();
            ExtensionHost.Stop();
            ExtensionHost.UnloadAll();
            _server?.Stop();
            LogWatch.Uninstall();
            _harmony?.UnpatchSelf();
            Log?.LogInfo($"{PluginInfo.Name} stopped.");
        }
    }
}