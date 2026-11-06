using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RTSys.PlugIn
{
    /// <summary>
    /// 文件日志工具：不依赖 Trace，直接写入 %APPDATA%\RTSysAI\<LogFileName>
    /// 线程安全，带时间戳，UTF-8
    ///
    /// 默认写 plugin.log。独立进程（如 PlugInWorker）可在启动时设置 LogFileName
    /// 为 "worker.log"，两者各自写自己的日志文件，互不干扰（不同进程，无竞争）。
    /// </summary>
    public static class PlugInLog
    {
        private static readonly object _lock = new object();
        private static string _logPath;
        private static string _logFileName = "plugin.log";
        private static bool _initialized;

        /// <summary>日志文件名（默认 "plugin.log"）。worker 进程应改为 "worker.log"。</summary>
        public static string LogFileName
        {
            get { return _logFileName; }
            set
            {
                lock (_lock)
                {
                    _logFileName = value ?? "plugin.log";
                    _initialized = false; // 重置以便下次 EnsureInitialized 重新生成路径
                    _logPath = null;
                }
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var dir = Path.Combine(appData, "RTSysAI");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, _logFileName);
                }
                catch
                {
                    _logPath = Path.Combine(Path.GetTempPath(), "RTSysAI_" + _logFileName);
                }
                _initialized = true;
            }
        }

        /// <summary>记录一行日志（带时间戳、线程ID）</summary>
        public static void Log(string msg)
        {
            EnsureInitialized();
            var line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] TID={1:D4} {2}", DateTime.Now, Thread.CurrentThread.ManagedThreadId, msg);
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* ignore logging failures */ }
        }

        /// <summary>记录异常（完整堆栈）</summary>
        public static void LogException(string context, Exception ex)
        {
            EnsureInitialized();
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] TID={1:D4} EXCEPTION in {2}: {3}",
                DateTime.Now, Thread.CurrentThread.ManagedThreadId, context, ex.Message));
            sb.AppendLine(ex.StackTrace);
            var inner = ex.InnerException;
            while (inner != null)
            {
                sb.AppendLine(" ---> Inner: " + inner.Message);
                sb.AppendLine(inner.StackTrace);
                inner = inner.InnerException;
            }
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>获取日志文件路径</summary>
        public static string LogPath
        {
            get { EnsureInitialized(); return _logPath; }
        }

        /// <summary>清空日志</summary>
        public static void Clear()
        {
            EnsureInitialized();
            try
            {
                lock (_lock)
                {
                    if (File.Exists(_logPath)) File.Delete(_logPath);
                }
            }
            catch { }
        }
    }
}