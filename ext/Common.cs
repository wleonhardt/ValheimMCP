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

        static readonly Dictionary<int, bool> _living = new Dictionary<int, bool>();
        /// <summary>Creatures (tamed or wild), ships, carts, fish, dropped items and players: never adopted, never cleared as "overlap", deleted only with force. Decided by the prefab's components, not its name.</summary>
        public static bool IsLiving(int prefabHash)
        {
            if (_living.TryGetValue(prefabHash, out var v)) return v;
            bool r = false;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null) r = go.GetComponent<Character>() != null || go.GetComponent<Tameable>() != null || go.GetComponent<Ship>() != null || go.GetComponent<Vagon>() != null || go.GetComponent<Fish>() != null || go.GetComponent<ItemDrop>() != null || go.GetComponent<Player>() != null;
                else return false;                                   // prefab table not ready: do not cache a guess
            }
            catch (Exception ex) { Once("IsLiving", ex); }
            _living[prefabHash] = r; return r;
        }
        public static bool IsLiving(string prefab) { return !string.IsNullOrEmpty(prefab) && (prefab == "Player" || IsLiving(prefab.GetStableHashCode())); }

        static readonly HashSet<string> _logged = new HashSet<string>();
        /// <summary>Log a swallowed exception once per call site, so a game update that breaks a reflective lookup shows in the log instead of as a silently wrong answer.</summary>
        public static void Once(string site, Exception ex)
        {
            if (!_logged.Add(site)) return;
            try { Plugin.Log?.LogWarning("[hubner-ext] " + site + ": " + ex.GetType().Name + ": " + ex.Message + " (logged once)"); } catch { }
        }

        public static string N(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }
        public static string S(string s) { return McpJson.Str(s); }
        public static string V(Vector3 v) { return "[" + N(v.x) + "," + N(v.y) + "," + N(v.z) + "]"; }
        public static ToolOutput Json(string s) { return ToolOutput.Ok(s); }
        public static double D(Dictionary<string, object> a, string k, double d) { return McpJson.Get(a, k, d); }
        public static bool Has(Dictionary<string, object> a, string k) { return a != null && a.ContainsKey(k); }
        /// <summary>[x,y,z] (or [x,z]) number list to a Vector3; NaN components for anything that is not a number.</summary>
        public static Vector3 Vec(List<object> l)
        {
            if (l == null) return new Vector3(float.NaN, float.NaN, float.NaN);
            if (l.Count == 2) return new Vector3((float)McpJson.At(l, 0), float.NaN, (float)McpJson.At(l, 1));
            return new Vector3((float)McpJson.At(l, 0), (float)McpJson.At(l, 1), (float)McpJson.At(l, 2));
        }

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
        public Dictionary<string, int> Ints; public Dictionary<string, string> Strings; public Dictionary<string, float> Floats;   // v3: extra ZDO fields a modify changed (door state, fuel ...), so undo restores them
        public long Seq;
    }

    /// <summary>Persistent write journal: every entry is appended to hubner-ext/journal.jsonl (full state, so it can be undone after a hot reload or restart);
    /// undo appends {"undone":seq}. Entries reload on startup; the file is compacted when the undone markers outnumber the live entries.
    /// Begin()/End() batch the appends of one tool call into a single file write (a plan of 3,000 pieces used to be 3,000 writes on the main thread).</summary>
    internal static class Journal
    {
        public static readonly List<JEntry> Entries = new List<JEntry>();
        private static long _seq;
        private static bool _loaded;
        private static StringBuilder _batch;
        private static string Dir { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext"); } }
        private static string File_ { get { return Path.Combine(Dir, "journal.jsonl"); } }

        private static void EnsureLoaded()
        {
            if (_loaded) return; _loaded = true;
            try
            {
                if (!File.Exists(File_)) return;
                var byseq = new Dictionary<long, JEntry>(); var order = new List<long>(); int undone = 0;
                foreach (var line in File.ReadAllLines(File_))
                {
                    var d = FlatJson.Parse(line); if (d == null) continue;
                    if (d.ContainsKey("undone")) { byseq.Remove((long)FlatJson.N(d, "undone")); undone++; continue; }
                    var e = new JEntry
                    {
                        Seq = (long)FlatJson.N(d, "seq"), Time = FlatJson.S(d, "t"), Kind = FlatJson.S(d, "kind"), Id = FlatJson.S(d, "id"), Prefab = FlatJson.S(d, "prefab"),
                        Text = FlatJson.S(d, "text"), Tag = FlatJson.S(d, "tag"), Plan = FlatJson.S(d, "plan"), Key = FlatJson.S(d, "key"), Group = FlatJson.S(d, "group"), Note = FlatJson.S(d, "note"),
                        Creator = (long)FlatJson.N(d, "creator"), Scale = Vector3.one
                    };
                    var p = FlatJson.A(d, "pos"); if (p != null && p.Length >= 3) e.Pos = new Vector3(p[0], p[1], p[2]);
                    var q = FlatJson.A(d, "rot"); if (q != null && q.Length >= 4) e.Rot = new Quaternion(q[0], q[1], q[2], q[3]); else e.Rot = Quaternion.identity;
                    var ints = FlatJson.O(d, "ints"); if (ints != null) { e.Ints = new Dictionary<string, int>(); foreach (var kv in ints) if (kv.Value is double dv) e.Ints[kv.Key] = (int)dv; }
                    var fl = FlatJson.O(d, "floats"); if (fl != null) { e.Floats = new Dictionary<string, float>(); foreach (var kv in fl) if (kv.Value is double dv) e.Floats[kv.Key] = (float)dv; }
                    var st = FlatJson.O(d, "strings"); if (st != null) { e.Strings = new Dictionary<string, string>(); foreach (var kv in st) if (kv.Value is string sv) e.Strings[kv.Key] = sv; }
                    byseq[e.Seq] = e; order.Add(e.Seq); _seq = Math.Max(_seq, e.Seq);
                }
                foreach (var s in order) if (byseq.TryGetValue(s, out var en)) { Entries.Add(en); byseq.Remove(s); }
                if (undone > 1000 && undone > Entries.Count) Compact();
            }
            catch (Exception ex) { U.Once("Journal.load", ex); }
        }

        /// <summary>Rewrite the file with only the live entries (drops the undone markers and the entries they cancel).</summary>
        public static void Compact()
        {
            try
            {
                var sb = new StringBuilder(); foreach (var e in Entries) sb.Append(Line(e));
                File.WriteAllText(File_ + ".tmp", sb.ToString()); File.Copy(File_ + ".tmp", File_, true); File.Delete(File_ + ".tmp");
            }
            catch (Exception ex) { U.Once("Journal.compact", ex); }
        }

        public static void Load() { EnsureLoaded(); }

        /// <summary>Start batching appends (one file write at End). Nested calls are fine.</summary>
        static int _depth;
        public static void Begin() { EnsureLoaded(); if (_depth++ == 0) _batch = new StringBuilder(); }
        public static void End()
        {
            if (--_depth > 0) return;
            _depth = 0; var b = _batch; _batch = null;
            if (b != null && b.Length > 0) Write(b.ToString());
        }

        static string Line(JEntry e)
        {
            var sb = new StringBuilder();
            sb.Append("{\"seq\":" + e.Seq + ",\"t\":" + U.S(e.Time) + ",\"kind\":" + U.S(e.Kind) + ",\"id\":" + U.S(e.Id) + ",\"prefab\":" + U.S(e.Prefab) + ",\"pos\":" + U.V(e.Pos)
                + ",\"rot\":[" + U.N(e.Rot.x) + "," + U.N(e.Rot.y) + "," + U.N(e.Rot.z) + "," + U.N(e.Rot.w) + "],\"text\":" + U.S(e.Text ?? "") + ",\"tag\":" + U.S(e.Tag ?? "") + ",\"plan\":" + U.S(e.Plan ?? "")
                + ",\"key\":" + U.S(e.Key ?? "") + ",\"creator\":" + e.Creator + ",\"group\":" + U.S(e.Group ?? "") + ",\"note\":" + U.S(e.Note ?? ""));
            if (e.Ints != null && e.Ints.Count > 0) { sb.Append(",\"ints\":{"); bool f = true; foreach (var kv in e.Ints) { if (!f) sb.Append(','); f = false; sb.Append(U.S(kv.Key) + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)); } sb.Append('}'); }
            if (e.Floats != null && e.Floats.Count > 0) { sb.Append(",\"floats\":{"); bool f = true; foreach (var kv in e.Floats) { if (!f) sb.Append(','); f = false; sb.Append(U.S(kv.Key) + ":" + U.N(kv.Value)); } sb.Append('}'); }
            if (e.Strings != null && e.Strings.Count > 0) { sb.Append(",\"strings\":{"); bool f = true; foreach (var kv in e.Strings) { if (!f) sb.Append(','); f = false; sb.Append(U.S(kv.Key) + ":" + U.S(kv.Value)); } sb.Append('}'); }
            return sb.Append("}\n").ToString();
        }

        static void Write(string text)
        {
            try { Directory.CreateDirectory(Dir); File.AppendAllText(File_, text); }
            catch (Exception ex) { U.Once("Journal.write", ex); }
        }

        public static void Add(JEntry e)
        {
            EnsureLoaded();
            e.Seq = ++_seq; e.Time = DateTime.UtcNow.ToString("o");
            Entries.Add(e);
            var line = Line(e);
            if (_batch != null) _batch.Append(line); else Write(line);
        }

        /// <summary>Mark an entry undone: removed from memory and an {"undone":seq} marker appended (so a restart does not replay it).</summary>
        public static void Remove(JEntry e)
        {
            for (int i = Entries.Count - 1; i >= 0; i--) if (ReferenceEquals(Entries[i], e)) { Entries.RemoveAt(i); break; }     // newest first: undo takes from the end
            var line = "{\"undone\":" + e.Seq + "}\n";
            if (_batch != null) _batch.Append(line); else Write(line);
        }
        public static void Flush() { }
    }
}
