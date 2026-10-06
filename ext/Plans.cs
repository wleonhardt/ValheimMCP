// Plan engine: ZDO-level, identical on the game client and the dedicated server twin.
// Every piece spawned through plan_apply carries hub_plan (plan id) and hub_key (stable identity), so verify / prune / undo are exact:
// no uid guessing, no position tolerance against the rest of the world, and a plan can never touch objects it did not create or adopt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Plans
    {
        public const string PlanKey = "hub_plan", ItemKey = "hub_key", CtagKey = "hub_ctag";
#if SERVER
        const bool W = false;       // the core's tools.write switch is a restart-time setting; on the server these tools use their own runtime gate instead
        static string FlagPath { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hubner-ext", "ALLOW_SERVER_WRITES"); } }
        /// <summary>The flag only counts for 30 minutes after it was last touched: forgetting to remove it cannot leave the server writable.</summary>
        internal static bool FlagFresh() { try { return File.Exists(FlagPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(FlagPath)).TotalMinutes < 30; } catch { return false; } }
        internal static string GateState() { return !File.Exists(FlagPath) ? "closed" : (FlagFresh() ? "open (flag touched < 30 min ago)" : "closed (flag expired: touch it again)"); }
        static ToolOutput Guard(Dictionary<string, object> a, Func<Dictionary<string, object>, ToolOutput> h)
        {
            if (McpJson.GetBool(a, "dry", false)) return h(a);
            if (!File.Exists(FlagPath)) return ToolOutput.Err("server writes are off: create " + FlagPath + " (and remove it when done)");
            if (!FlagFresh()) return ToolOutput.Err("server write flag expired (older than 30 min): touch " + FlagPath + " again");
            if (McpJson.GetStr(a, "confirm") != "server-write") return ToolOutput.Err("pass confirm:'server-write' with every server write");
            return h(a);
        }
#else
        const bool W = true;
        internal static bool FlagFresh() { return true; }
        internal static string GateState() { return "client (core tools.write)"; }
        static ToolOutput Guard(Dictionary<string, object> a, Func<Dictionary<string, object>, ToolOutput> h) { return h(a); }
#endif
        internal const bool WriteFlag = W;
        internal static ToolOutput GuardPublic(Dictionary<string, object> a, Func<Dictionary<string, object>, ToolOutput> h) { return Guard(a, h); }

        public static void Register(ToolRegistry r)
        {
            Audit.JobTools["clear_overlaps"] = a => Guard(a, ClearOverlaps); Audit.JobTools["plan_apply"] = a => Guard(a, Apply); Audit.JobTools["plan_remove"] = a => Guard(a, Remove); Audit.JobTools["zdo_delete"] = a => Guard(a, ZDelete); Audit.JobTools["zdo_set"] = a => Guard(a, ZSet);
            r.Add("plans", "List every plan id in the loaded/known world with piece counts and bounding box (objects tagged by plan_apply). Optional box filter x0,z0,x1,z1.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"}}}", ListPlans);
            r.Add("plan_apply",
                "Idempotent plan placement. plan: id string. items:[{prefab,x,y,z,yaw,text,tag,key,seat}] (max 6000). Per item: exact match by hub_key -> unchanged/moved/updated; else ADOPT an untagged piece of the same prefab within adoptTol (default 0.35 m; only objects with creator 0 that are not worldgen); else spawn. Objects of this plan inside box that no item claims are deleted (deleteExtra, default true). Never touches untagged objects except by adoption, never terrain/internal prefabs, never Player. 'seat':'ground' items ignore y when matching and are seated on the ground. dry=true reports only. Journaled under group=plan id (override with group). On the dedicated server twin this needs no teleport and no loaded zones.",
                "{\"type\":\"object\",\"properties\":{\"plan\":{\"type\":\"string\"},\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"box\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}},\"deleteExtra\":{\"type\":\"boolean\"},\"adopt\":{\"type\":\"boolean\"},\"adoptTol\":{\"type\":\"number\"},\"movedTol\":{\"type\":\"number\"},\"dry\":{\"type\":\"boolean\"},\"group\":{\"type\":\"string\"},\"examples\":{\"type\":\"number\"},\"confirm\":{\"type\":\"string\"}},\"required\":[\"plan\",\"items\"]}", a => Guard(a, Apply), W);
            r.Add("clear_overlaps", "Delete untagged, non-player objects (creator 0, no hub_plan: trees, rocks, bushes, pickables, ruin pieces) that overlap planned pieces. items:[{x,z,y0,y1,r}] footprints (r = half-size in metres, y0/y1 = vertical extent), margin metres (default 0.3), dry=true counts only. Never touches player-built objects (creator!=0), anything tagged by a plan, creatures, locations, signs or internal prefabs. Journaled under group. Works on the server twin with no teleports.",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"margin\":{\"type\":\"number\"},\"dry\":{\"type\":\"boolean\"},\"group\":{\"type\":\"string\"},\"examples\":{\"type\":\"number\"},\"confirm\":{\"type\":\"string\"}},\"required\":[\"items\"]}", a => Guard(a, ClearOverlaps), W);
            r.Add("plan_stats", "What the plans have put in the world, from the ZDOs: per plan id and per ctag (generator label): piece count, bounding box and centre; plus 'foreign': objects built by players (creator != 0, no hub_plan) inside each plan's box - things someone added or left behind. Optional box x0,z0,x1,z1 and ctagDepth (how many ':' segments of the ctag to group by, default 2).",
                "{\"type\":\"object\",\"properties\":{\"box\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}},\"ctagDepth\":{\"type\":\"number\"}}}", PlanStats);
            r.Add("plan_remove", "Delete objects tagged with a plan id inside the box (default: everywhere known). Filters: ctag (prefix of the generator label sent as item ctag, e.g. 'decor:Kitchen'), prefab (prefix); plan:'*' or all:true spans every plan (needs ctag or prefab). dry=true counts only. Journaled.",
                "{\"type\":\"object\",\"properties\":{\"plan\":{\"type\":\"string\"},\"ctag\":{\"type\":\"string\"},\"prefab\":{\"type\":\"string\"},\"all\":{\"type\":\"boolean\"},\"box\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}},\"dry\":{\"type\":\"boolean\"}}}", a => Guard(a, Remove), W);
            r.Add("zdo_set", "Server-side/ZDO-level edit by id, no instantiation needed: {id, x,y,z, yaw|rot:[rx,ry,rz], text, tag, strings:{k:v}, ints:{k:v} (e.g. door state 0=closed)}. edits:[...] (max 500). Takes ownership first. Refuses terrain/internal prefabs, Player, and player-built objects (creator!=0) unless force=true. Journaled under group.",
                "{\"type\":\"object\",\"properties\":{\"edits\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"group\":{\"type\":\"string\"},\"force\":{\"type\":\"boolean\"}},\"required\":[\"edits\"]}", a => Guard(a, ZSet), W);
            r.Add("zdo_delete", "Delete objects by id at ZDO level (any role, no instantiation). ids:[...] (max 2000). Refuses Player, terrain/internal prefabs and player-built objects (creator!=0) unless force=true. Journaled under group.",
                "{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"group\":{\"type\":\"string\"},\"force\":{\"type\":\"boolean\"}},\"required\":[\"ids\"]}", a => Guard(a, ZDelete), W);
            r.Add("doors", "Doors and gates (prefab name contains door/gate) in a box x0,z0,x1,z1 or x,z,radius, with open/closed state read from the ZDO: id, prefab, pos, state (0 closed, nonzero open), plan. Optional openOnly=true. Read-only.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"openOnly\":{\"type\":\"boolean\"},\"limit\":{\"type\":\"number\"}}}", DoorsList, false);
            r.Add("doors_set", "Open or close every door/gate in a box (same selectors as doors): state 0 = closed (default), 1 = open. Only doors whose state differs are written. Journaled. Server role needs the write gate + confirm.",
                "{\"type\":\"object\",\"properties\":{\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"state\":{\"type\":\"number\"},\"confirm\":{\"type\":\"string\"}}}", a => Guard(a, DoorsSet), W);
            r.Add("zdo_dump", "Every field stored on one ZDO (strings, floats, ints, longs, vectors, quats) plus owner, sector, prefab, plan tags. Field names come from ZDOVars; unknown hashes are listed as #hash only when probed.",
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}", Dump);
            r.Add("locations", "World locations (boss altars, vegvisirs, ruins, villages ...) known to this client: filter name substring, optional box. Returns name, position, placed flag.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}}}", Locations);
            r.Add("find_text", "Search sign text, portal tags and any ZDO 'text'/'tag' for a substring (case-insensitive) in a box (default whole world on the server). Returns id, prefab, position, text and tag.",
                "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}},\"required\":[\"q\"]}", FindText);
            r.Add("journal_groups", "Persistent write journal summary: groups with op counts and first/last time (survives hot reload and restarts).",
                "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"number\"}}}", Groups);
            r.Add("undo_group", "Undo a whole journal group (newest ops first): spawns are deleted, deletes are re-created, modifies restored. group:'<name>' or count:n for the last n ops. Works on any role and across reloads.",
                "{\"type\":\"object\",\"properties\":{\"group\":{\"type\":\"string\"},\"count\":{\"type\":\"number\"}}}", a => Guard(a, UndoGroup), W);
            r.Add("save_state", "Read-only: world save timings (saveStart, saveDone, thread running) so a caller can wait for world_save to finish.",
                "{\"type\":\"object\",\"properties\":{}}", SaveState, false);
            r.Add("world_save", "Ask the server (or this client's local world) to save now and report timings: the first half of an online-safe backup (copy the files after it returns).",
                "{\"type\":\"object\",\"properties\":{}}", WorldSave, false);               // saving is harmless on any role: no write gate
        }

        // ------------------------------------------------------------------ guards
        static bool Protected(ZDO z, string prefab, bool force, out string why)
        {
            why = null;
            if (U.IsForbidden(prefab)) { why = "forbidden prefab"; return true; }
            if (prefab == "Player" || prefab.StartsWith("Player")) { why = "player"; return true; }
            if (!force && z.GetLong("creator", 0L) != 0L && string.IsNullOrEmpty(z.GetString(PlanKey, ""))) { why = "player-built (creator!=0): pass force=true"; return true; }
            return false;
        }

        static bool IsPlanAdoptable(ZDO z, string prefab)
        {
            if (z.GetLong("creator", 0L) != 0L) return false;                          // player-built
            if (z.m_uid.UserID == 1L) return false;                                     // worldgen
            if (!string.IsNullOrEmpty(z.GetString(PlanKey, ""))) return false;         // already someone's
            if (prefab == "Player" || prefab.StartsWith("_")) return false;
            return true;
        }

        static void Own(ZDO z) { try { z.SetOwner(ZDOMan.GetSessionID()); } catch { } }

        static string KeyOf(string prefab, double x, double y, double z, bool seat)
        {
            return prefab + "|" + Math.Round(x, 1).ToString("0.0", CultureInfo.InvariantCulture) + "|" + Math.Round(z, 1).ToString("0.0", CultureInfo.InvariantCulture) + "|" + (seat ? "s" : (Math.Round(y * 2.0) / 2.0).ToString("0.0", CultureInfo.InvariantCulture));
        }

        static float[] Box(Dictionary<string, object> a, List<object> items)
        {
            var b = McpJson.GetList(a, "box");
            if (b != null && b.Count >= 4) return new[] { (float)(double)b[0], (float)(double)b[1], (float)(double)b[2], (float)(double)b[3] };
            float x0 = 1e9f, z0 = 1e9f, x1 = -1e9f, z1 = -1e9f;
            if (items != null)
                foreach (var o in items)
                {
                    var it = o as Dictionary<string, object>; if (it == null) continue;
                    var x = (float)McpJson.Get(it, "x", 0); var z = (float)McpJson.Get(it, "z", 0);
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
                }
            return new[] { x0 - 2f, z0 - 2f, x1 + 2f, z1 + 2f };
        }

        static List<ZDO> Scan(float[] box)
        {
            return Zdos.InBox(box[0], box[1], box[2], box[3]);
        }

        static int Hash(string prefab) { return prefab.GetStableHashCode(); }

        // ------------------------------------------------------------------ journal helpers
        static void Log(string group, string kind, ZDO z, string prefab, Vector3 pos, Quaternion rot, string text, string tag, string plan, string key, long creator)
        {
            Journal.Add(new JEntry { Kind = kind, Id = U.Id(z), Prefab = prefab, Pos = pos, Rot = rot, Text = text, Tag = tag, Plan = plan, Key = key, Creator = creator, Group = group, Scale = Vector3.one });
        }

        /// <summary>The one place objects are created: prefab hash set and verified, persistence on, plan tags written. Returns null (and destroys the half-made object) when the result is not a valid object.</summary>
        static ZDO CreateZdo(string prefab, Vector3 pos, Quaternion rot, string text, string tag, string plan, string key, out string err)
        {
            err = null;
            if (ZNetScene.instance != null && ZNetScene.instance.GetPrefab(prefab) == null) { err = prefab + ": unknown prefab"; return null; }
            var nz = ZDOMan.instance.CreateNewZDO(pos, Hash(prefab));
            nz.SetPrefab(Hash(prefab));                           // CreateNewZDO leaves the prefab hash 0 on this game version: set it explicitly
            if (nz.GetPrefab() != Hash(prefab)) { err = prefab + ": created object has prefab hash " + nz.GetPrefab(); try { ZDOMan.instance.DestroyZDO(nz); } catch { } return null; }
            nz.Persistent = true; nz.SetRotation(rot);
            if (text != null) nz.Set("text", text);
            if (tag != null) nz.Set("tag", tag);
            nz.Set(PlanKey, plan); nz.Set(ItemKey, key);
            return nz;
        }

        // optional per-item extras: ints:{k:v} (door state ...) and ctag (the generator's label, kept so plan_remove can filter by it)
        static bool NeedExtras(ZDO z, Dictionary<string, object> it)
        {
            if (it.ContainsKey("ints") && it["ints"] is Dictionary<string, object> d)
                foreach (var kv in d) if (z.GetInt(kv.Key, int.MinValue) != Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture)) return true;
            var ct = McpJson.GetStr(it, "ctag"); if (ct != null && z.GetString(CtagKey, "") != ct) return true;
            return false;
        }

        static void ApplyExtras(ZDO z, Dictionary<string, object> it)
        {
            if (it.ContainsKey("ints") && it["ints"] is Dictionary<string, object> d)
                foreach (var kv in d) z.Set(kv.Key, Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture));
            var ct = McpJson.GetStr(it, "ctag"); if (ct != null) z.Set(CtagKey, ct);
        }

        // ------------------------------------------------------------------ plan_apply
        static ToolOutput Apply(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var plan = McpJson.GetStr(a, "plan"); if (string.IsNullOrEmpty(plan)) return ToolOutput.Err("plan required");
            var items = McpJson.GetList(a, "items"); if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            if (items.Count > 6000) return ToolOutput.Err("max 6000 items per call");
            bool dry = McpJson.GetBool(a, "dry", false), deleteExtra = McpJson.GetBool(a, "deleteExtra", true), adopt = McpJson.GetBool(a, "adopt", true);
            float adoptTol = (float)McpJson.Get(a, "adoptTol", 0.35), movedTol = (float)McpJson.Get(a, "movedTol", 0.15);
            var group = McpJson.GetStr(a, "group") ?? plan; int maxEx = (int)McpJson.Get(a, "examples", 8);
            var box = Box(a, items);
            var zs = ZoneSystem.instance;

            var world = Scan(box);
            var tagged = new Dictionary<string, List<ZDO>>(); var loose = new Dictionary<string, List<ZDO>>();   // key -> zdos ; prefab|cell -> adoptable zdos
            var mine = new List<ZDO>();
            foreach (var z in world)
            {
                var p = Zdos.NameOf(z.GetPrefab());
                var pl = z.GetString(PlanKey, "");
                if (pl == plan)
                {
                    mine.Add(z); var k = z.GetString(ItemKey, "");
                    if (!tagged.TryGetValue(k, out var l)) tagged[k] = l = new List<ZDO>(); l.Add(z);
                }
                else if (adopt && string.IsNullOrEmpty(pl) && IsPlanAdoptable(z, p))
                {
                    var pos = z.GetPosition(); var ck = p + "|" + (int)Math.Floor(pos.x / 2f) + "|" + (int)Math.Floor(pos.z / 2f);
                    if (!loose.TryGetValue(ck, out var l)) loose[ck] = l = new List<ZDO>(); l.Add(z);
                }
            }

            var claimed = new HashSet<ZDO>(); var movedSet = new HashSet<ZDO>(); var labelItems = new List<KeyValuePair<Vector3, string>>(); int labelled = 0, labelPending = 0, extrasSet = 0;
            int spawned = 0, adopted = 0, moved = 0, updated = 0, unchanged = 0, errors = 0, deleted = 0, dupes = 0, skippedUnloaded = 0;
            var exSpawn = new List<string>(); var exMoved = new List<string>(); var exErr = new List<string>(); var exDel = new List<string>();
            var wanted = new HashSet<string>();
            foreach (var o in items)
            {
                var it = o as Dictionary<string, object>;
                try
                {
                    var prefab = McpJson.GetStr(it, "prefab");
                    if (string.IsNullOrEmpty(prefab)) { errors++; continue; }
                    if (U.IsForbidden(prefab) || prefab.StartsWith("Player")) { errors++; if (exErr.Count < maxEx) exErr.Add(prefab + ": forbidden"); continue; }
                    double x = McpJson.Get(it, "x", 0), y = McpJson.Get(it, "y", 0), zz = McpJson.Get(it, "z", 0), yaw = McpJson.Get(it, "yaw", 0);
                    bool seat = McpJson.GetStr(it, "seat") == "ground";
                    var text = McpJson.GetStr(it, "text"); var tag = McpJson.GetStr(it, "tag");
                    if (prefab.StartsWith("portal") && string.IsNullOrEmpty(tag)) { errors++; if (exErr.Count < maxEx) exErr.Add(prefab + ": portals need a tag"); continue; }
                    var key = McpJson.GetStr(it, "key") ?? KeyOf(prefab, x, y, zz, seat);
                    if (!wanted.Add(key)) { dupes++; continue; }                         // the same key twice in one plan: first wins
                    var pos = new Vector3((float)x, (float)y, (float)zz);
                    if (seat && zs != null && zs.GetGroundHeight(new Vector3(pos.x, 5000f, pos.z), out var gh)) pos.y = gh;
                    var lab = McpJson.GetStr(it, "label"); if (lab != null) labelItems.Add(new KeyValuePair<Vector3, string>(pos, lab));
                    var rot = Quaternion.Euler(0f, (float)yaw, 0f);
                    if (it.ContainsKey("rot") && McpJson.GetList(it, "rot") is List<object> rl && rl.Count >= 3) rot = Quaternion.Euler((float)(double)rl[0], (float)(double)rl[1], (float)(double)rl[2]);

                    ZDO z = null;
                    if (tagged.TryGetValue(key, out var tl))
                    {
                        z = tl.FirstOrDefault(c => !claimed.Contains(c));
                        for (int i = 1; i < tl.Count; i++) if (tl[i] != z && !claimed.Contains(tl[i])) { /* surplus copies fall into the extra sweep below */ }
                    }
                    bool wasAdopted = false;
                    if (z == null && adopt)
                    {
                        var p0 = pos; double best = adoptTol;
                        for (int dx = -1; dx <= 1 && z == null; dx++)
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (!loose.TryGetValue(prefab + "|" + ((int)Math.Floor(pos.x / 2f) + dx) + "|" + ((int)Math.Floor(pos.z / 2f) + dz), out var cand)) continue;
                                foreach (var c in cand)
                                {
                                    if (claimed.Contains(c)) continue;
                                    var cp = c.GetPosition();
                                    var dh = Math.Sqrt((cp.x - p0.x) * (cp.x - p0.x) + (cp.z - p0.z) * (cp.z - p0.z));
                                    var dy = Math.Abs(cp.y - p0.y);
                                    if (seat ? (dh < adoptTol && dy < 3f) : (dh < adoptTol && dy < adoptTol)) { if (dh + dy < best * 2) { best = dh; z = c; wasAdopted = true; } }
                                }
                            }
                    }
                    if (z != null)
                    {
                        claimed.Add(z);
                        if (z.GetPrefab() != Hash(prefab))                                           // wrong/zero prefab (made by an older version): recreate, an in-place fix never reaches connected clients
                        {
                            updated++; if (exMoved.Count < maxEx) exMoved.Add(prefab + " recreated (bad prefab) " + U.V(z.GetPosition()));
                            if (!dry)
                            {
                                var op = z.GetPosition(); var orot = z.GetRotation();
                                Log(group, "delete", z, prefab, op, orot, z.GetString("text", ""), z.GetString("tag", ""), plan, z.GetString(ItemKey, ""), z.GetLong("creator", 0L));
                                Destroy(z);
                                string rerr; var rz = CreateZdo(prefab, pos, rot, text, tag, plan, key, out rerr);
                                if (rz == null) { errors++; updated--; if (exErr.Count < maxEx) exErr.Add(rerr); } else { ApplyExtras(rz, it); claimed.Add(rz); Log(group, "spawn", rz, prefab, pos, rot, text, tag, plan, key, 0L); }
                            }
                            continue;
                        }
                        var cur = z.GetPosition(); var cr = z.GetRotation();
                        bool needMove = Vector3.Distance(cur, pos) > movedTol || Quaternion.Angle(cr, rot) > 2f;
                        bool needText = text != null && z.GetString("text", "") != text;
                        bool needTag = tag != null && z.GetString("tag", "") != tag;
                        bool needTagKey = wasAdopted || z.GetString(PlanKey, "") != plan || z.GetString(ItemKey, "") != key;
                        bool needExtras = NeedExtras(z, it);
                        if (!needMove && !needText && !needTag && !needTagKey && !needExtras) { unchanged++; continue; }
                        if (wasAdopted) adopted++;
                        if (needMove) { moved++; if (exMoved.Count < maxEx) exMoved.Add(prefab + " " + U.V(cur) + "->" + U.V(pos)); }
                        else if (needText || needTag || needExtras) updated++;
                        if (dry) continue;
                        Own(z);
                        Log(group, "modify", z, prefab, cur, cr, z.GetString("text", ""), z.GetString("tag", ""), z.GetString(PlanKey, ""), z.GetString(ItemKey, ""), z.GetLong("creator", 0L));
                        if (needMove) { z.SetPosition(pos); z.SetRotation(rot); ApplyToInstance(z, pos, rot); movedSet.Add(z); }
                        if (needText) z.Set("text", text);
                        if (needTag) z.Set("tag", tag);
                        z.Set(PlanKey, plan); z.Set(ItemKey, key); if (needExtras) { ApplyExtras(z, it); extrasSet++; }
                        continue;
                    }
                    // spawn
                    spawned++; if (exSpawn.Count < maxEx) exSpawn.Add(prefab + " " + U.V(pos));
                    if (dry) continue;
                    string cerr; var nz = CreateZdo(prefab, pos, rot, text, tag, plan, key, out cerr);
                    if (nz == null) { spawned--; errors++; if (exErr.Count < maxEx) exErr.Add(cerr); continue; }
                    claimed.Add(nz); ApplyExtras(nz, it);
                    Log(group, "spawn", nz, prefab, pos, rot, text, tag, plan, key, 0L);
                }
                catch (Exception ex) { errors++; if (exErr.Count < maxEx) exErr.Add("exception: " + ex.Message); }
            }

            if (labelItems.Count > 0)                                                    // chest plaques: the game makes an unplanned 'sign' next to a container once a client has instantiated it
            {
                var signs = world.Where(z => string.IsNullOrEmpty(z.GetString(PlanKey, "")) && Zdos.NameOf(z.GetPrefab()) == "sign").ToList(); var taken = new HashSet<ZDO>();
                foreach (var li in labelItems)
                {
                    ZDO best = null; double bd = 1.3;
                    foreach (var sg in signs)
                    {
                        if (taken.Contains(sg)) continue; var sp = sg.GetPosition();
                        var dh = Math.Sqrt((sp.x - li.Key.x) * (sp.x - li.Key.x) + (sp.z - li.Key.z) * (sp.z - li.Key.z));
                        if (dh < bd && Math.Abs(sp.y - li.Key.y) < 2.4) { bd = dh; best = sg; }
                    }
                    if (best == null) { labelPending++; continue; }
                    taken.Add(best);
                    if (best.GetString("text", "") != li.Value) { labelled++; if (!dry) { Own(best); best.Set("text", li.Value); } }
                }
            }
            if (!dry && movedSet.Count > 0) Audit.FixMoved(movedSet);                    // a moved object must be listed in exactly one sector, or the next save writes it twice
            if (deleteExtra)
                foreach (var z in mine)
                {
                    if (claimed.Contains(z)) continue;
                    var prefab = Zdos.NameOf(z.GetPrefab());
                    deleted++; if (exDel.Count < maxEx) exDel.Add(prefab + " " + U.V(z.GetPosition()));
                    if (dry) continue;
                    Log(group, "delete", z, prefab, z.GetPosition(), z.GetRotation(), z.GetString("text", ""), z.GetString("tag", ""), plan, z.GetString(ItemKey, ""), z.GetLong("creator", 0L));
                    Destroy(z);
                }

            var sb = new StringBuilder("{\"plan\":" + U.S(plan) + ",\"dry\":" + (dry ? "true" : "false") + ",\"items\":" + items.Count + ",\"unchanged\":" + unchanged + ",\"spawned\":" + spawned + ",\"adopted\":" + adopted + ",\"moved\":" + moved + ",\"updated\":" + updated + ",\"deleted\":" + deleted + ",\"duplicateKeys\":" + dupes + ",\"errors\":" + errors + ",\"box\":[" + string.Join(",", box.Select(v => U.N(v)).ToArray()) + "],\"group\":" + U.S(group) + ",\"labelled\":" + labelled + ",\"labelPending\":" + labelPending);
            sb.Append(",\"examples\":{\"spawn\":[" + string.Join(",", exSpawn.Select(U.S).ToArray()) + "],\"moved\":[" + string.Join(",", exMoved.Select(U.S).ToArray()) + "],\"deleted\":[" + string.Join(",", exDel.Select(U.S).ToArray()) + "],\"errors\":[" + string.Join(",", exErr.Select(U.S).ToArray()) + "]}}");
            return U.Json(sb.ToString());
        }

        static void ApplyToInstance(ZDO z, Vector3 pos, Quaternion rot)
        {
            if (ZNetScene.instance == null) return;
            var nv = ZNetScene.instance.FindInstance(z);
            if (nv == null) return;
            try { nv.ClaimOwnership(); nv.transform.position = pos; nv.transform.rotation = rot; } catch { }
        }

        static void Destroy(ZDO z)
        {
            Own(z);
            if (ZNetScene.instance != null)
            {
                var nv = ZNetScene.instance.FindInstance(z);
                if (nv != null) { try { nv.ClaimOwnership(); ZNetScene.instance.Destroy(nv.gameObject); return; } catch { } }
            }
            ZDOMan.instance.DestroyZDO(z);
        }

        static readonly string[] KeepNames = { "Skeleton", "Greydwarf", "Troll", "Boar", "Deer", "Neck", "Fish", "Seagal", "Crow", "Greyling", "Wolf", "Lox", "Goblin", "Draugr", "Blob", "Leech", "Surtling", "Ghost", "Hen", "Chicken", "Bat", "Serpent", "Player", "Location", "LocationProxy", "Music", "sign", "portal", "Chest", "TreasureChest", "Spawner", "Vegvisir", "BossStone", "StartTemple", "Pickable_ForestCryptRemains" };
        static float NaturalRadius(string n)
        {
            if (n.StartsWith("Beech") || n.StartsWith("Oak") || n.StartsWith("Birch")) return n.Contains("small") ? 2.0f : 3.5f;
            if (n.IndexOf("Tree", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("Pine") || n.StartsWith("Fir")) return 2.2f;
            if (n.StartsWith("Rock") || n.StartsWith("rock")) return 2.0f;
            if (n.IndexOf("Bush", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("shrub")) return 1.2f;
            return 0.6f;
        }

        static ToolOutput ClearOverlaps(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var items = McpJson.GetList(a, "items"); if (items == null || items.Count == 0) return ToolOutput.Err("items required");
            float margin = (float)McpJson.Get(a, "margin", 0.3); bool dry = McpJson.GetBool(a, "dry", false); var group = McpJson.GetStr(a, "group") ?? "clear_overlaps"; int maxEx = (int)McpJson.Get(a, "examples", 10);
            var fp = new List<float[]>(); float x0 = 1e9f, z0 = 1e9f, x1 = -1e9f, z1 = -1e9f;
            foreach (var o in items)
            {
                var it = o as Dictionary<string, object>; if (it == null) continue;
                float x = (float)McpJson.Get(it, "x", 0), z = (float)McpJson.Get(it, "z", 0), y0 = (float)McpJson.Get(it, "y0", -1e4), y1 = (float)McpJson.Get(it, "y1", 1e4), r = (float)McpJson.Get(it, "r", 1);
                fp.Add(new[] { x, z, y0, y1, r }); x0 = Math.Min(x0, x - r - 6); x1 = Math.Max(x1, x + r + 6); z0 = Math.Min(z0, z - r - 6); z1 = Math.Max(z1, z + r + 6);
            }
            var grid = new Dictionary<long, List<int>>(); Func<int, int, long> key = (gx, gz) => ((long)gx << 32) ^ (uint)gz;
            for (int i = 0; i < fp.Count; i++) { int gx = (int)Math.Floor(fp[i][0] / 4f), gz = (int)Math.Floor(fp[i][1] / 4f); var k = key(gx, gz); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(i); }
            int hit = 0, deleted = 0; var ex = new List<string>(); var byPrefab = new Dictionary<string, int>();
            foreach (var z in Zdos.InBox(x0, z0, x1, z1).ToList())
            {
                if (z.GetLong("creator", 0L) != 0L || !string.IsNullOrEmpty(z.GetString(PlanKey, ""))) continue;
                var name = Zdos.NameOf(z.GetPrefab());
                if (name.Length == 0 || name[0] == '_' || name[0] == '#' || U.IsForbidden(name) || KeepNames.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
                var p = z.GetPosition(); float rad = NaturalRadius(name) + margin; bool over = false;
                int cx = (int)Math.Floor(p.x / 4f), cz = (int)Math.Floor(p.z / 4f);
                for (int dx = -3; dx <= 3 && !over; dx++)
                    for (int dz = -3; dz <= 3 && !over; dz++)
                        if (grid.TryGetValue(key(cx + dx, cz + dz), out var l))
                            foreach (var i in l)
                            {
                                var f = fp[i]; float ddx = p.x - f[0], ddz = p.z - f[1];
                                if (ddx * ddx + ddz * ddz < (f[4] + rad) * (f[4] + rad) && p.y > f[2] - 2.5f && p.y < f[3] + 1.0f) { over = true; break; }
                            }
                if (!over) continue;
                hit++; byPrefab[name] = byPrefab.ContainsKey(name) ? byPrefab[name] + 1 : 1; if (ex.Count < maxEx) ex.Add(name + " " + U.V(p));
                if (dry) continue;
                Log(group, "delete", z, name, p, z.GetRotation(), z.GetString("text", ""), z.GetString("tag", ""), "", "", 0L);
                Destroy(z); deleted++;
            }
            return U.Json("{\"overlapping\":" + hit + ",\"deleted\":" + deleted + ",\"dry\":" + (dry ? "true" : "false") + ",\"byPrefab\":{" + string.Join(",", byPrefab.Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "},\"examples\":[" + string.Join(",", ex.Select(U.S).ToArray()) + "]}");
        }

        // ------------------------------------------------------------------ plans / plan_remove
        static ToolOutput ListPlans(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            float[] b = U.Has(a, "x0") ? new[] { (float)U.D(a, "x0", 0), (float)U.D(a, "z0", 0), (float)U.D(a, "x1", 0), (float)U.D(a, "z1", 0) } : new[] { -10500f, -10500f, 10500f, 10500f };
            var d = new Dictionary<string, float[]>(); var n = new Dictionary<string, int>();
            foreach (var z in Zdos.InBox(b[0], b[1], b[2], b[3]))
            {
                var pl = z.GetString(PlanKey, ""); if (string.IsNullOrEmpty(pl)) continue;
                var p = z.GetPosition();
                if (!d.TryGetValue(pl, out var bb)) { d[pl] = bb = new[] { 1e9f, 1e9f, -1e9f, -1e9f }; n[pl] = 0; }
                bb[0] = Math.Min(bb[0], p.x); bb[1] = Math.Min(bb[1], p.z); bb[2] = Math.Max(bb[2], p.x); bb[3] = Math.Max(bb[3], p.z); n[pl]++;
            }
            var rows = d.Keys.OrderBy(k => k).Select(k => "{\"plan\":" + U.S(k) + ",\"count\":" + n[k] + ",\"box\":[" + string.Join(",", d[k].Select(v => U.N(Math.Round(v, 1))).ToArray()) + "]}").ToArray();
            return U.Json("{\"plans\":[" + string.Join(",", rows) + "]}");
        }

        static ToolOutput PlanStats(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var bl = McpJson.GetList(a, "box");
            float[] b = bl != null && bl.Count >= 4 ? new[] { (float)(double)bl[0], (float)(double)bl[1], (float)(double)bl[2], (float)(double)bl[3] } : new[] { -10500f, -10500f, 10500f, 10500f };
            int depth = (int)McpJson.Get(a, "ctagDepth", 2);
            var plans = new Dictionary<string, float[]>(); var counts = new Dictionary<string, int>(); var groups = new Dictionary<string, float[]>(); var gcount = new Dictionary<string, int>(); var gplan = new Dictionary<string, string>();
            var all = Zdos.InBox(b[0], b[1], b[2], b[3]).ToList(); var foreign = new List<ZDO>();
            Action<Dictionary<string, float[]>, string, Vector3> grow = (d, k, p) =>
            {
                if (!d.TryGetValue(k, out var bb)) { d[k] = bb = new[] { 1e9f, 1e9f, -1e9f, -1e9f, 1e9f, -1e9f }; }
                bb[0] = Math.Min(bb[0], p.x); bb[1] = Math.Min(bb[1], p.z); bb[2] = Math.Max(bb[2], p.x); bb[3] = Math.Max(bb[3], p.z); bb[4] = Math.Min(bb[4], p.y); bb[5] = Math.Max(bb[5], p.y);
            };
            foreach (var z in all)
            {
                var pl = z.GetString(PlanKey, ""); var p = z.GetPosition();
                if (string.IsNullOrEmpty(pl)) { if (z.GetLong("creator", 0L) != 0) foreign.Add(z); continue; }
                grow(plans, pl, p); counts[pl] = (counts.TryGetValue(pl, out var c) ? c : 0) + 1;
                var ct = z.GetString(CtagKey, ""); if (ct.Length == 0) ct = "(none)";
                var parts = ct.Split(':'); var gk = pl + "|" + string.Join(":", parts.Take(depth).ToArray());
                grow(groups, gk, p); gcount[gk] = (gcount.TryGetValue(gk, out var gc) ? gc : 0) + 1; gplan[gk] = pl;
            }
            Func<float[], string> bx = bb => "[" + U.N(Math.Round(bb[0], 1)) + "," + U.N(Math.Round(bb[1], 1)) + "," + U.N(Math.Round(bb[2], 1)) + "," + U.N(Math.Round(bb[3], 1)) + "],\"y\":[" + U.N(Math.Round(bb[4], 1)) + "," + U.N(Math.Round(bb[5], 1)) + "]";
            var sbp = new List<string>();
            foreach (var kv in plans.OrderBy(k => k.Key))
            {
                var bb = kv.Value; int nf = foreign.Count(f => { var q = f.GetPosition(); return q.x >= bb[0] - 3 && q.x <= bb[2] + 3 && q.z >= bb[1] - 3 && q.z <= bb[3] + 3; });
                sbp.Add("{\"plan\":" + U.S(kv.Key) + ",\"pieces\":" + counts[kv.Key] + ",\"box\":" + bx(bb) + ",\"foreign\":" + nf + "}");
            }
            var sbg = new List<string>();
            foreach (var kv in groups.OrderBy(k => k.Key)) sbg.Add("{\"plan\":" + U.S(gplan[kv.Key]) + ",\"ctag\":" + U.S(kv.Key.Substring(kv.Key.IndexOf('|') + 1)) + ",\"pieces\":" + gcount[kv.Key] + ",\"box\":" + bx(kv.Value) + "}");
            return U.Json("{\"plans\":[" + string.Join(",", sbp.ToArray()) + "],\"groups\":[" + string.Join(",", sbg.ToArray()) + "],\"foreignTotal\":" + foreign.Count + "}");
        }

        static ToolOutput Remove(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var plan = McpJson.GetStr(a, "plan"); bool dry = McpJson.GetBool(a, "dry", false);
            var ctag = McpJson.GetStr(a, "ctag"); var pfx = McpJson.GetStr(a, "prefab"); bool all = plan == "*" || McpJson.GetBool(a, "all", false);
            if (string.IsNullOrEmpty(plan) && !all) return ToolOutput.Err("plan required (or plan:'*' / all:true for every plan)");
            if (all && string.IsNullOrEmpty(ctag) && string.IsNullOrEmpty(pfx)) return ToolOutput.Err("all-plans removal needs a ctag or prefab filter");
            var bl = McpJson.GetList(a, "box");
            float[] b = bl != null && bl.Count >= 4 ? new[] { (float)(double)bl[0], (float)(double)bl[1], (float)(double)bl[2], (float)(double)bl[3] } : new[] { -10500f, -10500f, 10500f, 10500f };
            int n = 0; var byPlan = new Dictionary<string, int>();
            foreach (var z in Zdos.InBox(b[0], b[1], b[2], b[3]).ToList())
            {
                var pl = z.GetString(PlanKey, ""); if (string.IsNullOrEmpty(pl)) continue;
                if (!all && pl != plan) continue;
                if (ctag != null && !z.GetString(CtagKey, "").StartsWith(ctag, StringComparison.Ordinal)) continue;
                var prefab = Zdos.NameOf(z.GetPrefab());
                if (pfx != null && !prefab.StartsWith(pfx, StringComparison.Ordinal)) continue;
                n++; byPlan[pl] = (byPlan.TryGetValue(pl, out var c) ? c : 0) + 1; if (dry) continue;
                Log("remove:" + pl, "delete", z, prefab, z.GetPosition(), z.GetRotation(), z.GetString("text", ""), z.GetString("tag", ""), pl, z.GetString(ItemKey, ""), z.GetLong("creator", 0L));
                Destroy(z);
            }
            return U.Json("{\"plan\":" + U.S(plan ?? "*") + ",\"removed\":" + n + ",\"dry\":" + (dry ? "true" : "false") + ",\"byPlan\":{" + string.Join(",", byPlan.Select(kv => U.S(kv.Key) + ":" + kv.Value).ToArray()) + "}}");
        }

        // ------------------------------------------------------------------ zdo_set / zdo_delete / zdo_dump
        static ToolOutput ZSet(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var edits = McpJson.GetList(a, "edits"); if (edits == null || edits.Count == 0) return ToolOutput.Err("edits required");
            if (edits.Count > 500) return ToolOutput.Err("max 500 edits per call");
            var group = McpJson.GetStr(a, "group") ?? "zdo_set"; bool force = McpJson.GetBool(a, "force", false);
            int done = 0; var errs = new List<string>(); var movedSet = new HashSet<ZDO>();
            foreach (var o in edits)
            {
                var e = o as Dictionary<string, object>;
                try
                {
                    var id = McpJson.GetStr(e, "id"); var z = U.FindZdo(id);
                    if (z == null) { errs.Add(id + ": not found"); continue; }
                    var prefab = Zdos.NameOf(z.GetPrefab());
                    if (Protected(z, prefab, force, out var why)) { errs.Add(id + ": " + why); continue; }
                    var pos = z.GetPosition(); var rot = z.GetRotation();
                    Log(group, "modify", z, prefab, pos, rot, z.GetString("text", ""), z.GetString("tag", ""), z.GetString(PlanKey, ""), z.GetString(ItemKey, ""), z.GetLong("creator", 0L));
                    Own(z);
                    var np = pos; if (U.Has(e, "x")) np.x = (float)McpJson.Get(e, "x", pos.x); if (U.Has(e, "y")) np.y = (float)McpJson.Get(e, "y", pos.y); if (U.Has(e, "z")) np.z = (float)McpJson.Get(e, "z", pos.z);
                    var nr = rot;
                    if (U.Has(e, "rot") && McpJson.GetList(e, "rot") is List<object> rl && rl.Count >= 3) nr = Quaternion.Euler((float)(double)rl[0], (float)(double)rl[1], (float)(double)rl[2]);
                    else if (U.Has(e, "yaw")) nr = Quaternion.Euler(0f, (float)McpJson.Get(e, "yaw", 0), 0f);
                    if (np != pos || nr != rot) { z.SetPosition(np); z.SetRotation(nr); ApplyToInstance(z, np, nr); movedSet.Add(z); }
                    var text = McpJson.GetStr(e, "text"); if (text != null) z.Set("text", text);
                    var tag = McpJson.GetStr(e, "tag"); if (tag != null) z.Set("tag", tag);
                    if (e.ContainsKey("ints") && e["ints"] is Dictionary<string, object> idict) foreach (var kv in idict) z.Set(kv.Key, Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture));
                    if (e.ContainsKey("strings") && e["strings"] is Dictionary<string, object> sd) foreach (var kv in sd) z.Set(kv.Key, Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
                    done++;
                }
                catch (Exception ex) { errs.Add("exception: " + ex.Message); }
            }
            if (movedSet.Count > 0) Audit.FixMoved(movedSet);
            return U.Json("{\"modified\":" + done + ",\"errors\":[" + string.Join(",", errs.Select(U.S).ToArray()) + "]}");
        }

        static bool IsDoor(string prefab) { var n = prefab.ToLowerInvariant(); return n.Contains("door") || n.Contains("gate"); }

        static List<ZDO> DoorZdos(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1;
            if (U.Has(a, "x0")) { x0 = (float)U.D(a, "x0", 0); z0 = (float)U.D(a, "z0", 0); x1 = (float)U.D(a, "x1", 0); z1 = (float)U.D(a, "z1", 0); }
            else { var x = (float)U.D(a, "x", 0); var z = (float)U.D(a, "z", 0); var rr = (float)U.D(a, "radius", 30); x0 = x - rr; x1 = x + rr; z0 = z - rr; z1 = z + rr; }
            var res = new List<ZDO>();
            foreach (var z in Zdos.InBox(x0, z0, x1, z1)) { var n = Zdos.NameOf(z.GetPrefab()); if (n != null && IsDoor(n)) res.Add(z); }
            return res;
        }

        static ToolOutput DoorsList(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            bool openOnly = McpJson.GetBool(a, "openOnly", false); int limit = (int)McpJson.Get(a, "limit", 500);
            var rows = new List<string>(); int open = 0, total = 0;
            foreach (var z in DoorZdos(a))
            {
                int st = z.GetInt("state", 0); total++; if (st != 0) open++;
                if (openOnly && st == 0) continue;
                if (rows.Count < limit) rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Zdos.NameOf(z.GetPrefab())) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"state\":" + st + ",\"plan\":" + U.S(z.GetString(PlanKey, "")) + "}");
            }
            return U.Json("{\"total\":" + total + ",\"open\":" + open + ",\"doors\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput DoorsSet(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            int want = (int)McpJson.Get(a, "state", 0); int changed = 0, same = 0;
            foreach (var z in DoorZdos(a))
            {
                if (z.GetInt("state", 0) == want) { same++; continue; }
                Log("doors_set", "modify", z, Zdos.NameOf(z.GetPrefab()), z.GetPosition(), z.GetRotation(), "", "", z.GetString(PlanKey, ""), z.GetString(ItemKey, ""), z.GetLong("creator", 0L));
                Own(z); z.Set("state", want); changed++;
            }
            return U.Json("{\"changed\":" + changed + ",\"unchanged\":" + same + ",\"state\":" + want + "}");
        }

        static ToolOutput ZDelete(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var ids = McpJson.GetList(a, "ids"); if (ids == null || ids.Count == 0) return ToolOutput.Err("ids required");
            if (ids.Count > 2000) return ToolOutput.Err("max 2000 ids per call");
            var group = McpJson.GetStr(a, "group") ?? "zdo_delete"; bool force = McpJson.GetBool(a, "force", false);
            int done = 0; var errs = new List<string>();
            foreach (var o in ids)
            {
                var id = Convert.ToString(o, CultureInfo.InvariantCulture);
                try
                {
                    var z = U.FindZdo(id); if (z == null) { errs.Add(id + ": not found"); continue; }
                    var prefab = Zdos.NameOf(z.GetPrefab());
                    if (Protected(z, prefab, force, out var why)) { errs.Add(id + ": " + why); continue; }
                    Log(group, "delete", z, prefab, z.GetPosition(), z.GetRotation(), z.GetString("text", ""), z.GetString("tag", ""), z.GetString(PlanKey, ""), z.GetString(ItemKey, ""), z.GetLong("creator", 0L));
                    Destroy(z); done++;
                }
                catch (Exception ex) { errs.Add(id + ": " + ex.Message); }
            }
            return U.Json("{\"deleted\":" + done + ",\"errors\":[" + string.Join(",", errs.Select(U.S).ToArray()) + "]}");
        }

        static Dictionary<int, string> _vars;
        static Dictionary<int, string> VarNames()
        {
            if (_vars != null) return _vars;
            _vars = new Dictionary<int, string>();
            foreach (var f in typeof(ZDOVars).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType == typeof(int) && f.Name.StartsWith("s_")) { try { _vars[(int)f.GetValue(null)] = f.Name.Substring(2); } catch { } }
            foreach (var k in new[] { "text", "tag", "creator", "scale", "health", "author", "hub_plan", "hub_key", "target", "support", "owner" }) _vars[k.GetStableHashCode()] = k;
            return _vars;
        }

        static ToolOutput Dump(Dictionary<string, object> a)
        {
            var z = U.FindZdo(McpJson.GetStr(a, "id")); if (z == null) return ToolOutput.Err("ZDO not found");
            var names = VarNames(); var sb = new StringBuilder();
            sb.Append("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Zdos.NameOf(z.GetPrefab())) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"rot\":" + U.V(z.GetRotation().eulerAngles) + ",\"owner\":" + z.GetOwner().ToString(CultureInfo.InvariantCulture) + ",\"persistent\":" + (z.Persistent ? "true" : "false") + ",\"fields\":{");
            var first = true;
            foreach (var kv in names)
            {
                string val = null;
                var h = kv.Key;
                var s = z.GetString(h, null); if (s != null) val = U.S(s);
                else { var f = z.GetFloat(h, float.NaN); if (!float.IsNaN(f)) val = U.N(f);
                    else { var i = z.GetInt(h, int.MinValue); if (i != int.MinValue) val = i.ToString(CultureInfo.InvariantCulture);
                        else { var l = z.GetLong(h, long.MinValue); if (l != long.MinValue) val = l.ToString(CultureInfo.InvariantCulture);
                            else { var v = z.GetVec3(h, new Vector3(float.NaN, 0, 0)); if (!float.IsNaN(v.x)) val = U.V(v); } } } }
                if (val == null) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append(U.S(kv.Value) + ":" + val);
            }
            return U.Json(sb.Append("}}").ToString());
        }

        static ToolOutput Locations(Dictionary<string, object> a)
        {
            var zs = ZoneSystem.instance; if (zs == null) return ToolOutput.Err("no ZoneSystem");
            var name = McpJson.GetStr(a, "name"); int limit = (int)McpJson.Get(a, "limit", 300); var rows = new List<string>(); int total = 0; string source = "instances";
            var f = typeof(ZoneSystem).GetField("m_locationInstances", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var d = f == null ? null : f.GetValue(zs) as System.Collections.IDictionary;
            if (d != null && d.Count > 0)
            {
                foreach (System.Collections.DictionaryEntry kv in d)
                {
                    var inst = kv.Value; var t = inst.GetType();
                    var loc = t.GetField("m_location").GetValue(inst); var pos = (Vector3)t.GetField("m_position").GetValue(inst); var placed = (bool)t.GetField("m_placed").GetValue(inst);
                    var nm = (string)loc.GetType().GetField("m_prefabName").GetValue(loc);
                    if (name != null && nm.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (U.Has(a, "x0") && !U.InBox(pos, a)) continue;
                    total++; if (rows.Count < limit) rows.Add("{\"name\":" + U.S(nm) + ",\"pos\":" + U.V(pos) + ",\"placed\":" + (placed ? "true" : "false") + "}");
                }
            }
            else
            {
                // a client does not generate locations: it only learns icon positions from the server (name per position)
                source = "icons";
                var fi = typeof(ZoneSystem).GetField("m_locationIcons", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var di = fi == null ? null : fi.GetValue(zs) as System.Collections.IDictionary;
                if (di != null)
                    foreach (System.Collections.DictionaryEntry kv in di)
                    {
                        var pos = (Vector3)kv.Key; var nm = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                        if (name != null && nm.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (U.Has(a, "x0") && !U.InBox(pos, a)) continue;
                        total++; if (rows.Count < limit) rows.Add("{\"name\":" + U.S(nm) + ",\"pos\":" + U.V(pos) + "}");
                    }
            }
            return U.Json("{\"source\":" + U.S(source) + ",\"total\":" + total + ",\"locations\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        static ToolOutput FindText(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var q = McpJson.GetStr(a, "q"); int limit = (int)McpJson.Get(a, "limit", 200); var rows = new List<string>(); int total = 0;
            float[] b = U.Has(a, "x0") ? new[] { (float)U.D(a, "x0", 0), (float)U.D(a, "z0", 0), (float)U.D(a, "x1", 0), (float)U.D(a, "z1", 0) } : new[] { -10500f, -10500f, 10500f, 10500f };
            foreach (var z in Zdos.InBox(b[0], b[1], b[2], b[3]))
            {
                var t = z.GetString("text", ""); var g = z.GetString("tag", "");
                if (t.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && g.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                total++; if (rows.Count < limit) rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Zdos.NameOf(z.GetPrefab())) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"text\":" + U.S(t) + ",\"tag\":" + U.S(g) + "}");
            }
            return U.Json("{\"total\":" + total + ",\"matches\":[" + string.Join(",", rows.ToArray()) + "]}");
        }

        // ------------------------------------------------------------------ journal tools
        static ToolOutput Groups(Dictionary<string, object> a)
        {
            var g = new Dictionary<string, int>(); var first = new Dictionary<string, string>(); var last = new Dictionary<string, string>();
            foreach (var e in Journal.Entries) { var k = e.Group ?? "(none)"; g[k] = g.ContainsKey(k) ? g[k] + 1 : 1; if (!first.ContainsKey(k)) first[k] = e.Time; last[k] = e.Time; }
            int limit = (int)McpJson.Get(a, "limit", 40);
            var rows = g.Keys.OrderByDescending(k => last[k]).Take(limit).Select(k => "{\"group\":" + U.S(k) + ",\"ops\":" + g[k] + ",\"first\":" + U.S(first[k]) + ",\"last\":" + U.S(last[k]) + "}").ToArray();
            return U.Json("{\"total\":" + Journal.Entries.Count + ",\"groups\":[" + string.Join(",", rows) + "]}");
        }

        static ToolOutput UndoGroup(Dictionary<string, object> a)
        {
            if (!Zdos.Ready) return ToolOutput.Err("world not ready");
            var group = McpJson.GetStr(a, "group"); int count = (int)McpJson.Get(a, "count", 0);
            if (string.IsNullOrEmpty(group) && count <= 0) return ToolOutput.Err("give group or count");
            var take = new List<JEntry>();
            for (int i = Journal.Entries.Count - 1; i >= 0; i--)
            {
                var e = Journal.Entries[i];
                if (!string.IsNullOrEmpty(group) ? (e.Group == group) : take.Count < count) take.Add(e);
                if (string.IsNullOrEmpty(group) && take.Count >= count) break;
            }
            int undone = 0; var errs = new List<string>();
            foreach (var e in take)
            {
                try
                {
                    if (e.Kind == "spawn") { var z = U.FindZdo(e.Id); if (z != null) { Own(z); ZDOMan.instance.DestroyZDO(z); } }
                    else if (e.Kind == "delete")
                    {
                        string uerr; var nz = CreateZdo(e.Prefab, e.Pos, e.Rot, e.Text, e.Tag, e.Plan ?? "", e.Key ?? "", out uerr); if (nz == null) { errs.Add(e.Id + ": " + uerr); continue; }
                        if (!string.IsNullOrEmpty(e.Text)) nz.Set("text", e.Text); if (!string.IsNullOrEmpty(e.Tag)) nz.Set("tag", e.Tag);
                        if (!string.IsNullOrEmpty(e.Plan)) { nz.Set(PlanKey, e.Plan); nz.Set(ItemKey, e.Key ?? ""); }
                        if (e.Creator != 0) nz.Set("creator", e.Creator);
                    }
                    else if (e.Kind == "modify")
                    {
                        var z = U.FindZdo(e.Id);
                        if (z == null) { errs.Add(e.Id + ": gone"); continue; }
                        Own(z); z.SetPosition(e.Pos); z.SetRotation(e.Rot); ApplyToInstance(z, e.Pos, e.Rot);
                        z.Set("text", e.Text ?? ""); z.Set("tag", e.Tag ?? "");
                        z.Set(PlanKey, e.Plan ?? ""); z.Set(ItemKey, e.Key ?? "");               // undo of an adoption removes the tag again
                    }
                    Journal.Remove(e); undone++;
                }
                catch (Exception ex) { errs.Add(e.Id + ": " + ex.Message); }
            }
            return U.Json("{\"undone\":" + undone + ",\"errors\":[" + string.Join(",", errs.Select(U.S).ToArray()) + "]}");
        }

        static ToolOutput SaveState(Dictionary<string, object> a)
        {
            if (ZNet.instance == null) return ToolOutput.Err("no ZNet");
            bool alive = false;
            try { var f = typeof(ZNet).GetField("m_saveThread", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); var th = f == null ? null : f.GetValue(ZNet.instance) as System.Threading.Thread; alive = th != null && th.IsAlive; } catch { }
            return U.Json("{\"saveStart\":" + U.N(ZNet.instance.SaveStartTime) + ",\"saveThreadStart\":" + U.N(ZNet.instance.SaveThreadStartTime) + ",\"saveDone\":" + U.N(ZNet.instance.SaveDoneTime) + ",\"threadRunning\":" + (alive ? "true" : "false") + "}");
        }

        static ToolOutput WorldSave(Dictionary<string, object> a)
        {
            if (ZNet.instance == null) return ToolOutput.Err("no ZNet");
            var t0 = Time.realtimeSinceStartup; int cleaned = 0;
            try { cleaned = Audit.SanitizeLists(); } catch { }
            try { var m = typeof(ZNet).GetMethod("SaveWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (m == null) return ToolOutput.Err("ZNet.SaveWorld not found"); m.Invoke(ZNet.instance, new object[] { false }); } catch (Exception ex) { return ToolOutput.Err("save failed: " + (ex.InnerException ?? ex).Message); }
            return U.Json("{\"requested\":true,\"listEntriesCleaned\":" + cleaned + ",\"server\":" + (ZNet.instance.IsServer() ? "true" : "false") + ",\"saveStart\":" + U.N(ZNet.instance.SaveStartTime) + ",\"saveDone\":" + U.N(ZNet.instance.SaveDoneTime) + ",\"note\":\"saving runs on a background thread: poll world_save again until saveDone > saveStart, then copy the world files\"}");
        }
    }
}
