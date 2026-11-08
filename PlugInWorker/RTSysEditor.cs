using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RTSys.PlugIn
{
    /// <summary>
    /// RTSys 编辑器（Scintilla）桥接。
    ///
    /// 插件与 RTSys 同进程，可直接操作宿主里已打开文件的编辑控件：
    ///   - 读取：SCI_GETTEXT（UTF-8）——返回用户当前看到的内容，包含未保存修改；
    ///   - 写入：SCI_SETTEXT（UTF-8）+ 向 MDI 子窗口发送 ID_FILE_SAVE，
    ///     由 RTSys 自己保存到磁盘（宿主负责 UTF-8 → GBK 落盘）。
    ///
    /// 未打开的文件返回 false，由调用方回退到项目文件/本地缓存。
    /// 在独立 worker 进程中 MainWindowHandle 不为 RTSys，会自然返回 false。
    /// </summary>
    internal static class RTSysEditor
    {
        private const int SCI_GETLENGTH = 2006;
        private const int SCI_GETTEXT = 2182;
        private const int SCI_SETTEXT = 2181;

        private const int WM_COMMAND = 0x111;
        private const int ID_FILE_SAVE = 0xE103;   // MFC 标准命令
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        /// <summary>该程序当前是否在 RTSys 编辑器中打开。</summary>
        public static bool IsOpen(string programName)
        {
            IntPtr frame, sci;
            return TryFind(programName, out frame, out sci);
        }

        /// <summary>读取编辑器当前内容（UTF-8，含未保存修改）。</summary>
        public static bool TryRead(string programName, out string text)
        {
            text = null;
            IntPtr frame, sci;
            if (!TryFind(programName, out frame, out sci)) return false;

            IntPtr lenPtr = SendMessage(sci, SCI_GETLENGTH, IntPtr.Zero, IntPtr.Zero);
            int len = lenPtr.ToInt32();
            if (len < 0) return false;
            if (len == 0) { text = ""; return true; }

            IntPtr buf = Marshal.AllocHGlobal(len + 1);
            try
            {
                SendMessage(sci, SCI_GETTEXT, (IntPtr)(len + 1), buf);
                var bytes = new byte[len + 1];
                Marshal.Copy(buf, bytes, 0, len + 1);
                int end = Array.IndexOf(bytes, (byte)0);
                if (end < 0) end = len;
                text = Encoding.UTF8.GetString(bytes, 0, end);
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// 把内容写入编辑器；save=true 时向宿主发送 ID_FILE_SAVE，让 RTSys 自己保存。
        /// 返回 false 表示该程序未在编辑器中打开（调用方应回退到磁盘写入）。
        /// </summary>
        public static bool TryWrite(string programName, string text, bool save)
        {
            IntPtr frame, sci;
            if (!TryFind(programName, out frame, out sci)) return false;

            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            IntPtr buf = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, buf, bytes.Length);
                Marshal.WriteByte(buf, bytes.Length, 0);
                SendMessage(sci, SCI_SETTEXT, IntPtr.Zero, buf);
            }
            finally { Marshal.FreeHGlobal(buf); }

            if (save)
            {
                IntPtr result;
                SendMessageTimeout(frame, WM_COMMAND, (IntPtr)ID_FILE_SAVE, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 5000, out result);
            }
            return true;
        }

        private static bool TryFind(string programName, out IntPtr frame, out IntPtr sci)
        {
            frame = IntPtr.Zero;
            sci = IntPtr.Zero;
            if (string.IsNullOrEmpty(programName)) return false;

            try
            {
                IntPtr main = Process.GetCurrentProcess().MainWindowHandle;
                if (main == IntPtr.Zero) return false;

                string target = programName.Trim();
                if (!target.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)) target += ".bas";

                IntPtr foundFrame = IntPtr.Zero;
                IntPtr foundSci = IntPtr.Zero;

                EnumChildWindows(main, (h, l) =>
                {
                    var sb = new StringBuilder(512);
                    GetWindowText(h, sb, sb.Capacity);
                    var title = sb.ToString().TrimStart('*', ' ');
                    if (!string.Equals(title, target, StringComparison.OrdinalIgnoreCase))
                        return true;

                    IntPtr sciHwnd = IntPtr.Zero;
                    EnumChildWindows(h, (ch, cl) =>
                    {
                        var cb = new StringBuilder(64);
                        GetClassName(ch, cb, cb.Capacity);
                        if (cb.ToString() == "Scintilla") { sciHwnd = ch; return false; }
                        return true;
                    }, IntPtr.Zero);

                    if (sciHwnd != IntPtr.Zero)
                    {
                        foundFrame = h;
                        foundSci = sciHwnd;
                        return false; // 找到即停止
                    }
                    return true;
                }, IntPtr.Zero);

                frame = foundFrame;
                sci = foundSci;
            }
            catch { }

            return frame != IntPtr.Zero && sci != IntPtr.Zero;
        }
    }
}
