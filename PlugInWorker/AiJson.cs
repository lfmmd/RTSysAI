using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- JSON Helpers ----

        private static string SerializeRequest(Dictionary<string, object> body)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in body)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(EscapeJson(kv.Key)).Append('"').Append(':');
                sb.Append(SerializeValue(kv.Value));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string SerializeValue(object val)
        {
            if (val == null) return "null";
            if (val is string s) return "\"" + EscapeJson(s) + "\"";
            if (val is bool b) return b ? "true" : "false";
            if (val is int || val is long || val is double || val is float || val is decimal)
                return val.ToString();
            if (val is Dictionary<string, object> dict)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(EscapeJson(kv.Key)).Append('"').Append(':');
                    sb.Append(SerializeValue(kv.Value));
                }
                sb.Append('}');
                return sb.ToString();
            }
            if (val is IList<object> list)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(SerializeValue(item));
                }
                sb.Append(']');
                return sb.ToString();
            }
            return _json.Serialize(val);
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string GetStringValue(Dictionary<string, object> dict, string key)
        {
            object val;
            return dict.TryGetValue(key, out val) ? val?.ToString() : null;
        }

        private static int GetIntValue(Dictionary<string, object> dict, string key)
        {
            object val;
            if (!dict.TryGetValue(key, out val) || val == null) return 0;
            int i;
            if (int.TryParse(val.ToString(), out i)) return i;
            // JavaScriptSerializer may deserialize numbers as double
            double d;
            if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out d)) return (int)d;
            return 0;
        }

        private static Dictionary<string, object> GetDictValue(Dictionary<string, object> dict, string key)
        {
            object val;
            if (dict.TryGetValue(key, out val) && val is Dictionary<string, object> d) return d;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                try { return _json.Deserialize<Dictionary<string, object>>(_json.Serialize(val)); } catch { }
            }
            return null;
        }

        private static List<Dictionary<string, object>> GetContentBlocks(Dictionary<string, object> response)
        {
            object val;
            if (!response.TryGetValue("content", out val)) return new List<Dictionary<string, object>>();
            if (val is List<Dictionary<string, object>> list) return list;
            if (val is System.Collections.ArrayList al)
            {
                var result = new List<Dictionary<string, object>>();
                foreach (var item in al)
                {
                    if (item is Dictionary<string, object> d)
                        result.Add(d);
                    else
                    {
                        try { result.Add(_json.Deserialize<Dictionary<string, object>>(_json.Serialize(item))); } catch { }
                    }
                }
                return result;
            }
            return new List<Dictionary<string, object>>();
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            object val;
            if (!d.TryGetValue(key, out val) || val == null) return null;
            if (val is bool b) return b ? "true" : "false";
            return val.ToString();
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result)) return result;
            }
            return 0;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int defaultValue)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                if (val is double dd) return (int)dd;
                if (val is long dl) return (int)dl;
                int result;
                if (int.TryParse(val.ToString(), out result)) return result;
            }
            return defaultValue;
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                if (val is double dd) return (long)dd;
                if (val is long dl) return dl;
                if (val is int di) return di;
                long result;
                if (long.TryParse(val.ToString(), out result)) return result;
            }
            return 0;
        }

        // 十六进制地址解析:支持 "0x4000"/"0X4000",无前缀先尝试十进制失败再按十六进制。
        // 与 ApiServer.TryParseAddr 规则一致,确保 AI 工具链与 REST 路由解析结果相同。
        private static int GetHexInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (!d.TryGetValue(key, out val) || val == null) return 0;
            if (val is double dd) return (int)dd;
            if (val is long dl) return (int)dl;
            if (val is int di) return di;
            var s = val.ToString().Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out int h1) ? h1 : 0;
            if (int.TryParse(s, out int dec)) return dec;
            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int h2) ? h2 : 0;
        }

        private static long GetHexLong(Dictionary<string, object> d, string key)
        {
            object val;
            if (!d.TryGetValue(key, out val) || val == null) return 0;
            if (val is double dd) return (long)dd;
            if (val is long dl) return dl;
            if (val is int di) return di;
            var s = val.ToString().Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                     System.Globalization.CultureInfo.InvariantCulture, out long h1) ? h1 : 0;
            if (long.TryParse(s, out long dec)) return dec;
            return long.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                                 System.Globalization.CultureInfo.InvariantCulture, out long h2) ? h2 : 0;
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
                return val.ToString().ToLowerInvariant() == "true";
            return false;
        }
    }
}
