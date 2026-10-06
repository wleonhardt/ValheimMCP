// Virtual peers: make the dedicated server instantiate the world around positions WE choose, with no player connected (adopted from ddormer/valheim-serverside, rewritten).
// A stock dedicated server has no reference position, so ZNetScene never creates GameObjects: no colliders, no WearNTear, no heightmaps. While at least one virtual peer exists,
// ZNetScene.CreateDestroyObjects is replaced by a loop over virtual peers + real peers: CreateLocalZones(pos), ReleaseNearbyZDOS(pos), FindSectorObjects(zone, simulationDistance),
// then the game's own CreateObjects / RemoveObjects with the union.  Off by default, capped, auto-expiring.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class VirtualPeers
    {
        sealed class VP { public string Name; public Vector3 Pos; public DateTime Expires; }
        static readonly List<VP> Peers = new List<VP>();
        static Harmony _h; static string _err = ""; static int _frames; static string _lastError = "";
        static MethodInfo _create, _remove, _find, _release, _local, _getZone; static FieldInfo _near, _distant, _simDist;
        const int MaxPeers = 4;

        public static void Register(ToolRegistry r)
        {
            r.Add("vpeer_add", "Server only: add a virtual peer (name, x, z, minutes default 30, max 240, at most 4). While one exists the server instantiates the world around it (colliders, heightmaps, WearNTear) so server-side physics tools work with no player connected. Costs CPU/RAM; it expires on its own.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"minutes\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Add, Plans.WriteFlag);
            r.Add("vpeer_remove", "Remove a virtual peer by name (or all with name '*'). The instantiated objects are removed by the game's own RemoveObjects on the next tick.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}", Remove);
            r.Add("vpeer_list", "Virtual peers with positions and minutes left, the number of instantiated objects, hook state and the last error.", "{\"type\":\"object\",\"properties\":{}}", List);
            r.Add("vpeer_probe", "Is physics alive at x,z on the server? Downward raycast from y=300 (hit height, collider, prefab) and the number of colliders within 20 m of the hit.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"z\"]}", Probe);
            Install();
        }

        static void Install()
        {
            if (_h != null) return;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                _create = typeof(ZNetScene).GetMethod("CreateObjects", F); _remove = typeof(ZNetScene).GetMethod("RemoveObjects", F);
                _near = typeof(ZNetScene).GetField("m_tempCurrentObjects", F); _distant = typeof(ZNetScene).GetField("m_tempCurrentDistantObjects", F);
                _find = typeof(ZDOMan).GetMethod("FindSectorObjects", F); _release = typeof(ZDOMan).GetMethod("ReleaseNearbyZDOS", F);
                _local = typeof(ZoneSystem).GetMethod("CreateLocalZones", F); _getZone = typeof(ZoneSystem).GetMethod("GetZone", F, null, new[] { typeof(Vector3) }, null);
                _simDist = typeof(ZoneSystem).GetField("m_simulationDistance", F);
                var miss = new[] { _create, _remove, _find, _release, _local, _getZone }.Any(m => m == null) || _near == null || _distant == null || _simDist == null;
                if (miss) { _err = "game API names differ in this version: virtual peers disabled"; return; }
                _h = new Harmony("hubner.ext.vpeers." + Guid.NewGuid().ToString("N"));
                _h.Patch(typeof(ZNetScene).GetMethod("CreateDestroyObjects", F), prefix: new HarmonyMethod(typeof(VirtualPeers).GetMethod("CreateDestroyPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception ex) { _err = ex.Message; }
        }

        static bool CreateDestroyPrefix(ZNetScene __instance)
        {
            try
            {
                if (Peers.Count == 0 || ZNet.instance == null || !ZNet.instance.IsDedicated() || ZoneSystem.instance == null || ZDOMan.instance == null) return true;
                Peers.RemoveAll(p => p.Expires < DateTime.UtcNow); if (Peers.Count == 0) return true;
                var near = (List<ZDO>)_near.GetValue(__instance); var distant = (List<ZDO>)_distant.GetValue(__instance); near.Clear(); distant.Clear();
                var pos = Peers.Select(p => p.Pos).ToList();
                var tn = new List<ZDO>(); var td = new List<ZDO>(); var sim = _simDist.GetValue(ZoneSystem.instance); var uid = ZNet.GetUID();
                foreach (var p in pos)
                {
                    _local.Invoke(ZoneSystem.instance, new object[] { p });
                    _release.Invoke(ZDOMan.instance, new object[] { p, uid });
                    var zone = _getZone.Invoke(null, new object[] { p }); tn.Clear(); td.Clear();
                    _find.Invoke(ZDOMan.instance, new object[] { zone, sim, tn, td }); near.AddRange(tn); distant.AddRange(td);
                }
                _create.Invoke(__instance, new object[] { near, distant }); _remove.Invoke(__instance, new object[] { near, distant }); _frames++;
                return false;
            }
            catch (Exception ex) { _lastError = ex.InnerException != null ? ex.InnerException.Message : ex.Message; return true; }
        }

        static ToolOutput Add(Dictionary<string, object> a)
        {
            if (_h == null) return ToolOutput.Err(string.IsNullOrEmpty(_err) ? "not installed" : _err);
            if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return ToolOutput.Err("only on a dedicated server (the client simulates its own area)");
            var name = McpJson.GetStr(a, "name") ?? "vp"; var ex = Peers.FirstOrDefault(p => p.Name == name);
            if (ex == null && Peers.Count >= MaxPeers) return ToolOutput.Err("at most " + MaxPeers + " virtual peers");
            var pos = new Vector3((float)U.D(a, "x", 0), 0, (float)U.D(a, "z", 0)); var mins = Math.Max(1, Math.Min(240, McpJson.Get(a, "minutes", 30)));
            if (ex == null) { ex = new VP { Name = name }; Peers.Add(ex); }
            ex.Pos = pos; ex.Expires = DateTime.UtcNow.AddMinutes(mins);
            return U.Json("{\"added\":" + U.S(name) + ",\"minutes\":" + U.N(mins) + ",\"peers\":" + Peers.Count + "}");
        }

        static ToolOutput Remove(Dictionary<string, object> a)
        {
            var n = McpJson.GetStr(a, "name"); int c = Peers.RemoveAll(p => n == "*" || p.Name == n); return U.Json("{\"removed\":" + c + ",\"peers\":" + Peers.Count + "}");
        }

        static ToolOutput List(Dictionary<string, object> a)
        {
            var rows = Peers.Select(p => "{\"name\":" + U.S(p.Name) + ",\"pos\":" + U.V(p.Pos) + ",\"minutesLeft\":" + U.N(Math.Round((p.Expires - DateTime.UtcNow).TotalMinutes, 1)) + "}").ToArray();
            int inst = 0; try { inst = ZNetScene.instance != null ? ZNetScene.instance.NrOfInstances() : 0; } catch { }
            return U.Json("{\"peers\":[" + string.Join(",", rows) + "],\"instances\":" + inst + ",\"frames\":" + _frames + ",\"installed\":" + (_h != null ? "true" : "false") + ",\"error\":" + U.S(_err) + ",\"lastError\":" + U.S(_lastError) + "}");
        }

        static ToolOutput Probe(Dictionary<string, object> a)
        {
            var o = new Vector3((float)U.D(a, "x", 0), 300f, (float)U.D(a, "z", 0)); RaycastHit h;
            if (!Physics.Raycast(o, Vector3.down, out h, 600f, ~0, QueryTriggerInteraction.Ignore)) return U.Json("{\"hit\":false,\"note\":\"no collider below: zone not instantiated (add a virtual peer first)\"}");
            var nv = h.collider.GetComponentInParent<ZNetView>(); var n = Physics.OverlapSphere(h.point, 20f).Length;
            return U.Json("{\"hit\":true,\"y\":" + U.N(Math.Round(h.point.y, 3)) + ",\"collider\":" + U.S(h.collider.GetType().Name) + ",\"prefab\":" + U.S(nv != null ? U.PrefabName(nv) : h.collider.transform.root.name) + ",\"collidersWithin20m\":" + n + "}");
        }
    }
}
