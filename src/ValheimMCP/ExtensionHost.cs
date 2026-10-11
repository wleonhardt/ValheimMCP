using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;

namespace ValheimMCP
{
    /// <summary>
    ///     Loads IMcpExtension implementations from BepInEx/hubner-ext/*.dll and hot-reloads them when the file
    ///     changes (polled every 2 s on a background thread; the load itself runs on the Unity main thread).
    ///     Assemblies are loaded from bytes so the file on disk is never locked and can be overwritten by scp.
    /// </summary>
    internal static class ExtensionHost
    {
        private static Thread _thread;
        private static volatile bool _stop;
        private static readonly Dictionary<string, DateTime> Seen = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, List<IMcpExtension>> Loaded = new Dictionary<string, List<IMcpExtension>>();
        public static string Dir { get { return Path.Combine(Paths.BepInExRootPath, "hubner-ext"); } }

        public static void Start()
        {
            Directory.CreateDirectory(Dir);
            _stop = false;
            _thread = new Thread(Poll) { IsBackground = true, Name = "valheimmcp-ext-poll" };
            _thread.Start();
        }

        public static void Stop() { _stop = true; }

        /// <summary>Plugin shutdown (main thread): give every extension its Unload so Harmony patches and cameras do not outlive the plugin.</summary>
        public static void UnloadAll()
        {
            foreach (var list in Loaded.Values)
                foreach (var ext in list)
                {
                    try { ext.Unload(); } catch (Exception ex) { Plugin.Log?.LogWarning("[ValheimMCP] extension Unload threw: " + ex.Message); }
                    Tools.RemoveOwner(ext.Name);
                }
            Loaded.Clear();
        }

        private static void Poll()
        {
            while (!_stop)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(Dir, "*.dll"))
                    {
                        var t = File.GetLastWriteTimeUtc(f);
                        if (Seen.TryGetValue(f, out var prev) && prev == t) continue;
                        // give scp a moment to finish writing
                        Thread.Sleep(500);
                        t = File.GetLastWriteTimeUtc(f);
                        var path = f;
                        var done = MainThreadDispatcher.RunBlocking<bool>(() => { Load(path); return true; }, 20000, out _, out var err);
                        if (err != null) { Seen[f] = t; Plugin.Log?.LogError("[ValheimMCP] extension load failed (fix the DLL and push again): " + err); }
                        else if (done) Seen[f] = t;      // timed out (game not ticking): retry next poll
                    }
                }
                catch (Exception ex) { Plugin.Log?.LogError("[ValheimMCP] extension poll: " + ex.Message); }
                Thread.Sleep(2000);
            }
        }

        private static void Load(string path)
        {
            if (Loaded.TryGetValue(path, out var olds))
            {
                foreach (var old in olds)
                {
                    try { old.Unload(); } catch (Exception ex) { Plugin.Log?.LogWarning("[ValheimMCP] extension Unload threw: " + ex.Message); }
                    Tools.RemoveOwner(old.Name);
                }
                Loaded.Remove(path);
            }
            var asm = Assembly.Load(File.ReadAllBytes(path));
            var mine = new List<IMcpExtension>();
            Loaded[path] = mine;
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || !typeof(IMcpExtension).IsAssignableFrom(type)) continue;
                var ext = (IMcpExtension)Activator.CreateInstance(type);
                mine.Add(ext);
                try
                {
                    ext.Register(new ToolRegistry(ext.Name));
                }
                catch
                {
                    // a half-registered extension must not leave tools or patches behind
                    try { ext.Unload(); } catch (Exception ex) { Plugin.Log?.LogWarning("[ValheimMCP] extension Unload threw: " + ex.Message); }
                    Tools.RemoveOwner(ext.Name);
                    mine.Remove(ext);
                    throw;
                }
                Plugin.Log?.LogInfo("[ValheimMCP] extension loaded: " + ext.Name + " (" + Path.GetFileName(path) + ")");
            }
            if (mine.Count == 0) Plugin.Log?.LogWarning("[ValheimMCP] no IMcpExtension in " + path);
        }
    }
}
