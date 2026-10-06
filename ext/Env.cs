// Client-local environment control: time of day and weather for renders. Nothing is written to the world; the override is released by env_release, a timer, or leaving the world.
#if !SERVER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Env
    {
        static float _releaseAt = -1f; static bool _timeHeld; static string _weather = "";
        const float MaxHoldSeconds = 600f;

        static readonly Dictionary<string, float> Presets = new Dictionary<string, float> { { "dawn", 0.20f }, { "morning", 0.30f }, { "noon", 0.50f }, { "day", 0.50f }, { "afternoon", 0.65f }, { "dusk", 0.78f }, { "night", 0.95f }, { "midnight", 0.0f } };

        public static void Register(ToolRegistry r)
        {
            r.Add("time_set", "Hold the CLIENT's time of day for renders: t = day fraction 0..1 or a preset (dawn, morning, noon, day, afternoon, dusk, night, midnight). Visual only (the world clock is not touched); released by env_release or after 10 minutes. Returns the resulting day fraction and isDay/isNight.",
                "{\"type\":\"object\",\"properties\":{\"t\":{\"type\":[\"number\",\"string\"]}},\"required\":[\"t\"]}", TimeSet, true);
            r.Add("weather_set", "Force a weather/environment on the CLIENT for renders (e.g. Clear, Misty, Rain, ThunderStorm, Snow, Twilight_Clear). Empty string clears. Visual only; released by env_release or after 10 minutes.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}", WeatherSet, true);
            r.Add("env_state", "Current client environment: day fraction, isDay/isNight, environment name, and whether a time/weather override is held (with seconds until it auto-releases).", "{\"type\":\"object\",\"properties\":{}}", State);
            r.Add("env_release", "Release any time/weather override held by time_set / weather_set.", "{\"type\":\"object\",\"properties\":{}}", Release, true);
        }

        static FieldInfo F(string n) { return typeof(EnvMan).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); }

        static ToolOutput TimeSet(Dictionary<string, object> a)
        {
            var em = EnvMan.instance; if (em == null) return ToolOutput.Err("no EnvMan (not in a world)");
            float t;
            var raw = a.ContainsKey("t") ? a["t"] : null;
            if (raw is string str)
            {
                if (!Presets.TryGetValue(str.ToLowerInvariant(), out t) && !float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out t)) return ToolOutput.Err("unknown time '" + str + "'");
            }
            else t = (float)Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            t = Mathf.Repeat(t, 1f);
            var fd = F("m_debugTimeOfDay"); var ft = F("m_debugTime");
            if (fd == null || ft == null) return ToolOutput.Err("EnvMan debug time fields not found in this game version");
            fd.SetValue(em, true); ft.SetValue(em, t); _timeHeld = true; _releaseAt = Time.time + MaxHoldSeconds;
            return State(null);
        }

        static ToolOutput WeatherSet(Dictionary<string, object> a)
        {
            var em = EnvMan.instance; if (em == null) return ToolOutput.Err("no EnvMan (not in a world)");
            var name = McpJson.GetStr(a, "name") ?? "";
            try { em.SetForceEnvironment(name); } catch (Exception ex) { return ToolOutput.Err("SetForceEnvironment failed: " + ex.Message); }
            _weather = name; if (name.Length > 0) _releaseAt = Time.time + MaxHoldSeconds;
            return State(null);
        }

        static ToolOutput Release(Dictionary<string, object> a) { DoRelease(); return State(null); }

        static void DoRelease()
        {
            var em = EnvMan.instance; _releaseAt = -1f;
            if (em != null)
            {
                try { var fd = F("m_debugTimeOfDay"); if (fd != null) fd.SetValue(em, false); } catch { }
                try { if (_weather.Length > 0) em.SetForceEnvironment(""); } catch { }
            }
            _timeHeld = false; _weather = "";
        }

        public static void Tick() { if (_releaseAt > 0f && Time.time >= _releaseAt) DoRelease(); }

        static ToolOutput State(Dictionary<string, object> a)
        {
            var em = EnvMan.instance; if (em == null) return U.Json("{\"world\":false}");
            float frac = 0f; try { frac = em.GetDayFraction(); } catch { }
            bool day = false, night = false; try { day = EnvMan.IsDay(); night = EnvMan.IsNight(); } catch { }
            string env = ""; try { var m = typeof(EnvMan).GetMethod("GetCurrentEnvironment", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); var e = m != null ? m.Invoke(em, null) : null; if (e != null) { var f = e.GetType().GetField("m_name"); env = f != null ? Convert.ToString(f.GetValue(e)) : ""; } } catch { }
            return U.Json("{\"dayFraction\":" + U.N(Math.Round(frac, 3)) + ",\"isDay\":" + (day ? "true" : "false") + ",\"isNight\":" + (night ? "true" : "false") + ",\"environment\":" + U.S(env) + ",\"timeHeld\":" + (_timeHeld ? "true" : "false") + ",\"weatherForced\":" + U.S(_weather) + ",\"autoReleaseSeconds\":" + (_releaseAt > 0 ? U.N(Math.Round(_releaseAt - Time.time)) : "null") + "}");
        }
    }
}
#endif
