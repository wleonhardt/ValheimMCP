// world_clock: the game's day and who is holding up the night skip. Works on the dedicated server (it reads the world time and the character ZDOs, not the local player).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ValheimMCP;

namespace HubnerExt
{
    internal static class ClockTools
    {
        public static void Register(ToolRegistry r)
        {
            r.Add("world_clock", "The game's clock and the night skip, from the world time (works on the dedicated server): day number, the day fraction as the game uses it (0.25 dawn, 0.75 dusk), isDay / isAfternoon / isNight, seconds to dusk and to dawn, the day length (read from the game: it is a setting), whether the clock allows sleeping, and who holds up the skip: the night is skipped only when the clock allows it and EVERY character is in bed (characters not in bed are listed by name).",
                "{\"type\":\"object\",\"properties\":{}}", WorldClock);
        }

        static ToolOutput WorldClock(Dictionary<string, object> a)
        {
            if (ZNet.instance == null) return ToolOutput.Err("no world loaded");
            var total = ZNet.instance.GetTimeSeconds();
            var em = EnvMan.instance;
            var len = em != null ? (double)em.m_dayLengthSec : 0;
            if (len <= 0) return ToolOutput.Err("the day length is not known here (no EnvMan): the clock cannot be computed");
            var f = DayClock.Fraction(total, len);
            var notInBed = new List<string>(); var chars = 0; var inBed = 0;
            try
            {
                foreach (var z in ZNet.instance.GetAllCharacterZDOS())
                {
                    chars++;
                    if (z.GetBool(ZDOVars.s_inBed)) inBed++; else notInBed.Add(z.GetString(ZDOVars.s_playerName, "?"));
                }
            }
            catch (Exception ex) { U.Once("world_clock.characters", ex); }
            var skipping = false; try { skipping = em.IsTimeSkipping(); } catch (Exception ex) { U.Once("world_clock.skip", ex); }
            var sinceSleep = double.PositiveInfinity; try { var f0 = typeof(Game).GetField("m_lastSleepTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public); if (f0 != null && Game.instance != null) sinceSleep = ZNet.instance.GetTimeSeconds() - (double)f0.GetValue(Game.instance); } catch (Exception ex) { U.Once("world_clock.lastSleep", ex); }
            return U.Json("{\"time\":" + U.N(Math.Round(total, 1)) + ",\"day\":" + (int)(total / len) + ",\"dayLengthSec\":" + U.N(len) + ",\"dayFraction\":" + U.N(Math.Round(f, 3)) + ",\"rawFraction\":" + U.N(Math.Round(DayClock.Raw(total, len), 3)) +
                ",\"phase\":" + U.S(DayClock.Phase(f)) + ",\"isDay\":" + B(DayClock.IsDay(f)) + ",\"isAfternoon\":" + B(DayClock.IsAfternoon(f)) + ",\"isNight\":" + B(DayClock.IsNight(f)) +
                ",\"secondsToDusk\":" + U.N(Math.Round(DayClock.SecondsUntil(total, len, 0.75))) + ",\"secondsToDawn\":" + U.N(Math.Round(DayClock.SecondsUntil(total, len, 0.25))) +
                ",\"sleepWindow\":" + B(DayClock.IsAfternoon(f) || DayClock.IsNight(f)) + ",\"characters\":" + chars + ",\"inBed\":" + inBed + ",\"notInBed\":[" + string.Join(",", notInBed.Select(U.S).ToArray()) + "]," +
                "\"nightSkips\":" + B(DayClock.NightSkips(f, chars, inBed, skipping, sinceSleep)) + ",\"timeSkipping\":" + B(skipping) + ",\"secondsSinceLastSleep\":" + (double.IsInfinity(sinceSleep) ? "null" : U.N(Math.Round(sinceSleep))) + "}");
        }

        static string B(bool v) { return v ? "true" : "false"; }
    }
}
