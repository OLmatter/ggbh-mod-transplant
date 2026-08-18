using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
class Test {
private static object ParseJson(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            if (c == '{')
            {
                pos++;
                var dict = new Dictionary<string, object>();
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
                while (pos < s.Length)
                {
                    SkipWs(s, ref pos);
                    string key = ParseString(s, ref pos);
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ':') pos++;
                    object val = ParseJson(s, ref pos);
                    Console.WriteLine("    iter key=[" + key + "] pos=" + pos + " val=" + (val == null ? "null" : val.GetType().Name));
                    dict[key] = val;
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == '}') { pos++; break; }
                    break;
                }
                return dict;
            }
            if (c == '[')
            {
                pos++;
                var list = new List<object>();
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ']') { pos++; return list; }
                while (pos < s.Length)
                {
                    object val = ParseJson(s, ref pos);
                    list.Add(val);
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == ']') { pos++; break; }
                    break;
                }
                return list;
            }
            if (c == '"') return ParseString(s, ref pos);
            if (c == 't' && pos + 4 <= s.Length && s.Substring(pos, 4) == "true") { pos += 4; return true; }
            if (c == 'f' && pos + 5 <= s.Length && s.Substring(pos, 5) == "false") { pos += 5; return false; }
            if (c == 'n' && pos + 4 <= s.Length && s.Substring(pos, 4) == "null") { pos += 4; return null; }
            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E')) pos++;
            if (pos > start)
            {
                double d;
                if (double.TryParse(s.Substring(start, pos - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            }
            return null;
        }
private static string ParseString(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != '"') { pos++; return ""; }
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') break;
                if (c == '\\' && pos < s.Length)
                {
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 <= s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(pos, 4), System.Globalization.NumberStyles.HexNumber, null, out code)) sb.Append((char)code);
                                pos += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\n' || s[pos] == '\r' || s[pos] == '\t')) pos++;
        }
    static void Main(string[] args) {
        string json = File.ReadAllText(args[0], Encoding.UTF8);
        Console.WriteLine("len=" + json.Length + " first3=" + ((int)json[0]).ToString("X4") + " " + ((int)json[1]).ToString("X4") + " " + ((int)json[2]).ToString("X4"));
        int pos = 0;
        object root = ParseJson(json, ref pos);
        var d = root as Dictionary<string, object>;
        Console.WriteLine("pos=" + pos + " root=" + (root == null ? "null" : root.GetType().Name) + " keys=" + (d == null ? -1 : d.Count));
        if (d != null) foreach (var k in d.Keys) Console.WriteLine("  key: " + k);
    }
}
