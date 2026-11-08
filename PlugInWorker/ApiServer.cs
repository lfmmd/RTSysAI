using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal class ApiServer : IDisposable
    {
        private int _port = 9090;
        private HttpListener _listener;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private bool _running;

        /// <summary>每个 HTTP 请求完成后触发（请求 + 响应摘要），供插件窗口显示。</summary>
        public static event Action<string> RequestCompleted;

        #region ---- 本地 HTTP 鉴权 / 敏感操作 nonce ----

        private static readonly object _authLock = new object();
        private static readonly object _nonceLock = new object();
        private static string _authToken;
        private static string _sensitiveNonce;
        private static DateTime _sensitiveNonceExpiryUtc;

        /// <summary>当前 HTTP API 鉴权令牌（客户端放在 X-Auth-Token 头）。</summary>
        public static string CurrentToken
        {
            get { EnsureAuthToken(); return _authToken; }
        }

        private static string TokenFilePath
        {
            get { return Path.Combine(AiService.DataDir, "api_token.txt"); }
        }

        private static void EnsureAuthToken()
        {
            lock (_authLock)
            {
                if (!string.IsNullOrEmpty(_authToken)) return;
                try
                {
                    Directory.CreateDirectory(AiService.DataDir);
                    var path = TokenFilePath;
                    if (File.Exists(path))
                    {
                        var existing = File.ReadAllText(path).Trim();
                        if (existing.Length >= 32) { _authToken = existing; return; }
                    }
                    var token = NewRandomToken(32);
                    File.WriteAllText(path, token, new UTF8Encoding(false));
                    TryRestrictFileToCurrentUser(path);
                    _authToken = token;
                    PlugInLog.Log("ApiServer: 已生成 HTTP API 鉴权令牌 " + path);
                }
                catch (Exception ex)
                {
                    _authToken = NewRandomToken(32);
                    PlugInLog.LogException("ApiServer.EnsureAuthToken", ex);
                }
            }
        }

        private static string NewRandomToken(int bytes)
        {
            var buffer = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buffer);
            return BitConverter.ToString(buffer).Replace("-", "").ToLowerInvariant();
        }

        private static void TryRestrictFileToCurrentUser(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                var security = fi.GetAccessControl();
                security.SetAccessRuleProtection(true, false);
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                fi.SetAccessControl(security);
            }
            catch { }
        }

        /// <summary>由插件窗口“授权一次”按钮调用；返回 5 分钟内有效的一次性 nonce。</summary>
        public static string IssueSensitiveNonce()
        {
            lock (_nonceLock)
            {
                _sensitiveNonce = NewRandomToken(16);
                _sensitiveNonceExpiryUtc = DateTime.UtcNow.AddMinutes(5);
                return _sensitiveNonce;
            }
        }

        private static bool ConsumeSensitiveNonce(string provided)
        {
            lock (_nonceLock)
            {
                if (string.IsNullOrEmpty(_sensitiveNonce) || string.IsNullOrEmpty(provided)) return false;
                if (DateTime.UtcNow > _sensitiveNonceExpiryUtc) { _sensitiveNonce = null; return false; }
                var ok = FixedTimeEquals(provided, _sensitiveNonce);
                if (ok) _sensitiveNonce = null; // 一次性使用
                return ok;
            }
        }

        internal static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var ab = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            int diff = ab.Length ^ bb.Length;
            int count = Math.Min(ab.Length, bb.Length);
            for (int i = 0; i < count; i++) diff |= ab[i] ^ bb[i];
            return diff == 0;
        }

        private static bool IsAuthorized(HttpListenerContext ctx)
        {
            try
            {
                // 禁止任何跨源浏览器请求（不提供 CORS 头）。
                if (!string.IsNullOrEmpty(ctx.Request.Headers["Origin"])) return false;
                var remote = ctx.Request.RemoteEndPoint;
                if (remote == null || !IPAddress.IsLoopback(remote.Address)) return false;
                var provided = ctx.Request.Headers["X-Auth-Token"];
                if (string.IsNullOrEmpty(provided)) return false;
                EnsureAuthToken();
                return FixedTimeEquals(provided, _authToken);
            }
            catch { return false; }
        }

        /// <summary>运动控制类端点：默认关闭，需配置开启且每次请求携带 UI 一次性 nonce。</summary>
        private static bool IsMotionControlRequest(string[] segments, string method)
        {
            if (segments.Length < 2 || segments[0] != "api") return false;
            var route = segments[1];
            if (route == "programs" && segments.Length >= 4)
            {
                var action = segments[3];
                return action == "run" || action == "stop";
            }
            if (route == "vr" || route == "table" || route == "io" || route == "drive")
                return method == "PUT" || method == "POST" || method == "PATCH";
            if (route == "ethercat")
                return method == "PUT" || method == "POST";
            return false;
        }

        private static bool IsMotionControlEnabled()
        {
            try
            {
                var cfgPath = AiService.ConfigPath;
                if (!File.Exists(cfgPath)) return false;
                var cfg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath));
                object val;
                if (cfg == null || !cfg.TryGetValue("enableMotionControlApi", out val) || val == null) return false;
                if (val is bool) return (bool)val;
                return string.Equals(val.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>由插件窗口写入 enableMotionControlApi（合并保留其他配置键）。</summary>
        public static void SetMotionControlEnabled(bool enabled)
        {
            try
            {
                Directory.CreateDirectory(AiService.DataDir);
                var json = new JavaScriptSerializer();
                Dictionary<string, object> cfg = null;
                if (File.Exists(AiService.ConfigPath))
                    cfg = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(AiService.ConfigPath));
                if (cfg == null) cfg = new Dictionary<string, object>();
                cfg["enableMotionControlApi"] = enabled;
                File.WriteAllText(AiService.ConfigPath, json.Serialize(cfg), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("ApiServer.SetMotionControlEnabled", ex);
            }
        }

        #endregion

        [ThreadStatic] private static DateTime _requestStartUtc;
        [ThreadStatic] private static string _requestBodySnippet;
        [ThreadStatic] private static bool _responseSent;

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static void RaiseRequestCompleted(HttpListenerContext ctx, int statusCode, string responseJson)
        {
            var handler = RequestCompleted;
            if (handler == null) return;
            try
            {
                var elapsedMs = _requestStartUtc == default(DateTime)
                    ? 0
                    : (int)(DateTime.UtcNow - _requestStartUtc).TotalMilliseconds;
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ');
                sb.Append(ctx.Request.HttpMethod).Append(' ').Append(ctx.Request.Url.PathAndQuery);
                if (!string.IsNullOrEmpty(_requestBodySnippet))
                    sb.Append("  >> ").Append(Truncate(_requestBodySnippet, 1200));
                sb.Append("  -> ").Append(statusCode).Append(" (").Append(elapsedMs).Append("ms)  ");
                sb.Append(Truncate(responseJson, 1200));
                handler(sb.ToString());
            }
            catch { }
            finally
            {
                _requestBodySnippet = null;
                _requestStartUtc = default(DateTime);
            }
        }

        // Task 路由需要 AiService 实例（task 状态是实例字段）。
        // 面板创建自己的 AiService 后通过 UseService 注入，保证 HTTP 与聊天面板看到同一份任务状态。
        private static readonly object _taskServiceLock = new object();
        private static volatile AiService _taskService;

        public static void UseService(AiService service)
        {
            if (service == null) return;
            lock (_taskServiceLock) { _taskService = service; }
        }

        private static AiService TaskService
        {
            get
            {
                if (_taskService != null) return _taskService;
                lock (_taskServiceLock)
                {
                    if (_taskService == null) _taskService = new AiService();
                    return _taskService;
                }
            }
        }

        private const string HttpPortConfigKey = "httpPort";
        private const int DefaultHttpPort = 9090;
        private static ApiServer _active;

        /// <summary>读取配置中的 HTTP 端口（无效/未配置时回退 9090）。</summary>
        public static int GetConfiguredPort()
        {
            try
            {
                var path = AiService.ConfigPath;
                if (File.Exists(path))
                {
                    var cfg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    object val;
                    if (cfg != null && cfg.TryGetValue(HttpPortConfigKey, out val) && val != null)
                    {
                        int p;
                        if (int.TryParse(val.ToString(), out p) && p >= 1024 && p <= 65535) return p;
                    }
                }
            }
            catch { }
            return DefaultHttpPort;
        }

        /// <summary>写入 httpPort 配置（合并保留其他键）。</summary>
        public static void SetConfiguredPort(int port)
        {
            try
            {
                Directory.CreateDirectory(AiService.DataDir);
                var json = new JavaScriptSerializer();
                Dictionary<string, object> cfg = null;
                if (File.Exists(AiService.ConfigPath))
                    cfg = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(AiService.ConfigPath));
                if (cfg == null) cfg = new Dictionary<string, object>();
                cfg[HttpPortConfigKey] = port;
                File.WriteAllText(AiService.ConfigPath, json.Serialize(cfg), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("ApiServer.SetConfiguredPort", ex);
            }
        }

        /// <summary>切换端口（供设置面板调用）：失败时尝试恢复原端口。</summary>
        public bool Restart(int port, out string error)
        {
            error = null;
            int old = _port;
            try
            {
                Stop();
                Start(port);
                if (_port != port)
                    error = "端口 " + port + " 不可用，已回退到 " + _port;
                return true;
            }
            catch (Exception ex)
            {
                error = "切换端口失败：" + ex.Message;
                try { Start(old); } catch { }
                return false;
            }
        }

        /// <summary>由设置面板调用，重启当前活动的服务实例。</summary>
        public static bool RestartActive(int port, out string error)
        {
            error = null;
            var active = _active;
            if (active == null)
            {
                error = "HTTP 服务尚未启动";
                return false;
            }
            return active.Restart(port, out error);
        }

        public void Start(int preferredPort = 0)
        {
            _active = this;
            if (preferredPort <= 0) preferredPort = GetConfiguredPort();
            // 优先使用配置/调用方端口，然后按 9090 → 9091 → 9092 → 9093 回退。
            var candidates = new List<int>();
            if (preferredPort > 0) candidates.Add(preferredPort);
            foreach (var p in new[] { 9090, 9091, 9092, 9093 })
                if (!candidates.Contains(p)) candidates.Add(p);

            Exception lastError = null;
            foreach (int port in candidates)
            {
                HttpListener candidate = null;
                try
                {
                    candidate = new HttpListener();
                    candidate.Prefixes.Add("http://localhost:" + port + "/");
                    candidate.Start();
                    // 若 Start 抛异常，上面的对象不会替换 _listener；成功后赋值
                    _listener = candidate;
                    _port = port;
                    _running = true;
                    candidate.BeginGetContext(OnRequest, null);
                    EnsureAuthToken();
                    PlugInLog.Log("ApiServer.Start() OK on port " + port);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { candidate?.Close(); } catch { }
                    PlugInLog.LogException("ApiServer.Start(port " + port + ")", ex);
                }
            }

            throw lastError ?? new InvalidOperationException("ApiServer.Start failed: all candidate ports exhausted");
        }

        public int Port
        {
            get { return _port; }
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        private void OnRequest(IAsyncResult ar)
        {
            if (!_running) return;
            HttpListenerContext ctx;
            try { ctx = _listener.EndGetContext(ar); }
            catch { return; }

            if (_running)
            {
                try { _listener.BeginGetContext(OnRequest, null); }
                catch (Exception ex) { PlugInLog.LogException("ApiServer.BeginGetContext", ex); }
            }

            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("ApiServer.OnRequest", ex);
                TrySendError(ctx, 500, ex.Message);
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            _requestStartUtc = DateTime.UtcNow;
            _requestBodySnippet = null;
            _responseSent = false;
            try
            {
                if (!IsAuthorized(ctx))
                {
                    SendJson(ctx, 401, new { success = false, error = "unauthorized" });
                    return;
                }
                HandleRequestCore(ctx);

                // 兜底：未匹配任何分支且未写响应的请求不能永久挂起。
                if (!_responseSent)
                    SendJson(ctx, 404, new { success = false, error = "Not found" });
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("ApiServer.HandleRequest", ex);
                TrySendError(ctx, 500, ex.Message);
            }
        }

        // 统一错误包装：返回 {success:false, error:"..."} 用于 MP 特有/不支持路由
        private static object NotSupported(string feature)
        {
            return new { success = false, error = feature + " not supported on RTSys" };
        }

        // 安全调用 handler，捕获异常并记录日志，返回 {success:false, error:...}
        private object InvokeHandler(Func<object> handler)
        {
            try
            {
                return handler();
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("ApiServer.InvokeHandler", ex);
                return new { success = false, error = ex.Message };
            }
        }

        // 安全调用带 body 的 handler
        private object InvokeHandler(Func<Dictionary<string, object>, object> handler, Dictionary<string, object> body)
        {
            try
            {
                return handler(body);
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("ApiServer.InvokeHandler(body)", ex);
                return new { success = false, error = ex.Message };
            }
        }

        // 检查 RTSysIP 是否已配置
        private static bool IsControllerConfigured()
        {
            try
            {
                if (System.IO.File.Exists(AiService.ConfigPath))
                {
                    var text = System.IO.File.ReadAllText(AiService.ConfigPath);
                    var cfg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                    object val;
                    if (cfg?.TryGetValue("RTSysIP", out val) == true && val != null)
                    {
                        var ip = val.ToString().Trim();
                        return !string.IsNullOrEmpty(ip);
                    }
                }
            }
            catch { }
            return false;
        }

        private void HandleProgramsRoute(HttpListenerContext ctx, string method, string[] segments, string[] rawSegments)
        {
            if (segments.Length == 2)
            {
                if (method == "GET")
                    HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListPrograms));
                else if (method == "POST")
                    HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.CreateProgram(b), body));
            }
            else if (segments.Length >= 3)
            {
                var name = Uri.UnescapeDataString(rawSegments[2]);
                if (segments.Length == 3)
                {
                    if (method == "DELETE")
                        HandleMethod(ctx, method, "DELETE", () => InvokeHandler(() => RTSysHandlers.DeleteProgram(name)));
                    else if (method == "PUT")
                        HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.RenameProgram(name, b), body));
                }
                else if (segments.Length == 4)
                {
                    var action = segments[3];
                    switch (action)
                    {
                        case "source":
                            if (method == "GET")
                                HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadSource(name)));
                            else if (method == "PUT")
                                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.WriteSource(name, b), body));
                            else if (method == "PATCH")
                                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.PatchSource(name, b), body));
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                            break;
                        case "open":
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(() => RTSysHandlers.OpenProgram(name)));
                            break;
                        case "rename":
                            if (method == "PUT")
                                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.RenameProgram(name, b), body));
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                            break;
                        case "copy":
                            if (method != "POST")
                            {
                                SendJson(ctx, 405, new { success = false, error = "Method not allowed" });
                                break;
                            }
                            HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.CopyProgram(name, b), body));
                            break;
                        case "upload":
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(() => RTSysHandlers.Upload(name)));
                            break;
                        case "download":
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(() => RTSysHandlers.Download(name)));
                            break;
                        case "compile":
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(() => RTSysHandlers.CompileProgram(name)));
                            break;
                        case "run":
                            HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.RunProgram(name, b), body));
                            break;
                        case "stop":
                            HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.StopProgram(name, b), body));
                            break;
                        case "process":
                            if (method == "GET")
                                HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.GetProgramProcess(name)));
                            else if (method == "PUT")
                                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.SetProgramProcess(name, b), body));
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                            break;
                        case "breakpoints":
                            if (method == "GET")
                                HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ListBreakpoints(name)));
                            else if (method == "DELETE")
                                HandleMethod(ctx, method, "DELETE", () => InvokeHandler(() => RTSysHandlers.ClearAllBreakpoints(name)));
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                            break;
                        case "breakpoint":
                            if (method == "POST" || method == "PUT")
                                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.SetBreakpoint(name, b), body));
                            else if (method == "DELETE")
                                HandleBody(ctx, method, body =>
                                {
                                    if (!body.ContainsKey("line"))
                                        return NotSupported("Breakpoints");
                                    return RTSysHandlers.SetBreakpoint(name,
                                        new Dictionary<string, object> { { "line", body["line"] }, { "enable", false } });
                                });
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                            break;
                        default:
                            SendJson(ctx, 404, new { error = "Not found" });
                            break;
                    }
                }
            }
        }

        private void HandleDriveRoute(HttpListenerContext ctx, string method, string[] segments, string[] rawSegments)
        {
            if (segments.Length == 4 && method == "GET")
            {
                int drvAxis, drvAddr;
                if (!int.TryParse(segments[2], out drvAxis))
                {
                    SendJson(ctx, 400, new { error = "Invalid axis" });
                    return;
                }
                if (!TryParseAddr(segments[3], out drvAddr))
                {
                    SendJson(ctx, 400, new { error = "Invalid address" });
                    return;
                }
                HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadDriveParam(drvAxis, drvAddr)));
            }
            else if (segments.Length == 4 && method == "PUT")
            {
                int drvAxis, drvAddr;
                if (!int.TryParse(segments[2], out drvAxis))
                {
                    SendJson(ctx, 400, new { error = "Invalid axis" });
                    return;
                }
                if (!TryParseAddr(segments[3], out drvAddr))
                {
                    SendJson(ctx, 400, new { error = "Invalid address" });
                    return;
                }
                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.WriteDriveParam(drvAxis, drvAddr, b), body));
            }
            else
                SendJson(ctx, 404, new { error = "Not found" });
        }

        private void HandleEthercatRoute(HttpListenerContext ctx, string method, string[] segments, string[] rawSegments)
        {
            if (segments.Length == 3 && segments[2] == "devices" && method == "GET")
            {
                int slot = 0;
                var slotStr = ctx.Request.QueryString["slot"];
                if (!string.IsNullOrEmpty(slotStr)) int.TryParse(slotStr, out slot);
                HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ScanEtherCAT(slot)));
            }
            else if (segments.Length == 3 && segments[2] == "sdo")
            {
                if (method == "GET")
                {
                    int ecSlot = 0; uint pos = 0, idx = 0, sub = 0;
                    int.TryParse(ctx.Request.QueryString["slot"] ?? "0", out ecSlot);
                    uint.TryParse(ctx.Request.QueryString["position"] ?? "0", out pos);
                    uint.TryParse(ctx.Request.QueryString["index"] ?? "0",
                                  System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out idx);
                    uint.TryParse(ctx.Request.QueryString["subindex"] ?? "0", out sub);
                    var t = ctx.Request.QueryString["type"] ?? "uint16";
                    HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.EtherCATReadSDO(ecSlot, pos, idx, sub, t)));
                }
                else if (method == "PUT")
                    HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.EtherCATWriteSDOFromDict(b), body));
                else
                    SendJson(ctx, 405, new { error = "Method not allowed" });
            }
            else
                SendJson(ctx, 404, new { error = "Not found" });
        }

        private void HandleIoRoute(HttpListenerContext ctx, string method, string[] segments, string[] rawSegments)
        {
            if (segments.Length == 3)
            {
                if (segments[2] == "digital" && method == "GET")
                    HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListDigitalIO));
                else if (segments[2] == "analogue" && method == "GET")
                    HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListAnalogueIO));
                else
                    SendJson(ctx, 404, new { error = "Not found" });
            }
            else if (segments.Length == 5)
            {
                int ioIndex;
                if (!int.TryParse(segments[4], out ioIndex))
                {
                    SendJson(ctx, 400, new { error = "Invalid IO index" });
                    return;
                }
                if (segments[2] == "digital")
                {
                    if (segments[3] != "line") { SendJson(ctx, 404, new { error = "Not found" }); return; }
                    if (method == "GET")
                        HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadDigitalIO(ioIndex)));
                    else if (method == "PUT")
                        HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.WriteDigitalIO(ioIndex, b), body));
                    else
                        SendJson(ctx, 405, new { error = "Method not allowed" });
                }
                else if (segments[2] == "analogue")
                {
                    if (segments[3] != "line") { SendJson(ctx, 404, new { error = "Not found" }); return; }
                    if (method == "GET")
                        HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadAnalogueIO(ioIndex)));
                    else if (method == "PUT")
                        HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.WriteAnalogueIO(ioIndex, b), body));
                    else
                        SendJson(ctx, 405, new { error = "Method not allowed" });
                }
                else
                    SendJson(ctx, 404, new { error = "Not found" });
            }
            else
                SendJson(ctx, 404, new { error = "Not found" });
        }

        private void HandleTableRoute(HttpListenerContext ctx, string method, string[] segments, string[] rawSegments)
        {
            if (segments.Length >= 3)
            {
                int addr;
                if (!int.TryParse(segments[2], out addr))
                {
                    SendJson(ctx, 400, new { error = "Invalid address" });
                    return;
                }
                if (method == "GET")
                {
                    var countStr = ctx.Request.QueryString["count"] ?? "1";
                    int count;
                    int.TryParse(countStr, out count);
                    if (count <= 0) count = 1;
                    HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadTable(addr, count)));
                }
                else if (method == "PUT")
                    HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.WriteTable(addr, b), body));
            }
        }

        private void HandleVrRoute(HttpListenerContext ctx, string method, string[] segments, string[] rawSegments)
        {
            if (segments.Length >= 3)
            {
                int addr;
                if (!int.TryParse(segments[2], out addr))
                {
                    SendJson(ctx, 400, new { error = "Invalid address" });
                    return;
                }
                if (method == "GET")
                {
                    var countStr = ctx.Request.QueryString["count"] ?? "1";
                    int count;
                    int.TryParse(countStr, out count);
                    if (count <= 0) count = 1;
                    HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadVR(addr, count)));
                }
                else if (method == "PUT")
                    HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.WriteVR(addr, b), body));
            }
        }

        private void HandleRequestCore(HttpListenerContext ctx)
        {
            // 路由匹配用小写路径；原始路径保留大小写，供程序名/技能名等参数使用。
            var rawPath = ctx.Request.Url.AbsolutePath.Trim('/');
            var path = rawPath.ToLowerInvariant();
            var method = ctx.Request.HttpMethod;

            // 不再提供 CORS 预检；浏览器跨源请求无法通过鉴权。
            if (method == "OPTIONS")
            {
                SendJson(ctx, 405, new { success = false, error = "Method not allowed" });
                return;
            }

            // Route matching
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var rawSegments = rawPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            // 运动控制类端点：默认关闭 + 一次性 UI nonce。
            if (IsMotionControlRequest(segments, method))
            {
                if (!IsMotionControlEnabled())
                {
                    SendJson(ctx, 403, new { success = false, error = "motion-control API is disabled (enable it in the plugin panel)" });
                    return;
                }
                if (!ConsumeSensitiveNonce(ctx.Request.Headers["X-Motion-Nonce"]))
                {
                    SendJson(ctx, 403, new { success = false, error = "motion-control request requires a one-time UI nonce (X-Motion-Nonce)" });
                    return;
                }
            }

            if (segments.Length == 0)
            {
                // 根路径：返回版本信息 + controllerConfigured
                var configured = IsControllerConfigured();
                SendJson(ctx, 200, new
                {
                    status = "ok",
                    version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                    controllerConfigured = configured,
                    rtsysIp = configured ? "configured" : "not configured"
                });
                return;
            }

            // /api/...
            if (segments.Length >= 1 && segments[0] == "api")
            {
                var route = segments.Length >= 2 ? segments[1] : "";

                switch (route)
                {
                    case "identity":
                        SendJson(ctx, 200, new
                        {
                            success = true,
                            protocol = "triosys-ai",
                            pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                            port = _port,
                            version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()
                        });
                        break;

                    case "status":
                        HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.GetStatus));
                        break;

                    case "project":
                        if (method == "POST" && segments.Length == 2)
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(() => RTSysHandlers.OpenProject(null)));
                        else if (method == "PUT" && segments.Length == 2)
                            HandleMethod(ctx, method, "PUT", () => InvokeHandler(RTSysHandlers.SaveProject));
                        else if (method == "POST" && segments.Length == 3 && segments[2] == "open")
                            HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.OpenProject(b), body));
                        else if (method == "GET" && segments.Length == 3 && segments[2] == "items")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListProjectItems));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "programs":
                        HandleProgramsRoute(ctx, method, segments, rawSegments);
                        break;

                    case "backups":
                        if (method == "GET" && segments.Length == 2)
                        {
                            SendJson(ctx, 200, ProgramBackup.ListBackups(ctx.Request.QueryString["name"]));
                        }
                        else if (method == "POST" && segments.Length == 4 && segments[3] == "restore")
                        {
                            var backupFile = Uri.UnescapeDataString(rawSegments[2]);
                            HandleBody(ctx, method, body =>
                            {
                                string target = null;
                                if (body != null && body.ContainsKey("name") && body["name"] != null)
                                    target = body["name"].ToString();
                                return InvokeHandler(() => ProgramBackup.RestoreBackup(backupFile, target));
                            });
                        }
                        else
                        {
                            SendJson(ctx, 404, new { error = "Not found" });
                        }
                        break;

                    case "vr":
                        HandleVrRoute(ctx, method, segments, rawSegments);
                        break;

                    case "table":
                        HandleTableRoute(ctx, method, segments, rawSegments);
                        break;

                    case "descriptors":
                        HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListDescriptors));
                        break;

                    case "axes":
                        if (segments.Length == 2)
                        {
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListAxes));
                        }
                        else if (segments.Length == 3)
                        {
                            int axisIndex;
                            if (!int.TryParse(segments[2], out axisIndex))
                            {
                                SendJson(ctx, 400, new { error = "Invalid axis index" });
                                return;
                            }
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.GetAxisDetail(axisIndex)));
                        }
                        else
                        {
                            SendJson(ctx, 404, new { error = "Not found" });
                        }
                        break;

                    case "sysvars":
                        if (segments.Length == 2)
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => NotSupported("System variables")));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "sysvar":
                        if (segments.Length == 3)
                        {
                            var svName = Uri.UnescapeDataString(rawSegments[2]);
                            if (method == "GET")
                                HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadSysVar(svName)));
                            else if (method == "PUT")
                                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.WriteSysVar(svName, b), body));
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "io":
                        HandleIoRoute(ctx, method, segments, rawSegments);
                        break;

                    case "plugins":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListAttachedPlugins));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "oscilloscope":
                        if (segments.Length == 3 && segments[2] == "open" && method == "POST")
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(() => NotSupported("Oscilloscope")));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "robots":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListRobots));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "recipes":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListRecipes));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "alarms":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListAlarms));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "remote-devices":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListRemoteDevices));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "msbus":
                        if (segments.Length == 3 && segments[2] == "scan" && method == "GET")
                        {
                            int slot = 0;
                            var slotStr = ctx.Request.QueryString["slot"];
                            if (!string.IsNullOrEmpty(slotStr)) int.TryParse(slotStr, out slot);
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ScanMsBus(slot)));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "ethercat":
                        HandleEthercatRoute(ctx, method, segments, rawSegments);
                        break;

                    case "drive":
                        HandleDriveRoute(ctx, method, segments, rawSegments);
                        break;

                    case "controller":
                        // 控制器连接控制路由（原 Adapter 副本独有，合并到 Worker 后删除死代码）
                        if (segments.Length == 3 && segments[2] == "autoconnect" && method == "POST")
                            HandleBody(ctx, method, body => InvokeHandler(_ => (object)RTSysHandlers.TriggerAutoConnectAsync().GetAwaiter().GetResult(), body));
                        else if (segments.Length == 3 && segments[2] == "simulator" && method == "POST")
                            HandleBody(ctx, method, body => InvokeHandler(_ => (object)RTSysHandlers.TriggerSimulatorConnectAsync().GetAwaiter().GetResult(), body));
                        else if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => new
                            {
                                connectionState = RTSysHandlers.GetConnectionState().ToString(),
                                currentMode = RTSysHandlers.GetCurrentConnectMode(),
                                cachedIp = RTSysHandlers.GetCachedIp(),
                                connected = RTSysHandlers.GetConnectionState() == RTSysHandlers.ControllerConnectionState.Connected
                            }));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "chat":
                        HandleMethod(ctx, method, "POST", () => InvokeHandler(() => NotSupported("Open chat window")));
                        break;

                    case "validate_basic":
                        if (method != "POST") { SendJson(ctx, 405, new { error = "Method not allowed" }); return; }
                        HandleBody(ctx, method, body =>
                        {
                            object v;
                            string code = body.TryGetValue("code", out v) && v != null ? v.ToString() : "";
                            return InvokeHandler(() => AiService.ValidateTrioBasicCodePublic(code));
                        });
                        break;

                    case "search_code":
                        if (method != "GET") { SendJson(ctx, 405, new { error = "Method not allowed" }); return; }
                        {
                            var queryStr = ctx.Request.QueryString["query"] ?? "";
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.SearchCode(queryStr)));
                        }
                        break;

                    case "events":
                        if (method != "GET") { SendJson(ctx, 405, new { error = "Method not allowed" }); return; }
                        long sinceTicks = 0;
                        var sinceStr = ctx.Request.QueryString["since"];
                        if (!string.IsNullOrEmpty(sinceStr))
                            long.TryParse(sinceStr, out sinceTicks);
                        HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.GetEvents(sinceTicks)));
                        break;

                    case "processes":
                        if (segments.Length == 2 && method == "GET")
                        {
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListProcesses));
                        }
                        else if (segments.Length == 4 && segments[3] == "variables" && method == "GET")
                        {
                            int pid;
                            if (!int.TryParse(segments[2], out pid))
                            {
                                SendJson(ctx, 400, new { error = "Invalid pid" });
                                return;
                            }
                            var variable = ctx.Request.QueryString["name"];
                            var program = ctx.Request.QueryString["program"];
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.GetProcessVariable(pid, program, variable)));
                        }
                        else
                        {
                            SendJson(ctx, 404, new { error = "Not found" });
                        }
                        break;

                    // ===== 新增 RTSys 特有路由 (AI 需要) =====
                    case "skills":
                        if (segments.Length == 2 && method == "GET")
                        {
                            var category = ctx.Request.QueryString["category"] ?? "";
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => AiService.DiscoverSkills(category)));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "skill":
                        if (segments.Length == 3 && method == "GET")
                        {
                            var skillName = Uri.UnescapeDataString(segments[2]);
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => AiService.ReadSkill(skillName)));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "lookup":
                        if (segments.Length == 2 && method == "GET")
                        {
                            var query = ctx.Request.QueryString["query"] ?? "";
                            var fullFlag = ctx.Request.QueryString["full"] ?? "";
                            var library = ctx.Request.QueryString["library"] ?? "";
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => AiService.LookupCommand(query, fullFlag, library)));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "tasks":
                        if (segments.Length == 2 && method == "GET")
                        {
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(TaskService.TaskList));
                        }
                        else if (segments.Length == 2 && method == "POST")
                        {
                            HandleBody(ctx, method, body => InvokeHandler(b => TaskService.TaskCreate(
                                GetString(b, "subject"),
                                GetString(b, "description")), body));
                        }
                        else if (segments.Length == 2 && method == "PUT")
                        {
                            HandleBody(ctx, method, body => InvokeHandler(b => TaskService.TaskUpdate(
                                GetInt(b, "id", 0),
                                GetString(b, "status"),
                                GetString(b, "subject"),
                                GetString(b, "description")), body));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "memory":
                        if (segments.Length == 2 && method == "GET")
                        {
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() =>
                            {
                                var mem = AiService.GetMemoryText();
                                return new { content = mem };
                            }));
                        }
                        else if (segments.Length == 2 && method == "POST")
                        {
                            // 持久记忆会进入系统提示词：要求 UI 一次性 nonce + 32KB 上限。
                            if (!ConsumeSensitiveNonce(ctx.Request.Headers["X-Confirm-Nonce"]))
                            {
                                SendJson(ctx, 403, new { success = false, error = "memory update requires a one-time UI nonce (X-Confirm-Nonce)" });
                                return;
                            }
                            HandleBody(ctx, method, body => InvokeHandler(b =>
                            {
                                var content = GetString(b, "content");
                                if (string.IsNullOrEmpty(content))
                                    return new { success = false, error = "content is required" };
                                if (Encoding.UTF8.GetByteCount(content) > 32 * 1024)
                                    return new { success = false, error = "content too large (max 32KB)" };
                                AiService.SaveMemory(content);
                                return new { success = true, message = "Memory updated successfully." };
                            }, body));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    default:
                        SendJson(ctx, 404, new { error = "Not found" });
                        break;
                }
            }
            else
            {
                SendJson(ctx, 404, new { error = "Not found" });
            }
        }

        private void HandleMethod(HttpListenerContext ctx, string actual, string expected, Func<object> handler)
        {
            if (actual != expected)
            {
                SendJson(ctx, 405, new { error = "Method not allowed" });
                return;
            }
            var result = handler();
            SendJson(ctx, 200, result);
        }

        private static bool TryParseAddr(string s, out int value)
        {
            // Accept "0x4000", "0X4000", "4000" (hex), or decimal "16384"
            if (string.IsNullOrEmpty(s)) { value = 0; return false; }
            var str = s.Trim();
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(str.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out value);
            // No prefix: try decimal first, then hex (for bare hex like "4000" interpreted as hex per TrioBASIC convention)
            if (int.TryParse(str, out value)) return true;
            return int.TryParse(str, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private const int MaxRequestBodyBytes = 2 * 1024 * 1024;

        private void HandleBody(HttpListenerContext ctx, string method, Func<Dictionary<string, object>, object> handler)
        {
            try
            {
                long declaredLength = ctx.Request.ContentLength64;
                if (declaredLength > MaxRequestBodyBytes)
                {
                    SendJson(ctx, 413, new { success = false, error = "Request body too large (max 2MB)" });
                    return;
                }
                var contentType = ctx.Request.ContentType;
                bool hasBody = declaredLength != 0;
                if (hasBody && (contentType == null ||
                                !contentType.TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
                {
                    SendJson(ctx, 415, new { success = false, error = "Content-Type must be application/json" });
                    return;
                }

                string bodyText;
                // application/json 默认 UTF-8；HttpListener 在 Content-Type 未带 charset 时回退到系统 ANSI(GBK)，
                // 会把中文源码解坏并导致 JavaScriptSerializer 报 500。
                var bodyEncoding = Encoding.UTF8;
                if (contentType != null && contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase) >= 0)
                    bodyEncoding = ctx.Request.ContentEncoding;
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, bodyEncoding))
                {
                    var sb = new StringBuilder();
                    var buffer = new char[8192];
                    int read;
                    int total = 0;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxRequestBodyBytes)
                        {
                            SendJson(ctx, 413, new { success = false, error = "Request body too large (max 2MB)" });
                            return;
                        }
                        sb.Append(buffer, 0, read);
                    }
                    bodyText = sb.ToString();
                }
                _requestBodySnippet = bodyText;

                var body = string.IsNullOrEmpty(bodyText) ? new Dictionary<string, object>() : _json.Deserialize<Dictionary<string, object>>(bodyText);
                var result = handler(body);
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("ApiServer.HandleBody", ex);
                TrySendError(ctx, 500, ex.Message);
            }
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null) return val.ToString();
            return null;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int defaultValue = 0)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result))
                    return result;
            }
            return defaultValue;
        }

        private void SendJson(HttpListenerContext ctx, int statusCode, object data)
        {
            _responseSent = true;
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var json = JsonText.UnescapeApostrophes(_json.Serialize(data));
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
            RaiseRequestCompleted(ctx, statusCode, json);
        }

        private void TrySendError(HttpListenerContext ctx, int statusCode, string message)
        {
            try
            {
                // 异常详情只写本地日志；对外返回稳定错误码，避免泄露路径/内部实现。
                PlugInLog.Log("ApiServer error " + statusCode + ": " + message);
                SendJson(ctx, statusCode, new { success = false, error = "internal error", code = statusCode });
            }
            catch { /* response may already be closed; best-effort error response */ }
        }

        public void Dispose() => Stop();
    }
}