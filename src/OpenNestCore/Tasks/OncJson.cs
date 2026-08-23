using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenNestCore.Tasks;

/// <summary>
/// 轻量 JSON 解析/序列化（IL2CPP 安全，零依赖，仅 System）。
/// 值类型：<see cref="string"/> / <see cref="double"/> / <see cref="bool"/> / null /
/// <see cref="Dictionary{TKey,TValue}"/>（对象）/ <see cref="List{T}"/>（数组）。
/// 任务文件（<see cref="OncMissionIO"/>）用它解析/生成 JSON。
/// 支持标准转义（\" \\ \/ \b \f \n \r \t \uXXXX）。不解析注释/尾逗号。
/// </summary>
public static class OncJson
{
    public static Dictionary<string, object> ParseObject(string json)
        => Parse(json) as Dictionary<string, object>;

    public static object Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int pos = 0;
        return ParseValue(json, ref pos);
    }

    public static string Serialize(object value)
    {
        var sb = new StringBuilder(256);
        WriteValue(sb, value);
        return sb.ToString();
    }

    // ---------- 序列化 ----------

    private static void WriteValue(StringBuilder sb, object v)
    {
        if (v == null) { sb.Append("null"); return; }
        if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
        if (v is string s) { WriteString(sb, s); return; }
        if (v is int i) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is long l) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is float f) { sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); return; }
        if (v is double d) { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
        if (v is Dictionary<string, object> obj)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in obj)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
            return;
        }
        if (v is System.Collections.IEnumerable en)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in en)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
            return;
        }
        WriteString(sb, v.ToString());
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        if (s != null)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
        sb.Append('"');
    }

    // ---------- 解析 ----------

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
            else break;
        }
    }

    private static object ParseValue(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        if (pos >= s.Length) return null;
        char c = s[pos];
        if (c == '{') return ParseObj(s, ref pos);
        if (c == '[') return ParseArr(s, ref pos);
        if (c == '"') return ParseStr(s, ref pos);
        if (c == 't') { pos += 4; return true; }
        if (c == 'f') { pos += 5; return false; }
        if (c == 'n') { pos += 4; return null; }
        return ParseNum(s, ref pos);
    }

    private static Dictionary<string, object> ParseObj(string s, ref int pos)
    {
        var o = new Dictionary<string, object>();
        pos++; // '{'
        SkipWs(s, ref pos);
        if (pos < s.Length && s[pos] == '}') { pos++; return o; }
        while (pos < s.Length)
        {
            SkipWs(s, ref pos);
            string key = ParseStr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ':') pos++;
            object val = ParseValue(s, ref pos);
            o[key] = val;
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; continue; }
            if (pos < s.Length && s[pos] == '}') { pos++; break; }
            break;
        }
        return o;
    }

    private static List<object> ParseArr(string s, ref int pos)
    {
        var arr = new List<object>();
        pos++; // '['
        SkipWs(s, ref pos);
        if (pos < s.Length && s[pos] == ']') { pos++; return arr; }
        while (pos < s.Length)
        {
            object val = ParseValue(s, ref pos);
            arr.Add(val);
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; continue; }
            if (pos < s.Length && s[pos] == ']') { pos++; break; }
            break;
        }
        return arr;
    }

    private static string ParseStr(string s, ref int pos)
    {
        var sb = new StringBuilder();
        pos++; // '"'
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '"') { pos++; return sb.ToString(); }
            if (c == '\\')
            {
                pos++;
                if (pos >= s.Length) break;
                char e = s[pos];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (pos + 4 < s.Length)
                        {
                            string hex = s.Substring(pos + 1, 4);
                            int code;
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                sb.Append((char)code);
                            pos += 4;
                        }
                        break;
                }
                pos++;
                continue;
            }
            sb.Append(c);
            pos++;
        }
        return sb.ToString();
    }

    private static object ParseNum(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' ||
               s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
            pos++;
        string num = s.Substring(start, pos - start);
        double d;
        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return d;
        return 0.0;
    }

    // ---------- 类型化读取辅助 ----------

    public static bool TryGetString(Dictionary<string, object> o, string key, out string val)
    {
        val = null;
        if (o != null && o.TryGetValue(key, out var v) && v != null)
        {
            val = v.ToString();
            return true;
        }
        return false;
    }

    public static string GetString(Dictionary<string, object> o, string key, string fallback = null)
        => TryGetString(o, key, out var s) ? s : fallback;

    public static int GetInt(Dictionary<string, object> o, string key, int fallback = 0)
    {
        if (o != null && o.TryGetValue(key, out var v) && v != null)
        {
            if (v is double d) return (int)d;
            if (v is long l) return (int)l;
            if (v is bool b) return b ? 1 : 0;
            int x;
            if (int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) return x;
        }
        return fallback;
    }

    public static float GetFloat(Dictionary<string, object> o, string key, float fallback = 0f)
    {
        if (o != null && o.TryGetValue(key, out var v) && v != null)
        {
            if (v is double d) return (float)d;
            if (v is long l) return l;
            float x;
            if (float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return x;
        }
        return fallback;
    }

    public static bool GetBool(Dictionary<string, object> o, string key, bool fallback = false)
    {
        if (o != null && o.TryGetValue(key, out var v))
        {
            if (v is bool b) return b;
            return v != null && v.ToString().ToLowerInvariant() == "true";
        }
        return fallback;
    }

    public static Dictionary<string, object> GetObject(Dictionary<string, object> o, string key)
        => (o != null && o.TryGetValue(key, out var v) && v is Dictionary<string, object> d) ? d : null;

    public static List<object> GetArray(Dictionary<string, object> o, string key)
        => (o != null && o.TryGetValue(key, out var v) && v is List<object> l) ? l : null;
}
