using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    /// <summary>
    /// RTSys 风格的控制器处理器：通过 ZMotionHandle 访问控制器，支持自动搜索、静态 IP、仿真器连接。
    /// 已移除 RTSysIP 硬性要求：优先尝试 ZMC_SearchEth/ZMC_SearchAndOpenEth 自动搜索连接，
    /// 再回退到配置文件静态 IP，最后尝试仿真器连接。
    /// </summary>
    internal static class RTSysHandlers
    {
        /// <summary>
        /// 控制器连接状态
        /// </summary>
        public enum ControllerConnectionState
        {
            Unknown,           // 尚未检查
            NotConfigured,     // 未配置 IP 且自动搜索失败
            Disconnected,      // 已有目标 IP/仿真但连接失败/已断开
            Connected          // 已连接
        }

        private static ZMotionHandle _handle;
        private static readonly object _handleLock = new object();

        // 连接尝试串行化 + 超时 + 失败冷却。
        // ZMC_SearchAndOpenEth / ZMC_SearchEth 是同步阻塞原生调用，控制器不可达时可能长时间
        // 不返回；不加托管侧超时会把对话/工具调用永久卡住（表现为发送键一直停在“停止”）。
        private static readonly System.Threading.SemaphoreSlim _connectGate = new System.Threading.SemaphoreSlim(1, 1);
        private static DateTime _lastConnectFailUtc = DateTime.MinValue;
        private const int AttemptTimeoutMs = 2500;
        private const int ConnectFailCooldownSeconds = 15;
        private const int ConnectGateTimeoutMs = 10000;
        private static string _cachedIp;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        // 连接模式
        private enum ConnectMode
        {
            AutoSearch,      // 自动搜索以太网/串口
            StaticIp,        // 使用配置的静态 IP
            Simulator        // 连接仿真器
        }

        // 当前连接模式（运行时确定）
        private static ConnectMode _currentMode = ConnectMode.AutoSearch;

        // 连接状态缓存：避免每个工具调用都重读 config.json。
        // SetRTSysIp 改 IP 时立即失效；否则按 TTL 过期。
        private static ControllerConnectionState _connStateCache = ControllerConnectionState.Unknown;
        private static DateTime _connStateCacheTime = DateTime.MinValue;
        private const int ConnStateCacheTtlSeconds = 5;

        /// <summary>
        /// 控制器未配置 IP 且自动搜索失败时的用户友好错误消息（中文，含可操作提示）
        /// </summary>
        internal const string ControllerNotConfiguredMessage =
            "无法连接控制器：自动搜索未发现设备，且未配置静态 IP。" +
            " 请在「设置」中填写 RTSys 控制器 IP，或检查网络/仿真器是否运行。";

        /// <summary>
        /// 控制器不可连时的用户友好错误消息
        /// </summary>
        internal const string ControllerUnreachableMessage =
            "控制器不可达：已尝试自动搜索/配置 IP/仿真器均无法建立连接。" +
            " 请检查网络连接、控制器电源、防火墙或仿真器是否运行。";

        // ========== 连接管理 ==========

        /// <summary>
        /// 检查控制器连接状态（不建立新连接，不抛异常）。
        /// 优先返回已有句柄状态；其次尝试自动搜索；最后读取静态 IP 配置。
        /// </summary>
        internal static ControllerConnectionState GetConnectionState()
        {
            lock (_handleLock)
            {
                // 1. 已有有效句柄
                if (_handle != null && !_handle.IsInvalid)
                {
                    _connStateCache = ControllerConnectionState.Connected;
                    _connStateCacheTime = DateTime.Now;
                    return ControllerConnectionState.Connected;
                }

                // 2. 缓存未过期
                if (_connStateCache != ControllerConnectionState.Unknown &&
                    (DateTime.Now - _connStateCacheTime).TotalSeconds < ConnStateCacheTtlSeconds)
                    return _connStateCache;

                // 3. 只依据已有句柄 / 静态 IP 配置判断，不在此处做网络搜索。
                //    ZMC_SearchEth 是同步阻塞调用且无超时；若在构建提示词（每轮对话）时执行，
                //    控制器不可达会让整个对话卡死、AI 请求根本发不出去。
                //    自动搜索保留在显式连接流程（GetHandleAsync）中。
                string ip = GetRTSysIpFromConfig();
                _connStateCache = string.IsNullOrEmpty(ip)
                    ? ControllerConnectionState.NotConfigured
                    : ControllerConnectionState.Disconnected;
                _connStateCacheTime = DateTime.Now;
                return _connStateCache;
            }
        }

        /// <summary>
        /// 是否已有可用的控制器句柄。仅读内存状态，不触发任何网络搜索/连接，
        /// 适合在构建提示词等不允许阻塞的路径中使用。
        /// </summary>
        internal static bool HasLiveConnection
        {
            get
            {
                lock (_handleLock)
                {
                    return _handle != null && !_handle.IsInvalid;
                }
            }
        }

        /// <summary>
        /// 结构化连接状态（供 controller_status 工具使用）。不触发网络搜索/连接。
        /// </summary>
        internal static object GetConnectionStatus()
        {
            var state = GetConnectionState();
            return new
            {
                connectionState = state.ToString(),
                currentMode = _currentMode.ToString(),
                cachedIp = _cachedIp,
                connected = HasLiveConnection
            };
        }

        /// <summary>
        /// 尝试通过 ZMC_SearchEth 自动发现控制器 IP（不建立连接）。
        /// 返回第一个发现的 IP，失败返回 null。
        /// </summary>
        private static int _searchEthHanging; // 1 = 上次原生搜索仍未返回，避免继续堆积阻塞线程

        private static string TryDiscoverControllerIp()
        {
            if (System.Threading.Volatile.Read(ref _searchEthHanging) != 0)
                return null;

            string found = null;
            var task = Task.Run(() =>
            {
                try
                {
                    StringBuilder buffer = new StringBuilder(1024);
                    int result = ZMotionAPI.ZMC_SearchEth(buffer, buffer.Capacity);
                    if (result == ZMotionErrors.SUCCESS && buffer.Length > 0)
                    {
                        // ZMC_SearchEth 返回格式通常为 "IP1,IP2,..." 或单个 IP
                        string[] ips = buffer.ToString().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        if (ips.Length > 0)
                        {
                            found = ips[0].Trim();
                            PlugInLog.Log("自动发现控制器 IP: " + found);
                        }
                    }
                }
                catch (Exception ex)
                {
                    PlugInLog.LogException("TryDiscoverControllerIp", ex);
                }
            });

            // ZMC_SearchEth 是同步原生调用且无超时；最多等 3 秒，避免聊天/HTTP 线程无限阻塞。
            if (!task.Wait(3000))
            {
                System.Threading.Volatile.Write(ref _searchEthHanging, 1);
                PlugInLog.Log("ZMC_SearchEth 超过 3s 未返回，放弃本次自动搜索（后台原生线程仍阻塞）");
                return null;
            }
            return found;
        }

        /// <summary>
        /// 自动连接控制器：优先 SearchAndOpenEth -> SearchEth+OpenEth -> 静态 IP -> 仿真器
        /// </summary>
        private static async Task<ZMotionHandle> GetHandleAsync()
        {
            // 快速路径：已有有效句柄
            if (HasLiveConnection)
                return _handle;

            // 失败冷却：避免每条工具调用都重复触发无法返回的原生搜索（会不断占用线程）。
            if ((DateTime.UtcNow - _lastConnectFailUtc).TotalSeconds < ConnectFailCooldownSeconds)
                throw new ControllerUnavailableException(
                    GetConnectionState(),
                    ControllerUnreachableMessage + " (最近一次连接尝试刚失败，请稍后重试)");

            // 串行化：并发调用会同时阻塞在原生搜索里。
            if (!await _connectGate.WaitAsync(ConnectGateTimeoutMs))
                throw new ControllerUnavailableException(
                    GetConnectionState(),
                    ControllerUnreachableMessage + " (等待连接尝试超时)");

            try
            {
                if (HasLiveConnection)
                    return _handle;

                // 按优先级尝试各种连接方式。
                // RTSys 进程内运行时，宿主可能占用搜索通道（日志常见“设备忙”）；
                // 已配置静态 IP 时优先直连，搜索作为兜底。
                var attempts = new List<Func<Task<ZMotionHandle>>>();
                if (!string.IsNullOrEmpty(GetRTSysIpFromConfig()))
                {
                    attempts.Add(TryConnectStaticIp);
                    attempts.Add(TryConnectSearchAndOpenEth);
                    attempts.Add(TryConnectSearchEthThenOpen);
                }
                else
                {
                    attempts.Add(TryConnectSearchAndOpenEth);
                    attempts.Add(TryConnectSearchEthThenOpen);
                }
                attempts.Add(TryConnectSimulator);

                Exception lastError = null;
                foreach (var attempt in attempts)
                {
                    try
                    {
                        // 原生搜索无内部超时，必须在托管侧兜底。
                        // 必须用 Task.Run 包裹：部分 async 尝试在第一个 await 之前就会同步调用
                        // ZMC_SearchEth，直接调用 attempt() 会阻塞调用线程，使超时逻辑失效。
                        var handle = await AwaitWithTimeout(Task.Run(() => attempt()), AttemptTimeoutMs);
                        if (handle != null && !handle.IsInvalid)
                        {
                            lock (_handleLock)
                            {
                                _handle = handle;
                                _connStateCache = ControllerConnectionState.Connected;
                                _connStateCacheTime = DateTime.Now;
                            }
                            return handle;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        PlugInLog.Log("连接尝试失败: " + ex.Message);
                    }
                }

                // 所有尝试均失败
                _lastConnectFailUtc = DateTime.UtcNow;
                lock (_handleLock)
                {
                    _connStateCache = ControllerConnectionState.NotConfigured;
                    _connStateCacheTime = DateTime.Now;
                }
                throw new ControllerUnavailableException(ControllerConnectionState.NotConfigured,
                    ControllerNotConfiguredMessage + (lastError != null ? " 详情: " + lastError.Message : ""));
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>
        /// 为可能永久阻塞的原生调用提供托管侧超时。超时后调用方不再等待
        /// （底层线程可能仍在内核调用中，无法取消），避免对话/工具调用被无限期卡住。
        /// </summary>
        private static async Task<T> AwaitWithTimeout<T>(Task<T> task, int timeoutMs)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (completed != task)
            {
                PlugInLog.Log("连接尝试超时 (" + timeoutMs + " ms)，放弃等待");
                return default(T);
            }
            return await task;
        }

        /// <summary>
        /// 尝试 1: ZMC_SearchAndOpenEth - 搜索并直接打开连接
        /// </summary>
        private static async Task<ZMotionHandle> TryConnectSearchAndOpenEth()
        {
            PlugInLog.Log("尝试 ZMC_SearchAndOpenEth 自动搜索并连接...");
            IntPtr h = IntPtr.Zero;
            int result = await Task.Run(() => ZMotionAPI.ZMC_SearchAndOpenEth("", out h));
            if (result == ZMotionErrors.SUCCESS && h != IntPtr.Zero)
            {
                _currentMode = ConnectMode.AutoSearch;
                var handle = new ZMotionHandle(h, ownsHandle: true);
                await handle.SetDefaultTimeoutAsync();
                PlugInLog.Log("ZMC_SearchAndOpenEth 连接成功");
                return handle;
            }
            PlugInLog.Log("ZMC_SearchAndOpenEth 失败: " + ZMotionErrors.GetDescription(result));
            return null;
        }

        /// <summary>
        /// 尝试 2: ZMC_SearchEth 发现 IP，再尝试 ZMC_OpenEth
        /// </summary>
        private static async Task<ZMotionHandle> TryConnectSearchEthThenOpen()
        {
            string discoveredIp = await Task.Run(() => TryDiscoverControllerIp());
            if (string.IsNullOrEmpty(discoveredIp))
            {
                PlugInLog.Log("ZMC_SearchEth 未发现控制器");
                return null;
            }

            PlugInLog.Log("尝试 ZMC_OpenEth 连接发现的 IP: " + discoveredIp);
            try
            {
                var handle = await ZMotionHandle.ConnectAsync(discoveredIp);
                _currentMode = ConnectMode.AutoSearch;
                _cachedIp = discoveredIp;
                PlugInLog.Log("ZMC_OpenEth 连接成功: " + discoveredIp);
                return handle;
            }
            catch (ZMotionException ex)
            {
                PlugInLog.Log("ZMC_OpenEth 连接失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 尝试 3: 使用配置文件中的静态 IP
        /// </summary>
        private static async Task<ZMotionHandle> TryConnectStaticIp()
        {
            string ip = GetRTSysIpFromConfig();
            if (string.IsNullOrEmpty(ip))
            {
                PlugInLog.Log("无静态 IP 配置，跳过");
                return null;
            }

            PlugInLog.Log("尝试静态 IP 连接: " + ip);
            try
            {
                var handle = await ZMotionHandle.ConnectAsync(ip);
                _currentMode = ConnectMode.StaticIp;
                _cachedIp = ip;
                PlugInLog.Log("静态 IP 连接成功: " + ip);
                return handle;
            }
            catch (ZMotionException ex)
            {
                PlugInLog.Log("静态 IP 连接失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 尝试 4: 连接仿真器 (zmotionsimu.dll / ZvEpm.dll)
        /// 通过 ZMC_Open 尝试本地仿真连接，或加载仿真 DLL
        /// </summary>
        private static async Task<ZMotionHandle> TryConnectSimulator()
        {
            PlugInLog.Log("尝试连接仿真器...");

            // 尝试 4a: 通过 ZMC_Open 连接本地仿真 (type=0 或特殊标识)
            try
            {
                IntPtr h = IntPtr.Zero;
                int result = await Task.Run(() => ZMotionAPI.ZMC_Open(0, "simulator", out h));
                if (result == ZMotionErrors.SUCCESS && h != IntPtr.Zero)
                {
                    _currentMode = ConnectMode.Simulator;
                    var handle = new ZMotionHandle(h, ownsHandle: true);
                    await handle.SetDefaultTimeoutAsync();
                    PlugInLog.Log("仿真器连接成功 (ZMC_Open type=0)");
                    return handle;
                }
            }
            catch (Exception ex)
            {
                PlugInLog.Log("仿真器 ZMC_Open 失败: " + ex.Message);
            }

            // 尝试 4b: 尝试加载 zmotionsimu.dll 并检查其导出函数
            try
            {
                string simuDll = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                    "zmotionsimu.dll");
                if (File.Exists(simuDll))
                {
                    PlugInLog.Log("发现仿真 DLL: " + simuDll);
                    // 这里可以通过 P/Invoke 调用 zmotionsimu.dll 的导出函数
                    // 暂时标记为仿真模式，后续可扩展
                }
            }
            catch (Exception ex)
            {
                PlugInLog.Log("加载仿真 DLL 失败: " + ex.Message);
            }

            return null;
        }

        /// <summary>
        /// 连接状态缓存失效（改 IP / 主动断开 / 模式切换后调用）
        /// </summary>
        internal static void InvalidateConnectionStateCache()
        {
            lock (_handleLock)
            {
                _connStateCache = ControllerConnectionState.Unknown;
                _connStateCacheTime = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 中心化：控制器不可用时的结构化错误（返回 JSON，不抛异常）。
        /// AI 可据此向用户说明需要配置 IP 或检查网络/仿真器。
        /// </summary>
        internal static object ControllerUnavailableError()
        {
            return ControllerUnavailableError(GetConnectionState());
        }

        /// <summary>
        /// 中心化：控制器不可用时的结构化错误（指定连接状态）。
        /// 字段：success/error/hint/connectionState/requiresController/currentMode/suggestion —— AI 直接转述 hint 即可。
        /// </summary>
        internal static object ControllerUnavailableError(ControllerConnectionState state)
        {
            string msg;
            switch (state)
            {
                case ControllerConnectionState.NotConfigured:
                    msg = ControllerNotConfiguredMessage;
                    break;
                case ControllerConnectionState.Disconnected:
                    msg = ControllerUnreachableMessage;
                    break;
                default:
                    msg = "控制器连接状态未知，无法执行该操作。";
                    break;
            }
            return new
            {
                success = false,
                error = msg,
                hint = msg,
                connectionState = state.ToString(),
                requiresController = true,
                currentMode = _currentMode.ToString(),
                suggestion = "可尝试：1) 检查网络和控制器电源 2) 在设置中配置静态 IP 3) 启动仿真器 (ZvEpm/zmotionsimu)"
            };
        }

        /// <summary>
        /// 获取当前连接模式（供外部 API 调用）
        /// </summary>
        public static string GetCurrentConnectMode()
        {
            return _currentMode.ToString();
        }

        /// <summary>
        /// 获取当前缓存的 IP（供外部 API 调用）
        /// </summary>
        public static string GetCachedIp()
        {
            return _cachedIp;
        }

        private static string GetRTSysIpFromConfig()
        {
            try
            {
                if (File.Exists(AiService.ConfigPath))
                {
                    var text = File.ReadAllText(AiService.ConfigPath);
                    var cfg = _json.Deserialize<Dictionary<string, object>>(text);
                    object val;
                    if (cfg.TryGetValue("RTSysIP", out val) && val != null)
                        return val.ToString().Trim();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 设置静态 IP（用户手动配置时调用）。设置后会切换到 StaticIp 模式并清除缓存。
        /// RTSysIP 现在是可选配置：不填时系统会自动搜索或尝试仿真器。
        /// </summary>
        public static void SetRTSysIp(string ip)
        {
            try
            {
                var cfg = new Dictionary<string, object>();
                if (File.Exists(AiService.ConfigPath))
                {
                    var text = File.ReadAllText(AiService.ConfigPath);
                    cfg = _json.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
                }
                cfg["RTSysIP"] = ip ?? "";
                File.WriteAllText(AiService.ConfigPath, _json.Serialize(cfg));
                _cachedIp = ip;
                _currentMode = ConnectMode.StaticIp;

                lock (_handleLock)
                {
                    if (_handle != null && !_handle.IsInvalid)
                    {
                        _handle.DisposeSafe();
                        _handle = null;
                    }
                }
                InvalidateConnectionStateCache();
            }
            catch { }
        }

        /// <summary>
        /// 手动触发自动搜索连接（用户点击「自动连接」按钮时调用）
        /// </summary>
        public static async Task<object> TriggerAutoConnectAsync()
        {
            lock (_handleLock)
            {
                _currentMode = ConnectMode.AutoSearch;
                if (_handle != null && !_handle.IsInvalid)
                {
                    _handle.DisposeSafe();
                    _handle = null;
                }
            }
            InvalidateConnectionStateCache();

            try
            {
                _lastConnectFailUtc = DateTime.MinValue; // 手动连接：跳过失败冷却
                var handle = await GetHandleAsync();
                return new { success = true, mode = _currentMode.ToString(), ip = _cachedIp };
            }
            catch (ControllerUnavailableException ex)
            {
                return ControllerUnavailableError(ex.State);
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message };
            }
        }

        /// <summary>
        /// 手动触发仿真器连接
        /// </summary>
        public static async Task<object> TriggerSimulatorConnectAsync()
        {
            lock (_handleLock)
            {
                _currentMode = ConnectMode.Simulator;
                if (_handle != null && !_handle.IsInvalid)
                {
                    _handle.DisposeSafe();
                    _handle = null;
                }
            }
            InvalidateConnectionStateCache();

            try
            {
                _lastConnectFailUtc = DateTime.MinValue; // 手动连接：跳过失败冷却
                var handle = await GetHandleAsync();
                return new { success = true, mode = _currentMode.ToString() };
            }
            catch (ControllerUnavailableException ex)
            {
                return ControllerUnavailableError(ex.State);
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message };
            }
        }

        // ========== 统一错误包装 ==========

        private static object Error(string msg) => new { success = false, error = msg };

        /// <summary>
        /// 控制器不可用异常（未配置 IP / 控制器不可达 / 仿真器不可用）。
        /// 由 Safe/SafeAsync 统一捕获，转成结构化 tool_result，不外泄堆栈。
        /// </summary>
        internal class ControllerUnavailableException : Exception
        {
            public ControllerConnectionState State { get; }

            public ControllerUnavailableException(ControllerConnectionState state, string message) : base(message)
            {
                State = state;
            }
        }

        private static async Task<object> SafeAsync(Func<Task<object>> func)
        {
            try
            {
                return await Task.Run(func);
            }
            catch (ControllerUnavailableException ex)
            {
                return ControllerUnavailableError(ex.State);
            }
            catch (ZMotionException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private static object Safe(Func<object> func)
        {
            try
            {
                return Task.Run(func).GetAwaiter().GetResult();
            }
            catch (ControllerUnavailableException ex)
            {
                return ControllerUnavailableError(ex.State);
            }
            catch (ZMotionException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private static object Safe(Func<Task<object>> func)
        {
            try
            {
                return Task.Run(func).GetAwaiter().GetResult();
            }
            catch (ControllerUnavailableException ex)
            {
                return ControllerUnavailableError(ex.State);
            }
            catch (ZMotionException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        // ========== 核心接口：保持与原 Handlers 相同签名 ==========

        // ---- Status ----
        public static object GetStatus()
        {
            return Safe(async () =>
            {
                // 未配置 IP：返回结构化状态（不是错误），AI 可据此询问用户 IP
                var state = GetConnectionState();
                var pendingUploads = ProgramWorkspace.GetPendingUploads();
                if (state == ControllerConnectionState.NotConfigured)
                {
                    return new
                    {
                        success = true,
                        connected = false,
                        connectionState = state.ToString(),
                        controller = "RTSys",
                        firmware = (string)null,
                        project = "RTSys Controller",
                        ip = (string)null,
                        axisCount = 0,
                        state = 0,
                        pendingUploads,
                        hint = ControllerNotConfiguredMessage
                    };
                }

                // 已配置 IP 但无活动连接：不触发阻塞式连接，返回离线状态（与 AiPrompt 一致）。
                if (!HasLiveConnection)
                {
                    return new
                    {
                        success = true,
                        connected = false,
                        connectionState = state.ToString(),
                        controller = "RTSys",
                        firmware = (string)null,
                        project = "RTSys Controller",
                        ip = _cachedIp,
                        axisCount = 0,
                        state = 0,
                        pendingUploads
                    };
                }

                // 有活动连接：尝试获取完整状态。
                var handle = await GetHandleAsync();
                var status = await handle.GetFullStatusAsync();
                return new
                {
                    success = true,
                    connected = true,
                    connectionState = ControllerConnectionState.Connected.ToString(),
                    controller = "RTSys",
                    firmware = status.FirmwareVersion?.ToString() ?? "unknown",
                    project = "RTSys Controller",
                    ip = _cachedIp,
                    axisCount = status.AxisCount,
                    state = status.State,
                    pendingUploads
                };
            });
        }

        // ---- Programs ----
        /// <summary>
        /// 仅列“当前项目目录 + 本地缓存”的程序，不触发任何控制器搜索/连接。
        /// 供构建提示词等禁止阻塞的路径使用。
        /// </summary>
        internal static object ListOfflinePrograms()
        {
            var localPrograms = ProgramWorkspace.ListLocalPrograms();
            var projectPrograms = ProgramWorkspace.ListProjectPrograms();
            var items = MergePrograms(localPrograms, projectPrograms, null, includeLocalOnly: false)
                .Select(m => new
                {
                    name = m.Name,
                    type = "basic",
                    dialect = "rtbasic",
                    isEditable = true,
                    isExecutable = true,
                    storage = m.Storage,
                    origin = m.Origin,
                    isDirty = m.IsDirty,
                    lastSyncedUtc = m.LastSyncedUtc
                }).ToList();
            return new { success = true, items, projectDir = ProgramWorkspace.ProjectDir };
        }

        public static object ListPrograms()
        {
            var localPrograms = ProgramWorkspace.ListLocalPrograms();
            var projectPrograms = ProgramWorkspace.ListProjectPrograms();

            // 无活动连接时，返回项目目录（离线安全，且不触发阻塞式连接）。
            // 本地缓存（origin=local、无工程/控制器对应的项）属于历史残留，
            // 不应出现在当前项目的文件列表中，仅在已连接控制器时才参与合并。
            if (!HasLiveConnection)
            {
                var items = MergePrograms(localPrograms, projectPrograms, null, includeLocalOnly: false)
                    .Select(m => new
                    {
                        name = m.Name,
                        type = "basic",
                        dialect = "rtbasic",
                        isEditable = true,
                        isExecutable = true,
                        storage = m.Storage,
                        origin = m.Origin,
                        isDirty = m.IsDirty,
                        lastSyncedUtc = m.LastSyncedUtc
                    }).ToList();
                return new { success = true, items };
            }

            // 已配置：合并本地 + 控制器程序。
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var controllerPrograms = await handle.ListProgramsAsync();

                var items = MergePrograms(localPrograms, projectPrograms, controllerPrograms)
                    .Select(m => new
                    {
                        name = m.Name,
                        type = "basic",
                        dialect = "rtbasic",
                        isEditable = true,
                        isExecutable = true,
                        storage = m.Storage,
                        origin = m.Origin,
                        isDirty = m.IsDirty,
                        lastSyncedUtc = m.LastSyncedUtc
                    }).ToList();

                return new { success = true, items };
            });
        }

        /// <summary>
        /// 合并本地缓存 / 工程目录 / 控制器程序，统一 origin 词表：
        /// project+controller | project | both(local+controller) | local | controller。
        /// controllerPrograms 为 null 表示离线（不触发控制器连接）。
        /// includeLocalOnly=false 时，跳过仅存在于本地缓存（无工程/控制器对应）的项，
        /// 使文件列表以当前项目/控制器为准，不被历史缓存污染。
        /// </summary>
        private static List<MergedProgram> MergePrograms(
            List<ProgramWorkspace.ProgramInfo> localPrograms,
            List<ProgramWorkspace.ProgramInfo> projectPrograms,
            ICollection<string> controllerPrograms,
            bool includeLocalOnly = true)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in localPrograms) names.Add(p.name);
            foreach (var p in projectPrograms) names.Add(p.name);
            if (controllerPrograms != null)
                foreach (var n in controllerPrograms) names.Add(n);

            var result = new List<MergedProgram>(names.Count);
            foreach (var name in names)
            {
                var local = localPrograms.FirstOrDefault(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase));
                var proj = projectPrograms.FirstOrDefault(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase));
                bool hasLocal = local != null;
                bool hasProject = proj != null;
                bool hasController = controllerPrograms != null
                    && controllerPrograms.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

                if (!includeLocalOnly && hasLocal && !hasProject && !hasController) continue;

                string origin;
                if (hasProject && hasController) origin = "project+controller";
                else if (hasProject) origin = "project";
                else if (hasLocal && hasController) origin = "both";
                else if (hasLocal) origin = "local";
                else origin = "controller";

                result.Add(new MergedProgram
                {
                    Name = name,
                    Origin = origin,
                    Storage = hasProject ? "project" : (hasController ? "controller" : "local"),
                    Group = hasProject ? "Project" : (hasController ? "Controller" : "Local"),
                    IsDirty = local?.isDirty == true,
                    LastSyncedUtc = local?.lastSyncedUtc
                });
            }
            return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private sealed class MergedProgram
        {
            public string Name;
            public string Origin;
            public string Storage;
            public string Group;
            public bool IsDirty;
            public string LastSyncedUtc;
        }

        /// <summary>允许 AI/HTTP 传入带 .bas 的程序名；统一去掉扩展名，避免误入控制器兜底路径。</summary>
        private static string NormalizeProgramName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            var n = name.Trim();
            if (n.EndsWith(".bas", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4).Trim();
            return n;
        }

        public static object ReadSource(string name, Dictionary<string, object> body = null)
        {
            name = NormalizeProgramName(name);
            return Safe(async () =>
            {
                string source;
                bool fromLocal = false;
                bool fromProject = false;
                bool fromEditor = false;

                // 读取优先级：RTSys 编辑器（用户当前看到的内容，含未保存修改）
                // → 当前项目目录 → 本地缓存 → 控制器。
                if (RTSysEditor.TryRead(name, out var editorText))
                {
                    source = editorText;
                    fromEditor = true;
                }
                else
                {
                    var projectSource = ProgramWorkspace.ReadProjectSource(name);
                    var localSource = projectSource == null ? ProgramWorkspace.ReadLocalSource(name) : null;
                    if (projectSource != null)
                    {
                        source = projectSource;
                        fromProject = true;
                    }
                    else if (localSource != null)
                    {
                        source = localSource;
                        fromLocal = true;
                    }
                    else
                    {
                        // 本地缓存未命中：若控制器已连接，先在控制器程序清单中确认存在，
                        // 再拉取并缓存；不存在时返回明确错误，避免落入上传兜底产生误导信息。
                        var handle = await GetHandleAsync();
                        var controllerNames = await handle.ListProgramsAsync();
                        bool existsOnController = controllerNames != null &&
                            controllerNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                        if (!existsOnController)
                            return Error($"程序不存在：{name}（编辑器/工程/本地缓存/控制器均未找到）");
                        source = await handle.ReadProgramAsync(name);
                        ProgramWorkspace.WriteLocalSource(name, source);
                        ProgramWorkspace.MarkSynced(name);
                    }
                }

                var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                int totalLines = lines.Length;
                int? startOpt = body != null ? GetInt(body, "startLine") : null;
                int? endOpt = body != null ? GetInt(body, "endLine") : null;

                if (startOpt.HasValue || endOpt.HasValue)
                {
                    int start = Math.Max(1, startOpt ?? 1);
                    if (start > totalLines) return Error($"startLine {start} exceeds total lines {totalLines}");
                    int end = Math.Min(totalLines, endOpt ?? totalLines);
                    if (end < start) end = start;
                    var sliceLines = lines.Skip(start - 1).Take(end - start + 1).ToList();
                    var slice = string.Join("\n", sliceLines);
                    return new
                    {
                        sourceCode = slice,
                        numberedSource = NumberLines(sliceLines, start),
                        startLine = start,
                        endLine = end,
                        totalLines,
                        remainingLines = Math.Max(0, totalLines - end),
                        truncated = false,
                        type = "basic",
                        dialect = "rtbasic",
                        fromLocal,
                        fromProject,
                        fromEditor
                    };
                }

                const int AutoPageLines = 400;
                const int AutoPageChars = 12000;
                if (totalLines > AutoPageLines || source.Length > AutoPageChars)
                {
                    int end = Math.Min(AutoPageLines, totalLines);
                    int charCount = 0;
                    for (int i = 0; i < end; i++)
                    {
                        charCount += lines[i].Length + 1;
                        if (charCount > AutoPageChars) { end = i; break; }
                    }
                    var sliceLines = lines.Take(end).ToList();
                    var slice = string.Join("\n", sliceLines);
                    return new
                    {
                        sourceCode = slice,
                        numberedSource = NumberLines(sliceLines, 1),
                        startLine = 1,
                        endLine = end,
                        totalLines,
                        remainingLines = Math.Max(0, totalLines - end),
                        truncated = true,
                        hint = $"Large file ({totalLines} lines, {source.Length} chars). Use startLine={end + 1} to read next chunk.",
                        type = "basic",
                        dialect = "rtbasic",
                        fromLocal,
                        fromProject,
                        fromEditor
                    };
                }

                return new { sourceCode = source, numberedSource = NumberLines(lines, 1), totalLines = totalLines, type = "basic", dialect = "rtbasic", fromLocal, fromProject, fromEditor };
            });
        }

        public static object WriteSource(string name, Dictionary<string, object> body, bool skipBackup = false)
        {
            name = NormalizeProgramName(name);
            if (string.IsNullOrWhiteSpace(name)) return Error("程序名不能为空");

            return Safe(() =>
            {
                var sourceCode = GetString(body, "sourceCode") ?? "";
                var validationErrors = ValidateRtBasicCode(sourceCode);
                if (validationErrors.Count > 0)
                    return Error("BLOCKED by RTBasic validation:\n  " + string.Join("\n  ", validationErrors));

                string backupFile = null;
                string backupError = null;
                string projectBackup = null;
                if (!skipBackup)
                {
                    var bk = ProgramBackup.BackupProgramSource(name, "write");
                    backupFile = bk.File;
                    backupError = bk.Error;
                    if (bk.SourceExisted && bk.Error != null)
                        return Error("备份失败，已取消写入: " + bk.Error);
                }

                // 优先：文件当前在 RTSys 编辑器中打开 → 写入编辑器并让宿主自己保存。
                // 这样编辑器立即刷新，且宿主负责 UTF-8 → GBK 落盘。
                if (RTSysEditor.TryWrite(name, sourceCode, true))
                {
                    ProgramWorkspace.WriteLocalSource(name, sourceCode);
                    return new
                    {
                        success = true,
                        savedToEditor = true,
                        savedToProject = ProgramWorkspace.IsProjectAvailable,
                        projectPath = GetProjectProgramPath(name),
                        savedLocally = true,
                        downloadedToController = false,
                        pendingUpload = true,
                        backup = backupFile,
                        backupError = backupError,
                        projectBackup = projectBackup,
                        message = $"程序 '{name}' 已写入 RTSys 编辑器，并由 RTSys 保存到磁盘；本地缓存已更新。"
                    };
                }

                // 有打开项目：写入项目文件（程序不在项目中则创建并加入 .zpj），同时更新本地缓存。
                if (ProgramWorkspace.IsProjectAvailable)
                {
                    string writeError;
                    bool created = false;
                    if (ProgramWorkspace.ProjectProgramExists(name))
                    {
                        writeError = ProgramWorkspace.WriteProjectProgram(name, sourceCode);
                    }
                    else
                    {
                        if (!skipBackup)
                        {
                            var pb = ProgramBackup.BackupProjectFile("write-create");
                            projectBackup = pb.ProjectFile;
                            if (pb.Error != null) return Error("工程文件备份失败，已取消写入: " + pb.Error);
                        }
                        writeError = ProgramWorkspace.CreateProjectProgram(name, sourceCode);
                        created = true;
                    }
                    if (writeError != null) return Error(writeError);

                    ProgramWorkspace.WriteLocalSource(name, sourceCode);
                    return new
                    {
                        success = true,
                        savedToProject = true,
                        projectPath = GetProjectProgramPath(name),
                        savedLocally = true,
                        downloadedToController = false,
                        pendingUpload = true,
                        backup = backupFile,
                        backupError = backupError,
                        projectBackup = projectBackup,
                        message = created
                            ? $"程序 '{name}' 已创建并加入项目，同时更新本地缓存。"
                            : $"程序 '{name}' 已写入项目文件，同时更新本地缓存。"
                    };
                }

                // 无项目：仅写本地缓存（离线编辑），不触碰控制器。
                ProgramWorkspace.WriteLocalSource(name, sourceCode);
                return new
                {
                    success = true,
                    savedToProject = false,
                    savedLocally = true,
                    downloadedToController = false,
                    source = "local",
                    origin = "local",
                    pendingUpload = true,
                    backup = backupFile,
                    backupError = backupError,
                    projectBackup = projectBackup,
                    message = $"程序 '{name}' 已保存到本地缓存（离线编辑）。尚未下载到控制器；连接后再执行下载以生效。"
                };
            });
        }

        public static object CompileProgram(string name)
        {
            name = NormalizeProgramName(name);
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string source = await handle.ReadProgramAsync(name);
                var validationErrors = ValidateRtBasicCode(source);
                if (validationErrors.Count > 0)
                    return new { success = false, errors = validationErrors.Select(e => new { line = 0, text = e }) };

                var (success, error) = await handle.CompileAndDownloadAsync(source, name);
                if (!success)
                    return new { success = false, error = error, errors = new[] { new { line = 0, text = error } } };
                return new { success = true };
            });
        }

        public static object RunProgram(string name, Dictionary<string, object> body = null)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                await handle.RunProgramAsync(name);
                return new { success = true };
            });
        }

        public static object StopProgram(string name, Dictionary<string, object> body = null)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                await handle.StopProgramAsync();
                return new { success = true };
            });
        }

        // ---- VR ----
        public static object ReadVR(int address, int count)
        {
            if (count <= 0) return Error("count must be >= 1");
            if (count > 1024) return Error("count too large (max 1024)");
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var values = await handle.ReadVRAsync(address, count);
                return new { address, count, values = values.Select(v => (double?)v).ToList() };
            });
        }

        public static object WriteVR(int address, Dictionary<string, object> body)
        {
            if (body == null || !body.ContainsKey("value"))
                return Error("value is required (refusing to write an implicit 0)");
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                double value = GetDouble(body, "value");
                await handle.WriteVRAsync(address, new[] { (int)value });
                return new { success = true };
            });
        }

        // ---- TABLE ----
        public static object ReadTable(int address, int count)
        {
            if (count <= 0) return Error("count must be >= 1");
            if (count > 1024) return Error("count too large (max 1024)");
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var values = await handle.ReadTableAsync(address, count);
                return new { address, count, values = values.Select(v => (double?)v).ToList() };
            });
        }

        public static object WriteTable(int address, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var vals = GetArray(body, "values");
                if (vals == null || vals.Length == 0) return Error("values array is required");
                var ints = vals.Select(v => Convert.ToInt32(v)).ToArray();
                await handle.WriteTableAsync(address, ints);
                return new { success = true };
            });
        }

        // ---- Axes ----
        public static object GetAxisStates()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var states = await handle.GetAllAxisStatesAsync();
                var result = states.Select((s, i) => new
                {
                    index = i,
                    position = s.position,
                    targetPos = s.targetPos,
                    velocity = s.velocity,
                    error = s.error,
                    status = $"0x{s.status:X}",
                    servoStatus = s.servoStatus,
                    homeStatus = s.homeStatus,
                    limitStatus = s.limitStatus,
                    alarmCode = s.alarmCode
                }).ToList();
                return new { axes = result };
            });
        }

        public static object ListAxes()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                byte count = handle.GetAxisCount();
                if (count == 0)
                    return Error("控制器未报告轴信息（ZMC_GetAxises 返回 0），无法列出轴；这不代表控制器没有轴。");

                var states = await handle.GetAllAxisStatesAsync();
                if (states == null || states.Length == 0)
                    return Error($"控制器报告 {count} 个轴，但该 DLL 版本不提供轴状态（position/velocity/状态位），无法返回实时轴状态。");

                var result = new List<object>();
                for (byte i = 0; i < count; i++)
                {
                    var s = i < states.Length ? states[i] : default(ZMotionExtensions.AxisState);
                    result.Add(new
                    {
                        index = (int)i,
                        name = $"Axis{i}",
                        typeName = "RTSys",
                        position = s.position,
                        targetPos = s.targetPos,
                        velocity = s.velocity,
                        error = s.error,
                        status = $"0x{s.status:X}",
                        servoStatus = s.servoStatus,
                        homeStatus = s.homeStatus,
                        limitStatus = s.limitStatus,
                        alarmCode = s.alarmCode
                    });
                }
                return new { axisCount = (int)count, axes = result };
            });
        }

        public static object GetAxisDetail(int index)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var state = await handle.GetAxisStateAsync((byte)index);
                return new
                {
                    index = index,
                    name = $"Axis{index}",
                    type = "RTSys",
                    position = state.position,
                    targetPos = state.targetPos,
                    velocity = state.velocity,
                    error = state.error,
                    status = $"0x{state.status:X}",
                    servoStatus = state.servoStatus,
                    homeStatus = state.homeStatus,
                    limitStatus = state.limitStatus,
                    alarmCode = state.alarmCode,
                    torque = state.torque,
                    temperature = state.temperature
                };
            });
        }

        // ---- Digital IO ----
        public static object GetIn(int index)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                bool value = await handle.GetInAsync((short)index);
                return new { index = index, value = value };
            });
        }

        public static object SetOutput(int index, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                bool value = GetBool(body, "value");
                await handle.SetOutputAsync((short)index, value);
                return new { success = true };
            });
        }

        public static object GetOut(int index)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var all = await handle.GetOutAllAsync();
                int byteIdx = index / 8;
                int bitIdx = index % 8;
                bool value = byteIdx < all.Length && (all[byteIdx] & (1 << bitIdx)) != 0;
                return new { index = index, value = value };
            });
        }

        public static object GetInAll()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var data = await handle.GetInAllAsync();
                return new { data = data };
            });
        }

        public static object GetOutAll()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var data = await handle.GetOutAllAsync();
                return new { data = data };
            });
        }

        public static object SetOutAll(Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var vals = GetArray(body, "values");
                if (vals == null) return Error("values array required");
                var bytes = vals.Select(v => Convert.ToByte(v)).ToArray();
                await handle.SetOutAllAsync(bytes);
                return new { success = true };
            });
        }

        // ---- Register ----
        public static object RegisterRead(int regType, int address, int count)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var data = await handle.RegisterReadAsync(regType, address, count);
                return new { data = data };
            });
        }

        public static object RegisterWrite(int regType, int address, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var vals = GetArray(body, "values");
                if (vals == null) return Error("values array required");
                var bytes = vals.Select(v => Convert.ToByte(v)).ToArray();
                await handle.RegisterWriteAsync(regType, address, bytes);
                return new { success = true };
            });
        }

        // ---- Direct Command ----
        public static object DirectCommand(string cmd)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string response = await handle.DirectCommandAsync(cmd);
                return response;
            });
        }

        // ---- Search Code (searches local cache; works offline) ----
        public static object SearchCode(string query, bool caseSensitive = false)
        {
            if (string.IsNullOrEmpty(query))
                return new { query = query, matchCount = 0, results = new object[0] };

            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            // 项目目录优先（同名程序以项目文件为准），其次本地缓存。
            // 本地缓存的 local-only 项（不属于当前项目的历史残留）不参与搜索结果。
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var projectPrograms = ProgramWorkspace.ListProjectPrograms();
            foreach (var prog in projectPrograms)
            {
                var s = ProgramWorkspace.ReadProjectSource(prog.name);
                if (s != null) { sources[prog.name] = s; origins[prog.name] = "project"; }
            }
            var projectNames = new HashSet<string>(projectPrograms.Select(p => p.name), StringComparer.OrdinalIgnoreCase);
            foreach (var prog in ProgramWorkspace.ListLocalPrograms())
            {
                if (sources.ContainsKey(prog.name)) continue;
                if (!projectNames.Contains(prog.name)) continue;
                var s = ProgramWorkspace.ReadLocalSource(prog.name);
                if (s != null) { sources[prog.name] = s; origins[prog.name] = prog.origin; }
            }

            var results = new List<object>();
            foreach (var kv in sources)
            {
                var lines = kv.Value.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(query, cmp) >= 0)
                    {
                        results.Add(new
                        {
                            program = kv.Key,
                            line = i + 1,
                            text = lines[i].Trim(),
                            origin = origins[kv.Key]
                        });
                    }
                }
            }

            return new
            {
                query = query,
                matchCount = results.Count,
                results
            };
        }

        // ---- Project Management ----

        /// <summary>在当前 RTSys 项目目录中定位程序 .bas 文件；找不到返回 null。</summary>
        private static string GetProjectProgramPath(string name)
        {
            var dir = ProgramWorkspace.ProjectDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            try
            {
                var sanitized = ProgramWorkspace.SanitizeName(name);
                foreach (var candidate in new[] { name, sanitized })
                {
                    var direct = Path.Combine(dir, candidate + ".bas");
                    if (File.Exists(direct)) return direct;
                }
                foreach (var f in Directory.GetFiles(dir, "*.bas", SearchOption.AllDirectories))
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(stem, sanitized, StringComparison.OrdinalIgnoreCase))
                        return f;
                }
            }
            catch { }
            return null;
        }

        /// <summary>当前打开项目的 .zpj 文件；优先取宿主标题里的文件名，找不到返回 null。</summary>
        private static string GetProjectZpjFile()
        {
            // 直接复用 ProgramWorkspace 的定位逻辑：优先宿主标题中的 .zpj 名，
            // 避免同一目录存在多个 .zpj 时取到“最近修改”的旧工程文件。
            return ProgramWorkspace.GetProjectFilePath();
        }

        /// <summary>当前打开项目的信息（目录、.zpj、程序文件清单）。</summary>
        private static object GetProjectInfo()
        {
            var files = ProgramWorkspace.ListProjectPrograms()
                .Select(p => new { name = p.name, path = GetProjectProgramPath(p.name) })
                .ToList();
            return new
            {
                success = true,
                projectDir = ProgramWorkspace.ProjectDir,
                zpjFile = GetProjectZpjFile(),
                files
            };
        }

        public static object OpenProject(Dictionary<string, object> body)
        {
            if (!ProgramWorkspace.IsProjectAvailable)
                return Error("当前没有打开的 RTSys 项目。请先在 RTSys 中打开一个项目（.zpj）。");
            return GetProjectInfo();
        }

        public static object SaveProject()
        {
            if (!ProgramWorkspace.IsProjectAvailable)
                return Error("RTSys does not support project files.");
            // 项目文件（.bas）在每次写入后已落盘，.zpj 由项目本身维护，无需额外动作。
            return GetProjectInfo();
        }

        public static object ListProjectItems()
        {
            var localPrograms = ProgramWorkspace.ListLocalPrograms();
            var projectPrograms = ProgramWorkspace.ListProjectPrograms();
            var hmiNames = ProgramWorkspace.ListProjectHmiFiles();

            // 无活动连接：返回当前项目目录的程序（离线可用，不触发连接）。
            // 本地缓存残留项不列出，仅在已连接控制器时才参与合并。
            if (!HasLiveConnection)
            {
                var items = new List<object>();
                foreach (var m in MergePrograms(localPrograms, projectPrograms, null, includeLocalOnly: false))
                {
                    items.Add(new
                    {
                        name = m.Name,
                        typeName = "Program",
                        group = m.Group,
                        itemType = "basic",
                        origin = m.Origin,
                        isDirty = m.IsDirty,
                        lastSyncedUtc = m.LastSyncedUtc
                    });
                }
                foreach (var hmi in hmiNames)
                {
                    items.Add(new
                    {
                        name = hmi,
                        typeName = "HMI",
                        group = "HMI",
                        itemType = "hmi",
                        origin = "project",
                        isDirty = false,
                        lastSyncedUtc = (string)null
                    });
                }
                return new { success = true, items, fileName = ProgramWorkspace.ProjectDir ?? "RTSys Controller (Local Cache)" };
            }

            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var programs = await handle.ListProgramsAsync();

                var items = new List<object>();
                foreach (var m in MergePrograms(localPrograms, projectPrograms, programs))
                {
                    items.Add(new
                    {
                        name = m.Name,
                        typeName = "Program",
                        group = m.Group,
                        itemType = "basic",
                        origin = m.Origin,
                        isDirty = m.IsDirty,
                        lastSyncedUtc = m.LastSyncedUtc
                    });
                }
                foreach (var hmi in hmiNames)
                {
                    items.Add(new
                    {
                        name = hmi,
                        typeName = "HMI",
                        group = "HMI",
                        itemType = "hmi",
                        origin = "project",
                        isDirty = false,
                        lastSyncedUtc = (string)null
                    });
                }
                return new { success = true, items, fileName = ProgramWorkspace.ProjectDir ?? "RTSys Controller" };
            });
        }

        // ---- Dialect ----
        public static object GetProgramDialect(string name)
        {
            return "rtbasic";
        }

        // ---- IEC (not supported) ----
        public static object ReadIecVariables(string name, string scope)
        {
            return Error("IEC variables not supported on RTSys");
        }

        public static object WriteIecVariables(string name, string scope, string text)
        {
            return Error("IEC variables not supported on RTSys");
        }

        public static object GetIecTaskDetail(string name)
        {
            return Error("IEC tasks not supported on RTSys");
        }

        // ---- Motion/Drive/EtherCAT (delegate to ZMotionExtensions) ----
        public static object ReadDriveParam(int axis, int address)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                int value = await handle.EcatSdoReadAsync(0, (short)axis, address, 0, 7); // UINT32
                return new { axis = axis, address = $"0x{address:X}", rawValue = value.ToString(), numericValue = value };
            });
        }

        public static object WriteDriveParam(int axis, int address, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string valueStr = GetString(body, "value") ?? "";
                int value = Convert.ToInt32(valueStr);
                await handle.EcatSdoWriteAsync(0, (short)axis, address, 0, 7, value);
                return new { success = true };
            });
        }

        public static object ScanEtherCAT(int slot)
        {
            // zmotion.dll 没有 EtherCAT 设备扫描导出；不能返回伪造的空设备列表。
            return Error("EtherCAT device scan is not exposed by zmotion.dll. Use read_ethercat_sdo with a known device position/index, or scan from the RTSys UI.");
        }

        public static object EtherCATReadSDO(int slot, uint position, uint index, uint subindex, string typeStr)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                int dataType = ParseCODataType(typeStr);
                int value = await handle.EcatSdoReadAsync((short)slot, (short)position, (int)index, (int)subindex, dataType);
                return new { slot = slot, position = position, index = $"0x{index:X}", subindex = subindex, type = typeStr, value = value };
            });
        }

        public static object EtherCATWriteSDO(int slot, uint position, uint index, uint subindex, string typeStr, object value)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                int dataType = ParseCODataType(typeStr);
                int intValue = Convert.ToInt32(value);
                await handle.EcatSdoWriteAsync((short)slot, (short)position, (int)index, (int)subindex, dataType, intValue);
                return new { success = true };
            });
        }

        // ---- Helpers ----

        /// <summary>
        /// 逐行加绝对行号（1 基，空行也占一行），与 RTSys 编辑器行号栏一致。
        /// 来源文件首行可能是空行，模型自行数行容易把空行漏掉导致整体偏移 1 行。
        /// </summary>
        private static string NumberLines(IList<string> lines, int firstLine)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
                sb.Append(firstLine + i).Append(": ").Append(lines[i]).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        private static List<string> ValidateRtBasicCode(string code)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(code)) return errors;

            var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // 危险模式检测
            var dangerousPatterns = new[]
            {
                new { Pattern = @"\b(DELETE|FORMAT|ERASE|CLEAR)\s+(FLASH|MEMORY|ALL)\b", Msg = "Dangerous flash/memory operation" },
                new { Pattern = @"\bZMC_\w+", Msg = "Direct DLL calls not allowed in RTBasic" },
                new { Pattern = @"\bASM\b|\bASSEMBLY\b", Msg = "Inline assembly not allowed" }
            };

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("'") || line.StartsWith("REM")) continue;

                // 检查危险模式
                foreach (var dp in dangerousPatterns)
                {
                    if (Regex.IsMatch(line, dp.Pattern, RegexOptions.IgnoreCase))
                        errors.Add($"Line {i + 1}: {dp.Msg}");
                }

                // 简单语法检查：未闭合的 IF/FOR/WHILE
                // 这里仅做基础检查，完整语法由控制器编译器验证
            }

            return errors;
        }

        private static int ParseCODataType(string s)
        {
            if (string.IsNullOrEmpty(s)) return 7; // UINT32
            switch (s.ToLowerInvariant())
            {
                case "bool": return 1;
                case "int8": return 2;
                case "uint8": return 3;
                case "int16": return 4;
                case "uint16": return 5;
                case "int32": return 6;
                case "uint32": return 7;
                case "real32": case "float": return 8;
                case "real64": case "double": return 9;
                case "string": return 10;
                default: return 7;
            }
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null) return val.ToString();
            return null;
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
                return Convert.ToDouble(val);
            return 0;
        }

        private static int? GetInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result))
                    return result;
            }
            return null;
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
            {
                if (val is bool b1) return b1;
                if (val is double dbl) return dbl != 0;
                if (val is int i) return i != 0;
                bool b2;
                if (bool.TryParse(val.ToString(), out b2)) return b2;
            }
            return false;
        }

        private static object[] GetArray(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true)
            {
                if (val is string) return null;
                if (val is System.Collections.IEnumerable en)
                    return en.Cast<object>().ToArray();
            }
            return null;
        }

        private static uint? GetUInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
            {
                uint result;
                if (uint.TryParse(val.ToString(), out result))
                    return result;
            }
            return null;
        }

        public static object PatchSource(string name, Dictionary<string, object> body)
        {
            name = NormalizeProgramName(name);
            if (string.IsNullOrWhiteSpace(name)) return Error("程序名不能为空");
            var ops = GetArray(body, "operations");
            if (ops == null || ops.Length == 0) return Error("operations is required");

            return Safe(() =>
            {
                // 源解析顺序：RTSys 编辑器（含未保存修改）→ 项目文件 → 本地缓存。
                bool fromEditor = false;
                bool fromProject = false;
                string currentSource = null;
                if (RTSysEditor.TryRead(name, out var editorSource))
                {
                    currentSource = editorSource;
                    fromEditor = true;
                }
                else
                {
                    fromProject = ProgramWorkspace.IsProjectAvailable;
                    currentSource = fromProject ? ProgramWorkspace.ReadProjectSource(name) : null;
                    if (currentSource == null)
                    {
                        fromProject = false;
                        currentSource = ProgramWorkspace.ReadLocalSource(name);
                    }
                }
                if (currentSource == null)
                    return Error($"程序 '{name}' 不存在（项目、编辑器和本地缓存均未找到）");

                currentSource = currentSource.Replace("\r\n", "\n").Replace("\r", "\n");

                var editOps = ops.Select(op => op as Dictionary<string, object>)
                    .Where(d => d != null)
                    .Select(d => new
                    {
                        oldString = GetString(d, "old_string") ?? GetString(d, "oldContent") ?? "",
                        newString = GetString(d, "new_string") ?? GetString(d, "content") ?? ""
                    })
                    .ToList();

                foreach (var op in editOps)
                {
                    if (string.IsNullOrEmpty(op.oldString))
                    {
                        currentSource = currentSource.TrimEnd('\n') + "\n" + op.newString + "\n";
                        continue;
                    }
                    int pos = currentSource.IndexOf(op.oldString);
                    if (pos < 0)
                        pos = currentSource.IndexOf(op.oldString, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        return Error("old_string not found: " + op.oldString.Substring(0, Math.Min(op.oldString.Length, 40)));
                    currentSource = currentSource.Substring(0, pos) + op.newString + currentSource.Substring(pos + op.oldString.Length);
                }

                // 写前备份当前源码；备份失败则取消补丁（避免不可回滚的修改）。
                var backup = ProgramBackup.BackupProgramSource(name, "patch");
                if (backup.SourceExisted && backup.Error != null)
                    return Error("备份失败，已取消补丁: " + backup.Error);

                // 写回同一来源，并同步更新本地缓存。
                if (fromEditor)
                {
                    RTSysEditor.TryWrite(name, currentSource, true);
                }
                else if (fromProject)
                {
                    var writeError = ProgramWorkspace.WriteProjectProgram(name, currentSource);
                    if (writeError != null) return Error(writeError);
                }
                ProgramWorkspace.WriteLocalSource(name, currentSource);

                return new
                {
                    success = true,
                    savedToEditor = fromEditor,
                    savedToProject = fromProject,
                    projectPath = fromProject ? GetProjectProgramPath(name) : null,
                    savedLocally = true,
                    downloadedToController = false,
                    source = fromEditor ? "editor" : (fromProject ? "project" : "local"),
                    pendingUpload = true,
                    backup = backup.File,
                    message = fromEditor
                        ? $"程序 '{name}' 的补丁已写入 RTSys 编辑器，并由 RTSys 保存到磁盘。"
                        : (fromProject
                            ? $"程序 '{name}' 的补丁已写回项目文件，并更新本地缓存。"
                            : $"程序 '{name}' 的补丁已写入本地缓存（离线编辑）。")
                };
            });
        }

        // ---- AI tools compatibility stubs (not supported on RTSys) ----
        public static object Compile(string name)
        {
            return CompileProgram(name);
        }

        public static object CopyProgram(string name, Dictionary<string, object> body)
        {
            name = NormalizeProgramName(name);
            if (string.IsNullOrWhiteSpace(name)) return Error("程序名不能为空");
            var newName = NormalizeProgramName(GetString(body, "newName"));
            if (string.IsNullOrWhiteSpace(newName)) return Error("newName is required");
            if (string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
                return Error("源程序名与目标程序名相同");

            var localSource = ProgramWorkspace.ReadLocalSource(name);
            bool inProject = ProgramWorkspace.IsProjectAvailable && ProgramWorkspace.ProjectProgramExists(name);
            if (!inProject && localSource == null)
                return Error($"程序 '{name}' 不存在（项目和本地缓存均未找到）");

            if (ProgramWorkspace.ReadLocalSource(newName) != null ||
                (ProgramWorkspace.IsProjectAvailable && ProgramWorkspace.ProjectProgramExists(newName)))
                return Error($"程序 '{newName}' 已存在");

            bool copiedToProject = false;
            bool internalProjectModel = false;
            string projectBackup = null;
            if (inProject)
            {
                var pb = ProgramBackup.BackupProjectFile("copy");
                projectBackup = pb.ProjectFile;
                if (pb.Error != null) return Error("工程文件备份失败，已取消复制: " + pb.Error);

                var copyError = ProgramWorkspace.CopyProjectProgram(name, newName);
                if (copyError != null) return Error(copyError);
                copiedToProject = true;

                // 宿主内部模型：把副本加入工程树；失败时保留文件级结果。
                string modelError;
                internalProjectModel = RTSysProjectModel.TryAddFileEntry(newName + ".bas", out modelError);
                if (!internalProjectModel)
                    PlugInLog.Log("CopyProgram(" + newName + "): internal add skipped/failed: " + modelError);
            }

            // 同时复制本地缓存。
            bool copiedLocally = localSource != null;
            if (copiedLocally)
                ProgramWorkspace.WriteLocalSource(newName, localSource);

            return new
            {
                success = true,
                sourceName = name,
                newName,
                copiedToProject,
                internalProjectModel,
                copiedLocally,
                projectBackup = projectBackup,
                projectPath = copiedToProject ? GetProjectProgramPath(newName) : null,
                message = $"程序 '{name}' 已复制为 '{newName}'。"
            };
        }

        public static object CreateProgram(Dictionary<string, object> body)
        {
            return Safe(() =>
            {
                var progName = NormalizeProgramName(GetString(body, "name") ?? "");
                if (string.IsNullOrWhiteSpace(progName))
                    return Error("程序名不能为空");

                // 有项目时只拒绝项目内同名程序；本地缓存中的同名旧程序可以被新工程程序覆盖
                //（缓存是跨工程共享的，旧工程的同名缓存不应阻塞当前工程新建）。
                if (ProgramWorkspace.IsProjectAvailable)
                {
                    if (ProgramWorkspace.ProjectProgramExists(progName))
                        return Error($"程序 '{progName}' 已存在（项目中）");
                }
                else if (ProgramWorkspace.ReadLocalSource(progName) != null)
                {
                    return Error($"程序 '{progName}' 已存在（本地缓存中）");
                }

                var source = GetString(body, "sourceCode") ?? GetDefaultProgramSource(progName);

                // 有打开项目：创建程序并加入 .zpj，同时写入本地缓存。
                if (ProgramWorkspace.IsProjectAvailable)
                {
                    var projectBackupResult = ProgramBackup.BackupProjectFile("create");
                    if (projectBackupResult.Error != null)
                        return Error("工程文件备份失败，已取消新建: " + projectBackupResult.Error);

                    var createError = ProgramWorkspace.CreateProjectProgram(progName, source);
                    if (createError != null) return Error(createError);

                    ProgramWorkspace.WriteLocalSource(progName, source);

                    // 宿主内部模型：把新程序加入工程树；失败时保留文件级结果。
                    string modelError;
                    bool internalProjectModel = RTSysProjectModel.TryAddFileEntry(progName + ".bas", out modelError);
                    if (!internalProjectModel)
                        PlugInLog.Log("CreateProgram(" + progName + "): internal add skipped/failed: " + modelError);

                    return new
                    {
                        success = true,
                        savedToProject = true,
                        projectPath = GetProjectProgramPath(progName),
                        savedLocally = true,
                        internalProjectModel,
                        pendingUpload = true,
                        projectBackup = projectBackupResult.ProjectFile,
                        message = internalProjectModel
                            ? $"程序 '{progName}' 已创建并加入工程（工程树已同步），同时保存到本地缓存。"
                            : $"程序 '{progName}' 已创建并加入项目文件，同时保存到本地缓存。"
                    };
                }

                // 无项目：仅写本地缓存（离线编辑），不触碰控制器。
                ProgramWorkspace.WriteLocalSource(progName, source);
                return new
                {
                    success = true,
                    savedToProject = false,
                    savedLocally = true,
                    downloadedToController = false,
                    source = "local",
                    origin = "local",
                    pendingUpload = true,
                    message = $"程序 '{progName}' 已新建并保存到本地缓存。尚未下载到控制器；连接后再执行下载以生效。"
                };
            });
        }

        /// <summary>生成默认 RTBasic 程序源码。</summary>
        private static string GetDefaultProgramSource(string progName)
        {
            return $@"'{progName} - 新建程序
' 自动生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}

BASE(0)
SPEED = 100
ACCEL = 500
DECEL = 500

END
";
        }

        public static object DeleteProgram(string name)
        {
            name = NormalizeProgramName(name);
            if (string.IsNullOrWhiteSpace(name)) return Error("程序名不能为空");

            bool inProject = ProgramWorkspace.IsProjectAvailable && ProgramWorkspace.ProjectProgramExists(name);
            bool inLocal = ProgramWorkspace.ReadLocalSource(name) != null;
            if (!inProject && !inLocal)
                return Error($"程序 '{name}' 不存在（项目和本地缓存均未找到）");

            // 删除不可逆：源码与工程文件必须备份成功，否则取消删除。
            var backup = ProgramBackup.BackupProgramSource(name, "delete");
            if (backup.SourceExisted && backup.Error != null)
                return Error("备份失败，已取消删除: " + backup.Error);

            string projectBackup = null;
            if (inProject)
            {
                var pb = ProgramBackup.BackupProjectFile("delete");
                projectBackup = pb.ProjectFile;
                if (pb.Error != null) return Error("工程文件备份失败，已取消删除: " + pb.Error);
            }

            bool internalProjectModel = false;
            if (inProject)
            {
                // 优先走宿主内部工程模型：选中树节点并调用宿主“删除文件”处理器，
                // 让 RTSys 自己更新内存模型、工程树与 .zpj（避免外部删文件后树残留）。
                string modelError;
                if (RTSysProjectModel.TryDelete(name, out modelError))
                {
                    internalProjectModel = true;
                    // 宿主只更新内存模型/工程树；磁盘文件与 .zpj 条目由这里同步删除。
                    var syncError = ProgramWorkspace.DeleteProjectProgram(name);
                    if (syncError != null)
                        PlugInLog.Log("DeleteProgram(" + name + "): internal delete ok, disk sync failed: " + syncError);
                }
                else
                {
                    var deleteError = ProgramWorkspace.DeleteProjectProgram(name);
                    if (deleteError != null) return Error(deleteError + (modelError != null ? " (internal: " + modelError + ")" : ""));
                }
            }
            if (inLocal)
                ProgramWorkspace.DeleteLocalSource(name);

            return new
            {
                success = true,
                name,
                deletedFromProject = inProject,
                deletedLocally = inLocal,
                internalProjectModel,
                backup = backup.File,
                projectBackup = projectBackup,
                message = internalProjectModel
                    ? $"程序 '{name}' 已通过 RTSys 工程模型删除（工程树与 .zpj 同步更新）。"
                    : (inProject && inLocal
                        ? $"程序 '{name}' 已从项目和本地缓存中删除。"
                        : (inProject
                            ? $"程序 '{name}' 已从项目中删除。"
                            : $"程序 '{name}' 已从本地缓存中删除。"))
            };
        }

        public static object RenameProgram(string name, Dictionary<string, object> body)
        {
            name = NormalizeProgramName(name);
            if (string.IsNullOrWhiteSpace(name)) return Error("程序名不能为空");
            var newName = NormalizeProgramName(GetString(body, "newName"));
            if (string.IsNullOrWhiteSpace(newName)) return Error("newName is required");
            var newNameError = ProgramWorkspace.ValidateProgramName(newName);
            if (newNameError != null) return Error(newNameError);
            if (string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
                return Error("源程序名与目标程序名相同");

            var oldLocal = ProgramWorkspace.ReadLocalSource(name);
            bool inProject = ProgramWorkspace.IsProjectAvailable && ProgramWorkspace.ProjectProgramExists(name);
            if (!inProject && oldLocal == null)
                return Error($"程序 '{name}' 不存在（项目和本地缓存均未找到）");

            if (ProgramWorkspace.ReadLocalSource(newName) != null ||
                (ProgramWorkspace.IsProjectAvailable && ProgramWorkspace.ProjectProgramExists(newName)))
                return Error($"程序 '{newName}' 已存在");

            var backup = ProgramBackup.BackupProgramSource(name, "rename");
            if (backup.SourceExisted && backup.Error != null)
                return Error("备份失败，已取消重命名: " + backup.Error);

            string projectBackup = null;
            if (inProject)
            {
                var pb = ProgramBackup.BackupProjectFile("rename");
                projectBackup = pb.ProjectFile;
                if (pb.Error != null) return Error("工程文件备份失败，已取消重命名: " + pb.Error);
            }

            bool renamedInProject = false;
            bool internalProjectModel = false;
            if (inProject)
            {
                // 优先走宿主内部工程模型：宿主同时完成磁盘改名、模型与工程树刷新。
                string modelError;
                if (RTSysProjectModel.TryRename(name, newName, out modelError))
                {
                    internalProjectModel = true;
                    renamedInProject = true;
                    // 宿主只更新内存模型；同步 .zpj 中的文件名（磁盘文件已由宿主改名）。
                    var syncError = ProgramWorkspace.RenameProjectEntry(name, newName);
                    if (syncError != null)
                        PlugInLog.Log("RenameProgram(" + name + "): internal rename ok, .zpj sync failed: " + syncError);
                }
                else
                {
                    var renameError = ProgramWorkspace.RenameProjectProgram(name, newName);
                    if (renameError != null) return Error(renameError);
                    renamedInProject = true;
                }
            }

            // 本地缓存改名：读旧 → 写新 → 删旧。
            bool renamedLocally = oldLocal != null;
            if (renamedLocally)
            {
                ProgramWorkspace.WriteLocalSource(newName, oldLocal);
                ProgramWorkspace.DeleteLocalSource(name);
            }

            return new
            {
                success = true,
                oldName = name,
                newName,
                renamedInProject,
                internalProjectModel,
                renamedLocally,
                backup = backup.File,
                projectBackup = projectBackup,
                projectPath = renamedInProject ? GetProjectProgramPath(newName) : null,
                message = $"程序 '{name}' 已重命名为 '{newName}'。"
            };
        }

        public static object Download(string name)
        {
            return Error("Download not supported on RTSys (no project files).");
        }

        public static object Upload(string name)
        {
            return Error("Upload not supported on RTSys (no project files).");
        }

        public static object OpenProgram(string name)
        {
            name = NormalizeProgramName(name);
            if (string.IsNullOrWhiteSpace(name)) return Error("程序名不能为空");
            if (!ProgramWorkspace.IsProjectAvailable)
                return Error("OpenProgram not supported on RTSys (no editor integration).");

            var path = GetProjectProgramPath(name);
            if (path == null)
                return Error($"程序无对应磁盘文件：{name}");

            var source = ProgramWorkspace.ReadProjectSource(name);
            if (source == null)
                return Error($"无法读取程序文件：{path}");

            return new
            {
                success = true,
                path,
                sourceCode = source,
                note = "Open this file from the RTSys project tree; it is already on disk."
            };
        }

        public static object GetProgramProcess(string name)
        {
            return Error("GetProgramProcess not supported on RTSys.");
        }

        public static object SetProgramProcess(string name, Dictionary<string, object> body)
        {
            return Error("SetProgramProcess not supported on RTSys.");
        }

        public static object ListBreakpoints(string name)
        {
            return Error("Breakpoints not supported on RTSys.");
        }

        public static object SetBreakpoint(string name, Dictionary<string, object> body)
        {
            return Error("Breakpoints not supported on RTSys.");
        }

        public static object ClearAllBreakpoints(string name)
        {
            return Error("Breakpoints not supported on RTSys.");
        }

        public static object ListProcesses()
        {
            return Error("Process listing not supported on RTSys.");
        }

        public static object GetProcessVariable(int pid, string program, string variable)
        {
            return Error("Process variables not supported on RTSys.");
        }

        public static object GetEvents(long sinceTicks)
        {
            return Error("Event log not supported on RTSys.");
        }

        public static object ListDescriptors()
        {
            return Error("Program type descriptors not supported on RTSys.");
        }

        public static object ListAttachedPlugins()
        {
            try
            {
                var roots = new List<string>();
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    if (!string.IsNullOrEmpty(baseDir))
                        roots.Add(Path.Combine(baseDir, "Plug_in"));
                }
                catch { }
                try
                {
                    var asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(asmDir))
                        roots.Add(Path.GetDirectoryName(asmDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                }
                catch { }

                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    var plugins = new List<object>();
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var dlls = Directory.GetFiles(dir, "*.dll")
                            .Select(f => Path.GetFileName(f)).ToList();
                        // 不返回安装目录绝对路径，避免通过 HTTP 泄露本机目录结构。
                        plugins.Add(new { name = Path.GetFileName(dir), dlls });
                    }
                    return new { success = true, plugins };
                }
                return new { success = false, error = "Plug_in directory not found", plugins = new object[0] };
            }
            catch (Exception ex) { return Error(ex.Message); }
        }

        public static object ListRecipes()
        {
            return Error("Recipes not supported on RTSys.");
        }

        public static object ListRobots()
        {
            return Error("Robots not supported on RTSys.");
        }

        public static object ListRemoteDevices()
        {
            return Error("Remote devices not supported on RTSys.");
        }

        public static object ListAlarms()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                int lastError = await handle.GetLastErrorAsync();
                var states = await handle.GetAllAxisStatesAsync();
                var axisAlarms = new List<object>();
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].alarmCode != 0)
                        axisAlarms.Add(new { axis = i, alarmCode = states[i].alarmCode });
                }
                return new
                {
                    success = true,
                    lastError,
                    lastErrorDescription = ZMotionErrors.GetDescription(lastError),
                    axisAlarms,
                    note = "zmotion.dll exposes only the last controller error code and per-axis alarm codes; there is no alarm history/table."
                };
            });
        }

        public static object ListDigitalIO()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var spec = await handle.GetSysSpecificationAsync();
                int inCount = spec.ioInCount;
                int outCount = spec.ioOutCount;
                if (inCount <= 0 && outCount <= 0)
                    return Error("控制器未提供数字 IO 规格（IO 数量不可用），无法列出数字 IO；这不代表控制器没有 IO。");
                var inBytes = await handle.GetInAllAsync();
                var outBytes = await handle.GetOutAllAsync();
                var inputs = new List<object>();
                var outputs = new List<object>();
                for (int i = 0; i < inCount; i++)
                    inputs.Add(new { index = i, value = GetBit(inBytes, i) });
                for (int i = 0; i < outCount; i++)
                    outputs.Add(new { index = i, value = GetBit(outBytes, i) });
                return new { ioInCount = inCount, ioOutCount = outCount, inputs, outputs };
            });
        }

        private static bool GetBit(byte[] data, int index)
        {
            int byteIdx = index / 8;
            int bitIdx = index % 8;
            return byteIdx >= 0 && byteIdx < data.Length && (data[byteIdx] & (1 << bitIdx)) != 0;
        }

        public static object ReadDigitalIO(int index)
        {
            if (index < 0) return Error("index must be >= 0");
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                bool input = await handle.GetInAsync((short)index);
                bool? output = null;
                try { output = await handle.GetOutputAsync((short)index); } catch { }
                return new { index, input, output };
            });
        }

        public static object WriteDigitalIO(int index, Dictionary<string, object> body)
        {
            if (body == null || !body.ContainsKey("value"))
                return Error("value is required");
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                bool value = GetBool(body, "value");
                await handle.SetOutputAsync((short)index, value);
                return new { success = true, index, value };
            });
        }

        public static object ListAnalogueIO()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var spec = await handle.GetSysSpecificationAsync();
                int aiCount = spec.aiCount;
                int aoCount = spec.aoCount;
                if (aiCount <= 0 && aoCount <= 0)
                    return Error("控制器未提供模拟 IO 规格（通道数不可用），无法列出模拟 IO；这不代表控制器没有模拟通道。");
                var inputs = new List<object>();
                var outputs = new List<object>();
                for (int i = 0; i < aiCount; i++)
                    inputs.Add(new { index = i, value = await handle.GetAInAsync((short)i) });
                for (int i = 0; i < aoCount; i++)
                    outputs.Add(new { index = i, value = await handle.GetDaOutAsync((short)i) });
                return new { aiCount, aoCount, inputs, outputs };
            });
        }

        public static object ReadAnalogueIO(int index)
        {
            if (index < 0) return Error("index must be >= 0");
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                float input = await handle.GetAInAsync((short)index);
                float? output = null;
                try { output = await handle.GetDaOutAsync((short)index); } catch { }
                return new { index, input, output };
            });
        }

        public static object WriteAnalogueIO(int index, Dictionary<string, object> body)
        {
            if (body == null || !body.ContainsKey("value"))
                return Error("value is required");
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                float value = (float)GetDouble(body, "value");
                await handle.SetDaOutAsync((short)index, value);
                return new { success = true, index, value };
            });
        }

        public static object ScanMsBus(int slot)
        {
            return Error("MSBus scan not supported on RTSys.");
        }

        public static object EtherCATWriteSDOFromDict(Dictionary<string, object> body)
        {
            // Expecting body with: slot, position, index, subindex, type, value
            if (body == null) return Error("body required");
            int slot = GetInt(body, "slot") ?? 0;
            uint position = GetUInt(body, "position") ?? 0;
            uint index = GetUInt(body, "index") ?? 0;
            uint subindex = GetUInt(body, "subindex") ?? 0;
            string type = GetString(body, "type") ?? "uint16";
            object value = body.ContainsKey("value") ? body["value"] : null;
            if (value == null) return Error("value required");
            return EtherCATWriteSDO(slot, position, index, subindex, type, value);
        }

        public static object ReadSysVar(string name)
        {
            return Error("System variables not directly accessible on RTSys. Use VR/TABLE.");
        }

        public static object WriteSysVar(string name, Dictionary<string, object> body)
        {
            return Error("System variables not directly writable on RTSys.");
        }

        // =====================================================================
        // HMI 内部对象模型能力（全部来自 RTSys 进程内 ExtMainHmi.dll 导出符号）
        //
        // 所有方法通过 HmiInternal 桥接调用 ExtMainHmi.dll：
        //   - ZHmi_FileClass/WindowClass/ControlClass/FeatureClass 对象模型
        //   - FindFeature / GetFeatureInt / GetFeatureDouble / GetFeatureString
        //   - SetFeatureInt / SetFeatureDouble / SetString / ReflashFeatureList
        //   - FindWindowW / FindControl
        //
        // 线程约束：ExtMainHmi 对象属于宿主 UI 线程；HmiInternal 直接调用原生符号，
        //           需确保在 UI 线程执行（与 RTSysProjectModel.UiInvoke 一致）。
        // =====================================================================

        /// <summary>按控件属性全集重读后拼 detail（ID/类型/名称/几何/功能/颜色/状态/绑定）。</summary>
        private static object BuildControlDetail(IntPtr ctl, int cid, int typeId, string name)
        {
            var feats = HmiInternal.EnumerateFeatures(ctl);
            var color = HmiInternal.GetFeatureInt(ctl, 1029, 0) ?? HmiInternal.GetFeatureInt(ctl, 1036, 0) ?? HmiInternal.GetFeatureInt(ctl, 1046, 0);
            var visible = HmiInternal.GetFeatureInt(ctl, 1021, 0);
            var display = HmiInternal.GetFeatureInt(ctl, 1024, 0);
            var device = HmiInternal.GetFeatureInt(ctl, 1011, 0);
            var regType = HmiInternal.GetFeatureInt(ctl, 1011, 1);
            var regNo = HmiInternal.GetFeatureInt(ctl, 1011, 2);
            var state = HmiInternal.GetFeatureInt(ctl, 1005, 0);
            return new
            {
                controlId = cid,
                type = typeId,
                typeName = HmiControlTypeName(typeId),
                name = name,
                geometry = new
                {
                    x = HmiInternal.GetFeatureInt(ctl, 1003, 0) ?? 0,
                    y = HmiInternal.GetFeatureInt(ctl, 1003, 1) ?? 0,
                    width = HmiInternal.GetFeatureInt(ctl, 1002, 0) ?? 0,
                    height = HmiInternal.GetFeatureInt(ctl, 1002, 1) ?? 0
                },
                color = color.HasValue ? (object)color.Value : null,
                visible,
                displayByBit = display,
                binding = device.HasValue ? new { device = device.Value, regType, regNo } : null,
                stateCount = state,
                featureCount = feats.Count
            };
        }

        /// <summary>列出当前打开 HMI 文件的所有窗口（编号/名称/几何/控件数）。</summary>
        public static object HmiListWindows()
        {
            return Safe(() =>
            {
                PlugInLog.Log("hmi_list_windows invoked");
                var result = HmiOnUiThread(() =>
                {
                    var file = HmiInternal.GetCurrentFileClass();
                    if (file == IntPtr.Zero)
                        return new { success = false, error = "未打开任何 HMI 文件（请先在 RTSys 中打开 .hmi 画面）", windows = new object[0] };

                    var windows = new List<object>();
                    foreach (var win in HmiInternal.EnumerateChain(file, HmiInternal.OffWindowHead))
                    {
                        var id = HmiInternal.GetFeatureInt(win, HmiInternal.FeatureWindowId, 0) ?? 0;
                        var name = HmiInternal.GetFeatureString(win, HmiInternal.FeatureWindowName, 0) ?? ("Window" + id);
                        int? w = null, h = null, x = null, y = null;
                        foreach (var f in HmiInternal.EnumerateFeatures(win))
                        {
                            if (f.MainId == HmiInternal.FeatureSize && f.SubId == 0) w = HmiInternal.GetFeatureInt(win, f.MainId, f.SubId, 0);
                            if (f.MainId == HmiInternal.FeatureSize && f.SubId == 1) h = HmiInternal.GetFeatureInt(win, f.MainId, f.SubId, 0);
                            if (f.MainId == HmiInternal.FeaturePosition && f.SubId == 0) x = HmiInternal.GetFeatureInt(win, f.MainId, f.SubId, 0);
                            if (f.MainId == HmiInternal.FeaturePosition && f.SubId == 1) y = HmiInternal.GetFeatureInt(win, f.MainId, f.SubId, 0);
                        }

                        var controls = HmiInternal.EnumerateChain(win, HmiInternal.OffControlHead);
                        windows.Add(new
                        {
                            id = id,
                            name,
                            width = w ?? 0,
                            height = h ?? 0,
                            x = x ?? 0,
                            y = y ?? 0,
                            controlCount = controls.Count,
                            type = HmiInternal.GetObjectType(win)
                        });
                    }
                    return new { success = true, windows, count = windows.Count };
                });
                if (result == null)
                    PlugInLog.Log("hmi_list_windows: UI thread invoke returned null");
                return result;
            });
        }

        /// <summary>读取指定窗口的控件列表（id/type/typeName/x/y/w/h/name/geometry/features 摘要）。</summary>
        public static object HmiReadWindow(int windowId)
        {
            return Safe(() => HmiOnUiThread(() =>
            {
                var file = HmiInternal.GetCurrentFileClass();
                if (file == IntPtr.Zero)
                    return new { success = false, error = "未打开任何 HMI 文件" };

                var win = HmiInternal.FindWindow(file, windowId);
                if (win == IntPtr.Zero)
                    return new { success = false, error = "窗口不存在：" + windowId };

                var controls = new List<object>();
                foreach (var ctl in HmiInternal.EnumerateChain(win, HmiInternal.OffControlHead))
                {
                    int cid = HmiInternal.GetFeatureInt(ctl, HmiInternal.FeatureControlId, 0) ?? 0;
                    int? w = null, h = null, x = null, y = null;
                    var texts = new List<string>();
                    foreach (var f in HmiInternal.EnumerateFeatures(ctl))
                    {
                        if (f.MainId == HmiInternal.FeatureSize && f.SubId == 0) w = HmiInternal.GetFeatureInt(ctl, f.MainId, f.SubId, 0);
                        if (f.MainId == HmiInternal.FeatureSize && f.SubId == 1) h = HmiInternal.GetFeatureInt(ctl, f.MainId, f.SubId, 0);
                        if (f.MainId == HmiInternal.FeaturePosition && f.SubId == 0) x = HmiInternal.GetFeatureInt(ctl, f.MainId, f.SubId, 0);
                        if (f.MainId == HmiInternal.FeaturePosition && f.SubId == 1) y = HmiInternal.GetFeatureInt(ctl, f.MainId, f.SubId, 0);
                        if (f.MainId == HmiInternal.FeatureDisplayText && f.SubId == 0)
                        {
                            var t = HmiInternal.GetFeatureString(ctl, f.MainId, f.SubId, 0);
                            if (!string.IsNullOrEmpty(t) && !texts.Contains(t)) texts.Add(t);
                        }
                    }

                    var typeId = HmiInternal.GetObjectType(ctl);
                    var typeName = HmiControlTypeName(typeId);
                    var name = HmiInternal.GetFeatureString(ctl, HmiInternal.FeatureControlName, 0);
                    controls.Add(new
                    {
                        id = cid,
                        type = typeId,
                        typeName,
                        name,
                        x = x ?? 0,
                        y = y ?? 0,
                        width = w ?? 0,
                        height = h ?? 0,
                        text = texts,
                        featureCount = HmiInternal.EnumerateFeatures(ctl).Count,
                        detail = BuildControlDetail(ctl, cid, typeId, name)
                    });
                }
                return new { success = true, windowId, controls, count = controls.Count };
            }));
        }

        /// <summary>
        /// 按 (mainId, subId, index) 读取特征值（类型感知）。
        /// 特征按注册表类型区分：1=整型、5=Double、3=字符串、0/2=整型；
        /// 之前只走 GetFeatureInt，String/Double 全部丢。
        /// </summary>
        /// <summary>
        /// 按（可选 windowId + 类型）编号解析目标对象。控件编号在各窗口间重复（键盘窗口占用 1~47，
        /// 工作画面 1~17 全重叠），因此未指定 windowId 时多窗命中会返回歧义错误而非静默命中首窗；
        /// 指定 windowId 时先 FindWindow(windowId) 再 FindControl(该窗口, controlId)。
        /// public 以便回归测试的反射辅助类（HmiTestHelper）直接调用。
        /// </summary>
        public static object HmiGetFeature(string targetType, int targetId, int mainId, int subId = 0, int index = 0, int? windowId = null)
        {
            return Safe(() => HmiOnUiThread(() =>
            {
                IntPtr obj;
                string ambError;
                if (!ResolveTarget(targetType, targetId, windowId, out obj, out ambError))
                    return new { success = false, error = ambError, windowId };
                if (obj == IntPtr.Zero)
                    return new { success = false, error = "目标对象不存在：" + targetType + "[" + targetId + "]" };

                var kind = HmiFeatureKind(mainId);
                switch (kind)
                {
                    case "string":
                        {
                            var s = HmiInternal.GetFeatureString(obj, mainId, subId, index);
                            if (s == null)
                            {
                                var fallback = HmiInternal.GetFeatureInt(obj, mainId, subId, index);
                                if (fallback == null)
                                    return new { success = false, error = "特征不存在或非字符串：" + mainId + "," + subId + "," + index };
                                return new { success = true, kind, mainId, subId, index, value = fallback.Value };
                            }
                            return new { success = true, kind, mainId, subId, index, value = s };
                        }
                    case "double":
                        {
                            var d = HmiInternal.GetFeatureDouble(obj, mainId, subId, index);
                            if (d == null)
                            {
                                var fallback = HmiInternal.GetFeatureInt(obj, mainId, subId, index);
                                if (fallback == null)
                                    return new { success = false, error = "特征不存在：" + mainId + "," + subId + "," + index };
                                return new { success = true, kind, mainId, subId, index, value = (double)fallback.Value };
                            }
                            return new { success = true, kind, mainId, subId, index, value = d.Value };
                        }
                    case "int":
                        {
                            var v = HmiInternal.GetFeatureInt(obj, mainId, subId, index);
                            if (v == null)
                                return new { success = false, error = "特征不存在：" + mainId + "," + subId + "," + index };
                            return new { success = true, kind, mainId, subId, index, value = v.Value };
                        }
                    default:
                        {
                            var v = HmiInternal.GetFeatureInt(obj, mainId, subId, index);
                            if (v != null)
                                return new { success = true, kind = "int", mainId, subId, index, value = v.Value };
                            var s = HmiInternal.GetFeatureString(obj, mainId, subId, index);
                            if (s != null)
                                return new { success = true, kind = "string", mainId, subId, index, value = s };
                            var d = HmiInternal.GetFeatureDouble(obj, mainId, subId, index);
                            if (d != null)
                                return new { success = true, kind = "double", mainId, subId, index, value = d.Value };
                            return new { success = false, error = "特征不存在：" + mainId + "," + subId + "," + index };
                        }
                    }
                }));
        }

        private static string HmiFeatureKind(int mainId)
        {
            switch (mainId)
            {
                case 1001: case 2000: case 20003:
                    return "string";
                case 1002: case 1003:
                    return "double";
                case 1000: case 20001:
                    return "int";
                case 1270: case 1081:
                    return "int";
                case 1029: case 1036: case 1046: // 颜色类
                    return "color";
                default:
                    return "auto";
            }
        }

        /// <summary>
        /// 按 (mainId, subId, index) 写入特征值（按 HmiFeatureKind 分发 Int/Double/String）。
        /// 写后回读比对（宿主 1270 锁 / 负值钳位时 SetFeatureInt 仍返回 0）。
        /// body 兼容 int/double/string 三种输入：{{targetType,targetId,mainId,subId,index,value,valueString,valueNumber}}
        /// </summary>
        /// <summary>
        /// 线程封送：ExtMainHmi 对象属宿主 UI 线程。封送失败时不激活/置顶 RTSys，直接返回结构化错误。
        /// </summary>
        private static object HmiOnUiThread(Func<object> func)
        {
            var result = RTSysProjectModel.UiInvokeAsync(func);
            return result ?? (object)new { success = false, error = "无法将 HMI 操作封送到 RTSys UI 线程（窗口不可用或响应超时）" };
        }

        public static object HmiSetFeature(string targetType, int targetId, int mainId, int subId, int value)
        {
            return HmiSetFeatureCompat(targetType, targetId, mainId, subId, 0, value, null, null, null);
        }

        public static object HmiSetFeatureCompat(string targetType, int targetId, int mainId, int subId, int index,
                                                object value, string valueString, double? valueNumber, int? windowId = null)
        {
            return Safe(() =>
            {
                var result = HmiOnUiThread(() =>
                {
                    IntPtr obj;
                    string ambError;
                    if (!ResolveTarget(targetType, targetId, windowId, out obj, out ambError))
                        return new { success = false, error = ambError, windowId };

                    var kind = HmiFeatureKind(mainId);
                    int rc = -1;
                    switch (kind)
                    {
                        case "string":
                            if (valueString == null && value != null) valueString = value.ToString();
                            if (valueString == null) valueString = string.Empty;
                            rc = HmiInternal.SetFeatureString(obj, mainId, subId, index, valueString);
                            HmiInternal.ReflashFeatureAction(obj, mainId, subId, index);
                            HmiInternal.ReflashFeatureList(obj);
                            bool refreshedS = HmiInternal.RefreshCanvas();
                            var afterS = HmiInternal.GetFeatureString(obj, mainId, subId, index);
                            bool okS = rc == 0 && afterS != null && afterS == valueString;
                            return new { success = okS, kind = "string", rc, mainId, subId, index, value = valueString,
                                         afterValue = afterS, refreshed = refreshedS, error = okS ? null : "写后回读不一致（String）" };
                        case "double":
                            {
                                double dv = valueNumber.HasValue ? valueNumber.Value : (value == null ? 0 : Convert.ToDouble(value));
                                if (mainId == HmiInternal.FeatureSize || mainId == HmiInternal.FeaturePosition)
                                {
                                    if (dv < 0 || dv != Math.Truncate(dv))
                                        return new { success = false, error = "HMI 几何值必须是非负整数" };
                                    int geometryValue = checked((int)dv);
                                    rc = HmiInternal.SetFeatureInt(obj, mainId, subId, index, geometryValue);
                                    HmiInternal.ReflashFeatureAction(obj, mainId, subId, index);
                                    HmiInternal.ReflashFeatureList(obj);
                                    bool geometryRefreshed = HmiInternal.RefreshCanvas();
                                    var geometryAfter = HmiInternal.GetFeatureInt(obj, mainId, subId, index);
                                    bool geometryOk = rc == 0 && geometryAfter.HasValue && geometryAfter.Value == geometryValue;
                                    return new { success = geometryOk, kind = "geometry", rc, mainId, subId, index,
                                                 value = geometryValue, afterValue = geometryAfter, refreshed = geometryRefreshed,
                                                 error = geometryOk ? null : "写后回读不一致（可能被 1270 位置锁拦截）" };
                                }
                                rc = HmiInternal.SetFeatureDouble(obj, mainId, subId, index, dv);
                                HmiInternal.ReflashFeatureAction(obj, mainId, subId, index);
                                HmiInternal.ReflashFeatureList(obj);
                                bool refreshedD = HmiInternal.RefreshCanvas();
                                var afterD = HmiInternal.GetFeatureDouble(obj, mainId, subId, index);
                                bool okD = rc == 0 && afterD.HasValue && Math.Abs(afterD.Value - dv) < 1e-9;
                                if (!okD)
                                {
                                    var fb = HmiInternal.GetFeatureInt(obj, mainId, subId, index);
                                    if (fb.HasValue && Math.Abs(fb.Value - dv) < 1e-9)
                                    {
                                        okD = rc == 0;
                                        return new { success = okD, kind = "double", rc, mainId, subId, index, value = dv,
                                                     afterValue = fb.Value, refreshed = refreshedD, error = okD ? null : "写后回读不一致（Double）" };
                                    }
                                }
                                return new { success = okD, kind = "double", rc, mainId, subId, index, value = dv,
                                             afterValue = afterD.HasValue ? (object)afterD.Value : null,
                                             refreshed = refreshedD,
                                             error = okD ? null : "写后回读不一致（Double）" };
                            }
                        case "int":
                        case "color":
                        case "auto":
                        default:
                            {
                                int iv = value == null ? (valueNumber.HasValue ? (int)valueNumber.Value : 0) : Convert.ToInt32(value);
                                rc = HmiInternal.SetFeatureInt(obj, mainId, subId, index, iv);
                                HmiInternal.ReflashFeatureAction(obj, mainId, subId, index);
                                HmiInternal.ReflashFeatureList(obj);
                                bool refreshed = HmiInternal.RefreshCanvas();
                                var after = HmiInternal.GetFeatureInt(obj, mainId, subId, index);
                                bool ok = rc == 0 && after.HasValue && after.Value == iv;
                                return new { success = ok, kind = "int", rc, mainId, subId, index, value = iv,
                                             afterValue = after.HasValue ? (object)after.Value : null,
                                             refreshed,
                                             error = ok ? null : "写后回读不一致（可能被 1270 位置锁或负值钳位拦截）" };
                            }
                    }
                });
                if (result == null)
                    return new { success = false, error = "无法封送到 RTSys UI 线程（RTSys 窗口未激活 / HMI 未处于编辑状态），请在 RTSys 中双击打开目标 HMI 画面后重试" };
                return result;
            });
        }

        /// <summary>仅供用户明确要求激活 RTSys 窗口时使用；HMI 操作不依赖前台状态。</summary>
        public static object ActivateRtsysWindow()
        {
            return Safe(() =>
            {
                PlugInLog.Log("activate_rtsys_window invoked");
                bool ok = RTSysProjectModel.EnsureForegroundWindow();
                return new { success = ok, activated = ok, error = ok ? null : "RTSys 主窗口未找到或无法激活" };
            });
        }

        /// <summary>设置控件几何，并校验宿主实际写入值。</summary>
        public static object HmiSetGeometry(Dictionary<string, object> body)
        {
            return Safe(() => HmiOnUiThread(() =>
            {
                var result = DoHmiSetGeometry(body);
                if (result == null)
                    return new { success = false, error = "无法封送到 RTSys UI 线程（RTSys 窗口未激活 / HMI 未处于编辑状态），请在 RTSys 中双击打开目标 HMI 画面后重试" };
                return result;
            }));
        }

        private static object DoHmiSetGeometry(Dictionary<string, object> body)
        {
            string targetType = GetString(body, "targetType") ?? "control";
            int targetId = GetInt(body, "targetId") ?? 0;
            int? x = GetInt(body, "x");
            int? y = GetInt(body, "y");
            int? width = GetInt(body, "width");
            int? height = GetInt(body, "height");
            int? windowId = GetInt(body, "windowId");
            bool isSize = GetBool(body, "isSize");
            bool refresh = GetBool(body, "refresh");

            if ((!isSize && !x.HasValue && !y.HasValue) || (isSize && !width.HasValue && !height.HasValue))
                return new { success = false, error = isSize ? "尺寸模式至少需要 width 或 height" : "位置模式至少需要 x 或 y" };
            if ((x.HasValue && x.Value < 0) || (y.HasValue && y.Value < 0) ||
                (width.HasValue && width.Value < 0) || (height.HasValue && height.Value < 0))
                return new { success = false, error = "HMI 几何值不能为负数" };

            IntPtr obj;
            string ambError;
            if (!ResolveTarget(targetType, targetId, windowId, out obj, out ambError))
                return new { success = false, error = ambError, windowId };

            var writes = new List<object>();
            int rc = 0;
            bool success = true;
            if (!isSize)
            {
                if (x.HasValue) success = SetGeometryValue(obj, HmiInternal.FeaturePosition, 0, x.Value, out rc, writes);
                if (success && y.HasValue) success = SetGeometryValue(obj, HmiInternal.FeaturePosition, 1, y.Value, out rc, writes);
            }
            else
            {
                if (width.HasValue) success = SetGeometryValue(obj, HmiInternal.FeatureSize, 0, width.Value, out rc, writes);
                if (success && height.HasValue) success = SetGeometryValue(obj, HmiInternal.FeatureSize, 1, height.Value, out rc, writes);
            }
            if (refresh) HmiInternal.ReflashFeatureList(obj);
            bool canvasRefreshed = HmiInternal.RefreshCanvas();
            return new { success, rc, targetType, targetId, refreshed = canvasRefreshed, featureListRefreshed = refresh, writes };
            }

        private static bool SetGeometryValue(IntPtr obj, int mainId, int subId, int value, out int rc, List<object> writes)
        {
            rc = HmiInternal.SetFeatureInt(obj, mainId, subId, 0, value);
            HmiInternal.ReflashFeatureAction(obj, mainId, subId, 0);
            var after = HmiInternal.GetFeatureInt(obj, mainId, subId, 0);
            bool success = rc == 0 && after == value;
            writes.Add(new { mainId, subId, value, after, success });
            return success;
        }

        /// <summary>
        /// 在当前打开的 HMI 文件里新建一个控件：NewAndAddControl(id,type,x,y,w,h)。
        /// body: { windowId, controlId, controlType, x, y, width, height }
        /// </summary>
        public static object HmiCreateControl(Dictionary<string, object> body)
        {
            return Safe(() => HmiOnUiThread(() =>
            {
                var file = HmiInternal.GetCurrentFileClass();
                if (file == IntPtr.Zero)
                    return new { success = false, error = "未打开任何 HMI 文件" };

                int windowId = GetInt(body, "windowId") ?? 10;
                int controlId = GetInt(body, "controlId") ?? 1;
                int controlType = GetInt(body, "controlType") ?? HmiDefaultButtonType();
                int x = GetInt(body, "x") ?? 0;
                int y = GetInt(body, "y") ?? 0;
                int w = GetInt(body, "width") ?? 100;
                int h = GetInt(body, "height") ?? 40;
                if (x < 0 || y < 0 || w < 0 || h < 0)
                    return new { success = false, error = "HMI 几何值不能为负数" };

                var win = HmiInternal.FindWindow(file, windowId);
                if (win == IntPtr.Zero)
                    return new { success = false, error = "窗口不存在：" + windowId };

                if (HmiInternal.FindControl(win, controlId) != IntPtr.Zero)
                    return new { success = false, error = "控件已存在：window " + windowId + " control " + controlId };

                var ctl = HmiInternal.NewAndAddControl(win, controlId, controlType, x, y, w, h);
                if (ctl == IntPtr.Zero)
                    return new { success = false, error = "NewAndAddControl 失败（控件类型可能需要对应控件 DLL）" };
                HmiInternal.ReflashFeatureAction(ctl, HmiInternal.FeaturePosition, 0, 0);
                HmiInternal.ReflashFeatureAction(ctl, HmiInternal.FeaturePosition, 1, 0);
                HmiInternal.ReflashFeatureAction(ctl, HmiInternal.FeatureSize, 0, 0);
                HmiInternal.ReflashFeatureAction(ctl, HmiInternal.FeatureSize, 1, 0);
                var actualX = HmiInternal.GetFeatureInt(ctl, HmiInternal.FeaturePosition, 0, 0);
                var actualY = HmiInternal.GetFeatureInt(ctl, HmiInternal.FeaturePosition, 1, 0);
                var actualW = HmiInternal.GetFeatureInt(ctl, HmiInternal.FeatureSize, 0, 0);
                var actualH = HmiInternal.GetFeatureInt(ctl, HmiInternal.FeatureSize, 1, 0);
                HmiInternal.ReflashFeatureList(ctl);
                bool refreshed = HmiInternal.RefreshCanvas();
                if (actualX != x || actualY != y || actualW != w || actualH != h)
                    return new { success = false, error = "新控件已创建，但几何回读与请求不一致", windowId, controlId, actualX, actualY, actualW, actualH, refreshed };

                return new
                {
                    success = true,
                    windowId,
                    controlId,
                    controlType,
                    x, y, w, h,
                    refreshed
                };
            }));
        }

        /// <summary>Button 控件类型（ExtMainHmi 内置）。</summary>
        private static int HmiDefaultButtonType() => 609;

        /// <summary>
        /// 按（可选 windowId + 类型）+ 编号解析目标对象指针。
        /// 控件编号在各窗口间重复（键盘窗口占用 1~47，工作画面 1~17 全重叠），
        /// 因此未指定 windowId 时若多个窗口命中同一编号，返回歧义错误而非静默命中首窗。
        /// 指定 windowId 时先 FindWindow(windowId) 再 FindControl(该窗口, controlId)。
        /// </summary>
        private static bool ResolveTarget(string targetType, int targetId, int? windowId, out IntPtr obj, out string error)
        {
            obj = IntPtr.Zero;
            error = null;
            string kind = string.Equals(targetType, "control", StringComparison.OrdinalIgnoreCase) ? "control" : "window";
            var file = HmiInternal.GetCurrentFileClass();
            if (file == IntPtr.Zero)
            {
                error = "未打开任何 HMI 文件（请先在 RTSys 中打开 .hmi 画面）";
                return false;
            }

            if (kind == "window")
            {
                obj = HmiInternal.FindWindow(file, targetId);
                if (obj == IntPtr.Zero)
                    error = "窗口不存在：" + targetId;
                return obj != IntPtr.Zero;
            }

            // kind == control
            if (windowId.HasValue)
            {
                var w = HmiInternal.FindWindow(file, windowId.Value);
                if (w == IntPtr.Zero)
                {
                    error = "窗口不存在：" + windowId.Value;
                    return false;
                }
                obj = HmiInternal.FindControl(w, targetId);
                if (obj == IntPtr.Zero)
                    error = "窗口 " + windowId.Value + " 内不存在控件：" + targetId;
                return obj != IntPtr.Zero;
            }

            var hits = new List<int>();
            foreach (var win in HmiInternal.EnumerateChain(file, HmiInternal.OffWindowHead))
            {
                if (HmiInternal.FindControl(win, targetId) != IntPtr.Zero)
                    hits.Add(HmiInternal.GetFeatureInt(win, HmiInternal.FeatureWindowId, 0) ?? 0);
            }
            if (hits.Count == 0)
            {
                error = "目标对象不存在：control[" + targetId + "]（所有窗口均无此编号）";
                return false;
            }
            if (hits.Count > 1)
            {
                error = "控件编号 " + targetId + " 在 " + hits.Count + " 个窗口重复（窗口 " +
                        string.Join(",", hits) + "），无法定位；请指定 windowId";
                return false;
            }

            var only = HmiInternal.FindWindow(file, hits[0]);
            obj = only == IntPtr.Zero ? IntPtr.Zero : HmiInternal.FindControl(only, targetId);
            if (obj == IntPtr.Zero)
                error = "目标对象不存在：control[" + targetId + "] @窗口 " + hits[0];
            return obj != IntPtr.Zero;
        }

        /// <summary>控件类型 ID → 名称映射（ExtMainHmi 内置 + ExtHmiControl_W 自定义）。</summary>
        private static string HmiControlTypeName(int typeId)
        {
            switch (typeId)
            {
                case 11: return "Window";
                case 600: return "BitState";
                case 601: return "WordState";
                case 602: return "BitModify";
                case 603: return "WordModify";
                case 604: return "BitSwitch";
                case 605: return "WordSwitch";
                case 606: return "List";
                case 607: return "Slip";
                case 608: return "KeyButton";
                case 609: return "Button";
                case 610: return "KeyButton";
                case 611: return "String show";
                case 612: return "Value show";
                case 616: return "Animation";
                case 620: return "Timer";
                case 621: return "Timer";
                case 630: return "FlowBlock";
                case 631: return "FlowBlock";
                case 640: return "Timer";
                case 641: return "Timer";
                case 645: return "Table";
                case 622: return "Pie Chart";
                case 623: return "Bar Chart";
                case 624: return "Meter";
                case 625: return "XY Chart";
                case 626: return "Trend Chart";
                case 627: return "Data Group Show";
                case 635: return "Event Show";
                case 636: return "Alarm Show";
                case 637: return "Alarm Bar";
                case 700: return "Line/Polyline/Polygon";
                case 701: return "Ellipse/Arc/Sector";
                case 702: return "Rect";
                case 703: return "StaticText";
                case 704: return "Picture";
                case 705: return "Import";
                case 706: return "Bezier";
                case 707: return "Import";
                case 708: return "Import";
                case 709: return "Import";
                case 710: return "Import";
                case 711: return "Import";
                case 712: return "Scale";
                case 713: return "String show";
                case 714: return "File3Edit";
                case 715: return "CAD";
                case 716: return "File browser";
                case 717: return "Report view";
                case 719: return "Progress Bar";
                case 720: return "Dynamic Scale";
                case 721: return "Tree";
                case 722: return "Scheduling";
                case 723: return "FlowBlock";
                case 724: return "Bar Code";
                case 725: return "Radio Button";
                case 726: return "Check Box";
                case 727: return "Message Out";
                case 728: return "LayDate";
                case 730: return "Menu";
                case 731: return "Tab control";
                case 732: return "Spin Button";
                case 733: return "Group box";
                case 734: return "Panel";
                case 735: return "Recipe View";
                case 736: return "Basic Extend";
                case 900: return "File3Edit";
                case 901: return "Custom";
                default: return "Type" + typeId;
            }
        }
    }
}
