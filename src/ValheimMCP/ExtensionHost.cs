using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;

namespace ValheimMCP
{
    /// <summary>
    ///     Loads IMcpExtension implementations from BepInEx/hubner-ext/*.dll and hot-reloads them when the file
    ///     changes (polled every 2 s on a background thread). Assemblies are loaded from bytes so the file on disk is never locked and can be
    ///     overwritten by scp. The poll thread loads the new assembly and checks that it binds to this core BEFORE the old extension is touched
    ///     (<see cref="BindCheck" />); only then does the main thread swap: unload the old one, retire its assembly (its queued calls are dropped), register the new.
    /// </summary>
    internal static class ExtensionHost
    {
        /// <summary>Mono cannot unload assemblies: every reload stays in memory. A long-lived server should be restarted now and then.</summary>
        public const int WarnReloads = 50;

        private static Thread _thread;
        private static volatile bool _stop;
        private static readonly Dictionary<string, DateTime> Seen = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, List<IMcpExtension>> Loaded = new Dictionary<string, List<IMcpExtension>>();
        private static int _reloads;
        private static readonly List<string> Events = new List<string>();
        public static string Dir { get { return Path.Combine(Paths.BepInExRootPath, "hubner-ext"); } }
        public static int Reloads { get { return Volatile.Read(ref _reloads); } }

        public static void Start()
        {
            Directory.CreateDirectory(Dir);
            _stop = false;
            _thread = new Thread(Poll) { IsBackground = true, Name = "valheimmcp-ext-poll" };
            _thread.Start();
        }

        public static void Stop() { _stop = true; }

        private static void Note(string s)
        {
            Plugin.Log?.LogInfo("[ValheimMCP] " + s);
            lock (Events) { Events.Add(DateTime.Now.ToString("HH:mm:ss ") + s); if (Events.Count > 40) Events.RemoveAt(0); }
        }

        /// <summary>Plugin shutdown (main thread): give every extension its Unload so Harmony patches and cameras do not outlive the plugin.</summary>
        public static void UnloadAll()
        {
            foreach (var list in Loaded.Values)
                foreach (var ext in list)
                {
                    try { ext.Unload(); } catch (Exception ex) { Plugin.Log?.LogWarning("[ValheimMCP] extension Unload threw: " + ex.Message); }
                    Tools.RemoveOwner(ext.Name);
                    MainThreadDispatcher.Retire(ext.GetType().Assembly);
                }
            Loaded.Clear();
        }

        /// <summary>The ext_status tool: loaded extensions, reload count, recent load events, queue drops, watchdog reports.</summary>
        public static string StatusJson()
        {
            var sb = new StringBuilder("{\"core\":" + Json.Str(PluginInfo.Version) + ",\"build\":" + Json.Str(BuildStamp.Of(typeof(ExtensionHost).Assembly)) +
                ",\"reloads\":" + Reloads + ",\"reloadAdvice\":" + Json.Str(Reloads >= WarnReloads ? "restart the game: every reload stays in memory (Mono cannot unload assemblies)" : "") +
                ",\"droppedCalls\":" + MainThreadDispatcher.DroppedCount + ",\"mainThreadStallReports\":" + MainWatch.Reports + ",\"extensions\":[");
            var first = true;
            foreach (var kv in Loaded)
                foreach (var ext in kv.Value)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"file\":" + Json.Str(Path.GetFileName(kv.Key)) + ",\"name\":" + Json.Str(ext.Name) + ",\"tools\":" + Tools.All().Count(t => t.Owner == ext.Name) +
                        ",\"build\":" + Json.Str(BuildStamp.Of(ext.GetType().Assembly)) + ",\"loadedAt\":" + Json.Str(Seen.TryGetValue(kv.Key, out var t) ? t.ToLocalTime().ToString("HH:mm:ss") : "") + "}");
                }
            sb.Append("],\"events\":");
            lock (Events) sb.Append(Json.Array(Events));
            return sb.Append('}').ToString();
        }

        private static void Poll()
        {
            var coreName = typeof(ExtensionHost).Assembly.GetName().Name;
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
                        Assembly asm;
                        try
                        {
                            asm = Assembly.Load(File.ReadAllBytes(path));
                            var missing = BindCheck.UnboundCoreRefs(asm, coreName, 5);
                            if (missing.Count > 0)
                                throw new InvalidOperationException("refused, the loaded extension stays: it uses core members this running ValheimMCP " + PluginInfo.Version + " lacks: " + string.Join("; ", missing.ToArray()) + ". Restart the game to load the newer core.");
                        }
                        catch (Exception ex) { Seen[f] = t; Note("extension load failed (" + Path.GetFileName(f) + "): " + ex.Message); Plugin.Log?.LogError("[ValheimMCP] extension load failed (fix the DLL and push again): " + ex.Message); continue; }

                        var done = MainThreadDispatcher.RunBlocking<bool>(() => { Load(path, asm); return true; }, 20000, out _, out var err);
                        if (err != null) { Seen[f] = t; Note("extension load failed (" + Path.GetFileName(f) + "): " + err.Message); Plugin.Log?.LogError("[ValheimMCP] extension load failed (fix the DLL and push again): " + err); }
                        else if (done) Seen[f] = t;      // timed out (game not ticking): retry next poll
                    }
                }
                catch (Exception ex) { Plugin.Log?.LogError("[ValheimMCP] extension poll: " + ex.Message); }
                Thread.Sleep(2000);
            }
        }

        /// <summary>The IMcpExtension types of an assembly: its exported types first (an extension's entry class is public), all types only when none of those is one.
        /// Assembly.GetTypes loads every type, which is slow for a large extension on every reload.</summary>
        internal static List<Type> ExtensionTypes(Assembly asm)
        {
            Func<Type, bool> isExt = t => !t.IsAbstract && typeof(IMcpExtension).IsAssignableFrom(t);
            var res = asm.GetExportedTypes().Where(isExt).ToList();
            return res.Count > 0 ? res : asm.GetTypes().Where(isExt).ToList();
        }

        private static void Load(string path, Assembly asm)
        {
            if (Loaded.TryGetValue(path, out var olds))
            {
                foreach (var old in olds)
                {
                    try { old.Unload(); } catch (Exception ex) { Plugin.Log?.LogWarning("[ValheimMCP] extension Unload threw: " + ex.Message); }
                    Tools.RemoveOwner(old.Name);
                    MainThreadDispatcher.Retire(old.GetType().Assembly);      // its queued calls are dropped, never run on the old code
                }
                Loaded.Remove(path);
            }
            var mine = new List<IMcpExtension>();
            Loaded[path] = mine;
            foreach (var type in ExtensionTypes(asm))
            {
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
                    MainThreadDispatcher.Retire(asm);
                    mine.Remove(ext);
                    throw;
                }
                var n = Interlocked.Increment(ref _reloads);
                Note("extension loaded: " + ext.Name + " (" + Path.GetFileName(path) + ", build " + BuildStamp.Of(asm) + ", load #" + n + ")");
                if (n == WarnReloads) Plugin.Log?.LogWarning("[ValheimMCP] " + n + " extension loads in this process: Mono cannot unload assemblies, so memory keeps growing. Restart the game soon.");
            }
            if (mine.Count == 0) Plugin.Log?.LogWarning("[ValheimMCP] no IMcpExtension in " + path);
        }
    }
}
