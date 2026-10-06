// Real character control (client only): walk the sandbox character with the game's own movement, gravity and collision, and join a server from the menu.
// Input is injected by a Harmony prefix on Player.SetControls while a walk is active; nothing else about the player is touched.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Control
    {
        static readonly string HarmonyId = "hubner.ext.control." + Guid.NewGuid().ToString("N");   // one id per load: an old load can never unpatch a newer one
        static Harmony _h;
        static AutoJoin _auto;
        static Walker _walker;
        internal static string Mode = "data";
        internal static string PendingPw;      // answered by AutoJoin when the server asks for it

        public static void Register(ToolRegistry r)
        {
            r.Add("player_state", "The sandbox character: position, grounded, god mode, flying, in water, health, and whether a walk is running.",
                "{\"type\":\"object\",\"properties\":{}}", State);
            r.Add("walk_to", "Walk the character with the game's real movement (gravity, collisions, doors opened on the way). points:[[x,z]|[x,y,z],...] waypoints, run:bool, timeout seconds (default 120), arrive radius (default 0.8). Starts the walk and returns at once: poll walk_status. A walk fails with the blocking object when the character makes no progress for stuckSeconds (default 4) after jump attempts.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"run\":{\"type\":\"boolean\"},\"timeout\":{\"type\":\"number\"},\"arrive\":{\"type\":\"number\"},\"stuckSeconds\":{\"type\":\"number\"},\"nav\":{\"type\":\"boolean\"},\"agent\":{\"type\":\"string\"},\"openDoors\":{\"type\":\"boolean\"},\"jump\":{\"type\":\"boolean\"}},\"required\":[\"points\"]}", WalkTo, true);
            r.Add("walk_status", "State of the current/last walk: running, waypoint index, position, result (arrived | blocked | timeout | stopped), blocking object, trace of positions (every 1 s with grounded flag).",
                "{\"type\":\"object\",\"properties\":{\"trace\":{\"type\":\"boolean\"}}}", WalkStatus);
            r.Add("walk_stop", "Cancel the running walk.", "{\"type\":\"object\",\"properties\":{}}", WalkStop, true);
            r.Add("menu_join", "Main-menu only: pick a character profile and join a dedicated server by host:port. {profile, host, port, password}. Uses the game's own FejdStartup join path, so no clicks are needed. Also runs at startup when the game is launched with -hubner-autojoin host:port,profile[,password].",
                "{\"type\":\"object\",\"properties\":{\"profile\":{\"type\":\"string\"},\"host\":{\"type\":\"string\"},\"port\":{\"type\":\"number\"},\"password\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\"}},\"required\":[\"profile\",\"host\"]}", MenuJoin, true);
            r.Add("ready_state", "One-call readiness of the character at (x,z): teleporting, grounded, godMode, position, zone loaded, terrain ground known, instance count within radius. Poll after teleport instead of combining zone_state and player_state.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"}}}", Ready);
            r.Add("nav_path", "Ask the game's own pathfinder for a walkable route: fromX,fromZ,toX,toZ (y = TERRAIN height unless fromY/toY are given: pass the floor height for targets inside buildings, towers and on decks, otherwise the goal lies under the floor and the route detours or fails), agent (default Humanoid). Returns found + waypoints; found=false means no route exists on the navmesh (doors count as passable). Use before walking, and via walk_to nav:true.",
                "{\"type\":\"object\",\"properties\":{\"fromX\":{\"type\":\"number\"},\"fromZ\":{\"type\":\"number\"},\"fromY\":{\"type\":\"number\"},\"toY\":{\"type\":\"number\"},\"toX\":{\"type\":\"number\"},\"toZ\":{\"type\":\"number\"},\"agent\":{\"type\":\"string\"}},\"required\":[\"toX\",\"toZ\"]}", NavPath);
            r.Add("menu_password", "Answer the server password dialog of a join that is waiting for it (menu_state shows connecting:true, passwordDialog:true). The extension types it on the next tick. Use when a join was started without the right password.",
                "{\"type\":\"object\",\"properties\":{\"password\":{\"type\":\"string\"}},\"required\":[\"password\"]}", MenuPassword, true);
            r.Add("menu_state", "Where the game is: main menu (profile list, selected profile), loading, or in a world. Read-only; works at the menu.",
                "{\"type\":\"object\",\"properties\":{}}", MenuState);
            Install();
        }

        public static void Install()
        {
            try
            {
                if (_h == null)
                {
                    _h = new Harmony(HarmonyId);
                    var m = typeof(Player).GetMethod("SetControls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null) _h.Patch(m, prefix: new HarmonyMethod(typeof(Control).GetMethod("SetControlsPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                    var fu = typeof(Player).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fu != null) _h.Patch(fu, postfix: new HarmonyMethod(typeof(Control).GetMethod("PlayerTick", BindingFlags.Static | BindingFlags.NonPublic)));
                    var zn = typeof(ZNet).GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);          // exists while connecting: answers the password dialog
                    if (zn != null) _h.Patch(zn, postfix: new HarmonyMethod(typeof(Control).GetMethod("MenuTick", BindingFlags.Static | BindingFlags.NonPublic)));
                    var fe = typeof(FejdStartup).GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fe != null) _h.Patch(fe, postfix: new HarmonyMethod(typeof(Control).GetMethod("MenuTick", BindingFlags.Static | BindingFlags.NonPublic)));
                }
                if (_walker == null) { _walker = new Walker(); _auto = new AutoJoin(); }
            }
            catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; }
        }
        internal static string LastError = "";
        internal static bool Healthy(out string detail)
        {
            var methods = _h == null ? 0 : _h.GetPatchedMethods().Count();
            detail = "harmony patches=" + methods + " walker=" + (_walker != null) + " auto=" + (_auto != null) + (string.IsNullOrEmpty(LastError) ? "" : " lastError=" + LastError);
            return _h != null && methods >= 4 && _walker != null;
        }

        public static void Uninstall()
        {
            try { if (_h != null) _h.UnpatchSelf(); } catch { }
            _h = null;
            _walker = null; _auto = null;
        }

        static void PlayerTick(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            try { if (_walker != null) _walker.Tick(); if (_auto != null) _auto.Tick(); } catch { }
            try { Env.Tick(); } catch { }
        }
        static void MenuTick() { try { if (_auto != null) _auto.Tick(); Env.Tick(); } catch { } }

        // Harmony prefix: Player.SetControls(Vector3 movedir, bool attack, ..., bool jump, bool crouch, bool run, ...) - replace move/jump/run while walking.
        static void SetControlsPrefix(Player __instance, ref Vector3 movedir, ref bool jump, ref bool run)
        {
            var w = _walker;
            if (w == null || !w.Active || __instance != Player.m_localPlayer) return;
            // SetControls takes the move direction relative to the character's look yaw; convert our world direction
            Quaternion yaw = Quaternion.identity;
            try { var f = typeof(Character).GetField("m_lookYaw", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (f != null) yaw = (Quaternion)f.GetValue(__instance); } catch { }
            movedir = Quaternion.Inverse(yaw) * w.MoveDir; jump = w.Jump; run = w.Run;
            w.Jump = false;
        }

        // ------------------------------------------------------------------ tools
        static ToolOutput State(Dictionary<string, object> a)
        {
            var p = Player.m_localPlayer;
            if (p == null) return ToolOutput.Err("no local player (menu or respawning)");
            var pos = p.transform.position;
            bool tele = false; try { var tf = typeof(Player).GetField("m_teleporting", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); tele = tf != null && (bool)tf.GetValue(p); } catch { }
            bool fly = false; try { var f = typeof(Player).GetField("m_debugFly", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); fly = f != null && (bool)f.GetValue(p); } catch { }
            bool god = false; try { god = p.InGodMode(); } catch { }
            return U.Json("{\"pos\":" + U.V(pos) + ",\"grounded\":" + (p.IsOnGround() ? "true" : "false") + ",\"god\":" + (god ? "true" : "false") + ",\"flying\":" + (fly ? "true" : "false") + ",\"inWater\":" + (p.IsSwimming() ? "true" : "false")
                + ",\"health\":" + U.N(p.GetHealth()) + ",\"teleporting\":" + (tele ? "true" : "false") + ",\"walking\":" + (_walker != null && _walker.Active ? "true" : "false") + ",\"dead\":" + (p.IsDead() ? "true" : "false") + "}");
        }

        static ToolOutput WalkTo(Dictionary<string, object> a)
        {
            if (Player.m_localPlayer == null) return ToolOutput.Err("no local player");
            if (_walker == null) { Install(); if (_walker == null) return ToolOutput.Err("controller not installed: " + LastError + " [harmony=" + (_h != null) + " walker=" + (_walker != null) + "]"); }
            try { var tf = typeof(Player).GetField("m_teleporting", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (tf != null && (bool)tf.GetValue(Player.m_localPlayer)) return ToolOutput.Err("character is still teleporting: wait until player_state.teleporting is false"); } catch { }
            var pts = McpJson.GetList(a, "points"); if (pts == null || pts.Count == 0) return ToolOutput.Err("points required");
            var list = new List<Vector3>();
            foreach (var o in pts)
            {
                var l = o as List<object>; if (l == null || l.Count < 2) continue;
                if (l.Count == 2) list.Add(new Vector3((float)(double)l[0], float.NaN, (float)(double)l[1]));
                else list.Add(new Vector3((float)(double)l[0], (float)(double)l[1], (float)(double)l[2]));
            }
            if (list.Count == 0) return ToolOutput.Err("no valid points");
            if (McpJson.GetBool(a, "nav", false))                                   // replace the straight legs by the game's own navmesh route
            {
                var nav = new List<Vector3>(); var cur = Player.m_localPlayer.transform.position;
                foreach (var wp in list)
                {
                    string err; var leg = NavLeg(cur, wp, McpJson.GetStr(a, "agent"), out err);
                    if (leg == null) return ToolOutput.Err("nav: " + err);
                    nav.AddRange(leg); cur = wp;
                }
                list = nav;
            }
            try { Player.m_localPlayer.SetGodMode(true); } catch { }
            try { var f = typeof(Player).GetField("m_debugFly", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (f != null) f.SetValue(Player.m_localPlayer, false); } catch { }
            _walker.NoJump = !McpJson.GetBool(a, "jump", true); _walker.Start(list, McpJson.GetBool(a, "run", false), (float)McpJson.Get(a, "timeout", 120), (float)McpJson.Get(a, "arrive", 0.8), (float)McpJson.Get(a, "stuckSeconds", 4), McpJson.GetBool(a, "openDoors", true));
            return U.Json("{\"started\":true,\"waypoints\":" + list.Count + "}");
        }

        static ToolOutput WalkStatus(Dictionary<string, object> a)
        {
            if (_walker == null) { Install(); if (_walker == null) return ToolOutput.Err("controller not installed: " + LastError + " [harmony=" + (_h != null) + " walker=" + (_walker != null) + "]"); }
            return U.Json(_walker.StatusJson(McpJson.GetBool(a, "trace", false)));
        }

        static ToolOutput WalkStop(Dictionary<string, object> a)
        {
            if (_walker != null) _walker.Stop("stopped");
            return U.Json("{\"stopped\":true}");
        }

        static ToolOutput Ready(Dictionary<string, object> a)
        {
            var p = Player.m_localPlayer; var zs = ZoneSystem.instance;
            if (p == null) return U.Json("{\"player\":false,\"ready\":false}");
            bool tele = false; try { var tf = typeof(Player).GetField("m_teleporting", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); tele = tf != null && (bool)tf.GetValue(p); } catch { }
            var pos = p.transform.position; float x = (float)U.D(a, "x", pos.x), z = (float)U.D(a, "z", pos.z), rad = (float)U.D(a, "radius", 30);
            bool loaded = zs != null && zs.IsZoneLoaded(new Vector3(x, 0, z)); float h; bool gok = zs != null && zs.GetGroundHeight(new Vector3(x, 5000f, z), out h);
            int inst = 0; foreach (var nv in U.Instances()) { var d = nv.transform.position - new Vector3(x, nv.transform.position.y, z); if (d.x * d.x + d.z * d.z < rad * rad) inst++; }
            bool near = U.Has(a, "x") ? ((pos.x - x) * (pos.x - x) + (pos.z - z) * (pos.z - z) < 36f) : true;
            bool god = false; try { god = p.InGodMode(); } catch { }
            bool ready = !tele && p.IsOnGround() && loaded && gok && near;
            return U.Json("{\"player\":true,\"ready\":" + (ready ? "true" : "false") + ",\"teleporting\":" + (tele ? "true" : "false") + ",\"grounded\":" + (p.IsOnGround() ? "true" : "false") + ",\"god\":" + (god ? "true" : "false") + ",\"pos\":" + U.V(pos) + ",\"zoneLoaded\":" + (loaded ? "true" : "false") + ",\"groundKnown\":" + (gok ? "true" : "false") + ",\"nearTarget\":" + (near ? "true" : "false") + ",\"instances\":" + inst + "}");
        }

        [ThreadStatic] static bool _navAllowPartial;
        static List<Vector3> NavLeg(Vector3 from, Vector3 to, string agent, out string err)
        {
            err = null; var res = new List<Vector3>();
            if (Pathfinding.instance == null) { err = "no Pathfinding instance"; return null; }
            Pathfinding.AgentType at;
            try { at = (Pathfinding.AgentType)Enum.Parse(typeof(Pathfinding.AgentType), agent ?? "Humanoid", true); } catch { err = "unknown agent " + agent; return null; }
            float h;
            if (float.IsNaN(from.y) && ZoneSystem.instance.GetGroundHeight(new Vector3(from.x, 5000f, from.z), out h)) from.y = h;
            if (float.IsNaN(to.y) && ZoneSystem.instance.GetGroundHeight(new Vector3(to.x, 5000f, to.z), out h)) to.y = h;
            bool ok = Pathfinding.instance.GetPath(from, to, res, at, false, true, false);
            if (!ok || res.Count == 0) { err = "no route from " + U.V(from) + " to " + U.V(to); return null; }
            var last = res[res.Count - 1]; float gap = Mathf.Sqrt((last.x - to.x) * (last.x - to.x) + (last.z - to.z) * (last.z - to.z));
            if (gap > 2.5f) { err = "route ends " + U.N(Math.Round(gap, 1)) + " m short of the target at " + U.V(last) + " (target unreachable on the navmesh: a closed door or gate blocks it, the target is inside a collider, or the y is wrong - pass toY for floors above terrain)"; if (!_navAllowPartial) return null; }
            return res;
        }

        static ToolOutput NavPath(Dictionary<string, object> a)
        {
            var p = Player.m_localPlayer; var from = p != null ? p.transform.position : Vector3.zero;
            if (U.Has(a, "fromX")) from = new Vector3((float)U.D(a, "fromX", 0), U.Has(a, "fromY") ? (float)U.D(a, "fromY", 0) : float.NaN, (float)U.D(a, "fromZ", 0));
            var to = new Vector3((float)U.D(a, "toX", 0), U.Has(a, "toY") ? (float)U.D(a, "toY", 0) : float.NaN, (float)U.D(a, "toZ", 0));
            string err; _navAllowPartial = true; var path = NavLeg(from, to, McpJson.GetStr(a, "agent"), out err); _navAllowPartial = false;
            if (path == null) return U.Json("{\"found\":false,\"reason\":" + U.S(err) + "}");
            if (err != null) return U.Json("{\"found\":false,\"partial\":true,\"reason\":" + U.S(err) + ",\"count\":" + path.Count + ",\"points\":[" + string.Join(",", path.Select(v => "[" + U.N(Math.Round(v.x, 2)) + "," + U.N(Math.Round(v.y, 2)) + "," + U.N(Math.Round(v.z, 2)) + "]").ToArray()) + "]}");
            return U.Json("{\"found\":true,\"count\":" + path.Count + ",\"points\":[" + string.Join(",", path.Select(v => "[" + U.N(Math.Round(v.x, 2)) + "," + U.N(Math.Round(v.y, 2)) + "," + U.N(Math.Round(v.z, 2)) + "]").ToArray()) + "]}");
        }

        // ------------------------------------------------------------------ menu
        static ToolOutput MenuState(Dictionary<string, object> a)
        {
            var f = FejdStartup.instance;
            var sb = new StringBuilder("{\"inWorld\":" + (Player.m_localPlayer != null ? "true" : "false") + ",\"menu\":" + (f != null ? "true" : "false"));
            if (f != null)
            {
                var profiles = new List<string>();
                try { var fl = typeof(FejdStartup).GetField("m_profiles", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); var l = fl == null ? null : fl.GetValue(f) as System.Collections.IList; if (l != null) foreach (var pf in l) { var nm = pf.GetType().GetMethod("GetName"); profiles.Add(nm != null ? (string)nm.Invoke(pf, null) : pf.ToString()); } } catch { }
                sb.Append(",\"profiles\":[" + string.Join(",", profiles.Select(U.S).ToArray()) + "]");
            }
            bool dlg = false; try { dlg = ZNet.instance != null && ZNet.IsPasswordDialogShowing(); } catch { }
            sb.Append(",\"connecting\":" + (ZNet.instance != null && Player.m_localPlayer == null ? "true" : "false") + ",\"passwordDialog\":" + (dlg ? "true" : "false") + ",\"passwordQueued\":" + (string.IsNullOrEmpty(PendingPw) ? "false" : "true"));
            return U.Json(sb.Append("}").ToString());
        }

        static ToolOutput MenuPassword(Dictionary<string, object> a)
        {
            var pw = McpJson.GetStr(a, "password"); if (string.IsNullOrEmpty(pw)) return ToolOutput.Err("password required");
            PendingPw = pw; bool dlg = false; try { dlg = ZNet.instance != null && ZNet.IsPasswordDialogShowing(); } catch { }
            return U.Json("{\"queued\":true,\"dialogShowing\":" + (dlg ? "true" : "false") + "}");
        }

        internal static string DoJoin(string profile, string host, int port, string password)
        {
            var f = FejdStartup.instance; if (f == null) return "not at the main menu";
            try
            {
                var sp = typeof(FejdStartup).GetMethod("SetSelectedProfile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (sp == null) return "SetSelectedProfile not found";
                sp.Invoke(f, new object[] { profile });
                // preferred: the game's own auto-join entry point (host:port)
                var aj = typeof(FejdStartup).GetMethod("AutoJoinServer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (!string.IsNullOrEmpty(password))
                {
                    var pw = typeof(FejdStartup).GetField("m_passwordInputField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    // the password is asked by the game after the join starts; remembered passwords are used if present
                }
                // second path (when AutoJoinServer only queues): build the join data ourselves and call the private JoinServer()
                if (Mode == "data")
                {
                    var ded = new ServerJoinDataDedicated(host, (ushort)port);
                    var data = new ServerJoinData(ded);
                    PendingPw = string.IsNullOrEmpty(password) ? null : password;
                    typeof(FejdStartup).GetMethod("SetServerToJoin", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(f, new object[] { data });
                    typeof(FejdStartup).GetMethod("JoinServer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null).Invoke(f, null);
                    return "join requested (data)";
                }
                if (aj == null) return "AutoJoinServer not found";
                PendingPw = string.IsNullOrEmpty(password) ? null : password;
                aj.Invoke(f, new object[] { host + ":" + port.ToString(CultureInfo.InvariantCulture) });
                return "join requested";
            }
            catch (Exception ex) { return "join failed: " + (ex.InnerException ?? ex).Message; }
        }

        static ToolOutput MenuJoin(Dictionary<string, object> a)
        {
            Mode = McpJson.GetStr(a, "mode") ?? "auto";
            var msg = DoJoin(McpJson.GetStr(a, "profile"), McpJson.GetStr(a, "host"), (int)McpJson.Get(a, "port", 2456), McpJson.GetStr(a, "password"));
            return msg.Contains("failed") || msg.Contains("not ") ? ToolOutput.Err(msg) : U.Json("{\"result\":" + U.S(msg) + "}");
        }

        // ------------------------------------------------------------------ walker
        internal sealed class Walker
        {
            public bool Active; public Vector3 MoveDir; public bool Jump, Run, NoJump;                       // NoJump: the walker never jumps, so a route that needs a hop to get over something fails (accessibility test)
            List<Vector3> _pts = new List<Vector3>(); int _i; float _t0, _timeout, _arrive, _stuckSecs; bool _doors;
            string _result = "idle", _blocker = ""; int _doorOpens; float _strafeUntil, _strafeSide; Vector3 _lastProg; float _lastProgT; int _jumps;
            readonly List<string> _trace = new List<string>(); float _nextTrace;

            public void Start(List<Vector3> pts, bool run, float timeout, float arrive, float stuck, bool doors)
            {
                _pts = pts; _i = 0; Run = run; _timeout = timeout; _arrive = arrive; _stuckSecs = stuck; _doors = doors;
                _t0 = Time.time; _result = "walking"; _blocker = ""; _jumps = 0; _doorOpens = 0; _trace.Clear(); _nextTrace = 0f;
                var p = Player.m_localPlayer; _lastProg = p.transform.position; _lastProgT = Time.time; Active = true;
            }

            public void Stop(string result) { Active = false; MoveDir = Vector3.zero; Jump = false; _result = result; }

            public void Tick()
            {
                var p = Player.m_localPlayer;
                if (!Active) return;
                if (p == null) { Stop("no player"); return; }
                var pos = p.transform.position;
                if (Time.time - _t0 > _timeout)
                {
                    if (_i < _pts.Count) { var tf = new Vector3(_pts[_i].x - pos.x, 0f, _pts[_i].z - pos.z); if (tf.sqrMagnitude > 0.01f) _blocker = Blocker(p, tf.normalized); }
                    Stop("timeout"); return;
                }
                if (Time.time >= _nextTrace) { _nextTrace = Time.time + 1f; _trace.Add("[" + U.N(pos.x) + "," + U.N(pos.y) + "," + U.N(pos.z) + "," + (p.IsOnGround() ? "1" : "0") + "]"); }
                var tgt = _pts[_i];
                var flat = new Vector3(tgt.x - pos.x, 0f, tgt.z - pos.z);
                if (flat.magnitude < _arrive)
                {
                    _i++;
                    if (_i >= _pts.Count) { Stop("arrived"); return; }
                    _lastProg = pos; _lastProgT = Time.time; return;
                }
                var dir = flat.normalized; MoveDir = dir;
                if (Time.time < _strafeUntil) MoveDir = (dir + Vector3.Cross(Vector3.up, dir) * _strafeSide * 0.9f).normalized;     // sidestep: an open door leaf can stand across the aim line
                try { p.SetLookDir(dir, 0f); } catch { }
                // progress watchdog: jump when stuck, open doors in the way, give up with the blocker named
                if ((pos - _lastProg).magnitude > 0.25f) { _lastProg = pos; _lastProgT = Time.time; _jumps = 0; }
                else if (Time.time - _lastProgT > 1.2f)
                {
                    if (_doors && TryDoor(p, dir)) { _lastProgT = Time.time; return; }
                    if (_doorOpens > 0 && Time.time >= _strafeUntil && Time.time - _lastProgT > 1.6f)
                    {
                        var o2 = pos + Vector3.up * 1.0f; var perp = Vector3.Cross(Vector3.up, dir);
                        float cl = 0f, cr = 0f; RaycastHit hh;
                        cl = Physics.Raycast(o2, (dir + perp * 0.6f).normalized, out hh, 2.5f, ~0, QueryTriggerInteraction.Ignore) ? hh.distance : 2.5f;
                        cr = Physics.Raycast(o2, (dir - perp * 0.6f).normalized, out hh, 2.5f, ~0, QueryTriggerInteraction.Ignore) ? hh.distance : 2.5f;
                        _strafeSide = cl >= cr ? 1f : -1f; _strafeUntil = Time.time + 1.4f; _lastProgT = Time.time; return;
                    }
                    if (_jumps < 3 && !NoJump) { Jump = true; _jumps++; _lastProgT = Time.time - 0.6f; }
                    else if (Time.time - _lastProgT > _stuckSecs) { _blocker = Blocker(p, dir); Stop("blocked"); }
                }
            }

            bool TryDoor(Player p, Vector3 dir)
            {
                // double doors leave a seam between the leaves that a thin ray passes through: look for any closed door within reach of the character's front
                var o = p.transform.position + Vector3.up * 1.0f + dir * 0.9f; bool any = false;
                var seen = new HashSet<Door>();
                foreach (var c in Physics.OverlapSphere(o, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                {
                    var d = c.GetComponentInParent<Door>();
                    if (d == null || !seen.Add(d)) continue;
                    try
                    {
                        var nv = d.GetComponentInParent<ZNetView>();
                        if (nv != null && nv.GetZDO() != null && nv.GetZDO().GetInt("state", 0) != 0) continue;     // already open: a second Interact would close it again
                        if (d.Interact(p, false, false)) { _doorOpens++; any = true; }
                    }
                    catch { }
                }
                return any;
            }

            internal static string Headroom(Vector3 pos)
            {
                float best = 99f; string what = "open sky";
                foreach (var h in Physics.RaycastAll(pos + Vector3.up * 0.1f, Vector3.up, 12f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.collider.GetComponentInParent<Player>() != null) continue;
                    if (h.distance < best) { best = h.distance; var nv = h.collider.GetComponentInParent<ZNetView>(); what = nv != null ? U.PrefabName(nv) : h.collider.name; }
                }
                return "headroom " + U.N(best) + " m (" + what + ")";
            }

            string Blocker(Player p, Vector3 dir)
            {
                var o = p.transform.position + Vector3.up * 0.9f;
                RaycastHit best = default; bool found = false; float bd = 1e9f;
                foreach (var h in Physics.RaycastAll(o, dir, 2f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.collider.GetComponentInParent<Player>() != null) continue;
                    if (h.distance < bd) { bd = h.distance; best = h; found = true; }
                }
                if (!found) return "nothing in front (hole or steep slope?) at " + U.V(p.transform.position);
                var nv = best.collider.GetComponentInParent<ZNetView>();
                return (nv != null ? U.PrefabName(nv) : best.collider.name) + " at " + U.V(best.point) + " (" + U.N(best.distance) + " m ahead); " + Headroom(p.transform.position);
            }

            public string StatusJson(bool trace)
            {
                var p = Player.m_localPlayer; var pos = p != null ? p.transform.position : Vector3.zero;
                var sb = new StringBuilder("{\"running\":" + (Active ? "true" : "false") + ",\"result\":" + U.S(_result) + ",\"index\":" + _i + ",\"of\":" + _pts.Count + ",\"pos\":" + U.V(pos) + ",\"grounded\":" + (p != null && p.IsOnGround() ? "true" : "false") + ",\"elapsed\":" + U.N(Time.time - _t0));
                sb.Append(",\"doorsOpened\":" + _doorOpens);
                if (!string.IsNullOrEmpty(_blocker)) sb.Append(",\"blocker\":" + U.S(_blocker));
                if (trace) sb.Append(",\"trace\":[" + string.Join(",", _trace.ToArray()) + "]");
                return sb.Append('}').ToString();
            }
        }

        // ------------------------------------------------------------------ auto join at startup: -hubner-autojoin host:port,profile[,password]
        internal sealed class AutoJoin
        {
            string _spec, _pw; float _next; bool _done; int _tries;
            public AutoJoin()
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-hubner-autojoin") _spec = args[i + 1];
                if (string.IsNullOrEmpty(_spec)) _done = true;
                else { var sp = _spec.Split(','); if (sp.Length > 2) _pw = sp[2]; }
            }
            float _godNext;
            public void Tick()
            {
                // the sandbox character is a tool: keep it invulnerable across respawns (it was being killed by wandering skeletons and trolls between teleports)
                if (Time.time >= _godNext) { _godNext = Time.time + 1f; var pl = Player.m_localPlayer; try { if (pl != null && !pl.InGodMode()) pl.SetGodMode(true); } catch { } }
                if (!string.IsNullOrEmpty(_pw)) PendingPw = _pw;
                if (!string.IsNullOrEmpty(PendingPw) && ZNet.instance != null)                // answer the server's password dialog when it appears
                {
                    try { if (ZNet.IsPasswordDialogShowing()) { var m = typeof(ZNet).GetMethod("OnPasswordEntered", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public); if (m != null) { m.Invoke(m.IsStatic ? null : (object)ZNet.instance, new object[] { PendingPw }); PendingPw = null; _pw = null; } } } catch { }
                }
                if (_done && string.IsNullOrEmpty(PendingPw)) return;
                if (_done || Time.time < _next) return;
                _next = Time.time + 5f;
                if (Player.m_localPlayer != null) { _done = true; _pw = null; return; }
                if (FejdStartup.instance == null || _tries >= 120) return;          // 120 attempts x 5 s: the menu can take minutes to come up (Steam/PlayFab login), the old 20 x 3 s gave up before it did
                if (ZNet.instance != null) return;                                   // already connecting: the password dialog is answered above
                var parts = _spec.Split(',');
                var hp = parts[0].Split(':'); int port = hp.Length > 1 ? int.Parse(hp[1], CultureInfo.InvariantCulture) : 2456;
                _tries++;
                Mode = "data";                                                       // AutoJoinServer only queues at the menu; the data path builds the join itself
                var res = DoJoin(parts.Length > 1 ? parts[1] : "ClaudeEyes", hp[0], port, parts.Length > 2 ? parts[2] : null);
                Debug.Log("[hubner autojoin] attempt " + _tries + ": " + res);
            }
        }
    }
}
