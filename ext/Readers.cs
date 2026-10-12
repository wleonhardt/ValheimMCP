// Readers for keeping a base and a server, all from the ZDO table and the prefabs (so they work on the dedicated server): net_peers, active_area, producers, loot_near, harvest_near,
// resource_nodes, spawners_near, wear_risk, absence_forecast. The rules are in ReaderRules.cs; each was read in the decompiled game first (see plans/research/2026-10-decompile-review.md).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class ReaderTools
    {
        const int Max = 300;

        public static void Register(ToolRegistry r)
        {
            r.Add("net_peers", "Who is connected and how the link is doing (server): per peer the name, ping, link quality both ways, bytes per second, the send queue (the ZDO sync stops for a peer above 10240 queued bytes, so a long queue means the world is not reaching that player), the simulation distance, the seconds since the peer's last ping, and the peer's zone only when it shares its position. On a dedicated server the Steam client API that gives ping and link quality is not running, so those are null there. No addresses or ids.",
                "{\"type\":\"object\",\"properties\":{}}", NetPeers);
            r.Add("active_area", "Is a place simulated? points:[[x,z],...] (max 100) or x,z. A zone is simulated only within 1.5 zones (96 m, Chebyshev, from the zone centre; with near distance 2 also under 112 m Euclidean) of a connected peer's reference position (ZNetScene.PointInsideActiveArea). Returns per point the zone, whether any peer covers it, and how many do. Crops, animals, fires, smelters and wear all stop outside it.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}}}", ActiveAreaTool);
            r.Add("producers", "Fermenters (barrels), beehives and sap collectors in a box (x,z,radius or x0,z0,x1,z1): content and seconds left to ready for a barrel (the timer restarts while its roof check point has no roof or under 70% cover, checked every 10 s while loaded), honey/sap level, units per interval, seconds to full, biome allowed for a hive (a hive also needs cover under its m_maxCover, 0.6 on the piece_beehive prefab here), the ancient root a sap collector drains and its level. Server and client.",
                BoxSchema, Producers);
            r.Add("loot_near", "Items lying on the ground in a box, with their age and what the owner does about them: a floor item older than 3600 s is destroyed unless it is inside a base above y 28, a player is within 25 m, it lies in tar, or it is a piece (ItemDrop.TimedDestruction). Returns name, stack, quality, durability, age, seconds left and why it is kept. Tar is not read.",
                BoxSchema, LootNear);
            r.Add("harvest_near", "Pickables in a box (berries, mushrooms, flowers, crops that are not Plants, surtling cores): picked or ready, minutes to respawn (the owner checks every 60 s while the zone is active), whether it respawns at all. Counts per prefab and the ready ones first. Plants and crops that grow are in farm_state.",
                BoxSchema, HarvestNear);
            r.Add("resource_nodes", "Mineable rocks (MineRock5), trees, logs and destructibles in a box: for a big rock the hit areas left of the total (the ZDO keeps one health float per area), for trees and logs the health fraction, and the tool tier needed (m_minToolTier). Use it to know what is left to mine and what a given pickaxe or axe can break.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"toolTier\":{\"type\":\"number\"},\"kind\":{\"type\":\"string\",\"description\":\"rock | tree | log | destructible: only that kind in the rows (the counts stay)\"},\"limit\":{\"type\":\"number\"}}}", ResourceNodes);
            r.Add("spawners_near", "Creature spawners and spawn areas in a box: the creature, its respawn time, the trigger distance, whether the creature it made is still alive and the minutes until it may spawn again (CreatureSpawner keeps 'alive_time'), the global keys it needs or is blocked by and whether they are set. Spawn areas (SpawnArea) list the creatures, caps and trigger distance. A spawner only runs with a player within its trigger distance and its zone active.",
                BoxSchema, SpawnersNear);
            r.Add("wear_risk", "What slowly damages the pieces in a box (WearNTear.UpdateWear): rain on unroofed pieces that wear (5% of max health per minute while wet, never below half health), the biome rules (Ashlands ash and lava, Deep North snow, pieces outside the biome they need), pieces already damaged, and the roof state from a model of the game's roof cast. No weather is read: a dedicated server has none. Needs the pieces' prefabs; judges up to `pieces` (default 600, max 1500) of the box, every piece in it can be a roof.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"},\"pieces\":{\"type\":\"number\"}}}", WearRisk);
            r.Add("absence_forecast", "What happens in a box while nobody is there for hours (hours, default 8): fires and cookers that run dry, smelters (the game catches up at most 3600 s of work), barrels that finish, hives and sap collectors that fill, pickables that come back, floor items that despawn. Everything stops when no peer is near (see active_area); this describes what the game catches up from timestamps when someone returns.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"hours\":{\"type\":\"number\"}}}", AbsenceForecast);
        }

        const string BoxSchema = "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"x0\":{\"type\":\"number\"},\"z0\":{\"type\":\"number\"},\"x1\":{\"type\":\"number\"},\"z1\":{\"type\":\"number\"},\"limit\":{\"type\":\"number\"}}}";

        /// <summary>The shield domes standing in the ZDO table near a box (ShieldGenerator: radius from its stored fuel). A dome cancels wear and the heat and cold that stop a plant.</summary>
        internal static List<ShieldFact> ShieldsNear(double x0, double z0, double x1, double z1)
        {
            var res = new List<ShieldFact>(); var s = S(); if (s == null || s.Shield.Count == 0 || !Zdos.Ready) return res;
            foreach (var z in Zdos.InBox((float)(x0 - 32), (float)(z0 - 32), (float)(x1 + 32), (float)(z1 + 32)))
            {
                var h = z.GetPrefab(); if (!s.Shield.Contains(h)) continue;
                var sg = s.All[h].GetComponent<ShieldGenerator>(); if (sg == null) continue;
                var p = z.GetPosition();
                res.Add(new ShieldFact { X = p.x, Y = p.y, Z = p.z, Radius = ShieldRules.Radius(z.GetFloat("fuel", sg.m_defaultFuel), sg.m_maxFuel, sg.m_minShieldRadius, sg.m_maxShieldRadius, sg.m_offWhenNoFuel) });
            }
            return res;
        }

        // ------------------------------------------------------------------ live cover (only where the object stands in a loaded zone, i.e. on a client near it)
        static MethodInfo _cover;

        /// <summary>The game's own Cover.GetCoverForPoint at a point, or null when it cannot be asked (no colliders loaded here).</summary>
        internal static string LiveCoverJson(Vector3 point)
        {
            try
            {
                if (_cover == null) _cover = typeof(Cover).GetMethod("GetCoverForPoint", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (_cover == null) return "null";
                var args = new object[] { point, 0f, false, 0.5f }; _cover.Invoke(null, args);
                return "{\"cover\":" + U.N(Math.Round((float)args[1], 3)) + ",\"roof\":" + ((bool)args[2] ? "true" : "false") + "}";
            }
            catch (Exception ex) { U.Once("LiveCover", ex); return "null"; }
        }

        /// <summary>The cover the game measures at a station's roof check point, from the live object (null on the server or away from the object).</summary>
        internal static string LiveStationCover(ZDO z)
        {
            try
            {
                var nv = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(z) : null; if (nv == null) return "null";
                var cs = nv.GetComponent<CraftingStation>(); if (cs == null || cs.m_roofCheckPoint == null) return "null";
                return LiveCoverJson(cs.m_roofCheckPoint.position);
            }
            catch (Exception ex) { U.Once("LiveStationCover", ex); return "null"; }
        }

        internal static string LiveInstanceCover(ZDO z)
        {
            try
            {
                var nv = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(z) : null; if (nv == null) return "null";
                var f = nv.GetComponent<Fermenter>(); if (f != null && f.m_roofCheckPoint != null) { var cj = LiveCoverJson(f.m_roofCheckPoint.position); string hover = ""; try { hover = f.GetHoverText(); } catch (Exception ex) { U.Once("LiveHover", ex); } return cj.Length > 1 && cj != "null" ? cj.Substring(0, cj.Length - 1) + ",\"hover\":" + U.S(hover.Replace('\n', ' ')) + "}" : cj; }
                var b = nv.GetComponent<Beehive>(); if (b != null && b.m_coverPoint != null) return LiveCoverJson(b.m_coverPoint.position);
                return "null";
            }
            catch (Exception ex) { U.Once("LiveInstanceCover", ex); return "null"; }
        }

        // ------------------------------------------------------------------ prefab sets
        sealed class Sets { public Dictionary<int, GameObject> All = new Dictionary<int, GameObject>(); public HashSet<int> Item = new HashSet<int>(), Pick = new HashSet<int>(), Ferm = new HashSet<int>(), Hive = new HashSet<int>(), Sap = new HashSet<int>(), Root = new HashSet<int>(), Mine = new HashSet<int>(), Tree = new HashSet<int>(), Destr = new HashSet<int>(), Spawner = new HashSet<int>(), Area = new HashSet<int>(), Wear = new HashSet<int>(), Shield = new HashSet<int>(), Fire = new HashSet<int>(), Smelt = new HashSet<int>(), Cook = new HashSet<int>(); }
        static Sets _sets;

        static Sets S()
        {
            if (_sets != null || ZNetScene.instance == null) return _sets;
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var dict = f != null ? f.GetValue(ZNetScene.instance) as Dictionary<int, GameObject> : null;
            if (dict == null || dict.Count == 0) return null;
            var s = new Sets();
            foreach (var kv in dict)
            {
                var go = kv.Value; if (go == null) continue; s.All[kv.Key] = go;
                if (go.GetComponent<ItemDrop>() != null && go.GetComponent<Piece>() == null) s.Item.Add(kv.Key);
                if (go.GetComponent<Pickable>() != null) s.Pick.Add(kv.Key);
                if (go.GetComponent<Fermenter>() != null) s.Ferm.Add(kv.Key);
                if (go.GetComponent<Beehive>() != null) s.Hive.Add(kv.Key);
                if (go.GetComponent<SapCollector>() != null) s.Sap.Add(kv.Key);
                if (go.GetComponent<ResourceRoot>() != null) s.Root.Add(kv.Key);
                if (go.GetComponent<MineRock5>() != null) s.Mine.Add(kv.Key);
                if (go.GetComponent<TreeBase>() != null || go.GetComponent<TreeLog>() != null) s.Tree.Add(kv.Key);
                if (go.GetComponent<Destructible>() != null && go.GetComponent<Piece>() == null && go.GetComponent<TreeBase>() == null) s.Destr.Add(kv.Key);
                if (go.GetComponent<CreatureSpawner>() != null) s.Spawner.Add(kv.Key);
                if (go.GetComponentInChildren<SpawnArea>() != null) s.Area.Add(kv.Key);
                if (go.GetComponent<WearNTear>() != null) s.Wear.Add(kv.Key);
                if (go.GetComponent<ShieldGenerator>() != null) s.Shield.Add(kv.Key);
                if (go.GetComponent<Fireplace>() != null) s.Fire.Add(kv.Key);
                if (go.GetComponent<Smelter>() != null) s.Smelt.Add(kv.Key);
                if (go.GetComponent<CookingStation>() != null) s.Cook.Add(kv.Key);
            }
            _sets = s; return s;
        }

        static bool Setup(Dictionary<string, object> a, out float x0, out float z0, out float x1, out float z1, out Sets s, out ToolOutput err)
        {
            x0 = z0 = x1 = z1 = 0; s = null; err = null;
            if (!Zdos.Ready) { err = ToolOutput.Err("world not ready"); return false; }
            string e; if (!RecipeTools.Box(a, out x0, out z0, out x1, out z1, out e)) { err = ToolOutput.Err(e ?? "give x,z[,radius] or x0,z0,x1,z1"); return false; }
            if (!WorldBounds.InWorld(x0, 0, z0) || !WorldBounds.InWorld(x1, 0, z1)) { err = ToolOutput.Err("the box must be finite and inside the world"); return false; }
            s = S(); if (s == null) { err = ToolOutput.Err("the prefab table is not ready"); return false; }
            return true;
        }

        static long NowTicks() { return ZNet.instance != null ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks; }
        static string B(bool v) { return v ? "true" : "false"; }
        static string N(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "null" : U.N(Math.Round(v, 1)); }
        static int Limit(Dictionary<string, object> a) { return (int)Math.Max(1, Math.Min(Max, U.D(a, "limit", 100))); }
        static string Name(int hash) { return Zdos.NameOf(hash); }

        static string Reply(string head, int total, List<string> rows, string extra = "") { return U.Json("{" + head + "\"total\":" + total + ",\"returned\":" + rows.Count + extra + ",\"rows\":[" + string.Join(",", rows.ToArray()) + "]}").Text; }

        // ------------------------------------------------------------------ net_peers, active_area
        static IEnumerable<ZNetPeer> Peers() { if (ZNet.instance == null) yield break; foreach (var p in ZNet.instance.GetPeers()) if (p != null && p.IsReady()) yield return p; }

        static ToolOutput NetPeers(Dictionary<string, object> a)
        {
            if (ZNet.instance == null) return ToolOutput.Err("no world loaded");
            var rows = new List<string>();
            foreach (var p in Peers())
            {
                // the socket reads go through the Steam CLIENT networking API, which a dedicated server (it uses the game-server one) answers with "Steamworks is not initialized": each read is tried alone and what fails is null, with the queue summed from the socket's own list and the last-ping age from the RPC as a fallback
                float lq = 0, rq = 0, outB = 0, inB = 0; int ping = 0, queue = -1, rate = -1; var haveQuality = false;
                try { p.m_socket.GetConnectionQuality(out lq, out rq, out ping, out outB, out inB); haveQuality = true; } catch (Exception ex) { U.Once("net_peers.quality", ex); }
                try { queue = p.m_socket.GetSendQueueSize(); } catch (Exception ex) { U.Once("net_peers.queue", ex); queue = QueueFromList(p.m_socket); }
                try { rate = p.m_socket.GetCurrentSendRate(); } catch (Exception ex) { U.Once("net_peers.rate", ex); }
                var lastPing = -1f; try { lastPing = p.m_rpc.GetTimeSinceLastPing(); } catch (Exception ex) { U.Once("net_peers.ping", ex); }
                var zone = p.m_publicRefPos ? ",\"zone\":[" + ActiveArea.Zone(p.m_refPos.x) + "," + ActiveArea.Zone(p.m_refPos.z) + "]" : "";
                rows.Add("{\"name\":" + U.S(ChatRules.ForReading(p.m_playerName, 40)) + ",\"inGame\":" + B(!p.m_characterID.IsNone()) + ",\"pingMs\":" + (haveQuality ? ping.ToString(CultureInfo.InvariantCulture) : "null") + ",\"qualityLocal\":" + (haveQuality ? U.N(Math.Round(lq, 2)) : "null") + ",\"qualityRemote\":" + (haveQuality ? U.N(Math.Round(rq, 2)) : "null") + ",\"outBytesPerSec\":" + (haveQuality ? U.N(Math.Round(outB)) : "null") + ",\"inBytesPerSec\":" + (haveQuality ? U.N(Math.Round(inB)) : "null") + ",\"sendQueueBytes\":" + (queue >= 0 ? queue.ToString(CultureInfo.InvariantCulture) : "null") + ",\"syncStalled\":" + (queue >= 0 ? B(queue > 10240) : "null") + ",\"sendRate\":" + (rate >= 0 ? rate.ToString(CultureInfo.InvariantCulture) : "null") + ",\"secondsSinceLastPing\":" + (lastPing >= 0 ? U.N(Math.Round(lastPing, 1)) : "null") + ",\"simulationNear\":" + p.m_simulationDistance.NearSimulationDistance + ",\"simulationFar\":" + p.m_simulationDistance.FarSimulationDistance + zone + "}");
            }
            var sd = ZNet.instance.GetSyncedSimulationDistance();
            return U.Json("{\"server\":" + B(ZNet.instance.IsServer()) + ",\"synced\":{\"near\":" + sd.NearSimulationDistance + ",\"far\":" + sd.FarSimulationDistance + ",\"classic\":" + B(sd.IsClassic) + "},\"count\":" + rows.Count + ",\"peers\":[" + string.Join(",", rows.ToArray()) + "],\"note\":\"a joined client sees only its server here; the queue limit of 10240 bytes is ZDOMan.SendZDOToPeers\"}");
        }

        static int QueueFromList(ISocket sock)
        {
            try { var f = sock.GetType().GetField("m_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); var q = f != null ? f.GetValue(sock) as IEnumerable<byte[]> : null; if (q == null) return -1; var n = 0; foreach (var b in q) n += b.Length; return n; }
            catch (Exception ex) { U.Once("net_peers.queueList", ex); return -1; }
        }

        static ToolOutput ActiveAreaTool(Dictionary<string, object> a)
        {
            if (ZNet.instance == null) return ToolOutput.Err("no world loaded");
            var pts = new List<double[]>();
            var l = McpJson.GetList(a, "points");
            if (l != null) foreach (var o in l) { var p = o as List<object>; if (p != null && p.Count >= 2) { var x = U.At(p, 0); var z = p.Count >= 3 ? U.At(p, 2) : U.At(p, 1); if (WorldBounds.InWorld(x, 0, z)) pts.Add(new[] { x, z }); } }
            else if (U.Has(a, "x") && WorldBounds.InWorld(U.D(a, "x", 0), 0, U.D(a, "z", 0))) pts.Add(new[] { U.D(a, "x", 0), U.D(a, "z", 0) });
            if (pts.Count == 0) return ToolOutput.Err("give points:[[x,z],...] or x,z (finite, inside the world)");
            if (pts.Count > 100) pts = pts.Take(100).ToList();
            var sd = ZNet.instance.GetSyncedSimulationDistance(); var refs = new List<Vector3>();
            foreach (var p in Peers()) if (!p.m_characterID.IsNone() || p.m_refPos != Vector3.zero) refs.Add(p.m_refPos);
            if (!ZNet.instance.IsServer() || !ZNet.instance.IsDedicated()) { try { refs.Add(ZNet.instance.GetReferencePosition()); } catch (Exception ex) { U.Once("active_area.ref", ex); } }
            var rows = new List<string>();
            foreach (var p in pts)
            {
                var n = refs.Count(rp => ActiveArea.Inside(rp.x, rp.z, p[0], p[1], sd.NearSimulationDistance, sd.IsClassic));
                rows.Add("{\"x\":" + U.N(p[0]) + ",\"z\":" + U.N(p[1]) + ",\"zone\":[" + ActiveArea.Zone(p[0]) + "," + ActiveArea.Zone(p[1]) + "],\"simulated\":" + B(n > 0) + ",\"coveredBy\":" + n + "}");
            }
            return U.Json("{\"referencePoints\":" + refs.Count + ",\"near\":" + sd.NearSimulationDistance + ",\"classic\":" + B(sd.IsClassic) + ",\"points\":[" + string.Join(",", rows.ToArray()) + "],\"note\":\"a dedicated server with no player connected simulates nothing (unless vpeer_add made a virtual peer)\"}");
        }

        // ------------------------------------------------------------------ producers
        static ToolOutput Producers(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1; Sets s; ToolOutput err; if (!Setup(a, out x0, out z0, out x1, out z1, out s, out err)) return err;
            var now = NowTicks(); var lim = Limit(a); var rows = new List<string>(); var total = 0;
            var roots = new List<KeyValuePair<Vector3, ZDO>>();
            foreach (var z in Zdos.InBox(x0 - 3, z0 - 3, x1 + 3, z1 + 3)) if (s.Root.Contains(z.GetPrefab())) roots.Add(new KeyValuePair<Vector3, ZDO>(z.GetPosition(), z));
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); GameObject go; if (!s.All.TryGetValue(h, out go)) continue;
                var p = z.GetPosition(); var head = "{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Name(h)) + ",\"pos\":" + U.V(p);
                if (s.Ferm.Contains(h))
                {
                    total++; if (rows.Count >= lim) continue;
                    var f = go.GetComponent<Fermenter>(); var content = z.GetInt("Content", 0); var start = z.GetLong("StartTime", 0L);
                    var since = start == 0 ? -1 : (now - start) / (double)TimeSpan.TicksPerSecond;
                    var status = ProducerRules.FermenterStatus(content != 0, since, f.m_fermentationDuration);
                    var conv = f.m_conversion.FirstOrDefault(c => c.m_from != null && c.m_from.gameObject.name.GetStableHashCode() == content);
                    rows.Add(head + ",\"kind\":\"fermenter\",\"status\":" + U.S(status) + ",\"content\":" + U.S(content == 0 ? "" : (s.All.ContainsKey(content) ? Name(content) : "unknown")) + ",\"produces\":" + (conv != null && conv.m_to != null ? U.S(conv.m_to.gameObject.name + " x" + conv.m_producedItems) : "null") + ",\"secondsLeft\":" + (status == "fermenting" ? N(ProducerRules.FermenterSecondsLeft(since, f.m_fermentationDuration)) : "null") + ",\"durationSeconds\":" + N(f.m_fermentationDuration) + ",\"liveCover\":" + LiveInstanceCover(z) + ",\"needs\":\"a roof and at least 70% cover at the roof check point, or the timer restarts\"}");
                }
                else if (s.Hive.Contains(h))
                {
                    total++; if (rows.Count >= lim) continue;
                    var b = go.GetComponent<Beehive>(); var level = z.GetInt("level", 0); var prod = z.GetFloat("product", 0f);
                    var biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p.x, p.z) : Heightmap.Biome.None;
                    var okBiome = WorldGenerator.instance != null && (biome & b.m_biome) != 0;
                    rows.Add(head + ",\"kind\":\"beehive\",\"level\":" + level + ",\"max\":" + b.m_maxHoney + ",\"item\":" + U.S(b.m_honeyItem != null ? b.m_honeyItem.gameObject.name : "") + ",\"secondsPerUnit\":" + N(b.m_secPerUnit) + ",\"secondsToFull\":" + (okBiome ? N(ProducerRules.SecondsToLevel(level, b.m_maxHoney, prod, b.m_secPerUnit)) : "null") + ",\"biome\":" + U.S(biome.ToString()) + ",\"biomeAllowed\":" + B(okBiome) + ",\"liveCover\":" + LiveInstanceCover(z) + ",\"needs\":\"under " + U.N(Math.Round(b.m_maxCover * 100)) + "% cover at the cover point\"}");
                }
                else if (s.Sap.Contains(h))
                {
                    total++; if (rows.Count >= lim) continue;
                    var c = go.GetComponent<SapCollector>(); var level = z.GetInt("level", 0); var prod = z.GetFloat("product", 0f);
                    ZDO root = null; var best = 1.5f; foreach (var kv in roots) { var d = Vector3.Distance(kv.Key, p); if (d < best) { best = d; root = kv.Value; } }
                    var rootLevel = root != null ? (double)root.GetFloat("level", 100f) : double.NaN;
                    var units = ProducerRules.SapUnitsAvailable(level, c.m_maxLevel, rootLevel);
                    rows.Add(head + ",\"kind\":\"sapcollector\",\"level\":" + level + ",\"max\":" + c.m_maxLevel + ",\"item\":" + U.S(c.m_spawnItem != null ? c.m_spawnItem.gameObject.name : "") + ",\"secondsPerUnit\":" + N(c.m_secPerUnit) + ",\"root\":" + (root != null ? "{\"id\":" + U.S(U.Id(root)) + ",\"level\":" + N(rootLevel) + "}" : "null") + ",\"secondsToFull\":" + (root != null || !c.m_mustConnectTo ? N(ProducerRules.SecondsToLevel(level, level + units, prod, c.m_secPerUnit)) : "null") + ",\"unitsTheRootCanGive\":" + units + ",\"note\":\"" + (root == null && c.m_mustConnectTo ? "no ancient root within 1.5 m: it makes nothing" : "the root level is the stored one; the root regenerates 1 per second while its zone runs") + "\"}");
                }
            }
            return U.Json(Reply("", total, rows));
        }

        // ------------------------------------------------------------------ loot_near, harvest_near
        static List<Vector3> PlayerPositions()
        {
            var l = new List<Vector3>();
            try { foreach (var p in Player.GetAllPlayers()) if (p != null) l.Add(p.transform.position); } catch (Exception ex) { U.Once("PlayerPositions", ex); }
            foreach (var p in Peers()) if (!p.m_characterID.IsNone()) l.Add(p.m_refPos);
            return l;
        }

        static ToolOutput LootNear(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1; Sets s; ToolOutput err; if (!Setup(a, out x0, out z0, out x1, out z1, out s, out err)) return err;
            var now = NowTicks(); var lim = Limit(a); var rows = new List<string>(); var total = 0; var players = PlayerPositions(); var going = 0;
            var bases = CreatureTools.AreasOf(EffectArea.Type.PlayerBase, x0 - 70, z0 - 70, x1 + 70, z1 + 70);        // ItemDrop.IsInsideBase: a PlayerBase area (fire, workbench, ward ...) at the item
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); if (!s.Item.Contains(h)) continue;
                var go = s.All[h]; var drop = go.GetComponent<ItemDrop>(); if (drop == null) continue;
                total++;
                var spawn = z.GetLong("spawntime", 0L); var age = spawn == 0 ? double.NaN : (now - spawn) / (double)TimeSpan.TicksPerSecond;
                var p = z.GetPosition(); var near = players.Any(pp => Vector3.Distance(pp, p) <= DropRules.PlayerRange);
                var inBase = bases.Any(b => Math.Sqrt((b.X - p.x) * (b.X - p.x) + (b.Y - p.y) * (b.Y - p.y) + (b.Z - p.z) * (b.Z - p.z)) < b.Radius);
                var kept = double.IsNaN(age) ? "no spawn time" : DropRules.KeptBy(drop.m_autoDestroy, age, inBase, p.y, near, false, z.GetBool("piece", false));
                var maybeBase = inBase && p.y > DropRules.BaseMinY;
                if (!double.IsNaN(age) && age >= DropRules.DespawnSeconds && kept == null) going++;
                if (rows.Count >= lim) continue;
                int stack = 1, quality = 1; float dur = 0; string iname = drop.m_itemData != null ? drop.m_itemData.m_shared.m_name : Name(h);
                try { var d = drop.m_itemData.Clone(); ItemDrop.LoadFromZDO(d, z); stack = d.m_stack; quality = d.m_quality; dur = d.m_durability; } catch (Exception ex) { U.Once("loot_near.itemData", ex); }
                rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(Name(h)) + ",\"name\":" + U.S(iname) + ",\"stack\":" + stack + ",\"quality\":" + quality + ",\"durability\":" + N(dur) + ",\"pos\":" + U.V(p) + ",\"ageSeconds\":" + N(age) + ",\"secondsLeft\":" + (kept == null && !double.IsNaN(age) ? N(DropRules.SecondsLeft(age)) : "null") + ",\"keptBy\":" + (kept == null ? "null" : U.S(kept)) + ",\"insideABase\":" + B(inBase) + ",\"mayBeInsideABase\":" + B(maybeBase && kept == null) + "}");
            }
            return U.Json(Reply("", total, rows, ",\"pastAnHourAndUnprotected\":" + going + ",\"note\":\"a base keeps an item only above y 28 and only inside a PlayerBase area (read from the prefabs' area radii); tar is not read\""));
        }

        static ToolOutput HarvestNear(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1; Sets s; ToolOutput err; if (!Setup(a, out x0, out z0, out x1, out z1, out s, out err)) return err;
            var now = NowTicks(); var lim = Limit(a); var ready = new List<string>(); var waiting = new List<string>(); var byPrefab = new Dictionary<string, int[]>(); var total = 0;
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); if (!s.Pick.Contains(h)) continue;
                var pk = s.All[h].GetComponent<Pickable>(); if (pk == null) continue; total++;
                var picked = z.GetBool("picked", pk.m_defaultPicked); var enabled = z.GetBool("enabled", true); var pt = z.GetLong("picked_time", 0L);
                var mins = PickRules.MinutesToRespawn(picked, enabled, pk.m_respawnTimeMinutes, pt, now);
                var nm = Name(h); int[] c; if (!byPrefab.TryGetValue(nm, out c)) byPrefab[nm] = c = new int[3]; c[0]++; if (!picked) c[1]++; else if (!double.IsNaN(mins)) c[2]++;
                var row = "{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(nm) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"picked\":" + B(picked) + ",\"enabled\":" + B(enabled) + ",\"respawnMinutes\":" + N(pk.m_respawnTimeMinutes) + ",\"minutesToRespawn\":" + (picked ? N(mins) : "0") + "}";
                (picked ? waiting : ready).Add(row);
            }
            var rows = ready.Concat(waiting).Take(lim).ToList();
            var groups = string.Join(",", byPrefab.OrderByDescending(kv => kv.Value[0]).Select(kv => "{\"prefab\":" + U.S(kv.Key) + ",\"count\":" + kv.Value[0] + ",\"ready\":" + kv.Value[1] + ",\"waiting\":" + kv.Value[2] + "}").ToArray());
            return U.Json(Reply("", total, rows, ",\"ready\":" + ready.Count + ",\"byPrefab\":[" + groups + "]"));
        }

        // ------------------------------------------------------------------ resource_nodes
        static ToolOutput ResourceNodes(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1; Sets s; ToolOutput err; if (!Setup(a, out x0, out z0, out x1, out z1, out s, out err)) return err;
            var kindWant = McpJson.GetStr(a, "kind") ?? ""; var lim = Limit(a); var tier = U.Has(a, "toolTier") ? (int)U.D(a, "toolTier", 0) : -1; var rows = new List<string>(); var total = 0; var rocks = 0; var areasLeft = 0;
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); GameObject go; if (!s.All.TryGetValue(h, out go)) continue;
                var p = z.GetPosition(); var nm = Name(h);
                if (s.Mine.Contains(h))
                {
                    var m = go.GetComponent<MineRock5>(); var areas = MineRules.DecodeAreas(z.GetString("health", ""));
                    var full = areas != null ? areas.Length : -1; var left = areas != null ? MineRules.Remaining(areas) : -1;
                    if (left == 0) continue;                       // a rock with nothing left is about to go
                    total++; rocks++; if (left > 0) areasLeft += left;
                    if (rows.Count < lim && (kindWant.Length == 0 || kindWant == "rock")) rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(nm) + ",\"kind\":\"rock\",\"pos\":" + U.V(p) + ",\"areasLeft\":" + (left >= 0 ? left.ToString(CultureInfo.InvariantCulture) : "null") + ",\"areas\":" + (full >= 0 ? full.ToString(CultureInfo.InvariantCulture) : "null") + ",\"hpPerArea\":" + N(m.m_health) + ",\"minToolTier\":" + m.m_minToolTier + (tier >= 0 ? ",\"yourToolWorks\":" + B(MineRules.ToolWorks(tier, m.m_minToolTier)) : "") + "}");
                }
                else if (s.Tree.Contains(h) || s.Destr.Contains(h))
                {
                    var tb = go.GetComponent<TreeBase>(); var tl = go.GetComponent<TreeLog>(); var ds = go.GetComponent<Destructible>();
                    double max = tb != null ? tb.m_health : tl != null ? tl.m_health : ds != null ? ds.m_health : 0; int minTier = tb != null ? tb.m_minToolTier : tl != null ? tl.m_minToolTier : ds != null ? ds.m_minToolTier : 0;
                    if (max <= 0) continue;
                    total++;
                    var hp = z.GetFloat("health", (float)max);
                    var kd = tb != null ? "tree" : tl != null ? "log" : "destructible";
                    if (rows.Count < lim && (kindWant.Length == 0 || kindWant == kd)) rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"prefab\":" + U.S(nm) + ",\"kind\":" + U.S(kd) + ",\"pos\":" + U.V(p) + ",\"healthFraction\":" + U.N(Math.Round(hp / max, 2)) + ",\"minToolTier\":" + minTier + (tier >= 0 ? ",\"yourToolWorks\":" + B(MineRules.ToolWorks(tier, minTier)) : "") + "}");
                }
            }
            return U.Json(Reply("", total, rows, ",\"bigRocks\":" + rocks + ",\"rockAreasLeft\":" + areasLeft + ",\"note\":\"the stored health of a rock lists one float per hit area; the tool tier of a tool is its m_toolTier (HitData.CheckToolTier)\""));
        }

        // ------------------------------------------------------------------ spawners_near
        static ToolOutput SpawnersNear(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1; Sets s; ToolOutput err; if (!Setup(a, out x0, out z0, out x1, out z1, out s, out err)) return err;
            var now = NowTicks(); var lim = Limit(a); var rows = new List<string>(); var total = 0; var zs = ZoneSystem.instance;
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); GameObject go; if (!s.All.TryGetValue(h, out go)) continue;
                if (s.Spawner.Contains(h))
                {
                    total++; if (rows.Count >= lim) continue;
                    var sp = go.GetComponent<CreatureSpawner>(); var spawned = z.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned); var hasSpawned = z.GetConnectionType() != ZDOExtraData.ConnectionType.None;
                    var alive = !spawned.IsNone() && ZDOMan.instance.GetZDO(spawned) != null; var aliveTicks = z.GetLong("alive_time", 0L);
                    var mins = SpawnerRules.MinutesToRespawn(alive, hasSpawned, sp.m_respawnTimeMinuts, aliveTicks, now);
                    var req = sp.m_requiredGlobalKey ?? ""; var blk = sp.m_blockingGlobalKey ?? "";
                    rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"kind\":\"spawner\",\"prefab\":" + U.S(Name(h)) + ",\"creature\":" + U.S(sp.m_creaturePrefab != null ? sp.m_creaturePrefab.name : "") + ",\"pos\":" + U.V(z.GetPosition()) + ",\"levels\":[" + sp.m_minLevel + "," + sp.m_maxLevel + "],\"alive\":" + B(alive) + ",\"hasSpawned\":" + B(hasSpawned) + ",\"respawnMinutes\":" + N(sp.m_respawnTimeMinuts) + ",\"minutesToRespawn\":" + N(mins) + ",\"triggerDistance\":" + N(sp.m_triggerDistance) + ",\"day\":" + B(sp.m_spawnAtDay) + ",\"night\":" + B(sp.m_spawnAtNight) + ",\"spawnsInPlayerBase\":" + B(sp.m_spawnInPlayerBase) + ",\"requiredKey\":" + U.S(req) + ",\"requiredKeySet\":" + (req.Length == 0 ? "null" : B(zs != null && zs.GetGlobalKey(req))) + ",\"blockingKey\":" + U.S(blk) + ",\"blockingKeySet\":" + (blk.Length == 0 ? "null" : B(zs != null && zs.GetGlobalKey(blk))) + "}");
                }
                else if (s.Area.Contains(h))
                {
                    foreach (var sa in go.GetComponentsInChildren<SpawnArea>())
                    {
                        total++; if (rows.Count >= lim) continue;
                        rows.Add("{\"id\":" + U.S(U.Id(z)) + ",\"kind\":\"spawnArea\",\"prefab\":" + U.S(Name(h)) + ",\"pos\":" + U.V(z.GetPosition()) + ",\"creatures\":[" + string.Join(",", sa.m_prefabs.Where(d => d.m_prefab != null).Select(d => U.S(d.m_prefab.name)).ToArray()) + "],\"intervalSeconds\":" + N(sa.m_spawnIntervalSec) + ",\"maxNear\":" + sa.m_maxNear + ",\"nearRadius\":" + N(sa.m_nearRadius) + ",\"maxTotal\":" + sa.m_maxTotal + ",\"triggerDistance\":" + N(sa.m_triggerDistance) + "}");
                    }
                }
            }
            return U.Json(Reply("", total, rows, ",\"note\":\"a spawner runs only for its owner, with a player inside the trigger distance; a CreatureSpawner also stays quiet inside a PlayerBase area unless spawnsInPlayerBase\""));
        }

        // ------------------------------------------------------------------ wear_risk
        static ToolOutput WearRisk(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1; Sets s; ToolOutput err; if (!Setup(a, out x0, out z0, out x1, out z1, out s, out err)) return err;
            if (WorldGenerator.instance == null) return ToolOutput.Err("no world generator");
            var lim = Limit(a); var pieces = new List<PlacedPiece>(); var zdos = new Dictionary<PlacedPiece, ZDO>(); var gos = new Dictionary<PlacedPiece, WearNTear>();
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); if (!s.Wear.Contains(h)) continue;
                var name = Name(h); var geo = PlanCheck.GeoOf(name); if (geo == null) continue;
                var p = z.GetPosition(); var q = z.GetRotation();
                var pp = new PlacedPiece { Id = U.Id(z), Geo = geo, Pos = new V3(p.x, p.y, p.z), Rot = new Quat(q.x, q.y, q.z, q.w), Planned = false };
                pieces.Add(pp); zdos[pp] = z; gos[pp] = s.All[h].GetComponent<WearNTear>();
                if (pieces.Count >= 4000) break;
            }
            var all = pieces; var cap = (int)Math.Max(50, Math.Min(1500, U.D(a, "pieces", 600)));        // every piece in the box is a possible roof; only the first `cap` are judged
            pieces = all.Take(cap).ToList();
            var solids = new List<CoverSolid>();
            var allPieces = pieces;
            foreach (var pp in all) foreach (var c in pp.Geo.Colliders.Where(c => Layers.InCover(c.Layer))) foreach (var b in pp.BoxesOf(new[] { c })) solids.Add(new CoverSolid { Box = b, Leaky = c.Leaky });
            var shieldsHere = ShieldsNear(x0, z0, x1, z1);
            double ashDamage = 0; try { if (Game.instance != null) ashDamage = Game.instance.m_ashDamage; } catch (Exception ex) { U.Once("wear_risk.ash", ex); }
            var rain = 0; var ash = 0; var outside = 0; var damaged = 0; var rows = new List<string>(); var roofless = 0;
            foreach (var pp in allPieces)
            {
                var w = gos[pp]; var z = zdos[pp]; var hp = z.GetFloat("health", w.m_health); var frac = w.m_health > 0 ? hp / w.m_health : 1.0;
                var near = solids.Where(sd => Math.Abs(sd.Box.C.X - pp.Pos.X) < 40 && Math.Abs(sd.Box.C.Z - pp.Pos.Z) < 40).ToList();
                var roofed = CoverModel.IsUnderRoof(new V3(pp.Pos.X, pp.Pos.Y + 0.1, pp.Pos.Z), near.Where(sd => sd.Box.C.Y > pp.Pos.Y - 1).ToList(), null);
                var biome = WorldGenerator.instance.GetBiome((float)pp.Pos.X, (float)pp.Pos.Z); var why = new List<string>();
                if (biome == Heightmap.Biome.AshLands)
                {
                    // Heightmap.GetLava (the lava paint) with the piece's height over the ground: only where the terrain is loaded
                    try
                    {
                        var wp = new Vector3((float)pp.Pos.X, (float)pp.Pos.Y, (float)pp.Pos.Z); var hm = Heightmap.FindHeightmap(wp); float gh;
                        if (hm != null && ZoneSystem.instance.GetGroundHeight(wp, out gh))
                        {
                            var lava = hm.GetLava(wp); var pct = WearRules.LavaPercentPerTick(lava, wp.y - gh, ShieldRules.Inside(shieldsHere, pp.Pos.X, pp.Pos.Y, pp.Pos.Z), w.m_ashDamageImmune, w.m_ashDamageResist);
                            if (pct > 0) why.Add("lava paint " + U.N(Math.Round(lava, 2)) + ": " + N(pct) + "% of its health every 2 s");
                        }
                    }
                    catch (Exception ex) { U.Once("wear_risk.lava", ex); }
                }
                if (w.m_noRoofWear && !roofed) { rain++; if (frac > WearRules.RainFloorHealth) why.Add("rain takes it down to half health in " + N(WearRules.SecondsOfRainToHalf(frac, w.m_health) / 60) + " min of wet weather"); }
                if (!roofed) roofless++;
                if (WearRules.AshApplies(biome == Heightmap.Biome.AshLands, ashDamage, ShieldRules.Inside(shieldsHere, pp.Pos.X, pp.Pos.Y, pp.Pos.Z), w.m_ashDamageImmune, roofed, w.m_ashDamageResist, frac)) { ash++; why.Add("ash damage " + N(ashDamage) + "% every 5 s with no roof"); }
                if (WearRules.OutsideRequiredBiome((int)w.m_requiredBiome, (int)biome)) { outside++; why.Add("outside the biome it needs: " + w.m_outsideRequiredBiomeDamage + "% per update"); }
                if (frac < 0.999) damaged++;
                if ((why.Count > 0 || frac < 0.5) && rows.Count < lim) rows.Add("{\"id\":" + U.S(pp.Id) + ",\"prefab\":" + U.S(pp.Geo.Prefab) + ",\"pos\":" + U.V(zdos[pp].GetPosition()) + ",\"healthFraction\":" + U.N(Math.Round(frac, 2)) + ",\"roofed\":" + B(roofed) + ",\"biome\":" + U.S(biome.ToString()) + ",\"risks\":[" + string.Join(",", why.Select(U.S).ToArray()) + "]}");
            }
            return U.Json("{\"piecesInBox\":" + all.Count + ",\"pieces\":" + allPieces.Count + ",\"damaged\":" + damaged + ",\"rainExposed\":" + rain + ",\"withoutARoof\":" + roofless + ",\"ashExposed\":" + ash + ",\"outsideRequiredBiome\":" + outside + ",\"ashDamageSetting\":" + N(ashDamage) + ",\"returned\":" + rows.Count + ",\"rows\":[" + string.Join(",", rows.ToArray()) + "],\"note\":\"rain never takes a piece below half health; the roof is a model of the game's roof cast on the pieces in the box (trees and rocks are not in it); lava paint is read in the Ashlands where the terrain is loaded, deep-north snow is not; a shield generator cancels all of it\"}");
        }

        // ------------------------------------------------------------------ absence_forecast
        static ToolOutput AbsenceForecast(Dictionary<string, object> a)
        {
            float x0, z0, x1, z1; Sets s; ToolOutput err; if (!Setup(a, out x0, out z0, out x1, out z1, out s, out err)) return err;
            var hours = Math.Max(0.1, Math.Min(240, U.D(a, "hours", 8))); var away = hours * 3600; var now = NowTicks();
            int fireOut = 0, fires = 0, smelters = 0, smeltersDone = 0, ready = 0, fermenting = 0, hivesFull = 0, hives = 0, sapFull = 0, saps = 0, picks = 0, picksBack = 0, loot = 0, lootGone = 0, cookers = 0, cookersEmpty = 0;
            double smelterWork = 0; var examples = new List<string>();
            foreach (var z in Zdos.InBox(x0, z0, x1, z1))
            {
                var h = z.GetPrefab(); GameObject go; if (!s.All.TryGetValue(h, out go)) continue;
                if (s.Fire.Contains(h))
                {
                    var fp = go.GetComponent<Fireplace>(); if (fp == null || fp.m_infiniteFuel || fp.m_secPerFuel <= 0) continue;
                    var fuel = z.GetFloat("fuel", 0f); if (!FireRules.Burning(z.GetInt("state", 1), fuel, false)) continue;
                    fires++; if (AbsenceRules.FireOutWithin(fuel, fp.m_secPerFuel, away)) { fireOut++; if (examples.Count < 8) examples.Add(Name(h) + " at " + U.V(z.GetPosition()) + " burns out after " + U.N(Math.Round(AbsenceRules.FireBurnsFor(fuel, fp.m_secPerFuel) / 3600.0, 1)) + " h"); }
                }
                else if (s.Smelt.Contains(h))
                {
                    var sm = go.GetComponent<Smelter>(); if (sm == null) continue;
                    var queued = z.GetInt(ZDOVars.s_queued, 0); if (queued <= 0) continue;                // no queue, no work to catch up (a windmill or kiln with nothing in it)
                    smelters++; var fuel = z.GetFloat(ZDOVars.s_fuel, 0f); var bake = z.GetFloat(ZDOVars.s_bakeTimer, 0f);
                    var spec = new SmelterSpec { MaxFuel = sm.m_maxFuel, MaxOre = sm.m_maxOre, SecPerProduct = sm.m_secPerProduct, FuelPerProduct = sm.m_fuelPerProduct, RequiresRoof = sm.m_requiresRoof };
                    var work = SmelterMath.WorkDoneWhileAway(away, SmelterMath.SecondsToEmpty(spec, queued, bake, 1), sm.m_maxFuel > 0 ? SmelterMath.FuelSecondsLeft(spec, fuel, 1) : double.PositiveInfinity);
                    smelterWork += work; if (work >= SmelterMath.SecondsToEmpty(spec, queued, bake, 1) - 1e-6) smeltersDone++;
                }
                else if (s.Cook.Contains(h))
                {
                    var cs = go.GetComponent<CookingStation>(); if (cs == null) continue;
                    var busy = Enumerable.Range(0, cs.m_slots.Length).Any(i => z.GetString("slot" + i, "").Length > 0); if (!busy) continue;
                    cookers++; if (cs.m_useFuel && cs.m_secPerFuel > 0 && z.GetFloat("fuel", 0f) * cs.m_secPerFuel < away) cookersEmpty++;
                }
                else if (s.Ferm.Contains(h))
                {
                    var f = go.GetComponent<Fermenter>(); if (z.GetInt("Content", 0) == 0) continue; var start = z.GetLong("StartTime", 0L); var since = start == 0 ? -1 : (now - start) / (double)TimeSpan.TicksPerSecond;
                    if (ProducerRules.FermenterStatus(true, since, f.m_fermentationDuration) == "ready") ready++; else { fermenting++; if (since + away > f.m_fermentationDuration) ready++; }
                }
                else if (s.Hive.Contains(h))
                {
                    var b = go.GetComponent<Beehive>(); hives++; var lvl = z.GetInt("level", 0); var biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(z.GetPosition().x, z.GetPosition().z) : Heightmap.Biome.None;
                    if (lvl >= b.m_maxHoney || ((biome & b.m_biome) != 0 && ProducerRules.SecondsToLevel(lvl, b.m_maxHoney, z.GetFloat("product", 0f), b.m_secPerUnit) <= away)) hivesFull++;
                }
                else if (s.Sap.Contains(h))
                {
                    var c = go.GetComponent<SapCollector>(); saps++; var lvl = z.GetInt("level", 0);
                    if (lvl >= c.m_maxLevel || ProducerRules.SecondsToLevel(lvl, c.m_maxLevel, z.GetFloat("product", 0f), c.m_secPerUnit) <= away) sapFull++;
                }
                else if (s.Pick.Contains(h))
                {
                    var pk = go.GetComponent<Pickable>(); if (pk == null || !z.GetBool("picked", pk.m_defaultPicked)) continue; picks++;
                    var mins = PickRules.MinutesToRespawn(true, true, pk.m_respawnTimeMinutes, z.GetLong("picked_time", 0L), now);
                    if (!double.IsNaN(mins) && mins * 60 <= away) picksBack++;
                }
                else if (s.Item.Contains(h))
                {
                    var spawn = z.GetLong("spawntime", 0L); if (spawn == 0) continue; loot++;
                    if (((now - spawn) / (double)TimeSpan.TicksPerSecond) + away >= DropRules.DespawnSeconds && z.GetPosition().y <= DropRules.BaseMinY && s.All[h].GetComponent<ItemDrop>().m_autoDestroy) lootGone++;
                }
            }
            return U.Json("{\"hours\":" + U.N(hours) + ",\"fires\":{\"burning\":" + fires + ",\"goOut\":" + fireOut + "},\"smelters\":{\"working\":" + smelters + ",\"finishTheQueue\":" + smeltersDone + ",\"secondsOfWorkCaughtUp\":" + N(smelterWork) + ",\"capPerAbsenceSeconds\":" + SmelterMath.CatchUpCapSeconds + "},\"cookers\":{\"withFood\":" + cookers + ",\"outOfFuel\":" + cookersEmpty + "},\"fermenters\":{\"readyOrReadyBy\":" + ready + ",\"stillFermenting\":" + fermenting + "},\"beehives\":{\"total\":" + hives + ",\"full\":" + hivesFull + "},\"sapCollectors\":{\"total\":" + saps + ",\"full\":" + sapFull + "},\"pickables\":{\"picked\":" + picks + ",\"backBy\":" + picksBack + "},\"floorItems\":{\"total\":" + loot + ",\"despawnedBy\":" + lootGone + "},\"examples\":[" + string.Join(",", examples.Select(U.S).ToArray()) + "],\"note\":\"read from the stored timestamps and fuel; nothing runs while no peer is near, and what the game catches up on return is what this lists. Items above y 28 inside a base may stay; tar is not read; cooked food may burn (see cookers)\"}");
        }
    }
}
