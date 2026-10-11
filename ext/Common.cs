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
    /// <summary>Config access that survives an extension newer than the loaded core (the extension hot-reloads, the core only on restart): a missing
    /// McpJson.Setting* on an old core yields the default instead of a MissingMethodException in every write tool.</summary>
    internal static class Cfg
    {
        // resolved by reflection: a direct call would make Mono throw MissingMethodException while JIT-compiling the CALLER on an old core, outside any try/catch
        static readonly MethodInfo MInt = typeof(McpJson).GetMethod("SettingInt", new[] { typeof(string), typeof(int) }), MStr = typeof(McpJson).GetMethod("Setting", new[] { typeof(string), typeof(string) }), MList = typeof(McpJson).GetMethod("SettingList", new[] { typeof(string) });
        /// <summary>The build stamp of an assembly (commit, dirty, branch, worktree), through the core by reflection so an older core still loads the extension.</summary>
        public static string BuildStamp(System.Reflection.Assembly a)
        {
            try { var t = typeof(McpJson).Assembly.GetType("ValheimMCP.BuildStamp"); var m = t != null ? t.GetMethod("Of", new[] { typeof(System.Reflection.Assembly) }) : null; return m != null ? (string)m.Invoke(null, new object[] { a }) : "core without build stamps"; }
            catch (Exception) { return "unknown"; /* allow: a stamp that cannot be read is shown as unknown, never an error */ }
        }
        public static bool CoreHasSettings { get { return MInt != null; } }
        public static int Int(string key, int dflt) { try { return MInt != null ? (int)MInt.Invoke(null, new object[] { key, dflt }) : dflt; } catch { return dflt; } }
        public static string Str(string key, string dflt) { try { return MStr != null ? (string)MStr.Invoke(null, new object[] { key, dflt }) : dflt; } catch { return dflt; } }
        public static bool Bool(string key, bool dflt) { var v = Str(key, null); if (v == null) return dflt; v = v.Trim().ToLowerInvariant(); return v == "true" || v == "yes" || v == "on" ? true : (v == "false" || v == "no" || v == "off" ? false : dflt); }
        public static List<string> List(string key) { try { return MList != null ? (List<string>)MList.Invoke(null, new object[] { key }) : null; } catch { return null; } }
    }

    // ------------------------------------------------------------------ helpers
    internal static class U
    {
        /// <summary>Element i of a JSON number list, NaN when it is not a number (local copy: the core's McpJson.At needs core 0.4).</summary>
        public static double At(List<object> l, int i)
        {
            if (l == null || i >= l.Count) return double.NaN;
            var v = l[i];
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            return double.NaN;
        }

        /// <summary>Is this character one of hubner.sandboxNames? Only those get god mode from teleport/walk: the extension must not make someone's real character invulnerable.</summary>
        public static bool IsSandbox(string playerName)
        {
            var names = Cfg.List("hubner.sandboxNames");
            return names != null && !string.IsNullOrEmpty(playerName) && names.Contains(playerName);
        }
        public static bool IsSandbox(Player p) { return p != null && IsSandbox(p.GetPlayerName()); }

        /// <summary>Prefabs that edit terrain or the terrain data itself. Never spawned, modified or deleted by these tools (design rule: no terrain editing).</summary>
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
        public static string N5(double d) { return d.ToString("0.#####", CultureInfo.InvariantCulture); }          // a rotation component: 3 decimals leave a quaternion visibly off after an undo
        public static string S(string s) { return McpJson.Str(s); }
        public static string V(Vector3 v) { return "[" + N(v.x) + "," + N(v.y) + "," + N(v.z) + "]"; }
        public static ToolOutput Json(string s) { return ToolOutput.Ok(s); }
        public static double D(Dictionary<string, object> a, string k, double d) { return McpJson.Get(a, k, d); }
        public static bool Has(Dictionary<string, object> a, string k) { return a != null && a.ContainsKey(k); }
        /// <summary>[x,y,z] (or [x,z]) number list to a Vector3; NaN components for anything that is not a number.</summary>
        public static Vector3 Vec(List<object> l)
        {
            if (l == null) return new Vector3(float.NaN, float.NaN, float.NaN);
            if (l.Count == 2) return new Vector3((float)U.At(l, 0), float.NaN, (float)U.At(l, 1));
            return new Vector3((float)U.At(l, 0), (float)U.At(l, 1), (float)U.At(l, 2));
        }

        public static string Id(ZDO z) { return z.m_uid.ID.ToString(CultureInfo.InvariantCulture) + ":" + z.m_uid.UserID.ToString(CultureInfo.InvariantCulture); }

        public static ZDO FindZdo(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var p = id.Split(':');
            if (p.Length != 2) return null;
            if (!uint.TryParse(p[0], out var idn) || !long.TryParse(p[1], out var user)) return null;
            if (ZDOMan.instance == null) return null;
            // new ZDOID(user, id) registers the user in a static table indexed by a ushort: an id with a made-up user must not be allowed to fill it (a ZDO that exists has its user there already)
            if (_userIds == null) _userIds = typeof(ZDOID).GetField("m_userIDs", BindingFlags.Static | BindingFlags.NonPublic) ;
            var known = _userIds != null ? _userIds.GetValue(null) as List<long> : null;
            if (known != null && !known.Contains(user)) return null;
            return ZDOMan.instance.GetZDO(new ZDOID(user, idn));
        }
        private static FieldInfo _userIds;

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
        public string World;                   // v5: the world (name#uid) the entry was written in; ids, tags and positions mean nothing in another one
        public string Boot, IdPlan, IdKey;     // v4: the world load the Id belongs to, and the plan identity the object has (ZDO ids are renumbered at every load: the tags are what survives a restart)
    }

    /// <summary>
    ///     Container contents in the ZDO. Since the chunked save format the game keeps a container's inventory as a BYTE ARRAY under "items" (Container.Save / Load use GetByteArray);
    ///     only a world saved by an older version holds the base64 string, which ZDOMan converts at load. Reading the string alone sees every chest empty.
    /// </summary>
    internal static class ContainerData
    {
        public static byte[] Read(ZDO z)
        {
            var b = z.GetByteArray("items", null); if (b != null && b.Length > 0) return b;
            var s = z.GetString("items", ""); if (s.Length == 0) return null;
            try { return Convert.FromBase64String(s); } catch (FormatException) { return null; /* allow: a string that is not base64 is not an inventory */ }
        }
        public static bool Has(ZDO z) { return Read(z) != null; }
        public static ZPackage Package(ZDO z) { var b = Read(z); return b == null ? null : new ZPackage(b); }
        public static string ToBase64(ZDO z) { var b = Read(z); return b == null ? "" : Convert.ToBase64String(b); }
        public static void Write(ZDO z, ZPackage pkg) { z.Set("items", pkg.GetArray()); }
        public static void SetFromBase64(ZDO z, string b64) { z.Set("items", Convert.FromBase64String(b64)); }
    }

    /// <summary>Persistent write journal: every entry is appended to hubner-ext/journal.jsonl (full state, so it can be undone after a hot reload or restart);
    /// undo appends {"undone":seq}. Entries reload on startup; the file is compacted when the undone markers outnumber the live entries.
    /// Begin()/End() batch the appends of one tool call into a single file write (a plan of 3,000 pieces used to be 3,000 writes on the main thread).</summary>
    internal static class Journal
    {
        public static readonly List<JEntry> Entries = new List<JEntry>();
        /// <summary>Counts every change to the journal: lets callers (the pre-save list sanitiser) skip work when nothing was written.</summary>
        public static long Version;
        /// <summary>Id of the current world load (ZDOMan session). Entries from another load cannot be found by id.</summary>
        /// <summary>The world being played (name#uid); empty before one is loaded.</summary>
        public static string CurrentWorld() { try { var w = ZNet.World; return w != null ? WorldKey.Of(w.m_name, w.m_uid) : ""; } catch (Exception ex) { U.Once("Journal.CurrentWorld", ex); return ""; } }
        public static string CurrentBoot() { try { return ZDOMan.instance != null ? ZDOMan.GetSessionID().ToString(CultureInfo.InvariantCulture) : ""; } catch { return ""; } }
        /// <summary>Last file error (empty = fine). A journal that cannot be written means writes can no longer be undone after a restart: status and selftest show it.</summary>
        public static string LastError = "";
        public static int SkippedLines;
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
                    var d = FlatJson.Parse(line); if (d == null) { if (line.Trim().Length > 0) SkippedLines++; continue; }
                    if (d.ContainsKey("undone")) { byseq.Remove((long)FlatJson.N(d, "undone")); undone++; continue; }
                    if (d.ContainsKey("remap")) { var from = FlatJson.S(d, "remap"); var to = FlatJson.S(d, "to"); foreach (var pe in byseq.Values) if (pe.Id == from) pe.Id = to; continue; }
                    var e = new JEntry
                    {
                        Seq = (long)FlatJson.N(d, "seq"), Time = FlatJson.S(d, "t"), Kind = FlatJson.S(d, "kind"), Id = FlatJson.S(d, "id"), Prefab = FlatJson.S(d, "prefab"),
                        Text = FlatJson.S(d, "text"), Tag = FlatJson.S(d, "tag"), Plan = FlatJson.S(d, "plan"), Key = FlatJson.S(d, "key"), Group = FlatJson.S(d, "group"), Note = FlatJson.S(d, "note"),
                        Creator = (long)FlatJson.N(d, "creator"), Boot = FlatJson.S(d, "boot"), World = FlatJson.S(d, "world"), IdPlan = FlatJson.S(d, "ip"), IdKey = FlatJson.S(d, "ik"), Scale = Vector3.one
                    };
                    var p = FlatJson.A(d, "pos"); if (p != null && p.Length >= 3) e.Pos = new Vector3(p[0], p[1], p[2]);
                    var sc = FlatJson.A(d, "scale"); if (sc != null && sc.Length >= 3) e.Scale = new Vector3(sc[0], sc[1], sc[2]);
                    var q = FlatJson.A(d, "rot"); if (q != null && q.Length >= 4) e.Rot = new Quaternion(q[0], q[1], q[2], q[3]); else e.Rot = Quaternion.identity;
                    var ints = FlatJson.O(d, "ints"); if (ints != null) { e.Ints = new Dictionary<string, int>(); foreach (var kv in ints) if (kv.Value is double dv) e.Ints[kv.Key] = (int)dv; }
                    var fl = FlatJson.O(d, "floats"); if (fl != null) { e.Floats = new Dictionary<string, float>(); foreach (var kv in fl) if (kv.Value is double dv) e.Floats[kv.Key] = (float)dv; }
                    var st = FlatJson.O(d, "strings"); if (st != null) { e.Strings = new Dictionary<string, string>(); foreach (var kv in st) if (kv.Value is string sv) e.Strings[kv.Key] = sv; }
                    byseq[e.Seq] = e; order.Add(e.Seq); _seq = Math.Max(_seq, e.Seq);
                }
                foreach (var s in order) if (byseq.TryGetValue(s, out var en)) { Entries.Add(en); byseq.Remove(s); }
                if (SkippedLines > 0) Plugin.Log?.LogWarning("[hubner-ext] journal: " + SkippedLines + " unreadable line(s) skipped (a torn write after a crash?)");
                if (undone > 1000 && undone > Entries.Count) Compact();
            }
            catch (Exception ex) { LastError = "load: " + ex.Message; U.Once("Journal.load", ex); }
        }

        /// <summary>Rewrite the file with only the live entries (drops the undone markers and the entries they cancel).</summary>
        public static void Compact()
        {
            try
            {
                var sb = new StringBuilder(); foreach (var e in Entries) sb.Append(Line(e));
                var tmp = File_ + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(File_)) File.Replace(tmp, File_, null); else File.Move(tmp, File_);       // a crash leaves either the old or the new file, never half of one
                LastError = "";
            }
            catch (Exception ex) { LastError = "compact: " + ex.Message; U.Once("Journal.compact", ex); }
        }

        public static void Load() { EnsureLoaded(); }

        public static long FileBytes() { try { return File.Exists(File_) ? new FileInfo(File_).Length : 0; } catch { return 0; } }

        /// <summary>Drop entries older than keepDays (their undo is given up) and rewrite the file. Returns how many were dropped.</summary>
        public static int Prune(double keepDays, bool dry)
        {
            EnsureLoaded();
            var cutoff = DateTime.UtcNow.AddDays(-keepDays).ToString("o");
            int n = 0;
            for (int i = Entries.Count - 1; i >= 0; i--) if (string.CompareOrdinal(Entries[i].Time ?? "", cutoff) < 0) { n++; if (!dry) Entries.RemoveAt(i); }
            if (!dry) Compact();
            return n;
        }

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
                + ",\"rot\":[" + U.N5(e.Rot.x) + "," + U.N5(e.Rot.y) + "," + U.N5(e.Rot.z) + "," + U.N5(e.Rot.w) + "],\"text\":" + U.S(e.Text ?? "") + ",\"tag\":" + U.S(e.Tag ?? "") + ",\"plan\":" + U.S(e.Plan ?? "")
                + ",\"key\":" + U.S(e.Key ?? "") + ",\"creator\":" + e.Creator + ",\"group\":" + U.S(e.Group ?? "") + ",\"note\":" + U.S(e.Note ?? "")
                + ",\"boot\":" + U.S(e.Boot ?? "") + ",\"world\":" + U.S(e.World ?? "") + ",\"ip\":" + U.S(e.IdPlan ?? "") + ",\"ik\":" + U.S(e.IdKey ?? ""));
            if (e.Scale != Vector3.one && e.Scale != Vector3.zero) sb.Append(",\"scale\":" + U.V(e.Scale));
            if (e.Ints != null && e.Ints.Count > 0) { sb.Append(",\"ints\":{"); bool f = true; foreach (var kv in e.Ints) { if (!f) sb.Append(','); f = false; sb.Append(U.S(kv.Key) + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)); } sb.Append('}'); }
            if (e.Floats != null && e.Floats.Count > 0) { sb.Append(",\"floats\":{"); bool f = true; foreach (var kv in e.Floats) { if (!f) sb.Append(','); f = false; sb.Append(U.S(kv.Key) + ":" + U.N(kv.Value)); } sb.Append('}'); }
            if (e.Strings != null && e.Strings.Count > 0) { sb.Append(",\"strings\":{"); bool f = true; foreach (var kv in e.Strings) { if (!f) sb.Append(','); f = false; sb.Append(U.S(kv.Key) + ":" + U.S(kv.Value)); } sb.Append('}'); }
            return sb.Append("}\n").ToString();
        }

        static void Write(string text)
        {
            try { Directory.CreateDirectory(Dir); File.AppendAllText(File_, text); LastError = ""; }
            catch (Exception ex) { LastError = "write: " + ex.Message; U.Once("Journal.write", ex); }
        }

        public static void Add(JEntry e)
        {
            EnsureLoaded();
            e.Seq = ++_seq; e.Time = DateTime.UtcNow.ToString("o"); Version++;
            if (string.IsNullOrEmpty(e.Boot)) e.Boot = CurrentBoot();
            if (string.IsNullOrEmpty(e.World)) e.World = CurrentWorld();
            if (e.IdPlan == null && !string.IsNullOrEmpty(e.Id)) { var live = U.FindZdo(e.Id); if (live != null) { e.IdPlan = live.GetString(Plans.PlanHash, ""); e.IdKey = live.GetString(Plans.ItemHash, ""); } }
            Entries.Add(e);
            var line = Line(e);
            if (_batch != null) _batch.Append(line); else Write(line);
        }

        /// <summary>Mark an entry undone: removed from memory and an {"undone":seq} marker appended (so a restart does not replay it).</summary>
        public static void Remove(JEntry e)
        {
            for (int i = Entries.Count - 1; i >= 0; i--) if (ReferenceEquals(Entries[i], e)) { Entries.RemoveAt(i); break; }     // newest first: undo takes from the end
            Version++;
            var line = "{\"undone\":" + e.Seq + "}\n";
            if (_batch != null) _batch.Append(line); else Write(line);
        }

        /// <summary>Undoing a delete recreates the object under a new id: every other entry that names the old id must follow, or an older spawn/modify of the same object can no longer be undone.</summary>
        public static void Remap(string oldId, string newId)
        {
            if (string.IsNullOrEmpty(oldId) || oldId == newId) return;
            var any = false;
            foreach (var x in Entries) if (x.Id == oldId) { x.Id = newId; any = true; }
            if (!any) return;
            Version++;
            var line = "{\"remap\":" + U.S(oldId) + ",\"to\":" + U.S(newId) + "}\n";
            if (_batch != null) _batch.Append(line); else Write(line);
        }
    }
}
