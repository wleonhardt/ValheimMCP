// Integrity and observability tools shared by client and server twin: zdo_audit, selftest, status, log_tail, job queue.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Audit
    {
        static Harmony _h;
        static readonly DateTime _start = DateTime.UtcNow;

        public static void Register(ToolRegistry r)
        {
            r.Add("zdo_audit", "Integrity scan of the ZDO sector lists: objects with prefab hash 0 / unknown prefab, stale list entries (an object listed in a sector it is not in), objects listed twice, and groups of identical objects (same prefab, position, rotation) that are not part of a plan. box x0,z0,x1,z1 optional. fix=true removes stale/duplicate list entries and destroys invalid-prefab objects that carry a hub_plan tag (ours); never touches anything else.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"fix\":{\"type\":\"boolean\"},\"examples\":{\"type\":\"number\"},\"confirm\":{\"type\":\"string\"}}}", a => McpJson.GetBool(a, "fix", false) ? Plans.GuardPublic(a, AuditTool) : AuditTool(a), Plans.WriteFlag);     // a read-only scan needs no gate
            r.Add("selftest", "Run the extension's own health checks (ZDO table, journal, tools, hooks, control, sector integrity, write gate) and report each as ok/fail with detail. Cheap; run after a reload or when something behaves oddly.",
                "{\"type\":\"object\",\"properties\":{}}", SelfTest);
            r.Add("players", "Connected peers (server: every player): name, position, host/steam id, uid, and sandbox=true when the name is the Hubner sandbox character (Odev/MaRkO/ClaudeEyes). Use before a restart: a sandbox-only list means the restart can proceed after the sandbox client is closed.",
                "{\"type\":\"object\",\"properties\":{}}", PlayersTool);
            r.Add("status", "One-call overview: role, version, uptime, object and player counts, write gate state, journal size, save state, queued jobs, hook state.",
                "{\"type\":\"object\",\"properties\":{}}", Status);
            r.Add("chat_tail", "In-game chat captured since the extension loaded (client only): since (sequence number, default 0), limit (default 100). Each row: seq, time (UTC), type (Normal/Shout/Whisper/Ping/...), name, text. Poll with since=<last seq> to read only new messages.",
                "{\"type\":\"object\",\"properties\":{\"since\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}}}", ChatTail);
            r.Add("log_tail", "Last lines of the BepInEx log (exceptions, warnings) without ssh: lines (default 60, max 400), filter substring (case-insensitive), level (error|warning|all).",
                "{\"type\":\"object\",\"properties\":{\"lines\":{\"type\":\"number\"},\"filter\":{\"type\":\"string\"},\"level\":{\"type\":\"string\"}}}", LogTail);
            r.Add("job_start", "Queue one of the long write tools (plan_apply, plan_remove, zdo_delete, zdo_set, zdo_audit) to run on the next game frames instead of inside the HTTP call, so callers do not hit their own timeout. tool + args as for the tool itself. Returns a job id; poll job_status.",
                "{\"type\":\"object\",\"properties\":{\"tool\":{\"type\":\"string\"},\"args\":{\"type\":\"object\"}},\"required\":[\"tool\"]}", JobStart, Plans.WriteFlag);
            r.Add("job_status", "Status/result of a queued job (queued | running | done | error) or the list of recent jobs when no id is given.",
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"number\"}}}", JobStatus);
            JobTools["zdo_audit"] = x => McpJson.GetBool(x, "fix", false) ? Plans.GuardPublic(x, AuditTool) : AuditTool(x);
            Install();
        }

        // ------------------------------------------------------------------ hooks (jobs run on ZNetScene.Update, which exists on client and server)
        public static void Install()
        {
            if (_h != null) return;
            _h = new Harmony("hubner.ext.audit." + Guid.NewGuid().ToString("N"));
            HookError = "";
            Func<string, MethodBase, string, bool, bool> patch = (label, m, hook, prefix) =>
            {
                try
                {
                    if (m == null) { HookError += label + ": method not found; "; return false; }
                    var hm = new HarmonyMethod(typeof(Audit).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic));
                    if (prefix) _h.Patch(m, prefix: hm); else _h.Patch(m, postfix: hm);
                    return true;
                }
                catch (Exception ex) { HookError += label + ": " + ex.Message + "; "; return false; }
            };
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            patch("ZNetScene.Update", typeof(ZNetScene).GetMethod("Update", F), "SceneTick", false);
            patch("Chat.OnNewChatMessage", typeof(Chat).GetMethod("OnNewChatMessage", F), "ChatMsg", false);                                  // client: every message the player would see
            patch("ZRoutedRpc.HandleRoutedRPC", typeof(ZRoutedRpc).GetMethod("HandleRoutedRPC", F), "RoutedChat", true);                     // messages addressed to this node
            patch("ZRoutedRpc.RPC_RoutedRPC", typeof(ZRoutedRpc).GetMethod("RPC_RoutedRPC", F), "RelayedChat2", true);                       // server: every message a client sends, before it is relayed
            patch("ZNet.SaveWorld", typeof(ZNet).GetMethod("SaveWorld", F), "BeforeSave", true);                                              // every save, autosaves included
        }
        public static void Uninstall() { try { if (_h != null) _h.UnpatchSelf(); } catch { } _h = null; }
        internal static string HookError = "";
        static void BeforeSave() { try { SanitizeLists(); } catch { } }
        // ------------------------------------------------------------------ chat capture (client: Chat.OnNewChatMessage fires for every message the player would see)
        static readonly List<string> ChatRows = new List<string>(); static int _chatSeq;
        static void ChatMsg(object[] __args)
        {
            try
            {
                string text = null, name = "", type = "";
                foreach (var o in __args)
                {
                    if (o == null) continue;
                    if (o is string st) { text = st; continue; }
                    if (o is Talker.Type tt) { type = tt.ToString(); continue; }
                    var t = o.GetType();
                    if (t.Name == "UserInfo") name = UiName(o);
                }
                if (text == null) return;
                lock (ChatRows)
                {
                    ChatRows.Add("{\"seq\":" + (++_chatSeq) + ",\"time\":" + U.S(DateTime.UtcNow.ToString("HH:mm:ss")) + ",\"type\":" + U.S(type) + ",\"name\":" + U.S(name) + ",\"text\":" + U.S(text) + "}");
                    if (ChatRows.Count > 500) ChatRows.RemoveAt(0);
                }
            }
            catch { }
        }
        static string UiName(object ui)
        {
            try
            {
                var t = ui.GetType();
                var f = t.GetField("Name") ?? t.GetField("m_name"); if (f != null) return f.GetValue(ui) as string ?? "";
                var p = t.GetProperty("Name"); if (p != null) return p.GetValue(ui, null) as string ?? "";
                var m = t.GetMethod("GetName"); if (m != null) return m.Invoke(ui, null) as string ?? "";
            }
            catch { }
            return "";
        }
        static void RelayedChat(object __0)                                                   // __0 = ZRpc, the package is the second argument
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            }
            catch { return; }
        }
        static void RelayedChat2(object __0, object __1)
        {
            try
            {
                var pkg = __1 as ZPackage; if (pkg == null) return;
                var tp = typeof(ZRoutedRpc).GetNestedType("RoutedRPCData", BindingFlags.Public | BindingFlags.NonPublic); if (tp == null) return;
                var d = Activator.CreateInstance(tp); var copy = new ZPackage(pkg.GetArray()); copy.SetPos(0);
                var de = tp.GetMethod("Deserialize"); if (de == null) return; de.Invoke(d, new object[] { copy });
                RoutedChat(d);
            }
            catch { }
        }
        static void RoutedChat(object __0)
        {
            try
            {
                if (__0 == null) return;
                var t = __0.GetType();
                var hashF = t.GetField("m_methodHash"); var parF = t.GetField("m_parameters");
                if (hashF == null || parF == null) return;
                int hash = (int)hashF.GetValue(__0); bool say = hash == "Say".GetStableHashCode(), msg = hash == "ChatMessage".GetStableHashCode();
                if (!say && !msg) return;                                                       // Normal and Whisper travel as the Talker RPC "Say" on the speaker's object, Shout and Ping as the routed "ChatMessage"
                var src = (ZPackage)parF.GetValue(__0);
                var pkg = new ZPackage(src.GetArray()); pkg.SetPos(0);
                if (msg) pkg.ReadVector3();
                int type = pkg.ReadInt();
                var ui = new UserInfo(); ui.Deserialize(ref pkg);
                string text = pkg.ReadString();
                lock (ChatRows)
                {
                    ChatRows.Add("{\"seq\":" + (++_chatSeq) + ",\"time\":" + U.S(DateTime.UtcNow.ToString("HH:mm:ss")) + ",\"type\":" + U.S(((Talker.Type)type).ToString()) + ",\"name\":" + U.S(UiName(ui)) + ",\"text\":" + U.S(text) + "}");
                    if (ChatRows.Count > 500) ChatRows.RemoveAt(0);
                }
            }
            catch { }
        }
        static ToolOutput ChatTail(Dictionary<string, object> a)
        {
            int since = (int)McpJson.Get(a, "since", 0), limit = Math.Max(1, (int)McpJson.Get(a, "limit", 100));
            var rows = new List<string>();
            lock (ChatRows) { int first = _chatSeq - ChatRows.Count; for (int i = 0; i < ChatRows.Count; i++) if (first + i + 1 > since) rows.Add(ChatRows[i]); }
            if (rows.Count > limit) rows = rows.GetRange(rows.Count - limit, limit);
            return U.Json("{\"last\":" + _chatSeq + ",\"count\":" + rows.Count + ",\"messages\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static void SceneTick() { try { RunJobs(); } catch { } }

        // ------------------------------------------------------------------ sector helpers
        static List<ZDO>[] Lists()
        {
            var f = typeof(ZDOMan).GetField("m_objectsBySector", BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null || ZDOMan.instance == null ? null : f.GetValue(ZDOMan.instance) as List<ZDO>[];
        }

        public static int ExpectedSector(Vector3 p)
        {
            var z = ZoneSystem.GetZone(p);
            return (int)ZoneSystem.SectorToIndex(z.x, z.y).Sector;
        }

        /// <summary>After objects were moved: drop every stale or repeated list entry for them in ONE pass over the sector lists, so a world save writes each object once.</summary>
        public static int FixMoved(HashSet<ZDO> moved)
        {
            var arr = Lists(); if (arr == null || moved.Count == 0) return 0;
            int removed = 0; var seen = new HashSet<ZDO>();
            for (int i = 0; i < arr.Length; i++)
            {
                var l = arr[i]; if (l == null) continue;
                for (int k = l.Count - 1; k >= 0; k--)
                {
                    var z = l[k]; if (!moved.Contains(z)) continue;
                    if (ExpectedSector(z.GetPosition()) != i) { l.RemoveAt(k); removed++; continue; }
                    if (!seen.Add(z)) { l.RemoveAt(k); removed++; }
                }
            }
            return removed;
        }

        /// <summary>Non-destructive: drop list entries that sit in the wrong sector or appear twice (objects stay). Run before every world save so the save file never contains an object twice.</summary>
        public static int SanitizeLists()
        {
            var arr = Lists(); if (arr == null) return 0;
            int removed = 0; var seen = new HashSet<ZDO>();
            for (int i = 0; i < arr.Length; i++)
            {
                var l = arr[i]; if (l == null) continue;
                for (int k = l.Count - 1; k >= 0; k--)
                {
                    var z = l[k];
                    if (ExpectedSector(z.GetPosition()) != i || !seen.Add(z)) { l.RemoveAt(k); removed++; }
                }
            }
            return removed;
        }

        // ------------------------------------------------------------------ zdo_audit
        static ToolOutput AuditTool(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var arr = Lists(); if (arr == null) return ToolOutput.Err("sector lists not available");
            bool fix = McpJson.GetBool(a, "fix", false); int maxEx = (int)McpJson.Get(a, "examples", 8);
            bool box = U.Has(a, "x0"); float x0 = (float)U.D(a, "x0", -1e9), z0 = (float)U.D(a, "z0", -1e9), x1 = (float)U.D(a, "x1", 1e9), z1 = (float)U.D(a, "z1", 1e9);
            int total = 0, invalid = 0, stale = 0, dupRef = 0, removed = 0, destroyed = 0;
            var exInvalid = new List<string>(); var exStale = new List<string>(); var exDup = new List<string>();
            var seen = new HashSet<ZDO>(); var groups = new Dictionary<string, List<ZDO>>(); var toDestroy = new List<ZDO>();
            for (int i = 0; i < arr.Length; i++)
            {
                var l = arr[i]; if (l == null) continue;
                for (int k = l.Count - 1; k >= 0; k--)
                {
                    var z = l[k]; var p = z.GetPosition();
                    if (box && (p.x < x0 || p.x > x1 || p.z < z0 || p.z > z1)) continue;
                    total++;
                    var hash = z.GetPrefab(); var name = hash == 0 ? "#0" : Zdos.NameOf(hash);
                    if (hash == 0 || (name.Length > 0 && name[0] == '#'))
                    {
                        invalid++; if (exInvalid.Count < maxEx) exInvalid.Add(U.Id(z) + " " + name + " " + U.V(p) + " plan=" + z.GetString(Plans.PlanKey, ""));
                        if (fix && !string.IsNullOrEmpty(z.GetString(Plans.PlanKey, ""))) toDestroy.Add(z);
                    }
                    bool bad = false;
                    if (ExpectedSector(p) != i) { stale++; bad = true; if (exStale.Count < maxEx) exStale.Add(U.Id(z) + " " + name + " in list " + i + " expected " + ExpectedSector(p)); }
                    if (!seen.Add(z)) { dupRef++; bad = true; if (exDup.Count < maxEx) exDup.Add(U.Id(z) + " " + name + " listed twice"); }
                    if (bad && fix) { l.RemoveAt(k); removed++; continue; }
                    if (string.IsNullOrEmpty(z.GetString(Plans.PlanKey, "")) && z.m_uid.UserID != 1L && hash != 0)
                    {
                        var key = hash + "|" + Math.Round(p.x, 1) + "|" + Math.Round(p.y, 1) + "|" + Math.Round(p.z, 1) + "|" + Math.Round(z.GetRotation().eulerAngles.y);
                        if (!groups.TryGetValue(key, out var g)) groups[key] = g = new List<ZDO>(); g.Add(z);
                    }
                }
            }
            foreach (var z in toDestroy) { try { z.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(z); destroyed++; } catch { } }
            var identical = groups.Values.Where(g => g.Count > 1).ToList();
            var exId = identical.Take(maxEx).Select(g => U.S(Zdos.NameOf(g[0].GetPrefab()) + " x" + g.Count + " at " + U.V(g[0].GetPosition()))).ToArray();
            return U.Json("{\"scanned\":" + total + ",\"invalidPrefab\":" + invalid + ",\"staleEntries\":" + stale + ",\"duplicateEntries\":" + dupRef + ",\"identicalGroups\":" + identical.Count + ",\"fixed\":{\"entriesRemoved\":" + removed + ",\"destroyed\":" + destroyed + "},\"examples\":{\"invalid\":[" + string.Join(",", exInvalid.Select(U.S).ToArray())
                + "],\"stale\":[" + string.Join(",", exStale.Select(U.S).ToArray()) + "],\"duplicates\":[" + string.Join(",", exDup.Select(U.S).ToArray()) + "],\"identical\":[" + string.Join(",", exId) + "]}}");
        }

        // ------------------------------------------------------------------ selftest / status
        sealed class Check { public string Name; public bool Ok; public string Detail; }
        static List<Check> RunChecks()
        {
            var c = new List<Check>();
            Action<string, bool, string> add = (n, ok, d) => c.Add(new Check { Name = n, Ok = ok, Detail = d });
            add("zdo table", Zdos.Ready, Zdos.Ready ? "ZDOMan + ZoneSystem ready" : "not ready");
            try { var d = Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext"); Directory.CreateDirectory(d); var f = Path.Combine(d, ".write-test"); File.WriteAllText(f, "x"); File.Delete(f); add("journal dir writable", true, d); } catch (Exception ex) { add("journal dir writable", false, ex.Message); }
            add("journal", true, Journal.Entries.Count + " entries in memory");
            add("job hook", _h != null && string.IsNullOrEmpty(HookError), string.IsNullOrEmpty(HookError) ? "ZNetScene.Update postfix" : HookError);
            if (Zdos.Ready)
            {
                var arr = Lists(); int bad = 0, n = 0;
                if (arr != null) for (int i = 0; i < arr.Length && n < 200000; i++) { var l = arr[i]; if (l == null) continue; foreach (var z in l) { n++; if (z.GetPrefab() == 0) bad++; } }
                add("sector lists", arr != null && bad == 0, arr == null ? "unavailable" : n + " sampled, " + bad + " with prefab 0");
            }
#if !SERVER
            add("control", Control.Healthy(out var cd), cd);
#else
            var flag = Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext", "ALLOW_SERVER_WRITES");
            add("server write gate", true, File.Exists(flag) ? "OPEN (flag file present, " + (Plans.FlagFresh() ? "fresh" : "expired") + ")" : "closed");
#endif
            return c;
        }

        static ToolOutput SelfTest(Dictionary<string, object> a)
        {
            var c = RunChecks();
            return U.Json("{\"ok\":" + (c.All(x => x.Ok) ? "true" : "false") + ",\"checks\":[" + string.Join(",", c.Select(x => "{\"name\":" + U.S(x.Name) + ",\"ok\":" + (x.Ok ? "true" : "false") + ",\"detail\":" + U.S(x.Detail) + "}").ToArray()) + "]}");
        }

        internal static string HealthSummary()
        {
            try { var f = RunChecks().Where(x => !x.Ok).Select(x => x.Name).ToArray(); return f.Length == 0 ? "ok" : "failing: " + string.Join(", ", f); } catch (Exception ex) { return "selftest error: " + ex.Message; }
        }

        static ToolOutput PlayersTool(Dictionary<string, object> a)
        {
            if (ZNet.instance == null) return U.Json("{\"count\":0,\"players\":[]}");
            var rows = new List<string>(); int sandbox = 0;
            foreach (var p in ZNet.instance.GetPeers())
            {
                string name = p.m_playerName ?? ""; bool sb = name == "Odev" || name == "MaRkO" || name == "ClaudeEyes"; if (sb) sandbox++;
                string host = ""; try { host = p.m_socket != null ? p.m_socket.GetHostName() : ""; } catch { }
                rows.Add("{\"name\":" + U.S(name) + ",\"host\":" + U.S(host) + ",\"uid\":" + p.m_uid + ",\"pos\":" + U.V(p.m_refPos) + ",\"sandbox\":" + (sb ? "true" : "false") + "}");
            }
            return U.Json("{\"count\":" + rows.Count + ",\"sandbox\":" + sandbox + ",\"players\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput Status(Dictionary<string, object> a)
        {
            var sb = new StringBuilder("{\"version\":" + U.S(Ext.Version) + ",\"role\":" + U.S(
#if SERVER
                "server"
#else
                "client"
#endif
                ) + ",\"uptimeMinutes\":" + U.N(Math.Round((DateTime.UtcNow - _start).TotalMinutes, 1)));
            if (Zdos.Ready) { sb.Append(",\"objects\":" + ZDOMan.instance.NrOfObjects()); }
            if (ZNet.instance != null)
            {
                var peers = ZNet.instance.GetPeers(); sb.Append(",\"peers\":" + (peers == null ? 0 : peers.Count));
                try { sb.Append(",\"saveStart\":" + U.N(ZNet.instance.SaveStartTime) + ",\"saveDone\":" + U.N(ZNet.instance.SaveDoneTime)); } catch { }
            }
            sb.Append(",\"journalEntries\":" + Journal.Entries.Count + ",\"jobsQueued\":" + _jobs.Count(j => j.State == "queued") + ",\"writeGate\":" + U.S(Plans.GateState()) + ",\"health\":" + U.S(HealthSummary()));
            return U.Json(sb.Append('}').ToString());
        }

        static ToolOutput LogTail(Dictionary<string, object> a)
        {
            var f = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
            if (!File.Exists(f)) return ToolOutput.Err("log not found: " + f);
            int n = Math.Max(1, Math.Min(400, (int)McpJson.Get(a, "lines", 60))); var filter = McpJson.GetStr(a, "filter"); var level = (McpJson.GetStr(a, "level") ?? "all").ToLowerInvariant();
            var lines = new List<string>();
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length, from = Math.Max(0, len - 600000); fs.Seek(from, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs)) { string line; while ((line = sr.ReadLine()) != null) lines.Add(line); }
            }
            IEnumerable<string> q = lines.Where(l => l.Length > 0);
            if (level == "error") q = q.Where(l => l.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (level == "warning") q = q.Where(l => l.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(filter)) q = q.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            var res = q.Reverse().Take(n).Reverse().Select(l => l.Length > 300 ? l.Substring(0, 300) : l).ToArray();
            return U.Json("{\"count\":" + res.Length + ",\"lines\":[" + string.Join(",", res.Select(U.S).ToArray()) + "]}");
        }

        // ------------------------------------------------------------------ jobs
        sealed class Job { public int Id; public string Tool; public Dictionary<string, object> Args; public string State = "queued"; public string Result; public DateTime Queued = DateTime.UtcNow; }
        static readonly List<Job> _jobs = new List<Job>(); static int _jobSeq;
        internal static Dictionary<string, Func<Dictionary<string, object>, ToolOutput>> JobTools = new Dictionary<string, Func<Dictionary<string, object>, ToolOutput>>();

        static ToolOutput JobStart(Dictionary<string, object> a)
        {
            var tool = McpJson.GetStr(a, "tool");
            if (tool == null || !JobTools.ContainsKey(tool)) return ToolOutput.Err("tool not allowed for jobs: " + tool + " (allowed: " + string.Join(", ", JobTools.Keys.ToArray()) + ")");
            var args = McpJson.GetObj(a, "args") ?? new Dictionary<string, object>();
            var j = new Job { Id = ++_jobSeq, Tool = tool, Args = args }; _jobs.Add(j);
            if (_jobs.Count > 50) _jobs.RemoveAt(0);
            return U.Json("{\"job\":" + j.Id + ",\"state\":\"queued\"}");
        }

        static void RunJobs()
        {
            var j = _jobs.FirstOrDefault(x => x.State == "queued"); if (j == null) return;
            j.State = "running";
            try { var o = JobTools[j.Tool](j.Args); j.Result = o.Text; j.State = o.IsError ? "error" : "done"; }
            catch (Exception ex) { j.Result = ex.Message; j.State = "error"; }
        }

        static ToolOutput JobStatus(Dictionary<string, object> a)
        {
            if (U.Has(a, "id"))
            {
                var j = _jobs.FirstOrDefault(x => x.Id == (int)McpJson.Get(a, "id", -1)); if (j == null) return ToolOutput.Err("no such job");
                return U.Json("{\"job\":" + j.Id + ",\"tool\":" + U.S(j.Tool) + ",\"state\":" + U.S(j.State) + ",\"result\":" + (j.Result != null && j.Result.StartsWith("{") ? j.Result : U.S(j.Result ?? "")) + "}");
            }
            return U.Json("{\"jobs\":[" + string.Join(",", _jobs.Select(j => "{\"job\":" + j.Id + ",\"tool\":" + U.S(j.Tool) + ",\"state\":" + U.S(j.State) + "}").ToArray()) + "]}");
        }
    }
}
