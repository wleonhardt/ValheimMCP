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

    /// <summary>JSON helpers for extensions (the core's own writer is internal).</summary>
    public static class McpJson
    {
        public static string Str(string s) { return Json.Str(s); }
        public static string Num(double d) { return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
        public static double Get(Dictionary<string, object> a, string key, double dflt)
        {
            return a != null && a.TryGetValue(key, out var v) && v is double d ? d : dflt;
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

        public void Add(string name, string description, string schemaJson, Func<Dictionary<string, object>, ToolOutput> handler, bool write = false)
        {
            Tools.Put(new ToolDef { Owner = _owner, Name = name, Description = description, SchemaJson = schemaJson, Handler = handler, Write = write });
        }

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
    }
}
