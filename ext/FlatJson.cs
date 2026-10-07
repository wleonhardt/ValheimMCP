// Parser for the one-line objects the journal writes: strings, numbers, number arrays, string arrays and one level of nested
// objects (ints/strings/floats maps). No Unity types: unit-tested in tests/.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HubnerExt
{
    internal static class FlatJson
    {
        public static Dictionary<string, object> Parse(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '{') return null;
            int i = 0;
            try { return Obj(s, ref i); }
            catch { return null; }
        }

        static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>(); i++;                                   // skip '{'
            while (i < s.Length)
            {
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                if (s[i] == ',') { i++; continue; }
                var k = Str(s, ref i); Ws(s, ref i); i++; Ws(s, ref i);                      // skip ':'
                d[k] = Val(s, ref i);
            }
            return d;
        }

        static object Val(string s, ref int i)
        {
            if (s[i] == '"') return Str(s, ref i);
            if (s[i] == '{') return Obj(s, ref i);
            if (s[i] == '[')
            {
                i++; var nums = new List<float>(); var strs = new List<string>(); bool isStr = false;
                while (true)
                {
                    Ws(s, ref i);
                    if (s[i] == ']') { i++; break; }
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '"') { isStr = true; strs.Add(Str(s, ref i)); continue; }
                    nums.Add((float)Num(s, ref i));
                }
                return isStr ? (object)strs.ToArray() : nums.ToArray();
            }
            if (s[i] == 't') { i += 4; return true; }
            if (s[i] == 'f') { i += 5; return false; }
            if (s[i] == 'n') { i += 4; return null; }
            return Num(s, ref i);
        }

        static double Num(string s, ref int i)
        {
            int j = i; while (j < s.Length && "+-0123456789.eE".IndexOf(s[j]) >= 0) j++;
            var v = double.Parse(s.Substring(i, j - i), CultureInfo.InvariantCulture); i = j; return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++; }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder(); i++;
            while (s[i] != '"')
            {
                if (s[i] == '\\')
                {
                    i++; char c = s[i];
                    if (c == 'n') sb.Append('\n'); else if (c == 'r') sb.Append('\r'); else if (c == 't') sb.Append('\t');
                    else if (c == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                    else sb.Append(c);
                }
                else sb.Append(s[i]);
                i++;
            }
            i++; return sb.ToString();
        }

        public static string S(Dictionary<string, object> d, string k) { return d != null && d.TryGetValue(k, out var v) ? v as string : null; }
        public static double N(Dictionary<string, object> d, string k) { return d != null && d.TryGetValue(k, out var v) && v is double x ? x : 0; }
        public static float[] A(Dictionary<string, object> d, string k) { return d != null && d.TryGetValue(k, out var v) ? v as float[] : null; }
        public static Dictionary<string, object> O(Dictionary<string, object> d, string k) { return d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null; }
    }

    /// <summary>Plan item identity (pure, unit-tested): the caller's `key` when given, else prefab + position rounded (0.1 m in the plane, 0.5 m in height, yaw to 1 degree).</summary>
    internal static class PlanKeys
    {
        public static string Of(string explicitKey, string prefab, double x, double y, double z, double yaw, bool seat)
        {
            if (!string.IsNullOrEmpty(explicitKey)) return explicitKey;
            var h = seat ? "s" : (Math.Round(y * 2.0) / 2.0).ToString("0.0", CultureInfo.InvariantCulture);
            var a = ((int)Math.Round(((yaw % 360) + 360) % 360)) % 360;
            return prefab + "|" + Math.Round(x, 1).ToString("0.0", CultureInfo.InvariantCulture) + "|" + Math.Round(z, 1).ToString("0.0", CultureInfo.InvariantCulture) + "|" + h + (a == 0 ? "" : "|r" + a.ToString(CultureInfo.InvariantCulture));
        }
    }
}
