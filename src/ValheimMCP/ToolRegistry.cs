using System;
using System.Collections.Generic;
using System.Text;

namespace ValheimMCP
{
    /// <summary>Result of an extension tool call: text, optional PNG, error flag.</summary>
    public sealed class ToolOutput
    {
        public string Text;
        public byte[] Png;
        public bool IsError;
        public static ToolOutput Ok(string text) { return new ToolOutput { Text = text }; }
        public static ToolOutput Err(string text) { return new ToolOutput { Text = text, IsError = true }; }
        public static ToolOutput Image(byte[] png, string caption = null) { return new ToolOutput { Png = png, Text = caption }; }
    }

    /// <summary>JSON helpers for extensions (the core's own writer and parser are internal).</summary>
    public static class McpJson
    {
        public static string Str(string s) { return Json.Str(s); }
        public static string Num(double d) { return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
        /// <summary>Parse JSON text: objects -> Dictionary, arrays -> List, numbers -> double. Throws FormatException.</summary>
        public static object Parse(string text) { return MiniJson.Parse(text); }
        /// <summary>Config scalar from valheimmcp.yml by dotted path (e.g. "hubner.writeFlagMinutes").</summary>
        public static string Setting(string path, string dflt) { return ModConfig.Setting(path, dflt); }
        public static int SettingInt(string path, int dflt) { return ModConfig.SettingInt(path, dflt); }
        public static List<string> SettingList(string path) { return ModConfig.SettingList(path); }
        /// <summary>A number argument. Accepts JSON numbers and numeric strings ("12.5"); anything else yields the default.</summary>
        public static double Get(Dictionary<string, object> a, string key, double dflt)
        {
            if (a == null || !a.TryGetValue(key, out var v) || v == null) return dflt;
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
            if (v is bool b) return b ? 1 : 0;
            return dflt;
        }
        /// <summary>Element i of a JSON number list as a double, or NaN when it is not a number: callers get a clean error instead of an InvalidCastException.</summary>
        public static double At(List<object> l, int i)
        {
            if (l == null || i >= l.Count) return double.NaN;
            var v = l[i];
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
            return double.NaN;
        }
        public static string GetStr(Dictionary<string, object> a, string key, string dflt = null)
        {
            return a != null && a.TryGetValue(key, out var v) && v is string s ? s : dflt;
        }
        public static bool GetBool(Dictionary<string, object> a, string key, bool dflt)
        {
            return a != null && a.TryGetValue(key, out var v) && v is bool b ? b : dflt;
        }
        public static List<object> GetList(Dictionary<string, object> a, string key)
        {
            return a != null && a.TryGetValue(key, out var v) ? v as List<object> : null;
        }
        public static Dictionary<string, object> GetObj(Dictionary<string, object> a, string key)
        {
            return a != null && a.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        }
    }

    /// <summary>One MCP tool contributed by an extension. Handlers run on the Unity main thread.</summary>
    public sealed class ToolDef
    {
        public string Owner;
        public string Name;
        public string Description;
        public string SchemaJson;
        public bool Write;
        /// <summary>read | ops | build | control | misc: what tools.profile filters tools/list by (every tool stays callable).</summary>
        public string Category = "misc";
        public Func<Dictionary<string, object>, ToolOutput> Handler;
    }

    /// <summary>An extension DLL implements this; ValheimMCP loads it from BepInEx/hubner-ext and hot-reloads on change.</summary>
    public interface IMcpExtension
    {
        string Name { get; }
        void Register(ToolRegistry registry);
        /// <summary>Called before the extension is replaced or unloaded.</summary>
        void Unload();
    }

    public sealed class ToolRegistry
    {
        private readonly string _owner;
        internal ToolRegistry(string owner) { _owner = owner; }

        public void Add(string name, string description, string schemaJson, Func<Dictionary<string, object>, ToolOutput> handler, bool write = false, string category = null)
        {
            Tools.Put(new ToolDef { Owner = _owner, Name = name, Description = description, SchemaJson = schemaJson, Handler = handler, Write = write, Category = category ?? (write ? "build" : "misc") });
        }

        /// <summary>Set the category of already registered tools in one go (an extension keeps one table instead of tagging every Add).</summary>
        public void SetCategories(Dictionary<string, string> byName) { foreach (var kv in byName) Tools.SetCategory(kv.Key, kv.Value); }

        public static bool WritesEnabled { get { return ModConfig.ToolsWrite; } }
    }

    internal static class Tools
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, ToolDef> Map = new Dictionary<string, ToolDef>();

        public static void Put(ToolDef t) { lock (Lock) Map[t.Name] = t; }
        public static void RemoveOwner(string owner)
        {
            lock (Lock)
            {
                var gone = new List<string>();
                foreach (var kv in Map) if (kv.Value.Owner == owner) gone.Add(kv.Key);
                foreach (var k in gone) Map.Remove(k);
            }
        }
        public static ToolDef Get(string name) { lock (Lock) return Map.TryGetValue(name, out var t) ? t : null; }
        public static List<ToolDef> All() { lock (Lock) return new List<ToolDef>(Map.Values); }
        public static void SetCategory(string name, string category) { lock (Lock) if (Map.TryGetValue(name, out var t)) t.Category = category; }

        public static HashSet<string> ProfileCategories(string profile) { return ToolProfiles.Categories(profile); }
        public static string Terse(string d) { return ToolProfiles.Terse(d); }
    }
}
