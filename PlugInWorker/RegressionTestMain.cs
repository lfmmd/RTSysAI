using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn.Tests
{
    /// <summary>
    /// Regression test for list_programs, get_status, list_project_items tools.
    /// Tests both the handler implementations and their registration in AiTools.
    ///
    /// 新增：验证控制器未配置 IP 时的 graceful degradation 行为：
    ///   - 非控制器工具（lookup_command / read_skill / discover_skills / task_* 等）无 IP 也能正常工作；
    ///   - 控制器依赖工具（list_programs / list_project_items / read_source / ...）无 IP 时返回结构化错误（非异常、非原始英文堆栈），
    ///     包含 connectionState="NotConfigured"、requiresController=true、中文提示；
    ///   - get_status 无 IP 时仍成功返回，携带 connectionState="NotConfigured"，AI 可据此询问用户 IP。
    /// </summary>
    internal static class RegressionTestMain
    {
        private static int _passed = 0;
        private static int _failed = 0;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private static void Assert(bool condition, string testName, string detail = "")
        {
            if (condition)
            {
                Console.WriteLine("  [PASS] " + testName);
                _passed++;
            }
            else
            {
                Console.WriteLine("  [FAIL] " + testName + " - " + detail);
                _failed++;
            }
        }

        private static void AssertEqual(object expected, object actual, string testName)
        {
            string expStr = _json.Serialize(expected);
            string actStr = _json.Serialize(actual);
            Assert(expStr == actStr, testName, "expected: " + expStr + ", actual: " + actStr);
        }

        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--sim", StringComparison.OrdinalIgnoreCase))
                return RunSimulatorTests();

            if (args != null && args.Length > 0 && string.Equals(args[0], "--matrix", StringComparison.OrdinalIgnoreCase))
                return RunToolMatrix(args.Length > 1 ? args[1] : "offline");

            Console.WriteLine("=== RTSys Plugin Regression Test ===");
            Console.WriteLine("Started: " + DateTime.Now);
            Console.WriteLine();

            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI");
            Directory.CreateDirectory(dataDir);

            string originalConfig = null;
            var configPath = Path.Combine(dataDir, "config.json");
            if (File.Exists(configPath))
            {
                originalConfig = File.ReadAllText(configPath);
            }

            try
            {
                // 测试基线：组 3/4/5/8 假定“未配置控制器 IP”。真实配置可能已填 IP，
                // 这里临时清空（finally 恢复完整原始配置），保证测试可重复。
                if (File.Exists(configPath))
                {
                    try
                    {
                        var baseline = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
                        if (baseline != null)
                        {
                            bool changed = false;
                            if (baseline.ContainsKey("RTSysIP")) { baseline["RTSysIP"] = ""; changed = true; }
                            // 测试隔离：绝不能写用户的真实 projectDir（否则 write_source 会写进真实工程）
                            if (baseline.ContainsKey("projectDir")) { baseline["projectDir"] = ""; changed = true; }
                            if (changed) File.WriteAllText(configPath, _json.Serialize(baseline));
                        }
                    }
                    catch { /* 配置解析失败则保持原样 */ }
                }
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();

                Console.WriteLine("Test Group 1: RTSysHandlers Method Signatures");
                TestRTSysHandlersSignatures();
                Console.WriteLine();

                Console.WriteLine("Test Group 2: AiTools Tool Registration");
                TestAiToolsRegistration();
                Console.WriteLine();

                Console.WriteLine("Test Group 3: Tool Execution (no controller configured)");
                TestToolExecution_NoController();
                Console.WriteLine();

                Console.WriteLine("Test Group 4: Non-Controller Tools Succeed Without IP");
                TestNonControllerToolsNoIp();
                Console.WriteLine();

                Console.WriteLine("Test Group 5: Connection State Helper");
                TestConnectionStateHelper();
                Console.WriteLine();

                Console.WriteLine("Test Group 6: Handler Response Shapes");
                TestHandlerResponseShapes();
                Console.WriteLine();

                Console.WriteLine("Test Group 7: PureIoTools Classification");
                TestPureIoToolsClassification();
                Console.WriteLine();

                Console.WriteLine("Test Group 8: Offline Local Program Cache (no controller IP)");
                TestOfflineLocalProgramCache();
                Console.WriteLine();

                Console.WriteLine("Test Group 9: Chat Path Must Not Block On Controller Discovery");
                TestChatPathNonBlocking();
                Console.WriteLine();

                Console.WriteLine("Test Group 10: Project Directory Programs (GBK)");
                TestProjectDirectorySupport();
                Console.WriteLine();

                Console.WriteLine("Test Group 11: Project Program CRUD (offline)");
                TestProjectProgramCrud();
                Console.WriteLine();

                Console.WriteLine("Test Group 12: P0 Hardening (path sandbox / rename / token / validate_basic)");
                TestP0Hardening();
                Console.WriteLine();

                Console.WriteLine("Test Group 13: HMI File Reader (offline)");
                TestHmiFileParser();
                Console.WriteLine();

            }
            finally
            {
                if (originalConfig != null)
                {
                    File.WriteAllText(configPath, originalConfig);
                }
                else if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
            }

            Console.WriteLine("=== Summary: " + _passed + " passed, " + _failed + " failed ===");
            return _failed > 0 ? 1 : 0;
        }

        /// <summary>P0 加固回归：路径沙箱、重命名校验、令牌比较、RTBasic 校验。</summary>
        private static void TestP0Hardening()
        {
            var root = Path.Combine(Path.GetTempPath(), "rtsys_p0_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var inside = Path.Combine(Path.GetFullPath(root), "a.bas");
                AssertEqual(inside, ProgramWorkspace.ResolveInside(root, "a.bas"), "ResolveInside 允许目录内相对路径");
                Assert(ProgramWorkspace.ResolveInside(root, @"..\evil.bas") == null, "ResolveInside 拒绝 .. 逃逸");
                Assert(ProgramWorkspace.ResolveInside(root, @"sub\..\..\evil.bas") == null, "ResolveInside 拒绝嵌套 .. 逃逸");
                Assert(ProgramWorkspace.ResolveInside(root, @"C:\Windows\evil.bas") == null, "ResolveInside 拒绝绝对路径");
                Assert(ProgramWorkspace.ResolveInside(root, @"\\server\share\evil.bas") == null, "ResolveInside 拒绝 UNC");
                Assert(ProgramWorkspace.ResolveInside(root, "a:b.bas") == null, "ResolveInside 拒绝冒号/备用数据流");

                Assert(ProgramWorkspace.ValidateProgramName(@"..\evil") != null, "ValidateProgramName 拒绝 ..");
                Assert(ProgramWorkspace.ValidateProgramName(@"C:\evil") != null, "ValidateProgramName 拒绝绝对路径");
                Assert(ProgramWorkspace.ValidateProgramName("GoodName") == null, "ValidateProgramName 接受普通名称");

                Assert(ApiServer.FixedTimeEquals("abc123", "abc123"), "令牌常量时间比较：相同");
                Assert(!ApiServer.FixedTimeEquals("abc123", "abc124"), "令牌常量时间比较：不同");

                var validation = AiService.ValidateTrioBasicCodePublic("MOVE(1,2)");
                var json = _json.Serialize(validation);
                Assert(json.Contains("\"ok\":true"), "validate_basic 接受 MOVE(1,2)", json);

                // Q12：手写 JSON 序列化必须与区域设置无关，且转义控制字符
                var serializeRequest = typeof(AiService).GetMethod("SerializeRequest", BindingFlags.NonPublic | BindingFlags.Static);
                var prevCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
                try
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                    var payload = new Dictionary<string, object>
                    {
                        { "n", 0.5 },
                        { "s", "a\u0001b\bc" }
                    };
                    var reqJson = (string)serializeRequest.Invoke(null, new object[] { payload });
                    Assert(reqJson.Contains("0.5") && !reqJson.Contains("0,5"),
                        "SerializeRequest 数字使用 InvariantCulture", reqJson);
                    Assert(reqJson.Contains("\\u0001") && reqJson.Contains("\\b"),
                        "SerializeRequest 转义控制字符", reqJson);
                }
                finally
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = prevCulture;
                }
                Assert(AiService.ContextTokenBudget == 100_000, "上下文预算统一为 100K tokens");

                // InitializeSkills 在已初始化时不得清空用户 skills 目录
                var skillsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI", "skills");
                var marker = Path.Combine(skillsDir, "_p0_marker.txt");
                try
                {
                    Directory.CreateDirectory(skillsDir);
                    File.WriteAllText(marker, "marker");
                    var svc = new AiService();
                    var initError = svc.InitializeSkills();
                    Assert(initError == null && File.Exists(marker),
                        "InitializeSkills 已初始化时不覆盖用户 skills 目录", "err=" + initError);
                }
                finally
                {
                    try { File.Delete(marker); } catch { }
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>
        /// HMI 只读解析：构造一个最小 .hmi（版本/魔数/窗口/按钮/文本/结束标记），
        /// 验证解码、对象分段、id 标志位掩码、GBK 文本与非 HMI 文件拒绝。
        /// </summary>
        private static void TestHmiFileParser()
        {
            var sample = BuildHmiSample();
            var parsed = HmiFile.Parse(sample, "Test.hmi", sample.Length);

            Assert((bool)parsed["success"], "HMI parse success");
            Assert((int)parsed["windowCount"] == 1, "HMI windowCount == 1", "actual: " + parsed["windowCount"]);
            Assert((int)parsed["objectCount"] == 2, "HMI objectCount == 2", "actual: " + parsed["objectCount"]);

            var windows = (List<object>)parsed["windows"];
            var win = (Dictionary<string, object>)windows[0];
            Assert((string)win["name"] == "主界面", "HMI window name decoded (GBK)", "actual: " + win["name"]);
            Assert((int)win["width"] == 800 && (int)win["height"] == 480, "HMI window geometry");

            var controls = (List<object>)win["controls"];
            Assert(controls.Count == 1, "HMI control count == 1", "actual: " + controls.Count);
            var ctl = (Dictionary<string, object>)controls[0];
            Assert((string)ctl["typeName"] == "Button", "HMI control typeName == Button", "actual: " + ctl["typeName"]);
            Assert((int)ctl["id"] == 2, "HMI control id flag masked", "actual: " + ctl["id"]);
            Assert((int)ctl["x"] == 10 && (int)ctl["y"] == 20, "HMI control geometry");
            var texts = (List<string>)ctl["texts"];
            Assert(texts.Count == 1 && texts[0] == "Start", "HMI control text decoded", "actual: " + string.Join(",", texts));

            var badResult = HmiFile.Parse(new byte[0x40], "bad.bin", 0x40);
            Assert(!(bool)badResult["success"], "non-HMI file rejected");
        }

        private static byte[] BuildHmiSample()
        {
            var body = new List<byte>();
            body.AddRange(Record(11, 8 + 36, WindowPayload(1, 800, 480, 0, 0)));

            var nameBytes = Encoding.GetEncoding(936).GetBytes("主界面");
            var namePayload = new byte[2 + nameBytes.Length + 1];
            Buffer.BlockCopy(nameBytes, 0, namePayload, 2, nameBytes.Length);
            body.AddRange(Record(20003, 8 + namePayload.Length, namePayload));

            body.AddRange(Record(609, 8 + 12, ControlPayload(0x01000002, 60, 55, 10, 20)));

            var textBytes = Encoding.ASCII.GetBytes("Start");
            var textPayload = new byte[2 + textBytes.Length + 1];
            Buffer.BlockCopy(textBytes, 0, textPayload, 2, textBytes.Length);
            body.AddRange(Record(2000, 8 + textPayload.Length, textPayload));

            body.AddRange(Record(0, 8, new byte[0]));

            var data = new byte[0x38 + body.Count];
            BitConverter.GetBytes(10).CopyTo(data, 0);
            BitConverter.GetBytes(0x38).CopyTo(data, 4);
            data[8] = (byte)'H';
            data[9] = (byte)'M';
            data[10] = (byte)'I';
            body.CopyTo(data, 0x38);

            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(2 - data[i]);
            return data;
        }

        private static byte[] Record(int type, int size, byte[] payload)
        {
            var r = new byte[size];
            BitConverter.GetBytes(type).CopyTo(r, 0);
            BitConverter.GetBytes(size).CopyTo(r, 4);
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, r, 8, payload.Length);
            return r;
        }

        private static byte[] WindowPayload(int id, int w, int h, int x, int y)
        {
            var p = new byte[36];
            BitConverter.GetBytes(id).CopyTo(p, 0);
            BitConverter.GetBytes((ushort)w).CopyTo(p, 4);
            BitConverter.GetBytes((ushort)h).CopyTo(p, 6);
            BitConverter.GetBytes((ushort)x).CopyTo(p, 8);
            BitConverter.GetBytes((ushort)y).CopyTo(p, 10);
            return p;
        }

        private static byte[] ControlPayload(int id, int w, int h, int x, int y)
        {
            var p = new byte[12];
            BitConverter.GetBytes(id).CopyTo(p, 0);
            BitConverter.GetBytes((ushort)w).CopyTo(p, 4);
            BitConverter.GetBytes((ushort)h).CopyTo(p, 6);
            BitConverter.GetBytes((ushort)x).CopyTo(p, 8);
            BitConverter.GetBytes((ushort)y).CopyTo(p, 10);
            return p;
        }

        /// <summary>
        /// 模拟器集成模式：连接 127.0.0.1 的 zsimu 模拟器，逐项打印真实返回值。
        /// 用法：RTSysPlugInWorkerTests.exe --sim
        /// </summary>
        private static int RunSimulatorTests()
        {
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI", "config.json");
            string original = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            try
            {
                Dictionary<string, object> cfg;
                try { cfg = original != null ? _json.Deserialize<Dictionary<string, object>>(original) : new Dictionary<string, object>(); }
                catch { cfg = new Dictionary<string, object>(); }
                if (cfg == null) cfg = new Dictionary<string, object>();
                cfg["RTSysIP"] = "127.0.0.1";
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath, _json.Serialize(cfg));
                RTSysHandlers.InvalidateConnectionStateCache();

                Console.WriteLine("=== Simulator Integration (127.0.0.1) ===");
                Dump("connect", RTSysHandlers.TriggerAutoConnectAsync().GetAwaiter().GetResult());
                Dump("controller_status", RTSysHandlers.GetConnectionStatus());
                Dump("get_status", RTSysHandlers.GetStatus());
                Dump("list_axes", RTSysHandlers.ListAxes());
                Dump("get_axis_detail(0)", RTSysHandlers.GetAxisDetail(0));
                Dump("read_vr(0,5)", RTSysHandlers.ReadVR(0, 5));
                Dump("read_table(0,5)", RTSysHandlers.ReadTable(0, 5));
                Dump("list_digital_io", RTSysHandlers.ListDigitalIO());
                Dump("read_digital_io(0)", RTSysHandlers.ReadDigitalIO(0));
                Dump("list_analogue_io", RTSysHandlers.ListAnalogueIO());
                Dump("read_analogue_io(0)", RTSysHandlers.ReadAnalogueIO(0));
                Dump("list_alarms", RTSysHandlers.ListAlarms());
                Dump("list_programs", RTSysHandlers.ListPrograms());
                Dump("list_plugins", RTSysHandlers.ListAttachedPlugins());
            }
            finally
            {
                if (original != null) File.WriteAllText(configPath, original);
                else if (File.Exists(configPath)) File.Delete(configPath);
                RTSysHandlers.InvalidateConnectionStateCache();
            }
            return 0;
        }

        private static void Dump(string name, object result)
        {
            try { Console.WriteLine(name + ": " + _json.Serialize(result)); }
            catch (Exception ex) { Console.WriteLine(name + ": <serialize failed: " + ex.Message + ">"); }
        }

        /// <summary>
        /// 全工具矩阵：直接调用 AiService.DispatchTool，逐个校验 schema 可见工具的返回。
        /// 用法：--matrix offline | sim
        /// </summary>
        private static int RunToolMatrix(string mode)
        {
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI", "config.json");
            string originalConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            string originalWorkspace = ProgramWorkspace.WorkspaceDir;
            var projDir = Path.Combine(Path.GetTempPath(), "RTSysAI_Matrix_" + Guid.NewGuid().ToString("N"));
            var cacheDir = Path.Combine(Path.GetTempPath(), "RTSysAI_MatrixCache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projDir);
            Directory.CreateDirectory(cacheDir);
            int pass = 0, fail = 0, skip = 0;

            try
            {
                var gbk = Encoding.GetEncoding(936);
                File.WriteAllBytes(Path.Combine(projDir, "MATRIX_PROG.bas"), gbk.GetBytes("' matrix\r\nMOVE(0, 100)\r\nSTOP\r\n"));
                File.WriteAllBytes(Path.Combine(projDir, "MATRIX_DEL.bas"), gbk.GetBytes("' del\r\nSTOP\r\n"));
                File.WriteAllText(Path.Combine(projDir, "TestPro.zpj"),
                    "[Controller]\r\nFiles=2\r\n[FileList]\r\n" +
                    "File1=MATRIX_PROG.bas\r\nFileType1=1\r\n" +
                    "File2=MATRIX_DEL.bas\r\nFileType2=1\r\n[OpenList]\r\n");

                Dictionary<string, object> cfg;
                try { cfg = _json.Deserialize<Dictionary<string, object>>(originalConfig ?? "{}") ?? new Dictionary<string, object>(); }
                catch { cfg = new Dictionary<string, object>(); }
                if (cfg == null) cfg = new Dictionary<string, object>();
                cfg["projectDir"] = projDir;
                cfg["RTSysIP"] = mode == "sim" ? "127.0.0.1" : "";
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath, _json.Serialize(cfg));
                ProgramWorkspace.WorkspaceDir = cacheDir;
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();

                var svc = new AiService();
                svc.OnConfirmWrite = (n, a) => true;
                svc.OnConfirmPlan = p => true;

                var getToolDefs = typeof(AiService).GetMethod("GetToolDefinitions", BindingFlags.NonPublic | BindingFlags.Static);
                var defs = getToolDefs.Invoke(null, null) as List<Dictionary<string, object>>;
                var dispatch = typeof(AiService).GetMethod("DispatchTool", BindingFlags.NonPublic | BindingFlags.Instance);
                var skipTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "research", "review", "debug", "explore", "verify",
                    "read_skill", "update_memory"
                };

                Console.WriteLine("=== Tool Matrix (" + mode + ") tools=" + (defs == null ? 0 : defs.Count) + " ===");
                foreach (var def in defs)
                {
                    var tool = def["name"]?.ToString();
                    if (string.IsNullOrEmpty(tool)) continue;
                    if (skipTools.Contains(tool)) { skip++; Console.WriteLine("[SKIP] " + tool); continue; }

                    object result;
                    try
                    {
                        result = dispatch.Invoke(svc, new object[] { tool, MatrixInput(tool) });
                    }
                    catch (Exception ex)
                    {
                        var e = ex.InnerException ?? ex;
                        fail++;
                        Console.WriteLine("[FAIL] " + tool + " EX " + e.GetType().Name + ": " + e.Message);
                        continue;
                    }

                    string json;
                    try { json = _json.Serialize(result); }
                    catch (Exception ex) { fail++; Console.WriteLine("[FAIL] " + tool + " serialize: " + ex.Message); continue; }

                    if (MatrixVerdict(tool, json)) { pass++; Console.WriteLine("[PASS] " + tool + " -> " + Truncate(json, 220)); }
                    else { fail++; Console.WriteLine("[FAIL] " + tool + " -> " + Truncate(json, 400)); }
                }
            }
            finally
            {
                if (originalConfig != null) File.WriteAllText(configPath, originalConfig);
                else if (File.Exists(configPath)) File.Delete(configPath);
                ProgramWorkspace.WorkspaceDir = originalWorkspace;
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();
                try { Directory.Delete(projDir, true); } catch { }
                try { Directory.Delete(cacheDir, true); } catch { }
            }

            Console.WriteLine("=== Matrix summary: " + pass + " passed, " + fail + " failed, " + skip + " skipped ===");
            return fail > 0 ? 1 : 0;
        }

        private static Dictionary<string, object> MatrixInput(string tool)
        {
            switch (tool)
            {
                case "read_source": return Dict("name", "MATRIX_PROG");
                case "write_source": return Dict("name", "MATRIX_PROG", "sourceCode", "' matrix\r\nMOVE(0, 200)\r\nSTOP\r\n");
                case "patch_source": return Dict("name", "MATRIX_PROG", "operations", new List<object> { Dict("old_string", "MOVE(0, 200)", "new_string", "MOVE(0, 300)") });
                case "create_program": return Dict("name", "MATRIX_NEW", "sourceCode", "' new\r\nSTOP\r\n");
                case "delete_program": return Dict("name", "MATRIX_DEL");
                case "rename_program": return Dict("name", "MATRIX_NEW", "newName", "MATRIX_REN");
                case "copy_program": return Dict("name", "MATRIX_REN", "newName", "MATRIX_CPY");
                case "open_program": return Dict("name", "MATRIX_PROG");
                case "read_vr": return Dict("address", 0, "count", 1);
                case "write_vr": return Dict("address", 0, "value", 1);
                case "read_table": return Dict("address", 0, "count", 1);
                case "write_table": return Dict("address", 0, "values", new List<object> { 1 });
                case "get_axis_detail": return Dict("index", 0);
                case "read_digital_io": return Dict("index", 0);
                case "write_digital_io": return Dict("index", 0, "value", false);
                case "read_analogue_io": return Dict("index", 0);
                case "write_analogue_io": return Dict("index", 0, "value", 0.0);
                case "read_drive_param": return Dict("axis", 0, "address", 0x1000, "nd", 4);
                case "write_drive_param": return Dict("axis", 0, "address", 0x1000, "value", 1);
                case "scan_ethercat": return Dict("slot", 0);
                case "read_ethercat_sdo": return Dict("slot", 0, "position", 0, "index", 4096, "subindex", 0, "type", "uint32");
                case "write_ethercat_sdo": return Dict("slot", 0, "position", 0, "index", 4096, "subindex", 0, "type", "uint32", "value", 1);
                case "lookup_command": return Dict("query", "MOVE");
                case "search_code": return Dict("query", "MOVE");
                case "task_create": return Dict("subject", "matrix task", "description", "matrix validation");
                case "task_update": return Dict("id", 1, "status", "completed");
                case "exit_plan_mode": return Dict("plan", "matrix plan");
                default: return Dict();
            }
        }

        private static Dictionary<string, object> Dict(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2)
                d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static bool MatrixVerdict(string tool, string json)
        {
            switch (tool)
            {
                case "get_status": return json.Contains("\"success\":true");
                case "list_programs": return json.Contains("\"success\":true") || json.Contains("\"items\"");
                case "read_source": return json.Contains("sourceCode");
                case "write_source":
                case "patch_source":
                case "create_program":
                case "delete_program":
                case "rename_program":
                case "copy_program":
                case "save_project":
                case "open_project":
                case "open_program":
                    return json.Contains("\"success\":true");
                case "search_code": return json.Contains("matchCount");
                case "list_project_items": return json.Contains("items");
                case "list_plugins": return json.Contains("plugins");
                case "controller_status": return json.Contains("connectionState");
                case "discover_skills": return json.Contains("skills") || json.Contains("name");
                case "task_create": return json.Contains("\"id\"");
                case "task_update": return json.Contains("success") || json.Contains("id");
                case "task_list": return json.Contains("tasks") || json.Contains("\"id\"") || json.Contains("[]");
                case "enter_plan_mode": return json.Contains("true");
                case "exit_plan_mode": return json.Contains("true") || json.Contains("success");
                default:
                    return !json.Contains("Unknown tool");
            }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        private static void TestRTSysHandlersSignatures()
        {
            var type = typeof(RTSysHandlers);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);

            var getStatus = methods.FirstOrDefault(m => m.Name == "GetStatus" && m.GetParameters().Length == 0);
            var listPrograms = methods.FirstOrDefault(m => m.Name == "ListPrograms" && m.GetParameters().Length == 0);
            var listProjectItems = methods.FirstOrDefault(m => m.Name == "ListProjectItems" && m.GetParameters().Length == 0);

            Assert(getStatus != null, "GetStatus() exists");
            Assert(listPrograms != null, "ListPrograms() exists");
            Assert(listProjectItems != null, "ListProjectItems() exists");

            if (getStatus != null)
            {
                Assert(getStatus.ReturnType == typeof(object), "GetStatus returns object");
            }
            if (listPrograms != null)
            {
                Assert(listPrograms.ReturnType == typeof(object), "ListPrograms returns object");
            }
            if (listProjectItems != null)
            {
                Assert(listProjectItems.ReturnType == typeof(object), "ListProjectItems returns object");
            }
        }

        private static void TestAiToolsRegistration()
        {
            var aiToolsType = typeof(AiService).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "AiService" && t.Namespace == "RTSys.PlugIn");

            Assert(aiToolsType != null, "AiService type found");

            var dispatchTool = aiToolsType?.GetMethod("DispatchTool", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert(dispatchTool != null, "DispatchTool method exists");

            var getToolDefs = aiToolsType?.GetMethod("GetToolDefinitions", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(getToolDefs != null, "GetToolDefinitions method exists");

            var toolDefs = getToolDefs.Invoke(null, null) as List<Dictionary<string, object>>;
            Assert(toolDefs != null, "Tool definitions returned");

            var toolNames = toolDefs.Select(t => (string)t["name"]).ToList();
            Assert(toolNames.Contains("get_status"), "get_status registered");
            Assert(toolNames.Contains("list_programs"), "list_programs registered");
            Assert(toolNames.Contains("list_project_items"), "list_project_items registered");
            Assert(toolNames.Contains("read_hmi"), "read_hmi registered");

            var statusTool = toolDefs.First(t => (string)t["name"] == "get_status");
            var programsTool = toolDefs.First(t => (string)t["name"] == "list_programs");
            var itemsTool = toolDefs.First(t => (string)t["name"] == "list_project_items");

            Assert(statusTool["description"].ToString().Contains("controller"), "get_status has proper description");
            Assert(programsTool["description"].ToString().Contains("program"), "list_programs has proper description");
            Assert(itemsTool["description"].ToString().Contains("project"), "list_project_items has proper description");
        }

        private static void TestToolExecution_NoController()
        {
            var aiServiceType = typeof(AiService);
            var svc = Activator.CreateInstance(aiServiceType);

            var executeTool = aiServiceType.GetMethod("ExecuteTool", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert(executeTool != null, "ExecuteTool method exists");

            // ---- 1) get_status 无 IP 时应“成功返回，带 connectionState=NotConfigured”（结构化，非异常） ----
            var result1 = executeTool.Invoke(svc, new object[] { "get_status", new Dictionary<string, object>() });
            var resultStr1 = result1 as string;
            Assert(resultStr1 != null, "get_status returns string");
            var parsed1 = _json.Deserialize<Dictionary<string, object>>(resultStr1);
            Assert(parsed1 != null, "get_status response is valid JSON");
            Assert(parsed1.ContainsKey("success"), "get_status has success field");
            Assert((bool)parsed1["success"] == true, "get_status success is true when no IP (still returns useful state)");
            Assert(parsed1.ContainsKey("connectionState"), "get_status has connectionState field");
            Assert((string)parsed1["connectionState"] == "NotConfigured", "get_status connectionState is NotConfigured");
            Assert(parsed1.ContainsKey("connected"), "get_status has connected field");
            Assert((bool)parsed1["connected"] == false, "get_status connected is false when no IP");

            // ---- 2) 仅那些“无本地缓存回退、必须连控制器”的工具在无 IP 时返回结构化错误 ----
            // list_programs / list_project_items 现在离线也可成功（来自本地缓存），单独验证。
            AssertControllerToolErrors(svc, executeTool, new[] { "read_source" });

            // ---- 3) list_programs / list_project_items 离线成功，返回本地缓存程序 ----
            var listProgramsResult = executeTool.Invoke(svc, new object[] { "list_programs", new Dictionary<string, object>() });
            var listProgramsStr = listProgramsResult as string;
            Assert(listProgramsStr != null, "list_programs returns string (offline)");
            var listProgramsParsed = _json.Deserialize<Dictionary<string, object>>(listProgramsStr);
            Assert(listProgramsParsed != null, "list_programs response is valid JSON (offline)");
            Assert(listProgramsParsed.ContainsKey("items"), "list_programs has items key (offline)");
            Assert(!listProgramsParsed.ContainsKey("error") || (bool)listProgramsParsed["success"] == true, "list_programs succeeds offline (local cache)");

            var listItemsResult = executeTool.Invoke(svc, new object[] { "list_project_items", new Dictionary<string, object>() });
            var listItemsStr = listItemsResult as string;
            Assert(listItemsStr != null, "list_project_items returns string (offline)");
            var listItemsParsed = _json.Deserialize<Dictionary<string, object>>(listItemsStr);
            Assert(listItemsParsed != null, "list_project_items response is valid JSON (offline)");
            Assert(listItemsParsed.ContainsKey("items"), "list_project_items has items key (offline)");
            Assert(!listItemsParsed.ContainsKey("error") || (bool)listItemsParsed["success"] == true, "list_project_items succeeds offline (local cache)");

            // ---- 4) read_source 对未缓存的程序在无控制器时返回结构化错误（NotConfigured） ----
            var readMissingResult = executeTool.Invoke(svc, new object[] { "read_source", new Dictionary<string, object> { { "name", "NOT_CACHED_PROG" } } });
            var readMissingStr = readMissingResult as string;
            var readMissingDict = _json.Deserialize<Dictionary<string, object>>(readMissingStr);
            Assert(readMissingDict != null, "read_source (missing, offline) response is valid JSON");
            Assert(readMissingDict.ContainsKey("success"), "read_source (missing) has success field");
            Assert((bool)readMissingDict["success"] == false, "read_source (missing) success is false");
            Assert(readMissingDict.ContainsKey("connectionState"), "read_source (missing) has connectionState");
            Assert((string)readMissingDict["connectionState"] == "NotConfigured", "read_source (missing) connectionState is NotConfigured");
            Assert(readMissingDict.ContainsKey("requiresController"), "read_source (missing) has requiresController");
            Assert((bool)readMissingDict["requiresController"] == true, "read_source (missing) requiresController is true");

            // ---- 3) 错误响应应是结构化 JSON：success=false + error 中文提示 + connectionState/requiresController ----
            foreach (var r in new[] { resultStr1 })
            {
                var parsed = _json.Deserialize<Dictionary<string, object>>(r);
                Assert(parsed != null, "Response is valid JSON");
            }
        }

        /// <summary>
        /// 断言若干控制器依赖工具在无 IP 时都返回结构化错误（不抛异常、不泄露堆栈）。
        /// </summary>
        private static void AssertControllerToolErrors(object svc, MethodInfo executeTool, string[] toolNames)
        {
            foreach (var name in toolNames)
            {
                var result = executeTool.Invoke(svc, new object[] { name, new Dictionary<string, object>() });
                var resultStr = result as string;
                Assert(resultStr != null, name + " returns string");

                var parsed = _json.Deserialize<Dictionary<string, object>>(resultStr);
                Assert(parsed != null, name + " response is valid JSON");

                // 必须有 success=false（结构化错误，不是成功响应）
                Assert(parsed.ContainsKey("success"), name + " response has success field");
                Assert((bool)parsed["success"] == false, name + " success is false when no IP");

                // 必须有可操作的中文提示（不是原始英文异常）
                Assert(parsed.ContainsKey("error"), name + " response has error field");
                var errMsg = parsed["error"]?.ToString() ?? "";
                Assert(!errMsg.StartsWith("RTSysIP not configured"), name + " error uses friendly Chinese message, not raw English");
                Assert(errMsg.Length > 0, name + " error message is non-empty");

                // 必须携带连接状态，AI 可据此判断是“未配置”还是“已配置不可达”
                Assert(parsed.ContainsKey("connectionState"), name + " response has connectionState field");
                Assert((string)parsed["connectionState"] == "NotConfigured", name + " connectionState is NotConfigured when no IP");

                // 标记需要控制器，AI 可据此向用户请求 IP
                Assert(parsed.ContainsKey("requiresController"), name + " response has requiresController field");
                Assert((bool)parsed["requiresController"] == true, name + " requiresController is true");

                Assert(!resultStr.Contains("InvalidOperationException"), name + " does not leak raw exception type");
                Assert(!resultStr.Contains("StackTrace"), name + " does not leak stack trace");
            }
        }

        private static Dictionary<string, object> ToDict(object result)
        {
            // Anonymous types are not castable to Dictionary; serialize then deserialize to inspect.
            var json = _json.Serialize(result);
            return _json.Deserialize<Dictionary<string, object>>(json);
        }

        private static void TestHandlerResponseShapes()
        {
            var statusResult = RTSysHandlers.GetStatus();
            var programsResult = RTSysHandlers.ListPrograms();
            var itemsResult = RTSysHandlers.ListProjectItems();

            // ---- get_status：无 IP 时成功返回并带连接状态（AI 可据此询问用户 IP） ----
            var statusDict = ToDict(statusResult);
            Assert(statusDict != null, "get_status returns serializable object");
            Assert(statusDict.ContainsKey("success"), "get_status has success field");
            Assert((bool)statusDict["success"] == true, "get_status success is true when no IP");
            Assert(statusDict.ContainsKey("connectionState"), "get_status has connectionState field");
            Assert((string)statusDict["connectionState"] == "NotConfigured", "get_status connectionState is NotConfigured");
            Assert(statusDict.ContainsKey("connected"), "get_status has connected field");
            Assert((bool)statusDict["connected"] == false, "get_status connected is false when no IP");
            Assert(statusDict.ContainsKey("hint"), "get_status has hint field (actionable guidance for AI)");
            Assert(statusDict["hint"].ToString().Contains("设置"), "get_status hint tells user where to configure the IP");

            // ---- list_programs / list_project_items：无 IP 时从本地缓存成功返回（离线可用） ----
            foreach (var result in new[] { programsResult, itemsResult })
            {
                var dict = ToDict(result);
                Assert(dict != null, "Handler returns serializable object");
                Assert(dict.ContainsKey("items"), "Result has items key (offline cache)");
                Assert(dict.ContainsKey("success"), "Result has success field");
                Assert((bool)dict["success"] == true, "Result success is true offline (local cache)");
            }

            var programsDict = ToDict(programsResult);
            Assert(programsDict.ContainsKey("items"), "list_programs has items key");
            // items only present on success path; items is a list

            var itemsDict = ToDict(itemsResult);
            Assert(itemsDict.ContainsKey("items"), "list_project_items has items key");
        }

        private static void TestPureIoToolsClassification()
        {
            var aiServiceType = typeof(AiService);
            var pureIoToolsField = aiServiceType.GetField("PureIoTools", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(pureIoToolsField != null, "PureIoTools field exists");

            var pureIoTools = pureIoToolsField.GetValue(null) as HashSet<string>;
            Assert(pureIoTools != null, "PureIoTools is HashSet");

            // These do network I/O (ZMotion P/Invoke) but NOT UI — they belong in PureIoTools
            // so they bypass DispatcherHelper.Invoke (which would crash if Capture() was never called).
            Assert(pureIoTools.Contains("get_status"), "get_status IN PureIoTools (does network I/O, no UI)");
            Assert(pureIoTools.Contains("list_programs"), "list_programs IN PureIoTools (does network I/O, no UI)");
            Assert(pureIoTools.Contains("list_project_items"), "list_project_items IN PureIoTools (does network I/O, no UI)");

            // These SHOULD be in PureIoTools (no I/O)
            Assert(pureIoTools.Contains("lookup_command"), "lookup_command in PureIoTools");
            Assert(pureIoTools.Contains("read_skill"), "read_skill in PureIoTools");
            Assert(pureIoTools.Contains("task_list"), "task_list in PureIoTools");
        }

        /// <summary>
        /// 非控制器工具在无 IP 时应正常工作（不返回连接错误）。
        /// lookup_command / read_skill / discover_skills / task_* 等纯内存/文档工具不依赖控制器。
        /// </summary>
        private static void TestNonControllerToolsNoIp()
        {
            var aiServiceType = typeof(AiService);
            var svc = Activator.CreateInstance(aiServiceType);
            var executeTool = aiServiceType.GetMethod("ExecuteTool", BindingFlags.NonPublic | BindingFlags.Instance);

            // lookup_command：纯文档查询，无 IP 也能返回（可能无匹配，但不应是连接错误）
            var lookupResult = executeTool.Invoke(svc, new object[] { "lookup_command", new Dictionary<string, object> { { "query", "MOVE" } } });
            var lookupStr = lookupResult as string;
            Assert(lookupStr != null, "lookup_command returns string");
            Assert(!lookupStr.Contains("RTSysIP not configured"), "lookup_command should not need controller IP");
            Assert(!lookupStr.Contains("NotConfigured"), "lookup_command should not report NotConfigured");

            // search_code：本地文档搜索，无 IP 也能返回（返回空结果，不应是连接错误）
            var searchResult = executeTool.Invoke(svc, new object[] { "search_code", new Dictionary<string, object> { { "query", "MOVE" } } });
            var searchStr = searchResult as string;
            Assert(searchStr != null, "search_code returns string");
            Assert(!searchStr.Contains("RTSysIP not configured"), "search_code should not need controller IP");

            // discover_skills：纯文档查询，无 IP 也能返回
            var discoverResult = executeTool.Invoke(svc, new object[] { "discover_skills", new Dictionary<string, object>() });
            var discoverStr = discoverResult as string;
            Assert(discoverStr != null, "discover_skills returns string");
            Assert(!discoverStr.Contains("RTSysIP not configured"), "discover_skills should not need controller IP");

            // list_plugins：纯元数据查询，无 IP 也能返回
            var pluginsResult = executeTool.Invoke(svc, new object[] { "list_plugins", new Dictionary<string, object>() });
            var pluginsStr = pluginsResult as string;
            Assert(pluginsStr != null, "list_plugins returns string");
            Assert(!pluginsStr.Contains("RTSysIP not configured"), "list_plugins should not need controller IP");

            // task_list：纯内存，无 IP 也能返回
            var taskResult = executeTool.Invoke(svc, new object[] { "task_list", new Dictionary<string, object>() });
            var taskStr = taskResult as string;
            Assert(taskStr != null, "task_list returns string");
            Assert(!taskStr.Contains("RTSysIP not configured"), "task_list should not need controller IP");

            // task_create：纯内存，无 IP 也能返回
            var createResult = executeTool.Invoke(svc, new object[] { "task_create", new Dictionary<string, object> { { "subject", "Test" }, { "description", "Desc" } } });
            var createStr = createResult as string;
            Assert(createResult != null, "task_create returns string");
            Assert(!createStr.Contains("RTSysIP not configured"), "task_create should not need controller IP");

            // update_memory：纯内存，无 IP 也能返回
            var memResult = executeTool.Invoke(svc, new object[] { "update_memory", new Dictionary<string, object> { { "content", "# Test memory" } } });
            var memStr = memResult as string;
            Assert(memResult != null, "update_memory returns string");
            Assert(!memStr.Contains("RTSysIP not configured"), "update_memory should not need controller IP");

            // enter_plan_mode / exit_plan_mode：纯状态机，无 IP 也能返回
            var enterResult = executeTool.Invoke(svc, new object[] { "enter_plan_mode", new Dictionary<string, object>() });
            var enterStr = enterResult as string;
            Assert(enterStr != null, "enter_plan_mode returns string");
            Assert(!enterStr.Contains("RTSysIP not configured"), "enter_plan_mode should not need controller IP");
        }

        /// <summary>
        /// 连接状态辅助：GetConnectionState 可区分“未配置 IP”与“已配置但断开”；
        /// get_status 返回 connectionState 字段，AI 可据此引导用户配置 IP。
        /// </summary>
        private static void TestConnectionStateHelper()
        {
            // 无 IP 时：GetConnectionState 应返回 NotConfigured
            var state = RTSysHandlers.GetConnectionState();
            Assert(state == RTSysHandlers.ControllerConnectionState.NotConfigured,
                "GetConnectionState returns NotConfigured when no IP", state.ToString());

            // get_status 应携带 connectionState 字段
            var statusResult = RTSysHandlers.GetStatus();
            var statusDict = ToDict(statusResult);
            Assert(statusDict.ContainsKey("connectionState"), "get_status has connectionState");
            Assert((string)statusDict["connectionState"] == "NotConfigured",
                "get_status connectionState is NotConfigured when no IP", statusDict["connectionState"]?.ToString());
        }

        /// <summary>
        /// 回归：控制器“已配置 IP 但不可达”时，对话路径不得被原生网络搜索阻塞。
        /// 曾经的缺陷：每轮对话构建提示词 → GetStatus/ListPrograms → GetHandleAsync →
        /// ZMC_SearchEth/ZMC_SearchAndOpenEth 同步阻塞且无超时，导致发送后永久无输出。
        /// </summary>
        private static void TestChatPathNonBlocking()
        {
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI", "config.json");
            string original = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                // 不可达的保留地址（TEST-NET-3），模拟“配置了 IP 但控制器不在”
                File.WriteAllText(configPath,
                    "{\"apiKey\":\"test\",\"model\":\"test\",\"apiUrl\":\"https://example.invalid\",\"RTSysIP\":\"203.0.113.1\"}");
                RTSysHandlers.InvalidateConnectionStateCache();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var state = RTSysHandlers.GetConnectionState();
                sw.Stop();
                Assert(sw.ElapsedMilliseconds < 1000,
                    "GetConnectionState 不触发阻塞式网络搜索",
                    "state=" + state + ", elapsed=" + sw.ElapsedMilliseconds + "ms");

                var svc = new AiService(); // 构造时 LoadConfig 读取上面的不可达 IP
                sw.Restart();
                for (int i = 0; i < 3; i++) svc.BuildDynamicContext();
                sw.Stop();
                Assert(sw.ElapsedMilliseconds < 1500,
                    "BuildDynamicContext 在控制器不可达时不阻塞",
                    "elapsed=" + sw.ElapsedMilliseconds + "ms");
            }
            finally
            {
                if (original != null) File.WriteAllText(configPath, original);
                else if (File.Exists(configPath)) File.Delete(configPath);
                RTSysHandlers.InvalidateConnectionStateCache();
            }
        }

        /// <summary>
        /// 回归：插件必须能看见 RTSys 当前打开项目的 .bas 文件（含 GBK 中文注释）。
        /// 覆盖 ProjectDir 解析、ListProjectPrograms、ReadProjectSource、ListPrograms、
        /// ReadSource、SearchCode。
        /// </summary>
        private static void TestProjectDirectorySupport()
        {
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI", "config.json");
            string original = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            var tempDir = Path.Combine(Path.GetTempPath(), "RTSysAI_Project_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 模拟 RTSys 项目目录：GBK 编码的 .bas
                var gbk = Encoding.GetEncoding(936);
                File.WriteAllBytes(Path.Combine(tempDir, "TaskMain.bas"), gbk.GetBytes("'主程序 注释\r\nMOVE(0, 1000)\r\n"));
                File.WriteAllBytes(Path.Combine(tempDir, "TaskAutoRun.bas"), gbk.GetBytes("'自动运行\r\nSTOP\r\n"));
                // .zpj 权威清单：只列 TaskMain/TaskAutoRun + HMI；Unused.bas 未加入项目
                File.WriteAllText(Path.Combine(tempDir, "TestPro.zpj"),
                    "[Controller]\r\nFiles=3\r\n[FileList]\r\n" +
                    "File1=TaskMain.bas\r\nFileType1=1\r\n" +
                    "File2=TaskAutoRun.bas\r\nFileType2=1\r\n" +
                    "File3=Hmi1.hmi\r\nFileType3=5\r\n[OpenList]\r\n");
                File.WriteAllBytes(Path.Combine(tempDir, "Unused.bas"), gbk.GetBytes("'未加入项目\r\nSTOP\r\n"));
                File.WriteAllBytes(Path.Combine(tempDir, "Hmi1.hmi"), BuildHmiSample());

                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath,
                    "{\"apiKey\":\"test\",\"model\":\"test\",\"apiUrl\":\"https://example.invalid\",\"RTSysIP\":\"\",\"projectDir\":\"" +
                    tempDir.Replace("\\", "\\\\") + "\"}");
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();

                Assert(string.Equals(ProgramWorkspace.ProjectDir, tempDir, StringComparison.OrdinalIgnoreCase),
                    "ProjectDir 从 config 解析成功", ProgramWorkspace.ProjectDir);

                var files = ProgramWorkspace.ListProjectPrograms();
                Assert(files.Any(f => f.name == "TaskMain"), "ListProjectPrograms 包含 TaskMain");
                Assert(files.Any(f => f.name == "TaskAutoRun"), "ListProjectPrograms 包含 TaskAutoRun");
                Assert(!files.Any(f => f.name == "Unused"), ".zpj 清单外的 .bas 不出现");
                Assert(!files.Any(f => f.name == "Hmi1"), ".zpj 中的 HMI 文件不出现在程序列表");

                Assert(ProgramWorkspace.ListProjectHmiFiles().Contains("Hmi1"), "ListProjectHmiFiles 包含 .zpj 中的 Hmi1");
                var projectItems = ToDict(RTSysHandlers.ListProjectItems())["items"] as System.Collections.IList;
                Assert(projectItems != null && projectItems.Cast<Dictionary<string, object>>()
                    .Any(i => (i["itemType"]?.ToString() == "hmi") && (i["name"]?.ToString() == "Hmi1")),
                    "ListProjectItems 包含 itemType=hmi 的条目");
                var hmiRead = ToDict(HmiFile.Read("Hmi1"));
                Assert(hmiRead.ContainsKey("success") && Convert.ToBoolean(hmiRead["success"]), "read_hmi 成功解析项目 HMI", hmiRead.ContainsKey("error") ? hmiRead["error"]?.ToString() : "");
                Assert(Convert.ToInt32(hmiRead["windowCount"]) == 1, "read_hmi windowCount == 1");

                var src = ProgramWorkspace.ReadProjectSource("TaskMain");
                Assert(src != null && src.Contains("主程序"), "ReadProjectSource 按 GBK 正确解码中文", src);

                var listDict = ToDict(RTSysHandlers.ListPrograms());
                var items = listDict["items"] as System.Collections.IList;
                var names = items == null ? new List<string>() :
                    items.Cast<Dictionary<string, object>>().Select(i => i["name"]?.ToString()).ToList();
                Assert(names.Contains("TaskMain"), "ListPrograms 包含项目程序 TaskMain", string.Join(",", names));

                var read = ToDict(RTSysHandlers.ReadSource("TaskMain", null));
                Assert((read["sourceCode"]?.ToString() ?? "").Contains("主程序"), "ReadSource 读取项目文件内容");
                Assert(read.ContainsKey("fromProject") && (bool)read["fromProject"], "ReadSource 标记 fromProject=true");

                var search = ToDict(RTSysHandlers.SearchCode("MOVE"));
                Assert(Convert.ToInt32(search["matchCount"]) > 0, "SearchCode 能搜索项目文件");
            }
            finally
            {
                if (original != null) File.WriteAllText(configPath, original);
                else if (File.Exists(configPath)) File.Delete(configPath);
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 项目文件 CRUD 回归（全部离线，不依赖控制器/模拟器）：
        ///   - 临时项目目录 + .zpj，验证 IsProjectAvailable；
        ///   - CreateProjectProgram 创建 .bas（GBK）、写入 .zpj 清单；
        ///   - WriteSource 写项目内程序后 ReadSource 返回新内容（savedToProject=true）；
        ///   - PatchSource 可作用于“仅在项目、不在本地缓存”的程序；
        ///   - Rename/Delete/Copy 同步更新文件与 .zpj 清单；
        ///   - 含路径分隔符 / .. 的程序名被拒绝；
        ///   - 无项目（IsProjectAvailable=false）时项目写操作返回明确错误；
        ///   - create_program 等工具出现在 BuildToolDefinitions 中。
        /// 所有临时状态在 finally 中恢复/清理，不污染真实项目与本地缓存。
        /// </summary>
        private static void TestProjectProgramCrud()
        {
            var configPath = Path.Combine(AiService.DataDir, "config.json");
            string originalConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
            var projectDir = Path.Combine(Path.GetTempPath(), "RTSysAI_ProjectCrud_" + Guid.NewGuid().ToString("N"));
            var workspaceDir = Path.Combine(Path.GetTempPath(), "RTSysAI_ProjectCrudWs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(workspaceDir);
            // 隔离本地缓存，避免写入用户的 %APPDATA%\RTSysAI\programs\
            ProgramWorkspace.WorkspaceDir = workspaceDir;
            try
            {
                var gbk = Encoding.GetEncoding(936);
                File.WriteAllBytes(Path.Combine(projectDir, "Main.bas"), gbk.GetBytes("'旧内容\r\nMOVE(0, 100)\r\n"));
                File.WriteAllText(Path.Combine(projectDir, "TestPro.zpj"),
                    "[Controller]\r\nFiles=2\r\n[FileList]\r\n" +
                    "File1=Main.bas\r\nFileType1=1\r\n" +
                    "File2=Hmi1.hmi\r\nFileType2=5\r\n[OpenList]\r\n");

                WriteTestConfig(configPath, projectDir);
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();

                Assert(ProgramWorkspace.IsProjectAvailable, "IsProjectAvailable=true（临时项目目录）");

                // ---- 1) CreateProjectProgram：创建 .bas（GBK）、可读、.zpj [FileList] 同步 ----
                var newSource = "'新建程序 中文注释\r\nMOVE(0, 500)\r\n";
                ProgramWorkspace.CreateProjectProgram("NewProg", newSource);
                var newProgPath = Path.Combine(projectDir, "NewProg.bas");
                Assert(File.Exists(newProgPath), "CreateProjectProgram 创建 NewProg.bas");
                Assert(ProgramWorkspace.ProjectProgramExists("NewProg"), "ProjectProgramExists(NewProg)=true");
                var newProgText = ProgramWorkspace.ReadProjectSource("NewProg") ?? "";
                Assert(newProgText.Contains("中文注释"), "ReadProjectSource 正确解码中文", newProgText);
                var newProgGbk = gbk.GetString(File.ReadAllBytes(newProgPath));
                Assert(newProgGbk.Contains("中文注释"), "NewProg.bas 以 GBK 编码写入", newProgGbk);
                Assert(ProjectFileListHasProgram(projectDir, "NewProg"), ".zpj [FileList] 增加 NewProg.bas");

                // ---- 2) WriteSource：写项目内程序后 ReadSource 返回新内容 ----
                var writeResult = ToDict(RTSysHandlers.WriteSource("Main", new Dictionary<string, object>
                {
                    { "name", "Main" },
                    { "sourceCode", "'项目内新内容\r\nMOVE(0, 2000)\r\n" }
                }));
                Assert(writeResult.ContainsKey("success") && (bool)writeResult["success"], "WriteSource 成功",
                    _json.Serialize(writeResult));
                Assert(writeResult.ContainsKey("savedToProject") && (bool)writeResult["savedToProject"],
                    "WriteSource 返回 savedToProject=true", _json.Serialize(writeResult));
                var mainRead = ToDict(RTSysHandlers.ReadSource("Main", null));
                var mainCode = mainRead.ContainsKey("sourceCode") ? mainRead["sourceCode"]?.ToString() ?? "" : "";
                Assert(mainCode.Contains("项目内新内容"), "ReadSource 返回项目内新内容", mainCode);
                Assert(!mainCode.Contains("旧内容"), "ReadSource 不再返回旧项目内容", mainCode);
                Assert(mainRead.ContainsKey("fromProject") && (bool)mainRead["fromProject"],
                    "ReadSource 标记 fromProject=true");

                // ---- 3) PatchSource：仅存在于项目、不在本地缓存的程序 ----
                ProgramWorkspace.CreateProjectProgram("PatchOnly", "'补丁前\r\nMOVE(0, 100)\r\n");
                ProgramWorkspace.DeleteLocalSource("PatchOnly");
                Assert(ProgramWorkspace.ReadLocalSource("PatchOnly") == null, "PatchOnly 不在本地缓存");
                var patchResult = ToDict(RTSysHandlers.PatchSource("PatchOnly", new Dictionary<string, object>
                {
                    { "name", "PatchOnly" },
                    { "operations", new List<object> { new Dictionary<string, object> { { "old_string", "MOVE(0, 100)" }, { "new_string", "MOVE(0, 300)" } } } }
                }));
                Assert(patchResult.ContainsKey("success") && (bool)patchResult["success"],
                    "PatchSource 对项目内程序成功", _json.Serialize(patchResult));
                var patched = ProgramWorkspace.ReadProjectSource("PatchOnly") ?? "";
                Assert(patched.Contains("MOVE(0, 300)"), "PatchSource 结果写回项目文件", patched);

                // ---- 4) Rename / Copy / Delete：文件与 .zpj 清单同步 ----
                var createTaskResult = ToDict(RTSysHandlers.CreateProgram(new Dictionary<string, object>
                {
                    { "name", "TaskA" },
                    { "sourceCode", "'任务A\r\nMOVE(0, 400)\r\n" }
                }));
                Assert(createTaskResult.ContainsKey("success") && (bool)createTaskResult["success"], "CreateProgram 创建 TaskA",
                    _json.Serialize(createTaskResult));
                Assert(ProjectFileListHasProgram(projectDir, "TaskA"), ".zpj 包含 TaskA");

                var renameResult = ToDict(RTSysHandlers.RenameProgram("TaskA", new Dictionary<string, object>
                {
                    { "name", "TaskA" }, { "newName", "TaskB" }
                }));
                Assert(renameResult.ContainsKey("success") && (bool)renameResult["success"], "RenameProgram TaskA→TaskB 成功",
                    _json.Serialize(renameResult));
                Assert(!File.Exists(Path.Combine(projectDir, "TaskA.bas")), "重命名后旧文件 TaskA.bas 不存在");
                Assert(File.Exists(Path.Combine(projectDir, "TaskB.bas")), "重命名后新文件 TaskB.bas 存在");
                Assert(!ProjectFileListHasProgram(projectDir, "TaskA"), ".zpj 移除 TaskA");
                Assert(ProjectFileListHasProgram(projectDir, "TaskB"), ".zpj 增加 TaskB");

                var copyResult = ToDict(RTSysHandlers.CopyProgram("TaskB", new Dictionary<string, object>
                {
                    { "name", "TaskB" }, { "newName", "TaskC" }
                }));
                Assert(copyResult.ContainsKey("success") && (bool)copyResult["success"], "CopyProgram TaskB→TaskC 成功",
                    _json.Serialize(copyResult));
                Assert(File.Exists(Path.Combine(projectDir, "TaskC.bas")), "复制后 TaskC.bas 存在");
                Assert(ProjectFileListHasProgram(projectDir, "TaskC"), ".zpj 增加 TaskC");

                // ProgramWorkspace.CopyProjectProgram 契约：null=成功
                object copyError = ProgramWorkspace.CopyProjectProgram("TaskC", "TaskD");
                Assert(copyError == null, "CopyProjectProgram 成功时返回 null", copyError?.ToString());
                Assert(File.Exists(Path.Combine(projectDir, "TaskD.bas")), "复制后 TaskD.bas 存在");
                Assert(ProjectFileListHasProgram(projectDir, "TaskD"), ".zpj 增加 TaskD");

                var deleteResult = ToDict(RTSysHandlers.DeleteProgram("TaskC"));
                Assert(deleteResult.ContainsKey("success") && (bool)deleteResult["success"], "DeleteProgram TaskC 成功",
                    _json.Serialize(deleteResult));
                Assert(!File.Exists(Path.Combine(projectDir, "TaskC.bas")), "删除后 TaskC.bas 不存在");
                Assert(!ProjectFileListHasProgram(projectDir, "TaskC"), ".zpj 移除 TaskC");

                // ---- 5) 名称校验：含路径分隔符 / .. 的名称被拒绝 ----
                foreach (var badName in new[] { "bad/name", "bad\\name", "..\\escape", "../escape" })
                {
                    var badCreate = ToDict(RTSysHandlers.CreateProgram(new Dictionary<string, object>
                    {
                        { "name", badName }, { "sourceCode", "'bad\r\nMOVE(0, 1)\r\n" }
                    }));
                    Assert(badCreate.ContainsKey("success") && !(bool)badCreate["success"],
                        "CreateProgram 拒绝非法名称: " + badName, _json.Serialize(badCreate));
                    Assert(!File.Exists(Path.Combine(projectDir, ProgramWorkspace.SanitizeName(badName) + ".bas")),
                        "非法名称未创建文件: " + badName);
                }
                var badRename = ToDict(RTSysHandlers.RenameProgram("Main", new Dictionary<string, object>
                {
                    { "name", "Main" }, { "newName", "../evil" }
                }));
                Assert(badRename.ContainsKey("success") && !(bool)badRename["success"], "RenameProgram 拒绝非法新名称");
                Assert(File.Exists(Path.Combine(projectDir, "Main.bas")), "非法重命名后 Main.bas 仍在");
                var badCopy = ToDict(RTSysHandlers.CopyProgram("Main", new Dictionary<string, object>
                {
                    { "name", "Main" }, { "newName", "a/b" }
                }));
                Assert(badCopy.ContainsKey("success") && !(bool)badCopy["success"], "CopyProgram 拒绝非法新名称");
                Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(projectDir), "escape.bas")), "非法名称未越界写文件");
                Assert(!File.Exists(Path.Combine(projectDir, "a_b.bas")), "CopyProgram 未按清洗名创建文件");

                // ---- 6) 无项目场景：项目写操作返回明确错误（不是成功） ----
                WriteTestConfig(configPath, Path.Combine(projectDir, "no_such_project"));
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();
                Assert(!ProgramWorkspace.IsProjectAvailable, "无项目时 IsProjectAvailable=false");
                object noProjectCreateError = null;
                string noProjectCreateThrow = null;
                try { noProjectCreateError = ProgramWorkspace.CreateProjectProgram("NoProj", "'x\r\nMOVE(0, 1)\r\n"); }
                catch (Exception ex) { noProjectCreateThrow = ex.Message; }
                Assert(noProjectCreateThrow == null, "无项目时 CreateProjectProgram 不抛异常", noProjectCreateThrow);
                Assert(IsFailureResult(noProjectCreateError), "无项目时 CreateProjectProgram 返回错误",
                    noProjectCreateError?.ToString());
                var noProjCreate = ToDict(RTSysHandlers.CreateProgram(new Dictionary<string, object>
                {
                    { "name", "NoProj" }, { "sourceCode", "'x\r\nMOVE(0, 1)\r\n" }
                }));
                Assert(noProjCreate.ContainsKey("success") && (bool)noProjCreate["success"],
                    "无项目时 CreateProgram 回退本地缓存并成功", _json.Serialize(noProjCreate));
                Assert(noProjCreate.ContainsKey("savedToProject") && !(bool)noProjCreate["savedToProject"],
                    "无项目时 CreateProgram 明确 savedToProject=false");
                Assert(noProjCreate.ContainsKey("savedLocally") && (bool)noProjCreate["savedLocally"],
                    "无项目时 CreateProgram 明确 savedLocally=true");

                // 恢复项目配置，供后续断言/清理
                WriteTestConfig(configPath, projectDir);
                ProgramWorkspace.ResetProjectDir();
                RTSysHandlers.InvalidateConnectionStateCache();

                // ---- 7) 工具 schema：项目 CRUD 工具已注册 ----
                var aiServiceType = typeof(AiService);
                var getToolDefs = aiServiceType.GetMethod("GetToolDefinitions", BindingFlags.NonPublic | BindingFlags.Static);
                var toolDefs = getToolDefs?.Invoke(null, null) as List<Dictionary<string, object>>;
                Assert(toolDefs != null, "GetToolDefinitions 返回工具列表");
                var toolNames = toolDefs == null ? new List<string>() : toolDefs.Select(t => t["name"]?.ToString()).ToList();
                foreach (var required in new[]
                {
                    "create_program", "delete_program", "rename_program", "copy_program",
                    "open_program", "save_project", "open_project"
                })
                {
                    Assert(toolNames.Contains(required), required + " 出现在 BuildToolDefinitions 中");
                }
            }
            finally
            {
                if (originalConfig != null) File.WriteAllText(configPath, originalConfig);
                else if (File.Exists(configPath)) File.Delete(configPath);
                ProgramWorkspace.ResetProjectDir();
                ProgramWorkspace.ResetToDefaultDir();
                RTSysHandlers.InvalidateConnectionStateCache();
                try { if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true); } catch { }
                try { if (Directory.Exists(workspaceDir)) Directory.Delete(workspaceDir, true); } catch { }
            }
        }

        /// <summary>写入测试用 config.json；projectDir 为 null 时不写该字段。</summary>
        private static void WriteTestConfig(string configPath, string projectDir)
        {
            var cfg = new Dictionary<string, object>
            {
                { "apiKey", "test" },
                { "model", "test" },
                { "apiUrl", "https://example.invalid" },
                { "RTSysIP", "" }
            };
            if (projectDir != null) cfg["projectDir"] = projectDir;
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            File.WriteAllText(configPath, _json.Serialize(cfg));
        }

        /// <summary>检查 .zpj 的 [FileList] 是否包含指定程序（接受 name 或 name.bas）。</summary>
        private static bool ProjectFileListHasProgram(string projectDir, string programName)
        {
            var target = programName + ".bas";
            foreach (var zpj in Directory.GetFiles(projectDir, "*.zpj"))
            {
                string text;
                try { text = File.ReadAllText(zpj); } catch { continue; }
                foreach (var raw in text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    if (!key.StartsWith("File", StringComparison.OrdinalIgnoreCase) ||
                        key.StartsWith("FileType", StringComparison.OrdinalIgnoreCase)) continue;
                    var val = line.Substring(eq + 1).Trim().Trim('"');
                    if (string.Equals(val, target, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(val, programName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>项目写入 API 约定 null=成功；兼容 bool=false / 非空字符串错误。</summary>
        private static bool IsFailureResult(object result)
        {
            if (result == null) return false;
            if (result is bool) return !(bool)result;
            return !string.IsNullOrEmpty(result.ToString());
        }

        /// <summary>
        /// 离线本地程序缓存测试：在“未配置控制器 IP”时，证明以下行为：
        ///   - list_programs / read_source / write_source / patch_source / search_code 使用本地缓存并成功；
        ///   - compile_program / run_program / read_vr 返回友好的“需要控制器”错误（不抛异常、无原始英文堆栈）；
        ///   - write_source / patch_source 返回 success=true + downloadedToController=false + pendingUpload=true，
        ///     且绝不声称程序已下载到控制器；
        ///   - read_source 对未缓存的程序在无控制器时返回结构化错误（connectionState=NotConfigured）。
        /// 使用隔离的临时工作区目录，不污染用户真实的 %APPDATA%\RTSysAI\programs\。
        /// </summary>
        private static void TestOfflineLocalProgramCache()
        {
            // 隔离工作区：用临时目录，避免污染 %APPDATA%\RTSysAI\programs\
            var tempDir = Path.Combine(Path.GetTempPath(), "RTSysAI_Test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ProgramWorkspace.WorkspaceDir = tempDir;

            // 原始配置（finally 中恢复）
            var configPath = Path.Combine(AiService.DataDir, "config.json");
            string originalConfig = null;
            if (File.Exists(configPath))
            {
                originalConfig = File.ReadAllText(configPath);
            }
            try
            {
                // 确保未配置 IP：清空 config.json 中的 RTSysIP（原始内容在 finally 中恢复）
                if (File.Exists(configPath))
                {
                    try
                    {
                        var cfg = _json.Deserialize<Dictionary<string, object>>(originalConfig);
                        if (cfg != null && cfg.ContainsKey("RTSysIP"))
                        {
                            cfg["RTSysIP"] = "";
                            File.WriteAllText(configPath, _json.Serialize(cfg));
                        }
                    }
                    catch { /* 配置解析失败则忽略，保持原样 */ }
                }
                RTSysHandlers.InvalidateConnectionStateCache();

                // 前置：在本地缓存中写入两个测试程序（模拟离线编辑后留下的缓存）
                ProgramWorkspace.WriteLocalSource("TEST_PROG", "MOVE(0,1000)\nSTOP\n");
                ProgramWorkspace.WriteLocalSource("OTHER_PROG", "VR[10]=5\nWAIT(1)\n");

                // ---- 1) list_programs：离线成功，返回本地缓存程序，origin="local" ----
                var listResult = RTSysHandlers.ListPrograms();
                var listDict = ToDict(listResult);
                Assert(listDict != null, "list_programs returns serializable object (offline)");
                Assert(listDict.ContainsKey("items"), "list_programs has items key");
                var items = listDict["items"] as System.Collections.IList;
                Assert(items != null, "list_programs items is a list");
                var names = items.Cast<Dictionary<string, object>>().Select(i => i["name"]?.ToString()).ToList();
                Assert(names.Contains("TEST_PROG"), "list_programs contains TEST_PROG (from local cache)");
                Assert(names.Contains("OTHER_PROG"), "list_programs contains OTHER_PROG (from local cache)");
                var testItem = items.Cast<Dictionary<string, object>>().FirstOrDefault(i => i["name"]?.ToString() == "TEST_PROG");
                Assert(testItem != null, "TEST_PROG item exists");
                Assert((string)testItem["origin"] == "local", "TEST_PROG origin is local");
                Assert((string)testItem["storage"] == "local", "TEST_PROG storage is local");

                // ---- 2) read_source：本地缓存命中，离线成功 ----
                var readResult = RTSysHandlers.ReadSource("TEST_PROG", null);
                var readDict = ToDict(readResult);
                Assert(readDict != null, "read_source returns serializable object (offline cache hit)");
                Assert(readDict.ContainsKey("sourceCode"), "read_source has sourceCode");
                Assert((readDict["sourceCode"]?.ToString() ?? "").Contains("MOVE(0,1000)"), "read_source returns cached source");
                Assert(readDict.ContainsKey("fromLocal"), "read_source has fromLocal field");
                Assert((bool)readDict["fromLocal"] == true, "read_source fromLocal is true (cache hit)");

                // ---- 3) read_source：未缓存程序 + 无控制器 → 结构化错误（NotConfigured），不抛异常 ----
                var readMissing = RTSysHandlers.ReadSource("NOT_CACHED_PROG", null);
                var readMissingDict = ToDict(readMissing);
                Assert(readMissingDict != null, "read_source (missing, offline) returns serializable object");
                Assert(readMissingDict.ContainsKey("success"), "read_source (missing) has success field");
                Assert((bool)readMissingDict["success"] == false, "read_source (missing) success is false");
                Assert(readMissingDict.ContainsKey("connectionState"), "read_source (missing) has connectionState");
                Assert((string)readMissingDict["connectionState"] == "NotConfigured", "read_source (missing) connectionState is NotConfigured");
                Assert(readMissingDict.ContainsKey("requiresController"), "read_source (missing) has requiresController");
                Assert((bool)readMissingDict["requiresController"] == true, "read_source (missing) requiresController is true");

                // ---- 4) write_source：离线成功，savedLocally=true, downloadedToController=false, pendingUpload=true ----
                var writeResult = RTSysHandlers.WriteSource("NEW_PROG", new Dictionary<string, object>
                {
                    { "name", "NEW_PROG" },
                    { "sourceCode", "MOVEABS(0,2000)\nDELAY(100)\n" }
                });
                var writeDict = ToDict(writeResult);
                Assert(writeDict != null, "write_source returns serializable object (offline)");
                Assert(writeDict.ContainsKey("success"), "write_source has success field");
                Assert((bool)writeDict["success"] == true, "write_source success is true offline");
                Assert(writeDict.ContainsKey("savedLocally"), "write_source has savedLocally");
                Assert((bool)writeDict["savedLocally"] == true, "write_source savedLocally is true");
                Assert(writeDict.ContainsKey("downloadedToController"), "write_source has downloadedToController");
                Assert((bool)writeDict["downloadedToController"] == false, "write_source downloadedToController is false offline");
                Assert(writeDict.ContainsKey("pendingUpload"), "write_source has pendingUpload");
                Assert((bool)writeDict["pendingUpload"] == true, "write_source pendingUpload is true offline");
                // 诚实性：绝不声称已下载到控制器（消息中不得出现“已下载到控制器”）
                Assert(!writeDict.ContainsKey("message") || !writeDict["message"].ToString().Contains("已下载到控制器"), "write_source message does NOT claim downloaded to controller");
                Assert(!writeDict.ContainsKey("message") || writeDict["message"].ToString().Contains("尚未下载到控制器"), "write_source message clearly states it is NOT yet downloaded to controller");

                // 确认文件确实写入了本地工作区
                var newProgPath = Path.Combine(tempDir, "NEW_PROG.bas");
                Assert(File.Exists(newProgPath), "write_source persisted NEW_PROG.bas to local workspace");

                // ---- 5) patch_source：离线成功，同write_source的诚实性字段 ----
                var patchResult = RTSysHandlers.PatchSource("TEST_PROG", new Dictionary<string, object>
                {
                    { "name", "TEST_PROG" },
                    { "operations", new List<object> { new Dictionary<string, object> { { "old_string", "STOP" }, { "new_string", "WAIT(5)" } } } }
                });
                var patchDict = ToDict(patchResult);
                Assert(patchDict != null, "patch_source returns serializable object (offline)");
                Assert(patchDict.ContainsKey("success"), "patch_source has success field");
                Assert((bool)patchDict["success"] == true, "patch_source success is true offline");
                Assert((bool)patchDict["savedLocally"] == true, "patch_source savedLocally is true");
                Assert((bool)patchDict["downloadedToController"] == false, "patch_source downloadedToController is false offline");
                Assert((bool)patchDict["pendingUpload"] == true, "patch_source pendingUpload is true offline");

                // ---- 6) search_code：离线成功，在本地缓存中搜索 ----
                var searchResult = RTSysHandlers.SearchCode("MOVEABS");
                var searchDict = ToDict(searchResult);
                Assert(searchDict != null, "search_code returns serializable object (offline)");
                Assert(searchDict.ContainsKey("matchCount"), "search_code has matchCount");
                Assert(Convert.ToInt64(searchDict["matchCount"]) >= 1, "search_code found at least 1 match offline");
                var searchResults = searchDict["results"] as System.Collections.IList;
                Assert(searchResults != null, "search_code results is a list");
                Assert(searchResults.Count >= 1, "search_code has at least one result offline");

                // ---- 7) compile_program：无控制器 → 友好错误，不抛异常 ----
                var compileResult = RTSysHandlers.CompileProgram("TEST_PROG");
                var compileDict = ToDict(compileResult);
                Assert(compileDict != null, "compile_program returns serializable object (offline)");
                Assert(compileDict.ContainsKey("success"), "compile_program has success field");
                Assert((bool)compileDict["success"] == false, "compile_program success is false offline");
                Assert(compileDict.ContainsKey("error"), "compile_program has error field");
                var compileErr = compileDict["error"]?.ToString() ?? "";
                Assert(!compileErr.StartsWith("RTSysIP not configured"), "compile_program error is friendly Chinese, not raw English");
                Assert(compileDict.ContainsKey("connectionState"), "compile_program has connectionState");
                Assert((string)compileDict["connectionState"] == "NotConfigured", "compile_program connectionState is NotConfigured");
                Assert(compileDict.ContainsKey("requiresController"), "compile_program has requiresController");
                Assert((bool)compileDict["requiresController"] == true, "compile_program requiresController is true");

                // ---- 8) run_program：无控制器 → 友好错误 ----
                var runResult = RTSysHandlers.RunProgram("TEST_PROG", null);
                var runDict = ToDict(runResult);
                Assert(runDict != null, "run_program returns serializable object (offline)");
                Assert(runDict.ContainsKey("success"), "run_program has success field");
                Assert((bool)runDict["success"] == false, "run_program success is false offline");
                Assert(runDict.ContainsKey("error"), "run_program has error field");
                Assert((string)runDict["connectionState"] == "NotConfigured", "run_program connectionState is NotConfigured");
                Assert((bool)runDict["requiresController"] == true, "run_program requiresController is true");

                // ---- 9) read_vr：无控制器 → 友好错误 ----
                var vrResult = RTSysHandlers.ReadVR(0, 1);
                var vrDict = ToDict(vrResult);
                Assert(vrDict != null, "read_vr returns serializable object (offline)");
                Assert(vrDict.ContainsKey("success"), "read_vr has success field");
                Assert((bool)vrDict["success"] == false, "read_vr success is false offline");
                Assert((string)vrDict["connectionState"] == "NotConfigured", "read_vr connectionState is NotConfigured");
                Assert((bool)vrDict["requiresController"] == true, "read_vr requiresController is true");

                // ---- 10) get_status：离线成功，携带 pendingUploads ----
                var statusResult = RTSysHandlers.GetStatus();
                var statusDict = ToDict(statusResult);
                Assert(statusDict != null, "get_status returns serializable object (offline)");
                Assert((bool)statusDict["success"] == true, "get_status success is true offline");
                Assert((string)statusDict["connectionState"] == "NotConfigured", "get_status connectionState is NotConfigured offline");
                Assert(statusDict.ContainsKey("pendingUploads"), "get_status has pendingUploads field");
                var pendingUploads = statusDict["pendingUploads"] as System.Collections.IList;
                Assert(pendingUploads != null, "get_status pendingUploads is a list");
                Assert(pendingUploads.Count >= 2, "get_status pendingUploads includes NEW_PROG and TEST_PROG");

                Console.WriteLine("    (isolated workspace: " + tempDir + ")");
            }
            finally
            {
                // 清理临时工作区并恢复默认目录
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
                catch { }
                ProgramWorkspace.ResetToDefaultDir();
                RTSysHandlers.InvalidateConnectionStateCache();

                // 恢复原始 config.json
                if (originalConfig != null)
                    File.WriteAllText(configPath, originalConfig);
                else if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }

        /// <summary>
        /// 旧版“本地程序缓存调查”：保留为引用，确认没有可离线读取的程序源码缓存（现已由 ProgramWorkspace 实现）。
        /// </summary>
        private static void TestLocalProgramCache()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var rtsysAiDir = Path.Combine(appData, "RTSysAI");
            var backupsDir = Path.Combine(rtsysAiDir, "backups");

            bool backupsDirExists = Directory.Exists(backupsDir);
            Assert(backupsDirExists, "RTSysAI 目录下存在 backups 子目录");

            var backupFiles = backupsDirExists ? Directory.GetFiles(backupsDir) : new string[0];
            Assert(backupFiles.Length == 0, "backups 目录为空（仅用于写前备份，不缓存可离线读取的程序源码）");

            // 确认没有 .src/.zar 或程序源文件可供离线读取
            var srcFiles = Directory.GetFiles(rtsysAiDir, "*.src", SearchOption.AllDirectories);
            Assert(srcFiles.Length == 0, "没有本地 .src 程序源文件可供离线读取");

            var zarFiles = Directory.GetFiles(rtsysAiDir, "*.zar", SearchOption.AllDirectories);
            Assert(zarFiles.Length == 0, "没有本地 .zar 编译文件可供离线读取");
        }
    }
}
