using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    // ------------------------------------------------------------------ helpers
    internal static class U
    {
        /// <summary>Prefabs that edit terrain or the terrain data itself. Never spawned, modified or deleted by these tools (owner rule: no land editing).</summary>
        private static readonly string[] Forbidden = { "digg", "raise", "terrain", "cultivat", "mud_road", "paved_road", "path_v2", "replant", "levelground", "level_ground", "flatten", "TerrainOp" };
        public static bool IsForbidden(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            if (prefab[0] == '_') return true;                       // _TerrainCompiler, _ZoneCtrl, ...
            foreach (var f in Forbidden) if (prefab.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static string N(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }
        public static string S(string s) { return McpJson.Str(s); }
        public static string V(Vector3 v) { return "[" + N(v.x) + "," + N(v.y) + "," + N(v.z) + "]"; }
        public static ToolOutput Json(string s) { return ToolOutput.Ok(s); }
        public static double D(Dictionary<string, object> a, string k, double d) { return McpJson.Get(a, k, d); }
        public static bool Has(Dictionary<string, object> a, string k) { return a != null && a.ContainsKey(k); }

        public static string Id(ZDO z) { return z.m_uid.ID.ToString(CultureInfo.InvariantCulture) + ":" + z.m_uid.UserID.ToString(CultureInfo.InvariantCulture); }

        public static ZDO FindZdo(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var p = id.Split(':');
            if (p.Length != 2) return null;
            if (!uint.TryParse(p[0], out var idn) || !long.TryParse(p[1], out var user)) return null;
            return ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(new ZDOID(user, idn));
        }

        private static FieldInfo _inst;
        public static IEnumerable<ZNetView> Instances()
        {
            if (ZNetScene.instance == null) yield break;
            if (_inst == null) _inst = typeof(ZNetScene).GetField("m_instances", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = _inst.GetValue(ZNetScene.instance) as IDictionary;
            if (dict == null) yield break;
            var list = new List<ZNetView>();
            foreach (var v in dict.Values) list.Add((ZNetView)v);
            foreach (var nv in list) if (nv != null && nv.GetZDO() != null) yield return nv;
        }

        public static string PrefabName(ZNetView nv)
        {
            var n = nv.gameObject.name;
            var i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return i > 0 ? n.Substring(0, i) : n;
        }

        public static Bounds? ColliderBounds(GameObject go)
        {
            Bounds? b = null;
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (b == null) b = c.bounds; else { var bb = b.Value; bb.Encapsulate(c.bounds); b = bb; }
            }
            return b;
        }

        public static bool MatchPrefab(string name, string[] filters)
        {
            if (filters == null || filters.Length == 0) return true;
            foreach (var f in filters) if (name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static string[] Split(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        public static string Row(ZNetView nv, bool bounds)
        {
            var z = nv.GetZDO();
            var t = nv.transform;
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(S(Id(z)))
              .Append(",\"prefab\":").Append(S(PrefabName(nv)))
              .Append(",\"pos\":").Append(V(t.position))
              .Append(",\"rot\":").Append(V(t.rotation.eulerAngles))
              .Append(",\"scale\":").Append(V(t.localScale))
              .Append(",\"creator\":").Append(z.GetLong("creator", 0L).ToString(CultureInfo.InvariantCulture));
            var txt = z.GetString("text", "");
            if (!string.IsNullOrEmpty(txt)) sb.Append(",\"text\":").Append(S(txt));
            if (bounds)
            {
                var b = ColliderBounds(nv.gameObject);
                if (b != null) sb.Append(",\"bounds\":{\"c\":").Append(V(b.Value.center)).Append(",\"s\":").Append(V(b.Value.size)).Append("}");
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static bool InBox(Vector3 p, Dictionary<string, object> a)
        {
            if (Has(a, "x0") && Has(a, "x1") && Has(a, "z0") && Has(a, "z1"))
            {
                var x0 = (float)Math.Min(D(a, "x0", 0), D(a, "x1", 0)); var x1 = (float)Math.Max(D(a, "x0", 0), D(a, "x1", 0));
                var z0 = (float)Math.Min(D(a, "z0", 0), D(a, "z1", 0)); var z1 = (float)Math.Max(D(a, "z0", 0), D(a, "z1", 0));
                if (p.x < x0 || p.x > x1 || p.z < z0 || p.z > z1) return false;
                if (Has(a, "y0") && p.y < D(a, "y0", -1e9)) return false;
                if (Has(a, "y1") && p.y > D(a, "y1", 1e9)) return false;
                return true;
            }
            if (Has(a, "x") && Has(a, "z"))
            {
                var r = (float)D(a, "radius", 20);
                var dx = p.x - (float)D(a, "x", 0); var dz = p.z - (float)D(a, "z", 0);
                if (dx * dx + dz * dz > r * r) return false;
                if (Has(a, "y") && Math.Abs(p.y - D(a, "y", 0)) > r) return false;
                return true;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ journal
    internal sealed class JEntry
    {
        public string Kind; // spawn | delete | modify
        public string Id;
        public string Prefab; public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public string Text; public long Creator;
        public string Note;
        public string Tag, Plan, Key, Group, Time;      // v2: plan identity, journal group, UTC time
        public long Seq;
    }

    /// <summary>Persistent write journal: every entry is appended to hubner-ext/journal.jsonl (full state, so it can be undone after a hot reload or restart);
    /// undo appends {"undone":seq}. Entries reload on startup.</summary>
    internal static class Journal
    {
        public static readonly List<JEntry> Entries = new List<JEntry>();
        private static long _seq;
        private static bool _loaded;
        private static string Dir { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext"); } }
        private static string File_ { get { return Path.Combine(Dir, "journal.jsonl"); } }

        private static void EnsureLoaded()
        {
            if (_loaded) return; _loaded = true;
            try
            {
                if (!File.Exists(File_)) return;
                var byseq = new Dictionary<long, JEntry>(); var order = new List<long>();
                foreach (var line in File.ReadAllLines(File_))
                {
                    var d = FlatJson.Parse(line); if (d == null) continue;
                    if (d.ContainsKey("undone")) { byseq.Remove((long)FlatJson.N(d, "undone")); continue; }
                    var e = new JEntry
                    {
                        Seq = (long)FlatJson.N(d, "seq"), Time = FlatJson.S(d, "t"), Kind = FlatJson.S(d, "kind"), Id = FlatJson.S(d, "id"), Prefab = FlatJson.S(d, "prefab"),
                        Text = FlatJson.S(d, "text"), Tag = FlatJson.S(d, "tag"), Plan = FlatJson.S(d, "plan"), Key = FlatJson.S(d, "key"), Group = FlatJson.S(d, "group"), Note = FlatJson.S(d, "note"),
                        Creator = (long)FlatJson.N(d, "creator"), Scale = Vector3.one
                    };
                    var p = FlatJson.A(d, "pos"); if (p != null && p.Length >= 3) e.Pos = new Vector3(p[0], p[1], p[2]);
                    var q = FlatJson.A(d, "rot"); if (q != null && q.Length >= 4) e.Rot = new Quaternion(q[0], q[1], q[2], q[3]); else e.Rot = Quaternion.identity;
                    byseq[e.Seq] = e; order.Add(e.Seq); _seq = Math.Max(_seq, e.Seq);
                }
                foreach (var s in order) if (byseq.TryGetValue(s, out var en)) { Entries.Add(en); byseq.Remove(s); }
            }
            catch { }
        }

        public static void Load() { EnsureLoaded(); }

        public static void Add(JEntry e)
        {
            EnsureLoaded();
            e.Seq = ++_seq; e.Time = DateTime.UtcNow.ToString("o");
            Entries.Add(e);
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.Append("{\"seq\":" + e.Seq + ",\"t\":" + U.S(e.Time) + ",\"kind\":" + U.S(e.Kind) + ",\"id\":" + U.S(e.Id) + ",\"prefab\":" + U.S(e.Prefab) + ",\"pos\":" + U.V(e.Pos)
                    + ",\"rot\":[" + U.N(e.Rot.x) + "," + U.N(e.Rot.y) + "," + U.N(e.Rot.z) + "," + U.N(e.Rot.w) + "],\"text\":" + U.S(e.Text ?? "") + ",\"tag\":" + U.S(e.Tag ?? "") + ",\"plan\":" + U.S(e.Plan ?? "")
                    + ",\"key\":" + U.S(e.Key ?? "") + ",\"creator\":" + e.Creator + ",\"group\":" + U.S(e.Group ?? "") + ",\"note\":" + U.S(e.Note ?? "") + "}\n");
                File.AppendAllText(File_, sb.ToString());
            }
            catch { }
        }

        public static void Remove(JEntry e)
        {
            Entries.Remove(e);
            try { File.AppendAllText(File_, "{\"undone\":" + e.Seq + "}\n"); } catch { }
        }
        public static void Flush() { }
    }

    /// <summary>Parses the flat one-line objects the journal writes (strings, numbers, number arrays).</summary>
    internal static class FlatJson
    {
        public static Dictionary<string, object> Parse(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '{') return null;
            var d = new Dictionary<string, object>(); int i = 1;
            try
            {
                while (i < s.Length)
                {
                    while (i < s.Length && (s[i] == ',' || s[i] == ' ')) i++;
                    if (i >= s.Length || s[i] == '}') break;
                    var k = Str(s, ref i); i++;                                    // skip ':'
                    object v;
                    if (s[i] == '"') v = Str(s, ref i);
                    else if (s[i] == '[') { i++; var l = new List<float>(); while (s[i] != ']') { if (s[i] == ',') { i++; continue; } int j = i; while (s[j] != ',' && s[j] != ']') j++; l.Add(float.Parse(s.Substring(i, j - i), CultureInfo.InvariantCulture)); i = j; } i++; v = l.ToArray(); }
                    else { int j = i; while (s[j] != ',' && s[j] != '}') j++; v = double.Parse(s.Substring(i, j - i), CultureInfo.InvariantCulture); i = j; }
                    d[k] = v;
                }
            }
            catch { return null; }
            return d;
        }
        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder(); i++;
            while (s[i] != '"')
            {
                if (s[i] == '\\') { i++; char c = s[i]; if (c == 'n') sb.Append('\n'); else if (c == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; } else sb.Append(c); }
                else sb.Append(s[i]);
                i++;
            }
            i++; return sb.ToString();
        }
        public static string S(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out var v) ? v as string : null; }
        public static double N(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out var v) && v is double x ? x : 0; }
        public static float[] A(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out var v) ? v as float[] : null; }
    }
}
