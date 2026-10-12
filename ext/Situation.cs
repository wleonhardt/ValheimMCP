// situation: one call that tells a model where the local character stands, in word fields and bands (no raw dumps), and explored_at: has the map been uncovered there. Client only (needs the local player and its map).
#if !SERVER
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
    internal static class SituationTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("situation", "One call for the local character, in word fields: day phase and seconds to dusk and dawn, whether sleeping is possible now, health and stamina bands, food (hungry, fading, fed), pack (light to encumbered), hostiles around (calm, watch, danger) and the nearest, the distance to its bed spawn point, whether it stands under a roof and in a base, the weather and wet or cold effects, what jobs run (a walk, queued jobs). Client only.",
                "{\"type\":\"object\",\"properties\":{\"radius\":{\"type\":\"number\",\"description\":\"how far to look for hostiles (default 30 m)\"}}}", Situation);
            r.Add("explored_at", "Has the local player's map been uncovered at these points? points:[[x,z],...] (max 200) or x,z. The map is the client's own (Minimap), so this answers for this character only.",
                "{\"type\":\"object\",\"properties\":{\"points\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}},\"x\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}}}", ExploredAt);
        }

        static string B(bool v) { return v ? "true" : "false"; }

        static ToolOutput Situation(Dictionary<string, object> a)
        {
            var p = Player.m_localPlayer; if (p == null) return ToolOutput.Err("no local player (menu or loading)");
            var radius = (float)Math.Max(5, Math.Min(100, U.D(a, "radius", 30)));
            var sb = new StringBuilder("{");
            try
            {
                var em = EnvMan.instance; var total = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0; var len = em != null ? (double)em.m_dayLengthSec : 0;
                if (len > 0)
                {
                    var f = DayClock.Fraction(total, len);
                    sb.Append("\"phase\":" + U.S(DayClock.Phase(f)) + ",\"secondsToDusk\":" + U.N(Math.Round(DayClock.SecondsUntil(total, len, 0.75))) + ",\"secondsToDawn\":" + U.N(Math.Round(DayClock.SecondsUntil(total, len, 0.25))) + ",\"sleepWindow\":" + B(DayClock.IsAfternoon(f) || DayClock.IsNight(f)) + ",");
                }
            }
            catch (Exception ex) { U.Once("situation.day", ex); }
            double hf = p.GetMaxHealth() > 0 ? p.GetHealth() / p.GetMaxHealth() : 1, sf = p.GetMaxStamina() > 0.01f ? p.GetStamina() / p.GetMaxStamina() : 1;
            sb.Append("\"health\":" + U.S(SituationBands.Health(hf)) + ",\"stamina\":" + U.S(SituationBands.Stamina(sf)));
            try
            {
                var foods = p.GetFoods(); double minLeft = double.PositiveInfinity;
                foreach (var f in foods) { var t = f.GetType().GetField("m_time"); if (t != null) minLeft = Math.Min(minLeft, Convert.ToDouble(t.GetValue(f), CultureInfo.InvariantCulture)); }
                sb.Append(",\"food\":" + U.S(SituationBands.Food(foods.Count, double.IsInfinity(minLeft) ? 0 : minLeft)) + ",\"foodsActive\":" + foods.Count);
            }
            catch (Exception ex) { U.Once("situation.food", ex); }
            sb.Append(",\"pack\":" + U.S(SituationBands.Pack(p.GetInventory().GetTotalWeight(), p.GetMaxCarryWeight(), p.IsEncumbered())));
            try
            {
                int near = 0, alert = 0; string nearest = ""; double nd = double.PositiveInfinity;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c == p || c.IsDead() || c.IsTamed() || c.IsPlayer() || c.GetFaction() == Character.Faction.Players) continue;
                    var d = Vector3.Distance(c.transform.position, p.transform.position); if (d > radius) continue;
                    near++; var ai = c.GetComponent<BaseAI>(); if (ai != null && ai.IsAlerted()) alert++;
                    if (d < nd) { nd = d; nearest = c.name.Replace("(Clone)", ""); }
                }
                sb.Append(",\"hostiles\":" + U.S(SituationBands.Danger(near, alert)) + ",\"hostilesNear\":" + near + (near > 0 ? ",\"nearestHostile\":" + U.S(nearest) + ",\"nearestHostileMetres\":" + U.N(Math.Round(nd)) : ""));
            }
            catch (Exception ex) { U.Once("situation.hostiles", ex); }
            try
            {
                var prof = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
                if (prof != null && prof.HaveCustomSpawnPoint()) sb.Append(",\"bed\":" + U.S(SituationBands.Distance(Vector3.Distance(prof.GetCustomSpawnPoint(), p.transform.position))));
                else sb.Append(",\"bed\":\"none claimed\"");
            }
            catch (Exception ex) { U.Once("situation.bed", ex); }
            var head = p.transform.position + Vector3.up * 1.5f;
            sb.Append(",\"cover\":" + ReaderTools.LiveCoverJson(head));
            try { sb.Append(",\"inBase\":" + B(EffectArea.IsPointInsideArea(p.transform.position, EffectArea.Type.PlayerBase, 0f) != null)); } catch (Exception ex) { U.Once("situation.base", ex); }
            try { sb.Append(",\"environment\":" + U.S(EnvMan.instance.GetCurrentEnvironment().m_name)); } catch (Exception ex) { U.Once("situation.env", ex); }
            try
            {
                var eff = p.GetSEMan().GetStatusEffects().Select(se => se.name.Replace("(Clone)", "")).Where(n => n.Length > 0).Distinct().Take(8).ToList();
                sb.Append(",\"effects\":[" + string.Join(",", eff.Select(U.S).ToArray()) + "]");
            }
            catch (Exception ex) { U.Once("situation.effects", ex); }
            try { sb.Append(",\"biome\":" + U.S(WorldGenerator.instance.GetBiome(p.transform.position).ToString())); } catch (Exception ex) { U.Once("situation.biome", ex); }
            try { var w = Control.WalkStatusWord(); sb.Append(",\"walking\":" + U.S(w)); } catch (Exception ex) { U.Once("situation.walk", ex); }
            sb.Append(",\"grounded\":" + B(p.IsOnGround()) + ",\"swimming\":" + B(p.IsSwimming()) + "}");
            return U.Json(sb.ToString());
        }

        static ToolOutput ExploredAt(Dictionary<string, object> a)
        {
            var mm = Minimap.instance; if (mm == null) return ToolOutput.Err("no map (menu or loading)");
            var m = typeof(Minimap).GetMethod("IsExplored", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(Vector3) }, null);
            if (m == null) return ToolOutput.Err("Minimap.IsExplored not found in this game version");
            var pts = new List<double[]>(); var l = McpJson.GetList(a, "points");
            if (l != null) foreach (var o in l) { var q = o as List<object>; if (q != null && q.Count >= 2) { var x = U.At(q, 0); var z = q.Count >= 3 ? U.At(q, 2) : U.At(q, 1); if (WorldBounds.InWorld(x, 0, z)) pts.Add(new[] { x, z }); } }
            else if (U.Has(a, "x") && WorldBounds.InWorld(U.D(a, "x", 0), 0, U.D(a, "z", 0))) pts.Add(new[] { U.D(a, "x", 0), U.D(a, "z", 0) });
            if (pts.Count == 0) return ToolOutput.Err("give points:[[x,z],...] or x,z (finite, inside the world)");
            var rows = new List<string>(); var n = 0;
            foreach (var q in pts.Take(200)) { var e = (bool)m.Invoke(mm, new object[] { new Vector3((float)q[0], 0f, (float)q[1]) }); if (e) n++; rows.Add("[" + U.N(q[0]) + "," + U.N(q[1]) + "," + B(e) + "]"); }
            return U.Json("{\"columns\":[\"x\",\"z\",\"explored\"],\"explored\":" + n + ",\"asked\":" + rows.Count + ",\"points\":[" + string.Join(",", rows.ToArray()) + "]}");
        }
    }
}
#endif
