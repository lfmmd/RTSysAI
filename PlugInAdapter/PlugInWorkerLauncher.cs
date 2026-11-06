using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RTSys.PlugIn
{
    /// <summary>
    /// 独立 worker 进程的启动与生命周期管理（运行在 RTSys.exe 宿主进程内）。
    ///
    /// 职责：
    ///   1. 检查 worker 进程是否已存活（心跳文件新鲜度 + 命名 Mutex 单实例）。
    ///   2. 若未存活，CreateProcess 启动 RTSysPlugInWorker.exe（同目录），传端口参数。
    ///   3. 等待 worker 就绪（轮询 HTTP /api/ 或就绪标记文件）。
    ///   4. 暴露 WorkerBaseUrl，供 ChatPanel 通过 HTTP 调用 worker。
    ///
    /// 设计要点：
    ///   - worker 崩溃时心跳文件不再更新，下次 EnsureWorkerRunning 会重新拉起。
    ///   - PlugIn_Destroy 不杀 worker（保留复用，宿主重启时继续使用）。
    ///   - 不抛异常给 native 边界，所有失败仅记日志并返回 false。
    /// </summary>
    public static class PlugInWorkerLauncher
    {
        private static readonly object _lock = new object();
        private static Process _workerProcess;
        private static string _workerBaseUrl;
        private static bool _ensured;

        /// <summary>Worker HTTP 基地址，例如 http://127.0.0.1:9090</summary>
        public static string WorkerBaseUrl
        {
            get { lock (_lock) { return _workerBaseUrl; } }
        }

        /// <summary>默认 worker 端口（与 Program.DefaultPort 一致）。</summary>
        public const int DefaultPort = 9090;

        /// <summary>
        /// 确保 worker 进程在运行并就绪。可多次调用，已运行则直接返回。
        /// </summary>
        public static bool EnsureWorkerRunning(int port = DefaultPort, int waitReadySeconds = 15)
        {
            lock (_lock)
            {
                if (_ensured && _workerProcess != null && !_workerProcess.HasExited)
                {
                    // 再次确认心跳新鲜，防止 worker 已死但进程对象未更新
                    if (IsHeartbeatFresh(10))
                    {
                        _workerBaseUrl = "http://127.0.0.1:" + port;
                        return true;
                    }
                    // worker 已死，清理并重新拉起
                    DisposeWorkerProcess();
                    _ensured = false;
                }

                try
                {
                    PlugInLog.Log($"[WorkerLauncher] 确保 worker 运行，port={port}");
                    string exePath = ResolveWorkerExePath();
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    {
                        PlugInLog.Log("[WorkerLauncher] 未找到 RTSysPlugInWorker.exe，放弃启动 worker");
                        return false;
                    }

                    // 检查是否已有存活的 worker（心跳新鲜）
                    if (IsHeartbeatFresh(5))
                    {
                        _workerBaseUrl = "http://127.0.0.1:" + port;
                        _ensured = true;
                        PlugInLog.Log("[WorkerLauncher] 检测到已有存活 worker，复用");
                        return true;
                    }

                    // 启动 worker 进程
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--port=" + port,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        WorkingDirectory = Path.GetDirectoryName(exePath)
                    };
                    _workerProcess = Process.Start(psi);
                    PlugInLog.Log($"[WorkerLauncher] 已启动 worker pid={_workerProcess.Id}");

                    // 等待就绪：轮询心跳文件（worker 每 2 秒写一次）
                    var deadline = DateTime.Now.AddSeconds(waitReadySeconds);
                    while (DateTime.Now < deadline)
                    {
                        if (_workerProcess.HasExited)
                        {
                            PlugInLog.Log("[WorkerLauncher] worker 进程在等待就绪期间已退出");
                            DisposeWorkerProcess();
                            return false;
                        }
                        if (IsHeartbeatFresh(10))
                        {
                            _workerBaseUrl = "http://127.0.0.1:" + port;
                            _ensured = true;
                            PlugInLog.Log("[WorkerLauncher] worker 已就绪");
                            return true;
                        }
                        Thread.Sleep(300);
                    }

                    PlugInLog.Log("[WorkerLauncher] worker 等待就绪超时");
                    return false;
                }
                catch (Exception ex)
                {
                    PlugInLog.LogException("[WorkerLauncher] EnsureWorkerRunning 异常", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 通知 worker 即将退出（可选）。当前实现不杀 worker，保留复用。
        /// 若希望彻底退出 worker，可在此调用 _workerProcess?.Kill()。
        /// </summary>
        public static void ShutdownWorker()
        {
            lock (_lock)
            {
                // 不杀 worker，保留以便下次复用（避免反复拉起开销）
                _ensured = false;
                _workerBaseUrl = null;
                _workerProcess = null;
                PlugInLog.Log("[WorkerLauncher] ShutdownWorker：保留 worker 进程，仅清理宿主端引用");
            }
        }

        private static void DisposeWorkerProcess()
        {
            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    // 不强制杀死，只释引用
                    _workerProcess.Dispose();
                }
            }
            catch { }
            _workerProcess = null;
        }

        private static bool IsHeartbeatFresh(int freshnessSeconds)
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var hbPath = Path.Combine(appData, "RTSysAI", "worker.heartbeat");
                if (!File.Exists(hbPath)) return false;
                var ts = File.GetLastWriteTimeUtc(hbPath);
                return (DateTime.UtcNow - ts).TotalSeconds < freshnessSeconds;
            }
            catch { return false; }
        }

        private static string ResolveWorkerExePath()
        {
            try
            {
                // worker exe 与适配器 DLL 同目录
                var dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (dir == null) return null;
                var exe = Path.Combine(dir, "RTSysPlugInWorker.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
            return null;
        }
    }
}
