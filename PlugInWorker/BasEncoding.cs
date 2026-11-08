using System;
using System.IO;
using System.Text;

namespace RTSys.PlugIn
{
    /// <summary>
    /// RTBasic .bas 的编码检测与写入策略。
    /// RTSys 状态栏的 ANSI/UTF-8 选择是宿主自绘控件，插件无法读取或设置；
    /// 因此按文件实际字节判断编码：已有文件保持原编码，新文件按配置（默认 ANSI/GBK）。
    /// </summary>
    internal enum BasEncodingKind
    {
        Ansi,
        Utf8,
        Utf8Bom
    }

    internal static class BasEncoding
    {
        internal const int AnsiCodePage = 936; // GBK

        internal static BasEncodingKind Detect(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return BasEncodingKind.Ansi;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return BasEncodingKind.Utf8Bom;
            // 纯 ASCII 在两种编码下字节相同；RTSys 默认 ANSI，按 ANSI 记，避免把模板误判成 UTF-8。
            var hasNonAscii = false;
            foreach (var b in bytes) { if (b >= 0x80) { hasNonAscii = true; break; } }
            if (!hasNonAscii) return BasEncodingKind.Ansi;
            return IsValidUtf8(bytes) ? BasEncodingKind.Utf8 : BasEncodingKind.Ansi;
        }

        internal static bool IsValidUtf8(byte[] bytes)
        {
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        internal static string Decode(byte[] bytes)
        {
            if (bytes == null) return "";
            switch (Detect(bytes))
            {
                case BasEncodingKind.Utf8Bom:
                    return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                case BasEncodingKind.Utf8:
                    return new UTF8Encoding(false).GetString(bytes);
                default:
                    try { return Encoding.GetEncoding(AnsiCodePage).GetString(bytes); }
                    catch { return Encoding.Default.GetString(bytes); }
            }
        }

        internal static byte[] Encode(string text, BasEncodingKind kind)
        {
            text = text ?? "";
            switch (kind)
            {
                case BasEncodingKind.Utf8Bom:
                {
                    var body = new UTF8Encoding(false).GetBytes(text);
                    var withBom = new byte[body.Length + 3];
                    withBom[0] = 0xEF;
                    withBom[1] = 0xBB;
                    withBom[2] = 0xBF;
                    Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
                    return withBom;
                }
                case BasEncodingKind.Utf8:
                    return new UTF8Encoding(false).GetBytes(text);
                default:
                    return Encoding.GetEncoding(AnsiCodePage).GetBytes(text);
            }
        }

        /// <summary>auto=保持已有文件编码；新文件跟随工程内 .bas 的实际编码（无文件则 ANSI）。</summary>
        internal static BasEncodingKind ResolveForWrite(string path)
        {
            var mode = AiService.BasEncodingMode;
            if (string.Equals(mode, "ansi", StringComparison.OrdinalIgnoreCase)) return BasEncodingKind.Ansi;
            if (string.Equals(mode, "utf8", StringComparison.OrdinalIgnoreCase)) return BasEncodingKind.Utf8;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return Detect(File.ReadAllBytes(path));
            }
            catch { }
            return ResolveDefaultForNewFile();
        }

        /// <summary>
        /// 新文件编码：统计当前工程目录内 .bas 的实际编码并跟随多数。
        /// 这样 RTSys 左下角“输出默认编码”切到 ANSI/UTF-8 后，插件无需用户再设置一次。
        /// </summary>
        internal static BasEncodingKind ResolveDefaultForNewFile()
        {
            try
            {
                var dir = ProgramWorkspace.ProjectDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return BasEncodingKind.Ansi;
                int ansi = 0, utf8 = 0, bom = 0;
                foreach (var file in Directory.GetFiles(dir, "*.bas", SearchOption.TopDirectoryOnly))
                {
                    switch (Detect(File.ReadAllBytes(file)))
                    {
                        case BasEncodingKind.Utf8: utf8++; break;
                        case BasEncodingKind.Utf8Bom: bom++; break;
                        default: ansi++; break;
                    }
                }
                if (bom > ansi && bom >= utf8) return BasEncodingKind.Utf8Bom;
                if (utf8 > ansi) return BasEncodingKind.Utf8;
                return BasEncodingKind.Ansi;
            }
            catch
            {
                return BasEncodingKind.Ansi;
            }
        }

        internal static byte[] EncodeForWrite(string path, string text)
        {
            return Encode(text, ResolveForWrite(path));
        }
    }
}
