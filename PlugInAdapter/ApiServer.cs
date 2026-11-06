using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

        public void Start()
        {
            // 端口回退：9090 → 9091 → 9092 → 9093 → 临时端口，全部占用时抛异常
            int[] candidates = new int[] { 9090, 9091, 9092, 9093 };
            Exception lastError = null;
            foreach (int port in candidates)
            {
                try
                {
                    var candidate = new HttpListener();
                    candidate.Prefixes.Add("http://localhost:" + port + "/");
                    candidate.Start();
                    // 若 Start 抛异常，上面的对象不会替换 _listener；成功后赋值
                    _listener = candidate;
                    _port = port;
                    _running = true;
                    candidate.BeginGetContext(OnRequest, null);
                    PlugInLog.Log("ApiServer.Start() OK on port " + port);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
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
            try
            {
                HandleRequestCore(ctx);
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
                    var cfg = _json.Deserialize<Dictionary<string, object>>(text);
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

        private void HandleRequestCore(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.Trim('/').ToLowerInvariant();
            var method = ctx.Request.HttpMethod;

            // Handle CORS preflight
            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                ctx.Response.Close();
                return;
            }

            // Route matching
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

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
                    case "status":
                        HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.GetStatus));
                        break;

                    case "test":
                        HandleMethod(ctx, method, "GET", () => InvokeHandler(() =>
                        {
                            var (pass, fail, report) = AiService.RunOptimizationTests();
                            return new { pass, fail, report };
                        }));
                        break;

                    case "project":
                        if (method == "POST" && segments.Length == 2)
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(RTSysHandlers.OpenProject));
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
                        if (segments.Length == 2)
                        {
                            if (method == "GET")
                                HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListPrograms));
                            else if (method == "POST")
                                HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.CreateProgram(b), body));
                        }
                        else if (segments.Length >= 3)
                        {
                            var name = Uri.UnescapeDataString(segments[2]);
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
                        break;

                    case "vr":
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
                        break;

                    case "table":
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
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.GetSystemVariables));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "sysvar":
                        if (segments.Length == 3)
                        {
                            var svName = Uri.UnescapeDataString(segments[2]);
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
                        break;

                    case "plugins":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(RTSysHandlers.ListAttachedPlugins));
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "oscilloscope":
                        if (segments.Length == 3 && segments[2] == "open" && method == "POST")
                            HandleMethod(ctx, method, "POST", () => InvokeHandler(RTSysHandlers.OpenOscilloscope));
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
                        break;

                    case "drive":
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
                            var ndStr = ctx.Request.QueryString["nd"];
                            int nd = 4;
                            if (!string.IsNullOrEmpty(ndStr)) int.TryParse(ndStr, out nd);
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(() => RTSysHandlers.ReadDriveParam(drvAxis, drvAddr, nd)));
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
                        break;

                    case "chat":
                        HandleMethod(ctx, method, "POST", () => InvokeHandler(RTSysHandlers.OpenChat));
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
                            var csStr = ctx.Request.QueryString["caseSensitive"];
                            bool cs;
                            bool.TryParse(csStr, out cs);
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
                            HandleMethod(ctx, method, "GET", () => InvokeHandler(AiService.TaskList));
                        }
                        else if (segments.Length == 2 && method == "POST")
                        {
                            HandleBody(ctx, method, body => InvokeHandler(b => AiService.TaskCreate(
                                GetString(b, "subject"),
                                GetString(b, "description")), body));
                        }
                        else if (segments.Length == 2 && method == "PUT")
                        {
                            HandleBody(ctx, method, body => InvokeHandler(b => AiService.TaskUpdate(
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
                            HandleBody(ctx, method, body => InvokeHandler(b =>
                            {
                                var content = GetString(b, "content");
                                if (string.IsNullOrEmpty(content))
                                    return new { success = false, error = "content is required" };
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

        private void HandleBody(HttpListenerContext ctx, string method, Func<Dictionary<string, object>, object> handler)
        {
            try
            {
                string bodyText;
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    bodyText = reader.ReadToEnd();

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
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            var bytes = Encoding.UTF8.GetBytes(_json.Serialize(data));
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private void TrySendError(HttpListenerContext ctx, int statusCode, string message)
        {
            try
            {
                SendJson(ctx, statusCode, new { error = message });
            }
            catch { /* response may already be closed; best-effort error response */ }
        }

        public void Dispose() => Stop();
    }
}