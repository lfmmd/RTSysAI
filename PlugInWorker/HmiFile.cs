using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RTSys.PlugIn
{
    /// <summary>
    /// ZMotion .hmi 画面文件只读解析器（离线，不依赖控制器或宿主）。
    ///
    /// 文件格式（已由 IDA + 真实样本验证）：
    ///   1. 全文件逐字节 b' = (2 - b) &amp; 0xFF 解码（zmotion.dll ZMC_HmiFileDecode 的等价实现）；
    ///   2. 头部 0x38 字节：[0]=版本(10)、[4]=首记录偏移(0x38)、[8..10]="HMI"；
    ///   3. 记录流：[u32 type][u32 size][payload]，size 含 8 字节头；type=0,size=8 为结束；
    ///      type &lt; 1000 为对象（11=窗口，其余为控件/库），type &gt;= 1000 为前一个对象的特征；
    ///   4. 对象头前 12 字节：[u32 id][u16 w][u16 h][u16 x][u16 y]（控件 id 带 0x01000000 标志位）。
    /// </summary>
    internal static class HmiFile
    {
        private const int HeaderSize = 0x38;
        private const int MaxControls = 500;
        private static readonly Encoding Gbk = Encoding.GetEncoding(936);

        private sealed class HmiObject
        {
            public int Type;
            public byte[] Payload;
            public readonly List<KeyValuePair<int, byte[]>> Features = new List<KeyValuePair<int, byte[]>>();
        }

        // RTSys.exe 内置控件类型（sub_4EE220 注册，已用 Hmi1.hmi 校验）。
        private static readonly Dictionary<int, string> ControlTypes = new Dictionary<int, string>
        {
            { 600, "BitState" }, { 601, "WordState" }, { 602, "BitModify" }, { 603, "WordModify" },
            { 604, "BitSwitch" }, { 605, "WordSwitch" }, { 606, "List" }, { 607, "Slip" },
            { 608, "KeyButton" }, { 609, "Button" }, { 610, "KeyButton" }, { 611, "String show" },
            { 612, "Value show" },
            { 616, "Animation" }, { 713, "String show" }, { 620, "Timer" }, { 621, "Timer" }, { 630, "FlowBlock" },
            { 631, "FlowBlock" }, { 640, "Timer" }, { 641, "Timer" }, { 645, "Table" },
            { 700, "Line/Polyline/Polygon" }, { 701, "Ellipse/Arc/Sector" }, { 702, "Rect" },
            { 703, "StaticText" }, { 704, "Picture" }, { 705, "Import" }, { 706, "Bezier" },
            { 707, "Import" }, { 708, "Import" }, { 709, "Import" }, { 710, "Import" },
            { 711, "Import" }, { 712, "Scale" }, { 714, "File3Edit" }, { 715, "CAD" },
            { 716, "File browser" }, { 717, "Report view" }, { 721, "Tree" }, { 723, "FlowBlock" },
            { 730, "Menu" }, { 731, "Tab control" }, { 733, "Group box" }, { 734, "Panel" },
            { 900, "File3Edit" }, { 901, "Custom" }
        };

        /// <summary>读取当前 RTSys 项目中的 .hmi 文件并返回窗口/控件摘要。</summary>
        public static object Read(string name)
        {
            try
            {
                var dir = ProgramWorkspace.ProjectDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return new { error = "当前没有打开的 RTSys 项目。" };

                var rel = (name ?? "").Trim();
                if (rel.Length == 0)
                    return new { error = "name is required" };
                if (!rel.EndsWith(".hmi", StringComparison.OrdinalIgnoreCase))
                    rel += ".hmi";

                var path = ProgramWorkspace.ResolveInside(dir, rel);
                if (path == null)
                    return new { error = "Invalid HMI file name: " + name };
                if (!File.Exists(path))
                {
                    var available = new List<string>();
                    try
                    {
                        foreach (var f in Directory.GetFiles(dir, "*.hmi"))
                            available.Add(Path.GetFileNameWithoutExtension(f));
                    }
                    catch { }
                    return new { error = "HMI file not found: " + rel, available };
                }

                var raw = File.ReadAllBytes(path);
                return Parse(raw, Path.GetFileName(path), raw.Length);
            }
            catch (Exception ex)
            {
                return new { error = "Failed to read HMI file: " + ex.Message };
            }
        }

        /// <summary>解析已编码的 .hmi 字节（internal 供回归测试直接调用）。</summary>
        internal static Dictionary<string, object> Parse(byte[] raw, string fileName, long sizeBytes)
        {
            var result = new Dictionary<string, object>();
            if (raw == null || raw.Length < HeaderSize + 8)
                return Error(result, "文件太小，不是有效的 ZMotion HMI 文件。");

            // ZMC_HmiFileDecode：b = ~(b - 3) == 2 - b
            var data = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                data[i] = (byte)(2 - raw[i]);

            if (data[8] != 'H' || data[9] != 'M' || data[10] != 'I')
                return Error(result, "魔数不是 HMI，可能不是 ZMotion .hmi 文件。");

            int version = BitConverter.ToInt32(data, 0);
            int firstRecord = BitConverter.ToInt32(data, 4);
            if (firstRecord < 0 || firstRecord > data.Length - 8)
                return Error(result, "首记录偏移无效。");

            var objects = new List<HmiObject>();
            int off = firstRecord;
            while (off + 8 <= data.Length)
            {
                int type = BitConverter.ToInt32(data, off);
                int size = BitConverter.ToInt32(data, off + 4);
                if (size < 8 || off + size > data.Length)
                    break;
                if (type == 0 && size == 8)
                    break;

                var payload = new byte[size - 8];
                Buffer.BlockCopy(data, off + 8, payload, 0, payload.Length);

                if (type < 1000)
                    objects.Add(new HmiObject { Type = type, Payload = payload });
                else if (objects.Count > 0)
                    objects[objects.Count - 1].Features.Add(new KeyValuePair<int, byte[]>(type, payload));

                off += size;
            }

            var windows = new List<object>();
            Dictionary<string, object> currentWindow = null;
            List<object> currentControls = null;
            int controlCount = 0;
            bool truncated = false;

            foreach (var obj in objects)
            {
                if (obj.Type == 11)
                {
                    if (currentWindow != null)
                    {
                        currentWindow["controls"] = currentControls;
                        windows.Add(currentWindow);
                    }

                    int id, w, h, x, y;
                    ReadGeometry(obj.Payload, out id, out w, out h, out x, out y);
                    var winName = FeatureString(obj, 20003);
                    currentWindow = new Dictionary<string, object>
                    {
                        { "id", id },
                        { "name", string.IsNullOrEmpty(winName) ? "Window" + id : winName },
                        { "width", w },
                        { "height", h },
                        { "x", x },
                        { "y", y }
                    };
                    currentControls = new List<object>();
                }
                else if (currentWindow != null && IsControlType(obj.Type))
                {
                    if (controlCount++ < MaxControls)
                        currentControls.Add(BuildControl(obj));
                    else
                        truncated = true;
                }
            }

            if (currentWindow != null)
            {
                currentWindow["controls"] = currentControls;
                windows.Add(currentWindow);
            }

            result["success"] = true;
            result["file"] = fileName;
            result["version"] = version;
            result["sizeBytes"] = sizeBytes;
            result["objectCount"] = objects.Count;
            result["windowCount"] = windows.Count;
            result["windows"] = windows;
            if (truncated)
                result["controlsTruncated"] = true;
            return result;
        }

        private static Dictionary<string, object> Error(Dictionary<string, object> result, string message)
        {
            result.Clear();
            result["success"] = false;
            result["error"] = message;
            return result;
        }

        // 对象类型 10..25/21000 是文件/资源库，不是窗口控件；控件类型为 100..999（含未注册的 902）。
        private static bool IsControlType(int type)
        {
            return (type >= 100 && type < 1000) || ControlTypes.ContainsKey(type);
        }

        private static Dictionary<string, object> BuildControl(HmiObject obj)
        {
            int id, w, h, x, y;
            ReadGeometry(obj.Payload, out id, out w, out h, out x, out y);

            string typeName;
            if (!ControlTypes.TryGetValue(obj.Type, out typeName))
                typeName = obj.Type == 902 ? "Type902" : "Type" + obj.Type;

            var item = new Dictionary<string, object>
            {
                { "type", obj.Type },
                { "typeName", typeName },
                { "id", id },
                { "width", w },
                { "height", h },
                { "x", x },
                { "y", y }
            };

            var name = FeatureString(obj, 1001);
            if (!string.IsNullOrEmpty(name))
                item["name"] = name;

            var texts = new List<string>();
            foreach (var f in obj.Features)
            {
                if (f.Key != 2000 && f.Key != 2001) continue;
                var text = DecodeString(f.Value);
                if (!string.IsNullOrEmpty(text) && !texts.Contains(text))
                    texts.Add(text);
            }
            if (texts.Count > 0)
                item["texts"] = texts;

            return item;
        }

        private static void ReadGeometry(byte[] payload, out int id, out int width, out int height, out int x, out int y)
        {
            id = width = height = x = y = 0;
            if (payload == null || payload.Length < 12) return;
            id = BitConverter.ToInt32(payload, 0) & 0x00FFFFFF;
            width = BitConverter.ToUInt16(payload, 4);
            height = BitConverter.ToUInt16(payload, 6);
            x = BitConverter.ToUInt16(payload, 8);
            y = BitConverter.ToUInt16(payload, 10);
        }

        private static string FeatureString(HmiObject obj, int featureType)
        {
            foreach (var f in obj.Features)
                if (f.Key == featureType)
                    return DecodeString(f.Value);
            return null;
        }

        /// <summary>
        /// 字符串特征载荷：前 1~2 字节为记录前缀（样本中为 00 00），其后为 GBK/NUL 结尾文本。
        /// 无法解析为可打印文本时返回 null。
        /// </summary>
        private static string DecodeString(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;

            int start = 0;
            while (start < payload.Length && (payload[start] == 0 || payload[start] == 1))
                start++;
            if (start >= payload.Length) return null;

            int end = start;
            while (end < payload.Length && payload[end] != 0)
                end++;
            if (end <= start) return null;

            string text;
            try { text = Gbk.GetString(payload, start, end - start).Trim(); }
            catch { return null; }
            if (text.Length == 0) return null;

            foreach (var ch in text)
                if (char.IsControl(ch))
                    return null;
            return text;
        }
    }
}
