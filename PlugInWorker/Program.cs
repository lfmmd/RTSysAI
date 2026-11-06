using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RTSys.PlugIn.Worker
{
    /// <summary>
    /// PlugInWorker 独立进程入口（x86, net48, 控制台/无窗口）。
    ///
    /// 职责：
    ///   1. 解析端口参数（默认 9090，可通过命令行参数或环境变量 RTSYS_AI_WORKER_PORT 覆盖）。
    ///   2. 启动 ApiServer（业务逻辑全部在此进程内运行，不再托管在 RTSys.exe）。
    ///   3. 心跳：每 2 秒向 %APPDATA%\RTSysAI\worker.heartbeat 写时间戳，供 PlugInWorkerLauncher
    ///      判断 worker 是否存活；worker 崩溃后心跳文件不再更新，宿主下次拉起新 worker。
    ///   4. 注册退出清理（停止 ApiServer、停止心跳）。
    /// </summary>
    internal static class Program
    {
        private static readonly Mutex _singleInstanceMutex;
        private static bool _mutexOwned = false;
        private static CancellationTokenSource _cts;
        private static Task _heartbeatTask;
        private static DateTime _startedAt;

        private const int DefaultPort = 9090;
        private const string MutexName = "Local\\RTSysAI_PlugInWorker_SingleInstance";

        static Program()
        {
            // 单实例互斥：同一时间只允许一个 worker 进程运行
            _singleInstanceMutex = new Mutex(false, MutexName, out _mutexOwned);
        }

        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { PlugInLog.LogException("Worker AppDomain unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "")); } catch { }
            };

            // 设置 worker 专用日志文件，与宿主的 plugin.log 分离
            PlugInLog.LogFileName = "worker.log";

            var port = ResolvePort(args);
            PlugInLog.Log($"Worker 启动: port={port} pid={Process.GetCurrentProcess().Id}");

            // 写就绪标记文件（启动完成后由 launcher 通过 HTTP 探测确认就绪）
            var readyFile = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI"), "worker.ready");

            _cts = new CancellationTokenSource();

            try
            {
                // 启动自动初始化 Skill 数据：首次运行时把打包的 skills\ 复制到 %APPDATA%\RTSysAI\skills。
                // 不做这一步的话，AI 启动后没有任何 RTBasic 参考库可用（lookup_command 全部落空）。
                // 失败只记日志，不阻断 worker 启动（设置面板里仍可手动点"初始化 Skill 数据"）。
                TryInitializeSkills();

                // 启动 ApiServer（复用现有 ApiServer.cs，全部在 worker 进程内）
                var server = new ApiServer();
                server.Start();
                PlugInLog.Log($"ApiServer 启动 OK on port {server.Port}");

                // 启动心跳（每 2 秒写 worker.heartbeat）
                StartHeartbeat();

                // 写就绪标记（供宿主/launcher 轮询确认 worker 已就绪）
                try { File.WriteAllText(readyFile, DateTime.Now.ToString("o")); } catch { }

                _startedAt = DateTime.Now;
                PlugInLog.Log("Worker 已就绪，等待退出信号...");

                // 阻塞主线程直到收到 Ctrl+C 或进程退出
                var exitEvent = new ManualResetEventSlim();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true; // 不终止进程，由我们优雅退出
                    exitEvent.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (s, e) => exitEvent.Set();

                exitEvent.Wait();

                PlugInLog.Log("Worker 收到退出信号，开始清理...");
            }
            catch (Exception ex)
            {
                try { PlugInLog.LogException("Worker 启动失败", ex); } catch { }
                return 1;
            }
            finally
            {
                StopHeartbeat();
                try { File.Delete(readyFile); } catch { }
                PlugInLog.Log("Worker 已退出");
            }

            return 0;
        }

        /// <summary>
        /// 启动时自动初始化 Skill 数据：把 worker 打包目录下的 skills\ 复制到 %APPDATA%\RTSysAI\skills。
        /// 若打包目录不存在或已初始化过，则静默跳过；只记日志，不抛异常（不阻断 worker 启动）。
        /// </summary>
        private static void TryInitializeSkills()
        {
            try
            {
                var svc = new AiService(); // 调用其 InitializeSkills
                var err = svc.InitializeSkills();
                if (string.IsNullOrEmpty(err))
                {
                    PlugInLog.Log("Skill 数据自动初始化完成");
                }
                else if (!err.Contains("未找到 skills 文件夹"))
                {
                    PlugInLog.Log($"Skill 自动初始化失败: {err}");
                }
                else
                {
                    PlugInLog.Log("Skill 自动初始化跳过：打包目录下无 skills 文件夹（开发环境/未部署时正常）");
                }
            }
            catch (Exception ex)
            {
                try { PlugInLog.LogException("Skill 自动初始化异常", ex); } catch { }
            }
        }

        private static int ResolvePort(string[] args)
        {
            // 优先：命令行参数 --port=9090 或 -p 9090
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p)) return p;
                if (a.StartsWith("--port=") && int.TryParse(a.Substring(7), out var p2)) return p2;
                if (a == "-p" && i + 1 < args.Length && int.TryParse(args[++i], out var p3)) return p3;
            }
            // 次：环境变量 RTSYS_AI_WORKER_PORT
            try
            {
                var env = Environment.GetEnvironmentVariable("RTSYS_AI_WORKER_PORT");
                if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var pe)) return pe;
            }
            catch { }
            return DefaultPort;
        }

        private static void StartHeartbeat()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "RTSysAI");
            try { Directory.CreateDirectory(dir); } catch { }
            var hbPath = Path.Combine(dir, "worker.heartbeat");

            _heartbeatTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        File.WriteAllText(hbPath, DateTime.Now.ToString("o"));
                    }
                    catch { /* 忽略心跳写入失败 */ }
                    await Task.Delay(2000, _cts.Token);
                }
            }, _cts.Token);
        }

        private static void StopHeartbeat()
        {
            try { _cts?.Cancel(); } catch { }
            try { _heartbeatTask?.Wait(3000); } catch { }
        }

        /// <summary>
        /// 供 PlugInWorkerLauncher 判断 worker 是否存活：心跳文件在 10 秒内更新即视为存活。
        /// </summary>
        internal static bool IsHeartbeatFresh(int freshnessSeconds = 10)
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
    }
}
