using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
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
        private static readonly ManualResetEventSlim _exitEvent = new ManualResetEventSlim(false);

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

            // 单实例：真正获取互斥体（原实现只创建未 WaitOne，形同虚设）。
            try
            {
                if (!_singleInstanceMutex.WaitOne(0))
                {
                    PlugInLog.Log("已有 worker 实例运行，退出");
                    return 2;
                }
                _mutexOwned = true;
            }
            catch (AbandonedMutexException)
            {
                // 上一个 worker 异常退出：由当前进程接管。
                _mutexOwned = true;
            }

            // 设置 worker 专用日志文件，与宿主的 plugin.log 分离
            PlugInLog.LogFileName = "worker.log";

            // 扩展原生 DLL 搜索路径：zmotion.dll 等位于 RTSys 安装目录（不在 worker 目录），
            // 不设置则所有 ZMC_* P/Invoke 抛 DllNotFoundException（0x8007007E），
            // 导致控制器相关工具全部失败、AI 对话无输出。
            RegisterNativeDllSearchPath();

            var port = ResolvePort(args);
            var parentPid = ResolveParentPid(args);
            PlugInLog.Log($"Worker 启动: port={port} pid={Process.GetCurrentProcess().Id} parent={parentPid}");

            // 写就绪标记文件（启动完成后由 launcher 通过 HTTP 探测确认就绪）
            var readyFile = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI"), "worker.ready");

            _cts = new CancellationTokenSource();

            try
            {
                // 启动自动初始化 Skill 数据：首次运行时把打包的 skills\ 复制到 %APPDATA%\RTSysAI\skills。
                // 不做这一步的话，AI 启动后没有任何 RTBasic 参考库可用（lookup_command 全部落空）。
                // 失败只记日志，不阻断 worker 启动（设置面板里仍可手动点"初始化 Skill 数据"）。
                TryInitializeSkills();

                // 启动 ApiServer（优先使用命令行/环境变量指定的端口）
                var server = new ApiServer();
                server.Start(port);
                PlugInLog.Log($"ApiServer 启动 OK on port {server.Port}");

                // 启动心跳（每 2 秒写 worker.heartbeat）
                StartHeartbeat();

                // 父进程（RTSys）退出后自动退出，避免孤儿 worker。
                StartParentMonitor(parentPid);

                // 写就绪标记（供宿主/launcher 轮询确认 worker 已就绪）
                // 就绪文件带 pid + 实际端口，外部工具可据此确认对端身份（不再假设端口）。
                try { File.WriteAllText(readyFile, Process.GetCurrentProcess().Id + "|" + server.Port + "|" + DateTime.Now.ToString("o")); } catch { }

                _startedAt = DateTime.Now;
                PlugInLog.Log("Worker 已就绪，等待退出信号...");

                // 阻塞主线程直到收到 Ctrl+C、父进程退出或进程退出
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true; // 不终止进程，由我们优雅退出
                    _exitEvent.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (s, e) => _exitEvent.Set();

                _exitEvent.Wait();

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

        private static int ResolveParentPid(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--parent" && i + 1 < args.Length && int.TryParse(args[++i], out var p)) return p;
                if (a.StartsWith("--parent=") && int.TryParse(a.Substring(9), out var p2)) return p2;
            }
            return 0;
        }

        /// <summary>父进程（RTSys）不存在时退出，避免 worker 成为孤儿进程。</summary>
        private static void StartParentMonitor(int parentPid)
        {
            if (parentPid <= 0) return;
            Task.Run(() =>
            {
                while (!_exitEvent.IsSet)
                {
                    try
                    {
                        var parent = Process.GetProcessById(parentPid);
                        if (parent.HasExited) break;
                    }
                    catch
                    {
                        break; // 进程不存在
                    }
                    _exitEvent.Wait(2000);
                }
                if (!_exitEvent.IsSet)
                {
                    PlugInLog.Log("父进程已退出，worker 退出");
                    _exitEvent.Set();
                }
            });
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

        /// <summary>
        /// 将 RTSys 安装目录加入原生 DLL 搜索路径，使 clr 能找到 zmotion.dll 等。
        /// 由于 PlugInWorker.exe 部署在 RTSys\Plug_in\AIPlugin\ 而 zmotion.dll 在 RTSys 安装目录（父目录），
        /// 必须显式将 RTSys 安装目录加入进程 DLL 搜索路径（SetDllDirectory + PATH），
        /// 否则 P/Invoke 加载 zmotion.dll 会抛 DllNotFoundException (0x8007007E)。
        /// </summary>
        private static void RegisterNativeDllSearchPath()
        {
            try
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory;  // .../RTSys/Plug_in/AIPlugin/
                var rtsysRoot = FindRTSysRoot(appDir);
                if (string.IsNullOrEmpty(rtsysRoot) || !Directory.Exists(rtsysRoot))
                {
                    PlugInLog.Log("RegisterNativeDllSearchPath: 未找到 RTSys 安装目录");
                    return;
                }

                // 1) SetDllDirectory 优先：直接加入 DLL 搜索路径
                SetDllDirectory(rtsysRoot);
                PlugInLog.Log($"SetDllDirectory: {rtsysRoot}");

                // 2) 也追加到进程 PATH，兼容子进程或 LoadLibrary
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (path.IndexOf(rtsysRoot, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Environment.SetEnvironmentVariable("PATH", path + ";" + rtsysRoot, EnvironmentVariableTarget.Process);
                    PlugInLog.Log("PATH appended: " + rtsysRoot);
                }

                // 3) 验证
                var zmotionPath = Path.Combine(rtsysRoot, "zmotion.dll");
                if (File.Exists(zmotionPath))
                    PlugInLog.Log("zmotion.dll found at: " + zmotionPath);
                else
                    PlugInLog.Log("WARNING: zmotion.dll NOT found at: " + zmotionPath);
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("RegisterNativeDllSearchPath", ex);
            }
        }

        /// <summary>
        /// 从 appDir 向上逐级查找 RTSys 安装目录（含 zmotion.dll 或 RTSys.exe）。
        /// 不能数固定层数：Path.GetDirectoryName 第一次调用只去掉尾部反斜杠，
        /// 两次调用实际只上溯一层（得到 ...\RTSys\Plug_in），导致 zmotion.dll 找不到。
        /// </summary>
        private static string FindRTSysRoot(string startDir)
        {
            var dir = startDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "zmotion.dll")) ||
                    File.Exists(Path.Combine(dir, "RTSys.exe")))
                    return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            return null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
