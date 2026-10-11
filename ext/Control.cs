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
        /// <summary>Who has the controls: the walker yields to the human for 1.5 s after any input and stops its clocks meanwhile.</summary>
        internal static readonly UserOverride User = new UserOverride();
        internal static string PendingPw;      // answered by AutoJoin when the server asks for it

        public static void Register(ToolRegistry r)
        {
            r.Add("player_state", "The sandbox character: position, grounded, god mode, flying, in water, health, and whether a walk is running.",
                "{\"type\":\"object\",\"properties\":{}}", State);
            r.Add("walk_to", "Walk the character with the game's real movement (gravity, collisions, doors opened on the way). points:[[x,z]|[x,y,z],...] waypoints, run:bool, timeout seconds (default 120), arrive radius (default 0.8). Starts the walk and returns at once: poll walk_status. The last point counts as reached only within arriveHeight (default 2 m, 0 ignores it) of its y when you gave one: a stand a storey away ends the walk as wrong_storey. A walk fails with the blocking object when the character makes no progress for stuckSeconds (default 4) after jump attempts; an overloaded character that cannot regenerate stamina ends as blocked (encumbered).",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"run\":{\"type\":\"boolean\"},\"timeout\":{\"type\":\"number\"},\"arrive\":{\"type\":\"number\"},\"arriveHeight\":{\"type\":\"number\"},\"stuckSeconds\":{\"type\":\"number\"},\"nav\":{\"type\":\"boolean\"},\"agent\":{\"type\":\"string\"},\"openDoors\":{\"type\":\"boolean\"},\"jump\":{\"type\":\"boolean\"}},\"required\":[\"points\"]}", WalkTo, true);
            r.Add("walk_status", "State of the current/last walk: running, waypoint index, position, result (arrived | wrong_storey | teleported | blocked | timeout | stopped), blocking object, trace of positions (every 1 s with grounded flag).",
                "{\"type\":\"object\",\"properties\":{\"trace\":{\"type\":\"boolean\"}}}", WalkStatus);
            r.Add("walk_stop", "Cancel the running walk.", "{\"type\":\"object\",\"properties\":{}}", WalkStop, true);
            r.Add("menu_join", "Main-menu only: pick a character profile and join a dedicated server by host:port. {profile, host, port}. Uses the game's own FejdStartup join path, so no clicks are needed. The server password is never a tool argument (it would end up in the model's context): set the environment variable HUBNER_AUTOJOIN_PASSWORD before starting the game. Also runs at startup when the game is launched with -hubner-autojoin host:port,profile.",
                "{\"type\":\"object\",\"properties\":{\"profile\":{\"type\":\"string\"},\"host\":{\"type\":\"string\"},\"port\":{\"type\":\"number\"},\"mode\":{\"type\":\"string\",\"description\":\"data (default, builds the join itself) | auto (the game's AutoJoinServer)\"}},\"required\":[\"profile\",\"host\"]}", MenuJoin, true);
            r.Add("ready_state", "One-call readiness of the character at (x,z): teleporting, grounded, godMode, position, zone loaded, terrain ground known, instance count within radius. Poll after teleport instead of combining zone_state and player_state.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"}}}", Ready);
            r.Add("nav_path", "Ask the game's own pathfinder for a walkable route: fromX,fromZ,toX,toZ (y = TERRAIN height unless fromY/toY are given: pass the floor height for targets inside buildings, towers and on decks, otherwise the goal lies under the floor and the route detours or fails), agent (default Humanoid). Returns found + waypoints; found=false means no route exists on the navmesh (doors count as passable). Use before walking, and via walk_to nav:true.",
                "{\"type\":\"object\",\"properties\":{\"fromX\":{\"type\":\"number\"},\"fromZ\":{\"type\":\"number\"},\"fromY\":{\"type\":\"number\"},\"toY\":{\"type\":\"number\"},\"toX\":{\"type\":\"number\"},\"toZ\":{\"type\":\"number\"},\"agent\":{\"type\":\"string\"}},\"required\":[\"toX\",\"toZ\"]}", NavPath);
            r.Add("menu_password", "Answer the server password dialog of a join that is waiting for it (menu_state shows connecting:true, passwordDialog:true) with the password from the environment variable HUBNER_AUTOJOIN_PASSWORD. The password is not a tool argument. The extension types it on the next tick.",
                "{\"type\":\"object\",\"properties\":{}}", MenuPassword, true);
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
                    if (m != null)
                    {
                        // capture what the human really pressed before anyone writes (First), write the walker's input after every other mod has had its say (Last)
                        _h.Patch(m, prefix: new HarmonyMethod(typeof(Control).GetMethod("CapturePrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                        _h.Patch(m, prefix: new HarmonyMethod(typeof(Control).GetMethod("SetControlsPrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last });
                    }
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
            try { if (_walker != null) _walker.Tick(); if (_auto != null) _auto.Tick(); } catch (Exception ex) { U.Once("PlayerTick", ex); }
            try { Env.Tick(); } catch (Exception ex) { U.Once("PlayerTick.env", ex); }
        }
        static void MenuTick() { try { if (_auto != null) _auto.Tick(); Env.Tick(); } catch (Exception ex) { U.Once("MenuTick", ex); } }

        // reflective field handles, resolved once (SetControlsPrefix runs every physics tick)
        const BindingFlags FI = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo FLookYaw = typeof(Character).GetField("m_lookYaw", FI), FTeleporting = typeof(Player).GetField("m_teleporting", FI), FDebugFly = typeof(Player).GetField("m_debugFly", FI);
        internal static bool Teleporting(Player p) { try { return FTeleporting != null && (bool)FTeleporting.GetValue(p); } catch (Exception ex) { U.Once("m_teleporting", ex); return false; } }
        static bool Flying(Player p) { try { return FDebugFly != null && (bool)FDebugFly.GetValue(p); } catch (Exception ex) { U.Once("m_debugFly", ex); return false; } }

        // Harmony prefix: Player.SetControls(Vector3 movedir, bool attack, ..., bool jump, bool crouch, bool run, ...) - replace move/jump/run while walking.
        static void CapturePrefix(Player __instance, Vector3 movedir, bool attack, bool secondaryAttack, bool block, bool jump, bool crouch, bool dodge)
        {
            if (__instance != Player.m_localPlayer) return;
            if (movedir.sqrMagnitude > 0.0001f || attack || secondaryAttack || block || jump || crouch || dodge) User.Touch(Time.time);
        }

        static void SetControlsPrefix(Player __instance, ref Vector3 movedir, ref bool jump, ref bool run)
        {
            var w = _walker;
            if (w == null || !w.Active || __instance != Player.m_localPlayer) return;
            if (User.Active(Time.time)) return;                                    // the human is driving
            // SetControls takes the move direction relative to the character's look yaw; convert our world direction
            Quaternion yaw = Quaternion.identity;
            try { if (FLookYaw != null) yaw = (Quaternion)FLookYaw.GetValue(__instance); } catch (Exception ex) { U.Once("m_lookYaw", ex); }
            movedir = Quaternion.Inverse(yaw) * w.MoveDir; jump = w.Jump; run = w.Run;
            w.Jump = false;
        }

        // ------------------------------------------------------------------ tools
        static ToolOutput State(Dictionary<string, object> a)
        {
            var p = Player.m_localPlayer;
            if (p == null) return ToolOutput.Err("no local player (menu or respawning)");
            var pos = p.transform.position;
            bool tele = Teleporting(p), fly = Flying(p);
            bool god = false; try { god = p.InGodMode(); } catch (Exception ex) { U.Once("InGodMode", ex); }
            return U.Json("{\"pos\":" + U.V(pos) + ",\"grounded\":" + (p.IsOnGround() ? "true" : "false") + ",\"god\":" + (god ? "true" : "false") + ",\"flying\":" + (fly ? "true" : "false") + ",\"inWater\":" + (p.IsSwimming() ? "true" : "false")
                + ",\"health\":" + U.N(p.GetHealth()) + ",\"teleporting\":" + (tele ? "true" : "false") + ",\"walking\":" + (_walker != null && _walker.Active ? "true" : "false") + ",\"dead\":" + (p.IsDead() ? "true" : "false") + "}");
        }

        static ToolOutput WalkTo(Dictionary<string, object> a)
        {
            if (Player.m_localPlayer == null) return ToolOutput.Err("no local player");
            if (_walker == null) { Install(); if (_walker == null) return ToolOutput.Err("controller not installed: " + LastError + " [harmony=" + (_h != null) + " walker=" + (_walker != null) + "]"); }
            if (Teleporting(Player.m_localPlayer)) return ToolOutput.Err("character is still teleporting: wait until player_state.teleporting is false");
            var pts = McpJson.GetList(a, "points"); if (pts == null || pts.Count == 0) return ToolOutput.Err("points required");
            var list = new List<Vector3>();
            foreach (var o in pts)
            {
                var l = o as List<object>; if (l == null || l.Count < 2) continue;
                var v = U.Vec(l); if (float.IsNaN(v.x) || float.IsNaN(v.z)) return ToolOutput.Err("points must be [x,z] or [x,y,z] numbers");
                list.Add(v);
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
            try { if (U.IsSandbox(Player.m_localPlayer)) Player.m_localPlayer.SetGodMode(true); } catch (Exception ex) { U.Once("SetGodMode", ex); }       // only the sandbox character is made invulnerable
            try { if (FDebugFly != null) FDebugFly.SetValue(Player.m_localPlayer, false); } catch (Exception ex) { U.Once("m_debugFly.set", ex); }
            _walker.NoJump = !McpJson.GetBool(a, "jump", true); _walker.ArriveHeight = (float)McpJson.Get(a, "arriveHeight", WalkFacts.StandReach); _walker.Start(list, McpJson.GetBool(a, "run", false), (float)McpJson.Get(a, "timeout", 120), (float)McpJson.Get(a, "arrive", 0.8), (float)McpJson.Get(a, "stuckSeconds", 4), McpJson.GetBool(a, "openDoors", true));
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
            bool tele = Teleporting(p);
            var pos = p.transform.position; float x = (float)U.D(a, "x", pos.x), z = (float)U.D(a, "z", pos.z), rad = (float)U.D(a, "radius", 30);
            bool loaded = zs != null && zs.IsZoneLoaded(new Vector3(x, 0, z)); float h; bool gok = zs != null && zs.GetGroundHeight(new Vector3(x, 5000f, z), out h);
            int inst = 0; foreach (var nv in U.Instances()) { var d = nv.transform.position - new Vector3(x, nv.transform.position.y, z); if (d.x * d.x + d.z * d.z < rad * rad) inst++; }
            bool near = U.Has(a, "x") ? ((pos.x - x) * (pos.x - x) + (pos.z - z) * (pos.z - z) < 36f) : true;
            bool god = false; try { god = p.InGodMode(); } catch { }
            bool ready = !tele && p.IsOnGround() && loaded && gok && near;
            return U.Json("{\"player\":true,\"ready\":" + (ready ? "true" : "false") + ",\"teleporting\":" + (tele ? "true" : "false") + ",\"grounded\":" + (p.IsOnGround() ? "true" : "false") + ",\"god\":" + (god ? "true" : "false") + ",\"pos\":" + U.V(pos) + ",\"zoneLoaded\":" + (loaded ? "true" : "false") + ",\"groundKnown\":" + (gok ? "true" : "false") + ",\"nearTarget\":" + (near ? "true" : "false") + ",\"instances\":" + inst + "}");
        }

        static List<Vector3> NavLeg(Vector3 from, Vector3 to, string agent, out string err, bool allowPartial = false)
        {
            err = null; var res = new List<Vector3>();
            if (Pathfinding.instance == null) { err = "no Pathfinding instance"; return null; }
            Pathfinding.AgentType at;
            try { at = (Pathfinding.AgentType)Enum.Parse(typeof(Pathfinding.AgentType), agent ?? "Humanoid", true); } catch { err = "unknown agent " + agent; return null; }
            float h;
            if (float.IsNaN(from.y) && ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(from.x, 5000f, from.z), out h)) from.y = h;
            if (float.IsNaN(to.y) && ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(to.x, 5000f, to.z), out h)) to.y = h;
            bool ok = Pathfinding.instance.GetPath(from, to, res, at, false, true, false);
            if (!ok || res.Count == 0) { err = "no route from " + U.V(from) + " to " + U.V(to); return null; }
            var last = res[res.Count - 1]; float gap = Mathf.Sqrt((last.x - to.x) * (last.x - to.x) + (last.z - to.z) * (last.z - to.z));
            if (gap > 2.5f) { err = "route ends " + U.N(Math.Round(gap, 1)) + " m short of the target at " + U.V(last) + " (target unreachable on the navmesh: a closed door or gate blocks it, the target is inside a collider, or the y is wrong - pass toY for floors above terrain)"; if (!allowPartial) return null; }
            return res;
        }

        static ToolOutput NavPath(Dictionary<string, object> a)
        {
            var p = Player.m_localPlayer; var from = p != null ? p.transform.position : Vector3.zero;
            if (U.Has(a, "fromX")) from = new Vector3((float)U.D(a, "fromX", 0), U.Has(a, "fromY") ? (float)U.D(a, "fromY", 0) : float.NaN, (float)U.D(a, "fromZ", 0));
            var to = new Vector3((float)U.D(a, "toX", 0), U.Has(a, "toY") ? (float)U.D(a, "toY", 0) : float.NaN, (float)U.D(a, "toZ", 0));
            string err; var path = NavLeg(from, to, McpJson.GetStr(a, "agent"), out err, true);
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
            var pw = Environment.GetEnvironmentVariable("HUBNER_AUTOJOIN_PASSWORD");
            if (string.IsNullOrEmpty(pw)) return ToolOutput.Err("HUBNER_AUTOJOIN_PASSWORD is not set in the game's environment (the password is deliberately not a tool argument)");
            PendingPw = pw; bool dlg = false; try { dlg = ZNet.instance != null && ZNet.IsPasswordDialogShowing(); } catch (Exception ex) { U.Once("IsPasswordDialogShowing", ex); }
            return U.Json("{\"queued\":true,\"dialogShowing\":" + (dlg ? "true" : "false") + "}");
        }

        internal static string DoJoin(string profile, string host, int port, string password)
        {
            if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(host)) return "join failed: profile and host are required";
            if (port < 1 || port > 65535) return "join failed: port must be 1-65535";
            var f = FejdStartup.instance; if (f == null) return "not at the main menu";
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var sp = typeof(FejdStartup).GetMethod("SetSelectedProfile", any, null, new[] { typeof(string) }, null);
                if (sp == null) return "join failed: SetSelectedProfile not found";
                sp.Invoke(f, new object[] { profile });
                PendingPw = string.IsNullOrEmpty(password) ? null : password;              // the game asks for the password after the join starts; AutoJoin.Tick answers the dialog
                if (Mode == "auto")
                {
                    // the game's own entry point (host:port); at the menu it may only queue the join
                    var aj = typeof(FejdStartup).GetMethod("AutoJoinServer", any, null, new[] { typeof(string) }, null);
                    if (aj == null) return "join failed: AutoJoinServer not found";
                    aj.Invoke(f, new object[] { host + ":" + port.ToString(CultureInfo.InvariantCulture) });
                    return "join requested (auto)";
                }
                // default: build the join data ourselves and call the private JoinServer()
                var data = new ServerJoinData(new ServerJoinDataDedicated(host, (ushort)port));
                typeof(FejdStartup).GetMethod("SetServerToJoin", any).Invoke(f, new object[] { data });
                typeof(FejdStartup).GetMethod("JoinServer", any, null, Type.EmptyTypes, null).Invoke(f, null);
                return "join requested (data)";
            }
            catch (Exception ex) { return "join failed: " + (ex.InnerException ?? ex).Message; }
        }

        static ToolOutput MenuJoin(Dictionary<string, object> a)
        {
            Mode = McpJson.GetStr(a, "mode") == "auto" ? "auto" : "data";
            var port = McpJson.Get(a, "port", 2456);
            var msg = DoJoin(McpJson.GetStr(a, "profile"), McpJson.GetStr(a, "host"), port >= 1 && port <= 65535 ? (int)port : 0, Environment.GetEnvironmentVariable("HUBNER_AUTOJOIN_PASSWORD"));
            return msg.StartsWith("join requested", StringComparison.Ordinal) ? U.Json("{\"result\":" + U.S(msg) + "}") : ToolOutput.Err(msg);
        }

        // ------------------------------------------------------------------ walker
        internal sealed class Walker
        {
            public bool Active; public Vector3 MoveDir; public bool Jump, Run, NoJump;                       // NoJump: the walker never jumps, so a route that needs a hop to get over something fails (accessibility test)
            List<Vector3> _pts = new List<Vector3>(); int _i; float _t0, _timeout, _arrive, _stuckSecs; bool _doors;
            string _result = "idle", _blocker = ""; float _strafeUntil, _strafeSide, _restUntil, _runUntil; bool _userRun;
            readonly List<string> _trace = new List<string>(); float _nextTrace;

            // route and progress: the body steers at a point a little ahead ON the line (Pursuit), never at a raw waypoint
            List<double[]> _line = new List<double[]>(); int _seg; double _segT;
            Vector3 _lastProg; float _lastProgT, _lastRemain, _lastTick; Vector3 _prevPos;
            // stall ladder
            int _stallsTotal, _spotStalls, _sideSteps, _reroutes; Vector3 _spotPos; string _lastStall = "";
            // doors
            int _doorPresses, _doorOpens; string _lockedDoor = "";
            readonly Dictionary<int, float> _pressedAt = new Dictionary<int, float>(); readonly Dictionary<int, float> _pending = new Dictionary<int, float>(); float _nextDoorLook;
            float _yielded;
            public float ArriveHeight = WalkFacts.StandReach; float _wrongStoreySince = -1f;

            public void Start(List<Vector3> pts, bool run, float timeout, float arrive, float stuck, bool doors)
            {
                _pts = pts; _i = 0; _userRun = run; Run = run; _timeout = timeout; _arrive = arrive; _stuckSecs = stuck; _doors = doors;
                _t0 = Time.time; _result = "walking"; _blocker = ""; _doorPresses = 0; _doorOpens = 0; _lockedDoor = ""; _trace.Clear(); _nextTrace = 0f;
                _stallsTotal = 0; _spotStalls = 0; _sideSteps = 0; _reroutes = 0; _lastStall = ""; _strafeUntil = 0; _restUntil = 0; _runUntil = 0; _yielded = 0;
                _pressedAt.Clear(); _pending.Clear(); _wrongStoreySince = -1f;
                var p = Player.m_localPlayer; _lastProg = p.transform.position; _prevPos = _lastProg; _spotPos = _lastProg; _lastProgT = Time.time; _lastTick = Time.time; Active = true;
                BuildLine(_lastProg);
            }

            void BuildLine(Vector3 from)
            {
                _line = new List<double[]> { new double[] { from.x, from.z } };
                for (var k = _i; k < _pts.Count; k++) _line.Add(new double[] { _pts[k].x, _pts[k].z });
                _seg = 0; _segT = 0; _lastRemain = Remaining();
            }

            float Remaining()
            {
                double total = 0;
                for (var s = _seg; s < _line.Count - 1; s++)
                {
                    var dx = _line[s + 1][0] - _line[s][0]; var dz = _line[s + 1][1] - _line[s][1];
                    total += Math.Sqrt(dx * dx + dz * dz) * (s == _seg ? 1 - _segT : 1);
                }
                return (float)total;
            }

            public void Stop(string result) { Active = false; MoveDir = Vector3.zero; Jump = false; _result = result; }

            public void Tick()
            {
                var p = Player.m_localPlayer;
                if (!Active) return;
                if (p == null) { Stop("no player"); return; }
                var now = Time.time; var pos = p.transform.position;

                // the human wins the controls: while they drive, no input is written and every clock of the walk stands still
                var y = (float)User.Yielded(now);
                if (y > 0f) { _t0 += y; _lastProgT += y; _strafeUntil += y; _restUntil += y; _runUntil += y; _yielded += y; _lastProg = pos; _spotPos = pos; _prevPos = pos; _lastTick = now; MoveDir = Vector3.zero; return; }
                _lastTick = now;

                // a portal, a respawn or a teleport moves the body far in one tick: the walk is over, not stuck
                var jump = pos - _prevPos; _prevPos = pos;
                if (Teleporting(p) || TeleportGuard.Jumped(jump.x, jump.y, jump.z)) { Stop("teleported"); return; }

                if (now - _t0 > _timeout)
                {
                    if (_i < _pts.Count) { var tf = new Vector3(_pts[_i].x - pos.x, 0f, _pts[_i].z - pos.z); if (tf.sqrMagnitude > 0.01f) _blocker = Blocker(p, tf.normalized); }
                    Stop("timeout"); return;
                }
                if (now >= _nextTrace) { _nextTrace = now + 1f; _trace.Add("[" + U.N(pos.x) + "," + U.N(pos.y) + "," + U.N(pos.z) + "," + (p.IsOnGround() ? "1" : "0") + "]"); }

                var tgt = _pts[_i];
                var flat = new Vector3(tgt.x - pos.x, 0f, tgt.z - pos.z);
                var last = _i >= _pts.Count - 1;
                var arrival = Pursuit.Arrival(flat.magnitude, last ? tgt.y - pos.y : double.NaN, _arrive, ArriveHeight);          // the height of the goal counts only at the end, and only when the caller gave a y
                if (arrival == ArriveState.WrongStorey)
                {
                    MoveDir = Vector3.zero;
                    if (_wrongStoreySince < 0f) _wrongStoreySince = now;
                    else if (now - _wrongStoreySince > 3f) { _blocker = "the feet are within " + U.N(Math.Round(flat.magnitude, 2)) + " m of the goal but " + U.N(Math.Round(Math.Abs(tgt.y - pos.y), 1)) + " m " + (tgt.y > pos.y ? "below" : "above") + " it: another storey, or the goal's y is wrong (arriveHeight:0 ignores the height)"; Stop("wrong_storey"); }
                    return;
                }
                _wrongStoreySince = -1f;
                if (arrival == ArriveState.Arrived)
                {
                    _i++;
                    if (_i >= _pts.Count) { Stop("arrived"); return; }
                    _lastProg = pos; _lastProgT = now; return;
                }

                // steer at the point a little ahead on the line
                var speed = Run ? 7f : 4f;
                double ax, az;
                Pursuit.Aim(_line, ref _seg, ref _segT, pos.x, pos.z, Pursuit.LookAhead(speed), out ax, out az);
                var aim = new Vector3((float)ax - pos.x, 0f, (float)az - pos.z);
                var dir = aim.sqrMagnitude > 0.0004f ? aim.normalized : flat.normalized; MoveDir = dir;
                if (now < _strafeUntil) MoveDir = (dir * 0.2f + Vector3.Cross(Vector3.up, dir) * _strafeSide * 0.8f).normalized;            // step aside: 0.8 sideways + 0.2 forward
                if (now < _restUntil) MoveDir = Vector3.zero;                                                                               // wait for stamina

                // slope, stamina and the run rule: a bank steeper than the slide limit is only climbed at a run
                var stam = p.GetMaxStamina() > 0.01f ? p.GetStamina() / p.GetMaxStamina() : 1f;
                var slope = Vector3.Angle(p.GetLastGroundNormal(), Vector3.up);
                var goesUp = RouteGoesUp(pos, new Vector3((float)ax, pos.y, (float)az));
                var bank = goesUp && slope > WalkFacts.SlideDeg && slope <= WalkFacts.RunClimbMaxDeg && stam > WalkFacts.RunMinStamina;
                Run = (_userRun && stam > 0.1f) || bank || now < _runUntil;

                if (_doors) DoorTick(p, pos, dir, now);

                // progress = the route ahead shrank AND the feet moved; a body pressing a wall (or sliding down a bank) has neither
                var remain = Remaining();
                if (StallLadder.Progressed(_lastRemain - remain, (pos - _lastProg).magnitude)) { _lastProg = pos; _lastProgT = now; _lastRemain = remain; }
                else if (now - _lastProgT > WalkFacts.StallSeconds && now >= _restUntil) OnStall(p, pos, dir, now, stam, slope, goesUp);
            }

            bool RouteGoesUp(Vector3 pos, Vector3 ahead)
            {
                float h;
                return ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(ahead.x, 5000f, ahead.z), out h) && h > pos.y + 0.3f;
            }

            void OnStall(Player p, Vector3 pos, Vector3 dir, float now, float stam, float slope, bool goesUp)
            {
                if ((pos - _spotPos).magnitude > WalkFacts.SameSpotRadius) { _spotPos = pos; _spotStalls = 0; }
                _spotStalls++; _stallsTotal++;
                var rise = NoJump ? float.NaN : RiseAhead(p, dir);
                var act = StallLadder.Pick(_spotStalls, _stallsTotal, rise, slope, goesUp, p.IsSwimming(), HazardAhead(pos, dir), stam, Run, _sideSteps, _reroutes, !p.IsEncumbered());
                _lastStall = act + " (rise " + (float.IsNaN(rise) ? "none" : U.N(Math.Round(rise, 2))) + ", slope " + U.N(Math.Round(slope)) + ")";
                _lastProgT = now;
                switch (act)
                {
                    case StallAction.Hop: Jump = true; break;
                    case StallAction.RunUp: _runUntil = now + 3f; break;
                    case StallAction.Rest: _restUntil = now + 1.5f; break;
                    case StallAction.SideStep:
                        {
                            _sideSteps++;
                            var o2 = pos + Vector3.up * 1.0f; var perp = Vector3.Cross(Vector3.up, dir); RaycastHit hh;
                            var cl = Physics.Raycast(o2, (dir + perp * 0.6f).normalized, out hh, 2.5f, ~0, QueryTriggerInteraction.Ignore) ? hh.distance : 2.5f;
                            var cr = Physics.Raycast(o2, (dir - perp * 0.6f).normalized, out hh, 2.5f, ~0, QueryTriggerInteraction.Ignore) ? hh.distance : 2.5f;
                            _strafeSide = Ledge.SideStepSide(cl, cr, HazardAhead(pos, (dir + perp).normalized), HazardAhead(pos, (dir - perp).normalized)); _strafeUntil = now + 0.6f; break;
                        }
                    case StallAction.Reroute:
                        {
                            _reroutes++;
                            string err; var leg = NavLeg(pos, _pts[_pts.Count - 1], "Humanoid", out err);
                            if (leg == null) { _blocker = "re-route failed: " + err + "; " + Blocker(p, dir); Stop("blocked"); return; }
                            _pts = new List<Vector3>(leg); _i = 0; BuildLine(pos); _lastProg = pos; break;
                        }
                    default:
                        _blocker = p.IsEncumbered() && stam < 0.3f ? "encumbered with no stamina: an overloaded character regenerates none and cannot move (drop weight)" : !string.IsNullOrEmpty(_lockedDoor) ? "locked door: " + _lockedDoor + " (ward or missing key); " + Blocker(p, dir) : Blocker(p, dir);
                        Stop("blocked"); break;
                }
            }

            static int _bodyMask;

            /// <summary>
            ///     Height of the top just ahead of the feet (a ledge, a log, a step) above the ground they stand on, measured with rays down from 1.5 m over the feet; NaN when there is none.
            ///     The slope of the ground is taken off (a plain bank is not a ledge) and only layers the body collides with count: not saplings, crops, dropped items or animals.
            /// </summary>
            static float RiseAhead(Player p, Vector3 dir)
            {
                if (_bodyMask == 0) _bodyMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
                var feet = p.transform.position; float best = float.NaN;
                var n = p.GetLastGroundNormal(); var slope = (float)Ledge.SlopeAlong(n.x, n.y, n.z, dir.x, dir.z);
                if (float.IsInfinity(slope)) slope = 0f;
                foreach (var d in new[] { 0.4f, 0.7f, 1.0f })
                {
                    var o = feet + dir * d + Vector3.up * 1.5f; RaycastHit h;
                    if (!Physics.Raycast(o, Vector3.down, out h, 1.6f + Mathf.Max(0f, slope) * d, _bodyMask, QueryTriggerInteraction.Ignore)) continue;
                    if (h.collider.GetComponentInParent<Player>() != null) continue;
                    var rise = (float)Ledge.Rise(h.point.y - feet.y, d, slope);
                    if (rise > 0.05f && (float.IsNaN(best) || rise > best)) best = rise;
                }
                return best;
            }

            /// <summary>A burning fire or a portal trigger within a metre ahead: never jump toward them.</summary>
            static bool HazardAhead(Vector3 pos, Vector3 dir)
            {
                foreach (var c in Physics.OverlapSphere(pos + dir * 1.2f + Vector3.up * 0.5f, 1.0f, ~0, QueryTriggerInteraction.Collide))
                {
                    if (c.GetComponentInParent<TeleportWorld>() != null) return true;
                    var f = c.GetComponentInParent<Fireplace>();
                    if (f != null && f.IsBurning()) return true;
                }
                return false;
            }

            static readonly MethodInfo CanInteractM = typeof(Door).GetMethod("CanInteract", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            /// <summary>
            ///     Doors on the way: only a door the line ahead crosses within 2.8 m, never a locked one (Door.Interact returns true for a ward-denied or keyed door that did not open), only when its
            ///     leaf is at rest, and not the same door twice within 2 s. Whether a door opened is read from its state afterwards, not from what Interact returned.
            /// </summary>
            void DoorTick(Player p, Vector3 pos, Vector3 dir, float now)
            {
                foreach (var kv in _pending.ToList())
                {
                    var nv0 = kv.Key; if (now - kv.Value < 0.4f) continue;
                    _pending.Remove(nv0);
                    var d0 = FindDoor(nv0); var z0 = d0 != null ? d0.GetComponentInParent<ZNetView>()?.GetZDO() : null;
                    if (z0 != null && z0.GetInt("state", 0) != 0) _doorOpens++;
                }
                if (now < _nextDoorLook) return;
                _nextDoorLook = now + 0.2f;
                var seen = new HashSet<Door>();
                foreach (var c in Physics.OverlapSphere(pos + dir * 1.6f + Vector3.up, 2.2f, ~0, QueryTriggerInteraction.Ignore))
                {
                    var d = c.GetComponentInParent<Door>();
                    if (d == null || !seen.Add(d)) continue;
                    try
                    {
                        var nv = d.GetComponentInParent<ZNetView>(); var z = nv != null ? nv.GetZDO() : null; if (z == null) continue;
                        var dp = d.transform.position;
                        var wardDenies = d.m_checkGuardStone && !PrivateArea.CheckAccess(dp, 0f, false);
                        var needsKey = d.m_keyItem != null; var haveKey = !needsKey || p.GetInventory().HaveItem(d.m_keyItem.m_itemData.m_shared.m_name);
                        var locked = DoorRules.Locked(wardDenies, needsKey, haveKey);
                        var rest = CanInteractM == null || (bool)CanInteractM.Invoke(d, null);
                        var id = d.GetInstanceID(); float last; var since = _pressedAt.TryGetValue(id, out last) ? now - last : 999f;
                        var decision = DoorRules.Decide(locked, rest, DoorRules.OnLineAhead(pos.x, pos.z, dir.x, dir.z, dp.x, dp.z), z.GetInt("state", 0) != 0, since);
                        if (decision == DoorDecision.SkipLocked && DoorRules.OnLineAhead(pos.x, pos.z, dir.x, dir.z, dp.x, dp.z)) _lockedDoor = U.PrefabName(nv) + " at " + U.V(dp);
                        if (decision != DoorDecision.Press) continue;
                        _pressedAt[id] = now; _doorPresses++;
                        d.Interact(p, false, false);
                        _pending[id] = now;
                    }
                    catch (Exception ex) { U.Once("Walker.door", ex); }
                }
            }

            static Door FindDoor(int instanceId)
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<Door>()) if (d.GetInstanceID() == instanceId) return d;
                return null;
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
                sb.Append(",\"stalls\":" + _stallsTotal + ",\"lastStall\":" + U.S(_lastStall) + ",\"doorPresses\":" + _doorPresses + ",\"doorsOpened\":" + _doorOpens + ",\"yieldedToUserSeconds\":" + U.N(Math.Round(_yielded, 1)));
                if (!string.IsNullOrEmpty(_lockedDoor)) sb.Append(",\"lockedDoor\":" + U.S(_lockedDoor));
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
                // -hubner-autojoin host:port,profile[,password]; the password is better passed as HUBNER_AUTOJOIN_PASSWORD (environment) so it is not in the process list
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-hubner-autojoin") _spec = args[i + 1];
                if (string.IsNullOrEmpty(_spec)) _done = true;
                else { var sp = _spec.Split(new[] { ',' }, 3); if (sp.Length > 2) _pw = sp[2]; }       // everything after the second comma is the password, commas included
                var env = Environment.GetEnvironmentVariable("HUBNER_AUTOJOIN_PASSWORD"); if (!string.IsNullOrEmpty(env)) _pw = env;
            }
            float _godNext;
            public void Tick()
            {
                // the sandbox character (hubner.sandboxNames) is a tool: keep it invulnerable across respawns (it was being killed by wandering skeletons and trolls between teleports). Nobody else is touched.
                if (Time.time >= _godNext) { _godNext = Time.time + 1f; var pl = Player.m_localPlayer; try { if (pl != null && U.IsSandbox(pl) && !pl.InGodMode()) pl.SetGodMode(true); } catch (Exception ex) { U.Once("AutoJoin.god", ex); } }
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
                var parts = _spec.Split(new[] { ',' }, 3);
                var hostPort = parts[0]; var colon = hostPort.LastIndexOf(':');
                bool hasPort = colon > 0 && hostPort.IndexOf(']') < colon;                  // IPv6 literals ([::1]:2456) keep their inner colons
                var host = (hasPort ? hostPort.Substring(0, colon) : hostPort).Trim('[', ']');
                int port = 2456;
                if (hasPort && !int.TryParse(hostPort.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) { Debug.LogWarning("[hubner autojoin] bad port in '" + hostPort + "'"); _done = true; return; }
                _tries++;
                Mode = "data";                                                       // AutoJoinServer only queues at the menu; the data path builds the join itself
                if (parts.Length < 2 || string.IsNullOrEmpty(parts[1])) { Debug.LogWarning("[hubner autojoin] -hubner-autojoin needs host:port,profile"); _done = true; return; }
                var res = DoJoin(parts[1], host, port, _pw);
                Debug.Log("[hubner autojoin] attempt " + _tries + ": " + res);
            }
        }
    }
}
