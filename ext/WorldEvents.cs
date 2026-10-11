// World-event watch for the dedicated server and the client: structures that appear, players who come, go or die. Everything comes from the ZDO table and the peer list,
// so it works with no player and no camera. The decisions (clustering, quiet time, the log) are the pure classes in Rules.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class WorldEvents
    {
        static Harmony _h;
        static readonly List<ZDO> Fresh = new List<ZDO>();                 // ZDOs created since the last tick; their prefab is known once their data arrived
        static readonly StructureDetector Detector = new StructureDetector();
        static readonly EventLog Log = new EventLog(300);
        static HashSet<string> _peers;
        static readonly HashSet<ZDOID> SeenTombstones = new HashSet<ZDOID>();
        static HashSet<int> _pieceH;
        static float _nextTick;
        static string _hookError = "";

        public static void Register(ToolRegistry r)
        {
            r.Add("world_events", "What happened in the world since you last looked, from the ZDO table and the peer list (works on the dedicated server): structure_appeared (centre, box, piece count, top prefabs, whether it came all at once, who: a player, the server's own tools, or a session that has left), player_arrived, player_left, player_died. since = the seq of the last row you saw (default 0), n = how many (default 20), kind filter. Events start when the extension loads; pieces loaded from the save are never events.",
                "{\"type\":\"object\",\"properties\":{\"since\":{\"type\":\"number\"},\"n\":{\"type\":\"number\"},\"kind\":{\"type\":\"string\"}}}", Query);
            Install();
        }

        public static void Install()
        {
            if (_h != null) return;
            try
            {
                var m = typeof(ZDOMan).GetMethod("CreateNewZDO", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(ZDOID), typeof(Vector3), typeof(int) }, null);
                if (m == null) { _hookError = "ZDOMan.CreateNewZDO(ZDOID, Vector3, int) not found in this game version"; return; }
                _h = new Harmony("hubner.ext.worldevents." + Guid.NewGuid().ToString("N"));
                _h.Patch(m, postfix: new HarmonyMethod(typeof(WorldEvents).GetMethod("Created", BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception ex) { _hookError = ex.Message; }
        }

        public static void Uninstall() { try { if (_h != null) _h.UnpatchSelf(); } catch (Exception ex) { U.Once("WorldEvents.Uninstall", ex); } _h = null; lock (Fresh) Fresh.Clear(); }

        static void Created(ZDO __result)
        {
            if (__result == null) return;
            lock (Fresh) { if (Fresh.Count < 100000) Fresh.Add(__result); }
        }

        /// <summary>Called once per frame from the scene hook; works every two seconds.</summary>
        public static void Tick()
        {
            var now = Time.realtimeSinceStartup;
            if (now < _nextTick) return; _nextTick = now + 2f;
            try
            {
                if (!Zdos.Ready) return;
                ReadFresh(now);
                foreach (var e in Detector.Flush(now)) EmitStructure(now, e);
                Peers(now);
            }
            catch (Exception ex) { U.Once("WorldEvents.Tick", ex); }
        }

        static void ReadFresh(float now)
        {
            List<ZDO> batch; lock (Fresh) { if (Fresh.Count == 0) return; batch = new List<ZDO>(Fresh); Fresh.Clear(); }
            if (_pieceH == null) _pieceH = PieceHashes();
            foreach (var z in batch)
            {
                if (z == null || !z.IsValid()) continue;
                var h = z.GetPrefab(); if (h == 0) continue;
                var name = Zdos.NameOf(h);
                if (Ownership.IsTombstonePrefab(name))
                {
                    if (!SeenTombstones.Add(z.m_uid)) continue;
                    var p = z.GetPosition();
                    Log.Add(now, "player_died", "{\"owner\":" + U.S(ChatRules.ForReading(z.GetString("ownerName", ""), 40)) + ",\"pos\":" + U.V(p) + ",\"tombstone\":" + U.S(U.Id(z)) + "}");
                    continue;
                }
                if (_pieceH == null || !_pieceH.Contains(h)) continue;
                var q = z.GetPosition();
                Detector.Add(new PieceSeen { T = now, Session = z.m_uid.UserID, X = q.x, Z = q.z, Prefab = name });
            }
        }

        static HashSet<int> PieceHashes()
        {
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var dict = ZNetScene.instance != null && f != null ? f.GetValue(ZNetScene.instance) as Dictionary<int, GameObject> : null;
            if (dict == null || dict.Count == 0) return null;
            var set = new HashSet<int>();
            foreach (var kv in dict) if (kv.Value != null && kv.Value.GetComponent<Piece>() != null) set.Add(kv.Key);
            return set;
        }

        static void EmitStructure(float now, StructureEvent e)
        {
            string who = "a session that has left";
            if (ZDOMan.instance != null && e.Session == ZDOMan.GetSessionID()) who = "the server's own tools";
            else if (ZNet.instance != null)
                foreach (var p in ZNet.instance.GetPeers()) if (p.m_uid == e.Session) { who = "player " + ChatRules.ForReading(p.m_playerName ?? "?", 40); break; }
            Log.Add(now, "structure_appeared", "{\"centre\":[" + U.N(Math.Round(e.CentreX, 1)) + "," + U.N(Math.Round(e.CentreZ, 1)) + "],\"box\":[" + U.N(Math.Round(e.MinX, 1)) + "," + U.N(Math.Round(e.MinZ, 1)) + "," + U.N(Math.Round(e.MaxX, 1)) + "," + U.N(Math.Round(e.MaxZ, 1)) + "],\"pieces\":" + e.Count +
                ",\"top\":[" + string.Join(",", e.Top.Select(kv => "{\"prefab\":" + U.S(kv.Key) + ",\"count\":" + kv.Value + "}").ToArray()) + "],\"spawnedAtOnce\":" + (e.SpawnedAtOnce ? "true" : "false") + ",\"by\":" + U.S(who) + ",\"session\":" + e.Session.ToString(CultureInfo.InvariantCulture) + "}");
        }

        static void Peers(float now)
        {
            if (ZNet.instance == null) return;
            var names = new HashSet<string>(ZNet.instance.GetPeers().Where(p => p.IsReady()).Select(p => ChatRules.ForReading(p.m_playerName ?? "?", 40)));
            if (_peers != null)
            {
                Presence.Diff(_peers, names, out var arrived, out var left);
                foreach (var n in arrived) Log.Add(now, "player_arrived", "{\"name\":" + U.S(n) + "}");
                foreach (var n in left) Log.Add(now, "player_left", "{\"name\":" + U.S(n) + "}");
            }
            _peers = names;
        }

        static ToolOutput Query(Dictionary<string, object> a)
        {
            var rows = Log.Query((long)U.D(a, "since", 0), Math.Max(1, (int)U.D(a, "n", 20)), McpJson.GetStr(a, "kind"));
            return U.Json("{\"last\":" + Log.Last + ",\"hook\":" + U.S(_h != null ? "installed" : "not installed: " + _hookError) + ",\"openClusters\":" + Detector.OpenClusters + ",\"events\":[" +
                string.Join(",", rows.Select(r => "{\"seq\":" + r.Seq + ",\"kind\":" + U.S(r.Kind) + ",\"t\":" + U.N(Math.Round(r.T, 1)) + ",\"data\":" + r.Json + "}").ToArray()) + "]}");
        }
    }
}
