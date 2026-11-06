using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- Persistent Memory ----

        private static readonly string MemoryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RTSysAI", "memory");

        public static string LoadMemory()
        {
            try
            {
                if (!Directory.Exists(MemoryDir)) return "";
                var sb = new StringBuilder();
                foreach (var file in Directory.GetFiles(MemoryDir, "*.md")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var content = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrEmpty(content))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.AppendFormat("### {0}", Path.GetFileNameWithoutExtension(file));
                        sb.AppendLine();
                        sb.AppendLine(content);
                    }
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        public static void SaveMemory(string content)
        {
            try
            {
                Directory.CreateDirectory(MemoryDir);
                var path = Path.Combine(MemoryDir, "memory.md");
                File.WriteAllText(path, content);
            }
            catch { }
        }

        public static void ClearMemory()
        {
            try
            {
                if (!Directory.Exists(MemoryDir)) return;
                foreach (var file in Directory.GetFiles(MemoryDir, "*.md"))
                    File.Delete(file);
            }
            catch { }
        }

        public static string GetMemoryText()
        {
            try
            {
                var path = Path.Combine(MemoryDir, "memory.md");
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch { return ""; }
        }
    }
}
