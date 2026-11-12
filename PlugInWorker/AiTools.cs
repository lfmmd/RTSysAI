using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- Tool Execution ----

        private static readonly HashSet<string> DisabledHmiTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hmi_list_windows", "hmi_read_window", "hmi_get_feature",
            "hmi_set_feature", "hmi_set_geometry", "hmi_create_control"
        };

        private static readonly HashSet<string> WriteTools = new HashSet<string>
        {
            "write_source", "patch_source", "write_vr", "write_table",
            "create_program", "delete_program", "rename_program", "copy_program",
            "restore_backup",
            "save_project", "open_project",
            "run_program", "stop_program",
            "update_memory",
            // HMI 内部对象模型写操作（调用 RTSys 进程内 ExtMainHmi.dll 导出符号）
            "hmi_set_feature", "hmi_set_geometry", "hmi_create_control"
        };

        // 低风险写工具子集：程序编辑（写/补丁/新建）+ 编译 + 保存/打开工程文件。纯工程文件操作，可重建、不碰实时控制器，
        // 自动放行免逐次确认（减少确认摩擦）。必须是 WriteTools 的子集——Plan Mode 仍靠 WriteTools 拦截全部写工具。
        // 删除/重命名/复制程序不可逆或易误操作，必须逐次人工确认，不得加入本集合。
        // 运行/停止/上下传/写 VR·TABLE·IEC 等影响实时控制器状态的，也不在本集合内，仍需人工逐次确认。
        private static readonly HashSet<string> AutoAllowWriteTools = new HashSet<string>
        {
            "write_source", "patch_source", "create_program", "save_project", "open_project"
        };

        // 纯 IO 工具：不需要 UI 线程（不访问 MP 项目模型），可绕过 DispatcherHelper.Invoke 直接调用。
        // 注意：Worker（RTSysPlugInWorker.exe）是头部控制台进程，从未调用 DispatcherHelper.Capture()，
        // 因此 _dispatcher 为 null。全部工具均放入本集合，避免 DispatchHelper.Invoke()
        // 因 _dispatcher 为 null 而抛 NullReferenceException。
        // 对 RTSys 不适用的 stub 工具（RTSysHandlers 返回 Error "not supported"）仍保留在
        // PureIoTools 中以避免崩溃，但已从 BuildToolDefinitions 中移除，AI 不会看到它们。
        private static readonly HashSet<string> PureIoTools = new HashSet<string>
        {
            "get_status", "list_programs", "read_source", "write_source", "patch_source",
            "read_vr", "write_vr", "read_table", "write_table", "list_axes",
            "get_axis_detail", "read_drive_param", "write_drive_param",
            "read_ethercat_sdo", "write_ethercat_sdo", "validate_basic", "run_program",
            "stop_program", "lookup_command", "read_skill", "discover_skills",
            "search_code", "update_memory", "list_plugins",
            "list_project_items", "list_backups", "restore_backup",
            "task_create", "task_update", "task_list",
            "enter_plan_mode", "exit_plan_mode", "research", "review", "debug",
            "explore", "verify",
            // 控制器连接控制工具（无需 UI 线程，纯 IO）
            "controller_autoconnect", "controller_simulator", "controller_status",
            "create_program", "delete_program", "copy_program",
            "rename_program", "save_project", "open_program",
            "open_project",
            "list_digital_io", "read_digital_io",
            "list_analogue_io", "read_analogue_io",
            "list_alarms",
            // HMI handlers marshal native object access to the RTSys UI thread themselves.
            "hmi_list_windows", "hmi_read_window", "hmi_get_feature",
            "hmi_set_feature", "hmi_set_geometry", "hmi_create_control"
        };

        private string ExecuteTool(string name, Dictionary<string, object> input)
        {
            try
            {
                if (DisabledHmiTools.Contains(name))
                    return "BLOCKED: HMI 操作能力目前已暂停，不能读取或修改 HMI 页面。";

                if (!AiToolPolicy.IsEnabled(name, ToolMode))
                    return "BLOCKED: Tool '" + name + "' is disabled by the current tool profile (" + ToolMode + "). Change the tool profile in settings to enable it.";

                // P0: lookup_command 同会话去重 — 避免重复查询同一命令的完整 HTML（每次 ~16KB）。
                // 仅主线生效：子 agent 的调用在隔离 subMessages，dedup 扫 _conversationHistory 既扫不到自己的、
                // 又会误命中主线历史返回误导性 note（子 agent 看不到那个 tool_result）。子 agent 靠 prompt + cap 兜底。
                if (!_inSubagent && string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase))
                {
                    var dedupResult = TryDedupLookupCommand(input);
                    if (dedupResult != null)
                        return _json.Serialize(dedupResult);
                }

                // 写前备份已下沉到 RTSysHandlers（write/patch/delete/rename 统一覆盖 AI/HTTP/UI）。

                // Write operations need confirmation
                if (WriteTools.Contains(name))
                {
                    // Plan Mode 拒绝：AI 必须先 exit_plan_mode 让用户审批计划
                    if (_planMode)
                    {
                        return "BLOCKED: Plan Mode is active. You cannot modify controller state, programs, or VR/TABLE in Plan Mode. " +
                               "Use read-only tools (read_source / list_programs / get_status / lookup_command / etc.) to complete your investigation, " +
                               "then call exit_plan_mode to present your plan for user approval. After approval, Plan Mode is disabled and write tools become available.";
                    }
                    // 风险分级 + 会话级许可：
                    // - 编辑/编译类（AutoAllowWriteTools）：本对话首次确认后置 _sessionEditApproved=true，后续同类直接放行；
                    // - 运行/上下传/变量写入类：每次都确认（不受会话许可影响）。
                    bool isEditClass = AutoAllowWriteTools.Contains(name);
                    bool needsConfirm = isEditClass ? !_sessionEditApproved : true;
                    if (needsConfirm)
                    {
                        var argsPreview = _json.Serialize(input);
                        var accepted = OnConfirmWrite?.Invoke(name, argsPreview) ?? false;
                        if (!accepted)
                            return "User rejected this operation.";
                        if (isEditClass)
                            _sessionEditApproved = true;
                    }
                }

                // 纯 IO 工具绕过 DispatcherHelper 直接执行（可在 Task.Run 内并行）；
                // 其他工具仍封送到 UI 线程（MP 项目模型访问需要 STA）。
                var result = PureIoTools.Contains(name)
                    ? DispatchTool(name, input)
                    : DispatcherHelper.Invoke(() => DispatchTool(name, input));
                var resultStr = JsonText.UnescapeApostrophes(_json.Serialize(result));
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: {Truncate(resultStr, 300)}");
                return resultStr;
            }
            catch (Exception ex)
            {
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: {Lang.L("错误", "ERROR")} - {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        // ExecuteTool 返回的 content 字符串是否表示工具执行失败，供主循环据此标 tool_result.is_error。
        // 失败信号：异常("Error: ") / Plan Mode 拒绝("BLOCKED:") / 用户拒绝("User rejected") /
        //          DispatchTool 内部 error（验证拦截 / 未知工具 / 工具内部错误，序列化成 {"error":...} 顶层键）。
        // "error": 不误判 compile 的 {"errors":[...]}（复数 s 隔断）或 read_source 文本（JSON 转义后引号被转义）；
        // compile 编译报错返回 {success:false, errors:[...]} 不属工具执行失败，正确地不标 is_error。
        internal static bool IsToolError(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            return content.StartsWith("Error: ", StringComparison.Ordinal)
                || content.StartsWith("BLOCKED:", StringComparison.Ordinal)
                || content.StartsWith("User rejected", StringComparison.Ordinal)
                || content.IndexOf("\"error\":", StringComparison.Ordinal) >= 0;
        }

        private object DispatchTool(string name, Dictionary<string, object> input)
        {
            switch (name)
            {
                case "research":
                case "review":
                case "debug":
                case "explore":
                case "verify":
                {
                    // 委派给子 agent（research/review/debug/explore/verify）：独立上下文跑调研循环，只回传结论文本。
                    // 差异：prompt（GetSubagentPrompt）+ schema 工具池（SubagentToolPools）+ thinking（review/debug/verify 跟随全局开关）。运行时拦截共用 SubagentReadTools 超集。
                    const int MaxSubagentDispatchesPerTurn = 2;
                    if (_subagentDispatchesThisTurn >= MaxSubagentDispatchesPerTurn)
                        return new { error = "subagent dispatch limit reached for this user request (" + MaxSubagentDispatchesPerTurn + "). Do not dispatch more subagents; produce the final answer from the information already gathered." };
                    var query = GetStr(input, "query");
                    if (string.IsNullOrEmpty(query))
                        return new { error = "query is required — describe what the subagent should investigate" };
                    int maxTurns = GetInt(input, "max_turns", 12);
                    var (conclusion, success) = RunSubagent(query, name, maxTurns, _currentCancellationToken);
                    // 子 agent 失败（API 错误 / 无文本产出）→ 返回 error 触发 tool_result.is_error，主模型据此重试或如实告知（不再把失败兜底文本伪装成结论）
                    if (!success)
                        return new { error = name + " subagent failed to produce a conclusion (API errors or no textual output). Consider retrying, possibly with a more specific query." };
                    // 只统计成功派发，避免一次 API 失败就耗掉重试额度
                    _subagentDispatchesThisTurn++;
                    return new { conclusion = conclusion };
                }
                case "get_status": return RTSysHandlers.GetStatus();
                case "list_programs": return RTSysHandlers.ListPrograms();
                case "list_backups": return ProgramBackup.ListBackups(GetStr(input, "name"));
                case "restore_backup": return ProgramBackup.RestoreBackup(GetStr(input, "file"), GetStr(input, "name"));
                case "read_source":
                {
                    var readResult = RTSysHandlers.ReadSource(GetStr(input, "name"), input);
                    var srcText = readResult is string s ? s : _json.Serialize(readResult);
                    RecordFileRead(GetStr(input, "name"), Truncate(srcText, MaxRestoredFileChars));
                    return readResult;
                }
                case "write_source":
                {
                    var progName = GetStr(input, "name");
                    if (RTSysHandlers.GetProgramDialect(progName) == "rtbasic")
                    {
                        var code = GetStr(input, "sourceCode") ?? "";
                        var errs = ValidateTrioBasicCodeErrors(code);
                        if (errs.Count > 0)
                            return new { error = "BLOCKED by RTBasic validation:\n  " + string.Join("\n  ", errs) };
                    }
                    return RTSysHandlers.WriteSource(progName, input);
                }
                case "patch_source":
                {
                    var progName = GetStr(input, "name");
                    if (RTSysHandlers.GetProgramDialect(progName) == "rtbasic")
                    {
                        var errs = new List<string>();
                        if (input.TryGetValue("operations", out var opsObj) && opsObj is List<object> ops)
                        {
                            foreach (var op in ops)
                            {
                                var dict = op as Dictionary<string, object>;
                                if (dict == null) continue;
                                var newStr = GetStr(dict, "new_string") ?? GetStr(dict, "content") ?? "";
                                if (!string.IsNullOrEmpty(newStr))
                                {
                                    errs.AddRange(ValidateTrioBasicCodeErrors(newStr));
                                }
                            }
                        }
                        if (errs.Count > 0)
                            return new { error = "BLOCKED by RTBasic validation:\n  " + string.Join("\n  ", errs) };
                    }
                    return RTSysHandlers.PatchSource(progName, input);
                }
                case "read_vr": return RTSysHandlers.ReadVR(GetHexInt(input, "address"), GetInt(input, "count", 1));
                case "write_vr": return RTSysHandlers.WriteVR(GetHexInt(input, "address"), input);
                case "read_table": return RTSysHandlers.ReadTable(GetHexInt(input, "address"), GetInt(input, "count", 1));
                case "write_table": return RTSysHandlers.WriteTable(GetHexInt(input, "address"), input);
                case "list_axes": return RTSysHandlers.ListAxes();
                case "get_axis_detail": return RTSysHandlers.GetAxisDetail(GetInt(input, "index"));
                case "copy_program": return RTSysHandlers.CopyProgram(GetStr(input, "name"), input);
                case "list_digital_io": return RTSysHandlers.ListDigitalIO();
                case "read_digital_io": return RTSysHandlers.ReadDigitalIO(GetInt(input, "index"));
                case "list_analogue_io": return RTSysHandlers.ListAnalogueIO();
                case "read_analogue_io": return RTSysHandlers.ReadAnalogueIO(GetInt(input, "index"));
                case "read_drive_param": return RTSysHandlers.ReadDriveParam(GetInt(input, "axis"), GetHexInt(input, "address"));
                case "write_drive_param": return RTSysHandlers.WriteDriveParam(GetInt(input, "axis"), GetHexInt(input, "address"), input);
                case "read_ethercat_sdo":
                    return RTSysHandlers.EtherCATReadSDO(GetInt(input, "slot", 0),
                                                    (uint)GetLong(input, "position"),
                                                    (uint)GetHexLong(input, "index"),
                                                    (uint)GetHexLong(input, "subindex"),
                                                    GetStr(input, "type") ?? "uint16");
                case "write_ethercat_sdo": return RTSysHandlers.EtherCATWriteSDOFromDict(input);
                case "list_alarms": return RTSysHandlers.ListAlarms();
                case "list_plugins": return RTSysHandlers.ListAttachedPlugins();
                case "open_project": return RTSysHandlers.OpenProject(input);
                case "list_project_items": return RTSysHandlers.ListProjectItems();
                case "hmi_list_windows": return RTSysHandlers.HmiListWindows();
                case "hmi_read_window": return RTSysHandlers.HmiReadWindow(GetInt(input, "windowId"));
                case "hmi_get_feature": return RTSysHandlers.HmiGetFeature(
                    GetStr(input, "targetType") ?? "control",
                    GetInt(input, "targetId"),
                    GetInt(input, "mainId"),
                    GetInt(input, "subId"),
                    GetInt(input, "index", 0),
                    input.ContainsKey("windowId") ? (int?)GetInt(input, "windowId") : (int?)null);
                case "hmi_set_feature":
                    {
                        object v = input.ContainsKey("value") ? input["value"] : null;
                        // 传 int/数值时 valueNumber 给数值通道；字符串值走 valueString。
                        // 之前无条件把 v.ToString() 塞给 valueString，导致数值 1003 写成字符串通道而挂。
                        double? vn = null;
                        object vnRaw;
                        if (input.TryGetValue("valueNumber", out vnRaw) && vnRaw != null)
                        {
                            vn = Convert.ToDouble(vnRaw);
                        }
                        if (vn == null && v != null && !(v is bool) && !(v is string))
                        {
                            try { vn = Convert.ToDouble(v); } catch { }
                        }
                        string vs = GetStr(input, "valueString");
                        if (vs == null && v is string) vs = (string)v;
                        return RTSysHandlers.HmiSetFeatureCompat(
                            GetStr(input, "targetType") ?? "control",
                            GetInt(input, "targetId"),
                            GetInt(input, "mainId"),
                            GetInt(input, "subId"),
                            GetInt(input, "index", 0),
                            v, vs, vn,
                            input.ContainsKey("windowId") ? (int?)GetInt(input, "windowId") : (int?)null);
                    }
                case "hmi_set_geometry":
                    // value/valueNumber 可能以字符串传入；HmiSetGeometry 用 GetDouble 解析并写 SetFeatureDouble(1002/1003)，
                    // 故此处仅透传 body（RTSysHandlers 内 Safe 兜底异常信息）。
                    return RTSysHandlers.HmiSetGeometry(input);
                case "hmi_create_control": return RTSysHandlers.HmiCreateControl(input);
                case "activate_rtsys_window":
                    return RTSysHandlers.ActivateRtsysWindow();
                case "create_program": return RTSysHandlers.CreateProgram(input);
                case "delete_program": return RTSysHandlers.DeleteProgram(GetStr(input, "name"));
                case "rename_program": return RTSysHandlers.RenameProgram(GetStr(input, "name"), input);
                case "compile_program": return RTSysHandlers.Compile(GetStr(input, "name"));
                case "run_program": return RTSysHandlers.RunProgram(GetStr(input, "name"), input);
                case "stop_program": return RTSysHandlers.StopProgram(GetStr(input, "name"), input);
                case "save_project": return RTSysHandlers.SaveProject();
                case "open_program": return RTSysHandlers.OpenProgram(GetStr(input, "name"));
                case "lookup_command": return LookupCommand(GetStr(input, "query"), GetStr(input, "full"), GetStr(input, "library"));
                case "validate_basic": return ValidateTrioBasicCodePublic(GetStr(input, "code") ?? "");
                case "read_skill": return ReadSkill(GetStr(input, "name"));
                case "discover_skills": return DiscoverSkills(GetStr(input, "category"));
                case "search_code": return RTSysHandlers.SearchCode(GetStr(input, "query"), GetBool(input, "caseSensitive"));
                case "update_memory":
                    {
                        var memContent = GetStr(input, "content");
                        if (string.IsNullOrEmpty(memContent))
                            return new { error = "content is required" };
                        SaveMemory(memContent);
                        return new { success = true, message = "Memory updated successfully." };
                    }

                case "task_create": return TaskCreate(GetStr(input, "subject"), GetStr(input, "description"));
                case "task_update": return TaskUpdate(GetInt(input, "id", 0), GetStr(input, "status"), GetStr(input, "subject"), GetStr(input, "description"));
                case "task_list": return TaskList();
                case "enter_plan_mode": return EnterPlanMode();
                case "exit_plan_mode": return ExitPlanMode(GetStr(input, "plan"));

                case "controller_autoconnect": return RTSysHandlers.TriggerAutoConnectAsync().Result;
                case "controller_simulator": return RTSysHandlers.TriggerSimulatorConnectAsync().Result;
                case "controller_status": return RTSysHandlers.GetConnectionStatus();

                default: return new { error = $"Unknown tool: {name}" };
            }
        }

        // ---- Plan Mode 实现 ----

        private object EnterPlanMode()
        {
            _planMode = true;
            OnPlanModeChanged?.Invoke(true);
            return new
            {
                plan_mode = true,
                hint = "Plan Mode is now active. All write/modify tools (write_source, patch_source, run_program, write_vr, etc.) are blocked. " +
                       "Use read-only tools to investigate, then call exit_plan_mode with your plan to request user approval."
            };
        }

        private object ExitPlanMode(string plan)
        {
            if (string.IsNullOrEmpty(plan))
                return new { error = "plan is required — present what you intend to do and why" };

            // 请求用户审批。若 UI 未挂 OnConfirmPlan（Phase 1 范围：不动 UI），
            // 默认批准以避免 AI 在 plan mode 内死锁。
            // 用户可在 ChatPanel 接 OnConfirmPlan 回调启用审批 UI（Phase 2）。
            bool approved = OnConfirmPlan != null ? OnConfirmPlan(plan) : true;
            if (!approved)
            {
                // 用户拒绝：保持 plan mode，让 AI 继续调研或修订计划
                return new
                {
                    plan_mode = true,
                    approved = false,
                    hint = "User did not approve the plan. Plan Mode is still active. Revise the plan and call exit_plan_mode again, or do more investigation first."
                };
            }

            _planMode = false;
            OnPlanModeChanged?.Invoke(false);
            return new
            {
                plan_mode = false,
                approved = true,
                hint = "Plan approved. Write/modify tools are now available. Proceed with the approved plan."
            };
        }

        // ---- Task / Todo 系统实现 ----
        // 状态存储在 AiSession 的 _tasks 字段；不进入 conversation history。

        internal object TaskCreate(string subject, string description)
        {
            if (string.IsNullOrEmpty(subject))
                return new { error = "subject is required" };
            lock (_tasksLock)
            {
                var t = new TaskItem
                {
                    Id = _nextTaskId++,
                    Subject = subject,
                    Description = description ?? "",
                    Status = "pending",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                _tasks.Add(t);
                return new
                {
                    id = t.Id,
                    subject = t.Subject,
                    description = t.Description,
                    status = t.Status,
                    hint = "Task created. Use task_update(id, status) to mark progress (pending | in_progress | completed)."
                };
            }
        }

        internal object TaskUpdate(int id, string status, string subject, string description)
        {
            if (id <= 0)
                return new { error = "id is required" };
            lock (_tasksLock)
            {
                var t = _tasks.Find(x => x.Id == id);
                if (t == null)
                    return new { error = $"Task {id} not found" };
                if (!string.IsNullOrEmpty(status))
                {
                    var s = status.ToLowerInvariant();
                    if (s != "pending" && s != "in_progress" && s != "completed")
                        return new { error = "status must be: pending | in_progress | completed" };
                    t.Status = s;
                }
                if (!string.IsNullOrEmpty(subject))
                    t.Subject = subject;
                if (description != null)
                    t.Description = description;
                t.UpdatedAt = DateTime.Now;
                return new
                {
                    id = t.Id,
                    subject = t.Subject,
                    description = t.Description,
                    status = t.Status,
                    updated_at = t.UpdatedAt.Value.ToString("HH:mm:ss")
                };
            }
        }

        internal object TaskList()
        {
            lock (_tasksLock)
            {
                if (_tasks.Count == 0)
                    return new { count = 0, tasks = new object[0], hint = "No tasks. Use task_create to start tracking multi-step work." };
                var summary = _tasks.Select(t => new
                {
                    id = t.Id,
                    subject = t.Subject,
                    status = t.Status
                }).ToList();
                return new
                {
                    count = summary.Count,
                    pending = summary.Count(x => x.status == "pending"),
                    in_progress = summary.Count(x => x.status == "in_progress"),
                    completed = summary.Count(x => x.status == "completed"),
                    tasks = summary
                };
            }
        }

        // ---- Tool Definitions ----

        private static List<Dictionary<string, object>> _cachedToolDefs;
        private static readonly object _toolDefLock = new object();

        private static List<Dictionary<string, object>> GetToolDefinitions()
        {
            lock (_toolDefLock)
            {
                if (_cachedToolDefs == null)
                    _cachedToolDefs = BuildToolDefinitions();
                // 浅拷贝：CallApiStream 会在最后一个 tool 上加 cache_control，
                // 浅拷贝只复制 List 引用，不动内部 Dictionary，加 cache_control 时
                // new Dictionary(...) 创建新对象，不影响缓存。
                return AiToolPolicy.Filter(_cachedToolDefs, ToolMode)
                    .Where(t => !DisabledHmiTools.Contains(GetStringValue(t, "name")))
                    .ToList();
            }
        }

        // 子 agent 运行时拦截用的只读工具【超集】（仅 RTSys 支持的只读工具，不含 stub）。
        private static readonly HashSet<string> SubagentReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_status", "list_programs", "read_source", "read_vr", "read_table",
            "list_axes", "lookup_command", "read_skill", "discover_skills",
            "search_code", "get_axis_detail", "read_drive_param", "validate_basic",
            "read_ethercat_sdo", "list_plugins", "list_project_items",
            // HMI 内部对象模型只读工具（调用 RTSys 进程内 ExtMainHmi.dll）
            "hmi_list_windows", "hmi_read_window", "hmi_get_feature",
            // 新增：控制器连接状态查询（只读，子 agent 可用）
            "controller_status"
        };

        // 各 agentType 的 schema 工具池（按定位裁剪）。
        private static readonly Dictionary<string, HashSet<string>> SubagentToolPools =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "research", SubagentReadTools },
            { "review", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "read_source", "search_code", "lookup_command", "list_programs", "list_axes", "read_vr", "get_status" } },
            { "debug", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "get_status", "list_axes", "get_axis_detail", "read_vr", "read_table", "read_source" } },
            { "explore", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "list_programs", "list_project_items", "read_source", "search_code", "lookup_command", "get_status", "list_axes" } },
            { "verify", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "read_source", "search_code", "lookup_command", "get_status", "list_axes", "read_vr", "read_table" } },
        };

        // 子 agent 工具集：按 agentType 从主 tool 列表过滤对应池 + 浅拷贝（不污染 _cachedToolDefs）+ 末项 cache_control。
        private List<Dictionary<string, object>> BuildSubagentToolDefinitions(string agentType)
        {
            var pool = SubagentToolPools.TryGetValue(agentType, out var p) ? p : SubagentReadTools;
            var all = GetToolDefinitions();
            var filtered = all
                .Where(t => pool.Contains(GetStringValue(t, "name")))
                .Select(t => new Dictionary<string, object>(t))
                .ToList();
            if (filtered.Count > 0)
            {
                var last = new Dictionary<string, object>(filtered[filtered.Count - 1])
                {
                    { "cache_control", new { type = "ephemeral" } }
                };
                filtered[filtered.Count - 1] = last;
            }
            return filtered;
        }

        private static List<Dictionary<string, object>> BuildToolDefinitions()
        {
            return new List<Dictionary<string, object>>
            {
                Tool("get_status", "Get controller connection status, product name, firmware version, project name. ALWAYS works (even when no controller IP is configured) — the response includes 'connectionState' (NotConfigured | Disconnected | Connected), 'connected' (bool), and a 'hint' with actionable guidance. Also returns 'pendingUploads': program names saved locally but not yet downloaded to the controller. Call this FIRST if a controller-dependent tool fails: if connectionState is NotConfigured, ASK THE USER for the RTSys controller IP (they can set it in 「设置」); if Disconnected, tell the user to check the network and that the controller is powered on.", NoParams()),
                Tool("list_programs", "List all programs available for editing. Works OFFLINE when no controller IP is configured: returns programs from the local cache (origin=\"local\"). When a controller is connected, merges local cache + controller programs and marks each item's origin (\"project\" | \"local\" | \"controller\" | \"both\" | \"project+controller\") plus isDirty/lastSyncedUtc so the AI can tell which programs are saved locally but not yet downloaded to the controller. Programs marked origin=\"local\" exist only in the local cache and need an explicit download to take effect on the controller.", NoParams()),
                Tool("list_backups", "List automatic pre-write backups (program .bas and project .zpj). Optional name filters by program. Returns {success,count,backups:[{file,program,kind,reason,time,size}]}. Backups are created automatically before write_source/patch_source/delete_program/rename_program/create_program/copy_program and use the original file encoding (ANSI/UTF-8 auto-detected).", Props(("name", "Program name filter (optional)", true))),
                Tool("restore_backup", "Restore a .bas program from an automatic backup. file comes from list_backups; name is the target program (optional, defaults to the program recorded in the backup file name). Requires confirmation. Project-file (.zpj) backups are for manual restore only.", Props(("file", "Backup file name from list_backups", false), ("name", "Target program name (optional)", true))),
                Tool("read_source", "Read source code of a program. Works OFFLINE: reads from the local cache first; if not cached and the controller is connected, pulls from the controller AND caches it locally. If the program is neither cached locally nor reachable on a controller, returns a friendly connection error. The system auto-returns the FIRST CHUNK for files >400 lines or >12000 chars — the response includes totalLines and a hint when more chunks exist. You MUST check totalLines vs endLine and continue with startLine=endLine+1 to read subsequent chunks; NEVER assume the first response contains the whole file. The response also includes numberedSource: every line prefixed with its exact 1-based absolute line number exactly as shown in the RTSys editor gutter (blank lines are counted). When you mention or reason about line numbers, ALWAYS use numberedSource's numbers instead of counting lines yourself. sourceCode is the raw text (no prefixes) — use it verbatim for patch_source old_string.", Props(
                    ("name", "Program name", false),
                    ("startLine", "Starting 1-based line number (optional, for pagination)", true),
                    ("endLine", "Ending 1-based line number (optional, for pagination)", true)
                )),
                Tool("write_source", "Write full source code to a program. Works OFFLINE: ALWAYS saves to the local cache first. If the controller is connected, also compiles and downloads to the controller; the response distinguishes savedLocally vs downloadedToController so you never falsely claim the program is on the controller. If not connected, returns success with pendingUpload=true and a clear message that the program is saved locally and pending download. Requires confirmation.", Props(
                    ("name", "Program name", false),
                    ("sourceCode", "Full source code to write", false)
                )),
                Tool("patch_source", "Apply text-level edits to an EXISTING program by finding and replacing exact text snippets. Works OFFLINE: ALWAYS saves to the local cache first; if connected, also downloads to the controller. The response distinguishes savedLocally vs downloadedToController (never claim the program is on the controller when it is only saved locally). If not connected, returns success with pendingUpload=true. REQUIRES the program to already exist — cannot create new programs (use write_source or create_program first). More reliable than line-number based editing.", PropsMixed(
                    ("name", "Program name", false, "string"),
                    ("operations", "Array of {old_string:exact text to find in source, new_string:replacement text}. old_string must be unique in the source. If old_string is empty, new_string is appended to the end.", false, "array")
                )),
                Tool("create_program", "在当前 RTSys 项目中新建程序：创建 .bas 源文件并同步更新 .zpj 工程文件，不需要控制器连接。sourceCode 可选，缺省创建空程序文件。需确认后执行。", Props(
                    ("name", "Program name (new .bas file name)", false),
                    ("sourceCode", "Initial source code (optional; defaults to an empty file)", true)
                )),
                Tool("delete_program", "从当前 RTSys 项目中删除程序：删除 .bas 源文件并同步更新 .zpj 工程文件，不需要控制器连接。每次操作均需人工确认。", Props("name", "Program name")),
                Tool("rename_program", "重命名当前 RTSys 项目中的程序：重命名 .bas 源文件并同步更新 .zpj 工程文件，不需要控制器连接。每次操作均需人工确认。", Props(
                    ("name", "Current program name", false),
                    ("newName", "New program name", false)
                )),
                Tool("copy_program", "将当前工程中的 .bas 程序直接复制为新名称（工程文件场景使用文件级复制，不需要读取或重写源码，也不依赖 AI 上下文），并同步更新 .zpj 工程清单。复制本地缓存程序时也由工具直接完成，不要用 read_source + create_program/write_source 重建副本。每次操作均需人工确认。", Props(
                    ("name", "Source program name", false),
                    ("newName", "New program name", false)
                )),
                Tool("open_program", "在当前 RTSys 项目中打开程序（.bas）使编辑器就绪；操作项目文件，不需要控制器连接。", Props("name", "Program name")),
                Tool("read_vr", "Read VR variable values from controller", Props(
                    ("address", "Starting VR address (0-based)", false),
                    ("count", "Number of values to read", true)
                )),
                Tool("write_vr", "Write a value to a VR variable (requires confirmation)", Props(
                    ("address", "VR address", false),
                    ("value", "Value to write", false)
                )),
                Tool("read_table", "Read TABLE values from controller", Props(
                    ("address", "Starting TABLE address", false),
                    ("count", "Number of values to read", true)
                )),
                Tool("write_table", "Write values to TABLE (requires confirmation)", PropsMixed(
                    ("address", "Starting TABLE address", false, "string"),
                    ("values", "Array of values to write", false, "array")
                )),
                Tool("list_axes", "List all configured axes on the controller with their CURRENT state (position, velocity, status bits, servo/home/limit state, alarm code). Requires a controller connection.", NoParams()),
                Tool("list_digital_io", "List all digital inputs and outputs with their CURRENT values, plus the controller's IO point counts. Requires a controller connection.", NoParams()),
                Tool("read_digital_io", "Read one digital point: its input state and (when available) its output state. Requires a controller connection.", Props(
                    ("index", "Digital point index (0-based)", false)
                )),
                Tool("list_analogue_io", "List all analogue input/output channels with their CURRENT values and channel counts. Requires a controller connection.", NoParams()),
                Tool("read_analogue_io", "Read one analogue channel: input value and (when available) output value. Requires a controller connection.", Props(
                    ("index", "Analogue channel index (0-based)", false)
                )),
                Tool("list_alarms", "Report the controller's last error code/description and per-axis alarm codes. Note: zmotion.dll exposes no alarm history/table.", NoParams()),
                Tool("validate_basic", "Validate RTBasic source code against the command/signature index. Returns {ok, errors[]}. Use BEFORE write_source when unsure about command names or argument counts; also catches hallucinated commands. Works offline.", Props(
                    ("code", "RTBasic source code to validate", false)
                )),
                Tool("run_program", "Run a program on the controller (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("process", "Process number", true)
                )),
                Tool("stop_program", "Stop a running program (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("process", "Process number", true)
                )),
                Tool("lookup_command", "Look up command/keyword reference. Default returns name + signature + description (lightweight). Use full=true for complete HTML. Use library to scope to rtbasic/iec/plcopen.", Props(
                    ("query", "Command name or keyword to search (e.g. MOVE, MC_MoveAbsolute, FOR)", false),
                    ("full", "Set to \"true\" to load complete HTML documentation (heavier, use only when examples or detailed parameters are needed)", true),
                    ("library", "Scope search to: rtbasic | iec | plcopen (default: search all libraries)", true)
                )),
                Tool("read_skill", "Load full markdown content of a skill listed in the 'Available Skills' section of the system prompt. BLOCKING REQUIREMENT: when the user's request matches a skill's 'Use when:' description, invoke read_skill BEFORE writing any code or generating other response about the task. NEVER mention or claim to follow a skill without calling this tool first. Note: skills whose full body is already shown in the system prompt (e.g. 'Safe Coding Rules (MANDATORY)') are pre-loaded — do NOT re-read those.", Props(
                    ("name", "Skill name from Available Skills", false)
                )),
                Tool("discover_skills", "List available markdown skills with their name, description, and when_to_use trigger. Use this FIRST when you are unsure which skills exist or which one matches the user's task — cheaper than calling read_skill by trial and error. After finding a relevant skill, call read_skill(name) to load its full body.", Props(
                    ("category", "Filter by category (general | rtbasic | iec | plcopen). null/empty returns all skills.", true)
                )),
                Tool("search_code", "Search for a text pattern across the current RTSys project files (.zpj FileList) and the local cache. Works OFFLINE (no controller needed). Honors caseSensitive. Returns matching lines with line numbers and the source origin.", Props(
                    ("query", "Search text or pattern", false),
                    ("caseSensitive", "Whether search is case sensitive (default false)", true)
                )),
                Tool("get_axis_detail", "Get detailed live state for a single axis: position, target position, velocity, following error, status bits, servo/home/limit state, alarm code, torque and temperature.", Props(
                    ("index", "Axis index (0-based)", false)
                )),
                Tool("read_drive_param", "Read a drive parameter via DRIVE_READ", Props(
                    ("axis", "Axis number", false),
                    ("address", "Hex drive parameter address", false)
                )),
                Tool("write_drive_param", "Write a drive parameter via DRIVE_WRITE (requires confirmation)", Props(
                    ("axis", "Axis number", false),
                    ("address", "Hex drive parameter address", false),
                    ("value", "Value to write (number or string)", false)
                )),
                Tool("read_ethercat_sdo", "Read an EtherCAT SDO via CANopen", Props(
                    ("slot", "Slot (default 0)", true),
                    ("position", "Device position", false),
                    ("index", "Object index (decimal; e.g. 0x1000 = 4096)", false),
                    ("subindex", "Subindex", true),
                    ("type", "uint16 (default) / int32 / real32 / bool / string / ...", true)
                )),
                Tool("write_ethercat_sdo", "Write an EtherCAT SDO via CANopen (requires confirmation)", Props(
                    ("slot", "Slot (default 0)", true),
                    ("position", "Device position", false),
                    ("index", "Object index (decimal)", false),
                    ("subindex", "Subindex", true),
                    ("type", "Data type (default uint16)", true),
                    ("value", "Value", false)
                )),
                Tool("list_plugins", "List plugins installed under the RTSys Plug_in directory (name, path, DLLs). This is a host-installation inventory, not controller-attached services.", NoParams()),
                Tool("list_project_items", "List all project items with name/type/group", NoParams()),
                // ===== HMI 内部对象模型（全部来自 RTSys 进程内 ExtMainHmi.dll 导出符号） =====
                Tool("hmi_list_windows", "列出当前打开的 HMI 画面中所有窗口（编号/名称/尺寸/位置/控件数）。调用 RTSys 内部 ExtMainHmi.dll（FindWindowW/遍历窗口链表），不依赖控制器连接。必须在 RTSys 已打开 .hmi 画面时才有结果。", NoParams()),
                Tool("hmi_read_window", "读取指定窗口下的全部控件列表（id/类型/名称/几何/显示文本/特征数），调用 RTSys 内部 ExtMainHmi.dll 遍历控件链表与特征链表。", Props(
                    ("windowId", "窗口编号（来自 hmi_list_windows 的 id）", false)
                )),
                Tool("hmi_get_feature", "读取控件/窗口特征值（mainId/subId/index 三元组），调用 RTSys 内部 ExtMainHmi.dll FindFeature+GetFeatureInt。常用：mainId=1000 控件编号、1001 名称、1002 尺寸、1003 位置、1270 位置锁、20001 窗口编号。注意：控件编号在各窗口间重复，读控件必须同时传 windowId 定位到具体窗口，否则多窗命中同一编号会报歧义。", Props(
                    ("targetType", "目标类型：control（默认）或 window", true),
                    ("targetId", "目标编号（控件 id 或窗口编号）", false),
                    ("windowId", "控件所属窗口编号（读控件时必填；窗口级调用可省略）", true),
                    ("mainId", "特征主 ID", false),
                    ("subId", "特征子 ID（默认 0）", true),
                    ("index", "数组特征索引（默认 0）", true)
                )),
                Tool("hmi_set_feature", "写入控件/窗口特征值（整型、double、字符串按特征类型分发；几何特征使用控件 SetFeatureInt 覆盖实现并遵守 1270 锁与几何同步守卫）。写后回读验证，并触发特征面板和 HMI 画布刷新。需要确认。控件编号在各窗口间重复，写控件必须同时传 windowId，否则多窗命中同一编号会报歧义。", Props(
                    ("targetType", "目标类型：control（默认）或 window", true),
                    ("targetId", "目标编号（控件 id 或窗口编号）", false),
                    ("windowId", "控件所属窗口编号（写控件时必填；窗口级调用可省略）", true),
                    ("mainId", "特征主 ID", false),
                    ("subId", "特征子 ID（默认 0）", true),
                    ("index", "数组特征索引（默认 0）", true),
                    ("value", "要写入的值（整型/字符串/数值，字符串特征用 valueString）", false)
                )),
                Tool("hmi_set_geometry", "设置控件几何（x/y 位置 或 width/height 尺寸），调用 RTSys 内部 ExtMainHmi.dll 控件 SetFeatureInt(1002/1003)，守卫 1270 位置锁并同步 double/1081 特征。负值会在写入前拒绝。每次写入后重绘 HMI 画布；refresh=true 时还刷新特征面板。需要确认。控件编号在各窗口间重复，必须同时传 windowId，否则多窗命中同一编号会报歧义。", PropsMixed(
                    ("targetType", "目标类型：control（默认）", true, "string"),
                    ("targetId", "目标控件 id", false, "integer"),
                    ("windowId", "控件所属窗口编号（必填）", false, "integer"),
                    ("x", "左上角 X 坐标（位置模式）", true, "integer"),
                    ("y", "左上角 Y 坐标（位置模式）", true, "integer"),
                    ("width", "宽度（尺寸模式）", true, "integer"),
                    ("height", "高度（尺寸模式）", true, "integer"),
                    ("isSize", "true 表示写 width/height，false 表示写 x/y（默认 false）", true, "boolean"),
                    ("refresh", "写入后是否触发特征列表刷新（默认 false）", true, "boolean")
                )),
                Tool("hmi_create_control", "在指定窗口新建控件。controlType 使用 hmi_read_window/list 控件类型编号；ID 不可与窗口内现有控件重复。需要用户确认。", PropsMixed(
                    ("windowId", "目标窗口编号", false, "integer"),
                    ("controlId", "新控件编号", false, "integer"),
                    ("controlType", "控件类型编号（例如 Button=609）", true, "integer"),
                    ("x", "左上角 X 坐标", true, "integer"),
                    ("y", "左上角 Y 坐标", true, "integer"),
                    ("width", "宽度", true, "integer"),
                    ("height", "高度", true, "integer")
                )),
                Tool("activate_rtsys_window", "仅当用户明确要求将 RTSys 窗口置于前台时调用。普通 HMI 读写不需要前台；HMI 工具失败时不要调用此工具重试。无参数，返回 {success, activated, error?}。", NoParams()),
                Tool("save_project", "保存当前 RTSys 项目：将 .bas 源文件同步写入 .zpj 工程文件，不需要控制器连接。", NoParams()),
                Tool("open_project", "打开当前 RTSys 项目：加载 .bas 源文件与 .zpj 工程文件，不需要控制器连接。", NoParams()),
                Tool("update_memory", "Update your persistent memory (survives across sessions). ONLY call this when the user EXPLICITLY asks you to remember something (e.g. \"记住…\" / \"记住这个\" / \"下次记住\" / \"remember this\"); NEVER call it on your own — not because you finished a task, found an issue, the user mentioned a detail in passing, or you think something might be useful. Content replaces the entire memory file. Include all previous memories you want to keep plus new information. Use markdown formatting. Keep under 2000 tokens.", Props("content", "Full memory content to save")),
                Tool("task_create", "Create a new task to track a step in a multi-step workflow. Use this proactively when the user's request requires 3+ distinct steps. Each task gets a stable id you can update later.", Props(
                    ("subject", "Short imperative title (e.g. 'Fix axis 2 homing sequence')", false),
                    ("description", "What needs to be done, including acceptance criteria if relevant", true)
                )),
                Tool("task_update", "Update an existing task's status or content. Mark in_progress when starting work, completed when done. Keep exactly one task in_progress at a time when possible.", Props(
                    ("id", "Task id from task_create / task_list", false),
                    ("status", "New status: pending | in_progress | completed", true),
                    ("subject", "New subject (optional)", true),
                    ("description", "New description (optional)", true)
                )),
                Tool("task_list", "List all tasks with their current status. Use this to verify progress and pick the next task to work on.", NoParams()),
                Tool("enter_plan_mode", "Enter Plan Mode: all write/modify tools (write_source, patch_source, run_program, write_vr, write_table, etc.) are blocked. Use this at the start of a non-trivial task to investigate first with read-only tools, then present a plan via exit_plan_mode for user approval before making any changes. Avoid for trivial single-step requests, AND for batch same-operation-on-many-items tasks (fix/check all programs) — those are independent sub-tasks; use `task_create` + process one at a time instead.", NoParams()),
                Tool("exit_plan_mode", "Present your plan to the user for approval and exit Plan Mode. The user will approve or reject. If rejected, Plan Mode stays active — revise the plan or do more investigation. If approved, write/modify tools become available.", Props(
                    ("plan", "The complete plan: what files/VR/programs you will change, why, and the verification steps. Be specific.", false)
                )),
                Tool("research", "Delegate an investigation task to a research subagent that runs in its OWN isolated context with read-only tools (lookup_command, read_source, read_skill, search_code, get_status, list_*, read_* etc.) and returns a digested conclusion. USE THIS whenever you need COMPLETE command info (full syntax / examples / params / preconditions) — the subagent reads the full docs in its own context and returns only a digest, so the raw HTML never pollutes the main conversation. Works for a single command or many; batch several commands into one research call when convenient. For a quick name/signature check use lookup_command directly. The subagent cannot write or modify anything.", Props(
                    ("query", "The investigation task: what to find out, which commands/files to consult, and what the conclusion should contain. Be specific about which commands' syntax/examples you need.", false),
                    ("max_turns", "Max investigation turns for the subagent (default 12, max 12). Lower for simple lookups.", true)
                )),
                Tool("review", "Delegate a code-review task to a review subagent that runs in its OWN isolated context with read-only tools and returns a SEVERITY-RANKED review report. USE THIS to review an EXISTING program for bugs, safety hazards, reserved-name collisions, and quality issues (e.g. before trusting unfamiliar code, or after a big change). The subagent reads the program fully, checks command usage against the docs, and reports Critical/Warning/Style findings with program:line pointers — it does NOT modify code. Do NOT use review for a single command lookup (use lookup_command) or to fix code (do that yourself after reading the report).", Props(
                    ("query", "The review task: which program(s) to review and what to focus on (bugs, safety, naming, dead code, etc.).", false),
                    ("max_turns", "Max review turns for the subagent (default 12, max 12).", true)
                )),
                Tool("debug", "Delegate a runtime-problem diagnosis to a debug subagent that runs in its OWN isolated context with read-only tools and returns a ROOT-CAUSE diagnosis. USE THIS to investigate why something misbehaves AT RUNTIME (axis won't move, error/fault reported, unexpected VR value, program not doing what it should) — the subagent reads LIVE controller state (axes, VR/TABLE, running processes, events, drive/EtherCAT faults) AND the relevant source code, then correlates them. It does NOT modify anything; it gives a diagnosis (symptom / root cause / evidence / fix direction). For a pure docs question use research; for static code quality use review.", Props(
                    ("query", "The diagnosis task: the observed symptom (what goes wrong, when), and which program/axis/VR is involved.", false),
                    ("max_turns", "Max diagnostic turns for the subagent (default 12, max 12).", true)
                )),
                Tool("explore", "Delegate a broad project survey to an explore subagent that runs in its OWN isolated context with read-only tools and returns a FINDINGS INDEX (what programs exist, what each does, where specific logic lives). USE THIS when you are UNFAMILIAR with the project and need a map before acting — the subagent builds a one-line-per-program overview by skimming (not deep-reading) each program and locating symbols via search_code. For deep syntax details of ONE command use research instead; explore favors breadth over depth.", Props(
                    ("query", "The exploration task: what to map or locate (project structure overview, or 'where is X defined / which programs use Y').", false),
                    ("max_turns", "Max exploration turns for the subagent (default 12, max 12).", true)
                )),
                Tool("verify", "Delegate an independent VERIFICATION to a verify subagent that runs in its OWN isolated context with read-only tools and returns a single VERDICT (PASS / FAIL / PARTIAL). USE THIS right after you write or modify a program — the subagent independently reads the code, checks each command's correct usage, and cross-checks LIVE controller state (are driven axes connected? are VR/TABLE indices initialized? do running processes set the expected values?) to judge whether the program is correct and safe. It does NOT compile or modify anything; you pass it the program (and any compile result you already obtained) and it gives an independent second opinion with a clear verdict. For listing defects without a verdict use review; for runtime fault diagnosis use debug.", Props(
                    ("query", "The verification task: which program(s) to verify, the source or program name, and any compile result you already obtained. State what 'correct and safe' means for this program.", false),
                    ("max_turns", "Max verification turns for the subagent (default 12, max 12).", true)
                )),

                // ===== 新增：控制器连接控制工具（RTSysIP 不再硬性要求，系统自动搜索/仿真器连接） =====
                Tool("controller_autoconnect", "自动搜索并连接 RTSys 控制器。不再需要手动填写 IP：系统会先尝试 ZMC_SearchEth 自动发现控制器，再尝试 ZMC_SearchAndOpenEth 直接连接，失败则回退到配置文件中的静态 IP，最后尝试仿真器连接。返回连接模式（AutoSearch | StaticIp | Simulator）和已连接 IP。", NoParams()),
                Tool("controller_simulator", "尝试连接仿真器（zmotionsimu.dll / ZvEpm.dll）。适用于没有实体控制器时的离线开发和测试。系统会先尝试 ZMC_Open(type=0) 连接本地仿真，再尝试加载 zmotionsimu.dll。返回连接成功与否及模式。", NoParams()),
                Tool("controller_status", "查询当前控制器连接状态。返回 connectionState（NotConfigured | Disconnected | Connected）、currentMode（AutoSearch | StaticIp | Simulator）、cachedIp 等信息。无需控制器连接即可查询。", NoParams())
            };
        }

        // ---- Tool-schema construction helpers ----

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, string[] required = null)
        {
            // 从 properties 中提取 __required（由 Props/PropsMixed 注入）
            var schema = new Dictionary<string, object>();
            var props = new Dictionary<string, object>();
            var reqList = new List<string>();
            if (required != null) reqList.AddRange(required);
            foreach (var kv in properties)
            {
                if (kv.Key == "__required")
                {
                    if (kv.Value is string[] arr) reqList.AddRange(arr);
                    continue;
                }
                props[kv.Key] = kv.Value;
            }
            schema["type"] = "object";
            schema["properties"] = props;
            if (reqList.Count > 0)
                schema["required"] = reqList.ToArray();

            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "input_schema", schema }
            };
        }

        private static Dictionary<string, object> NoParams() => new Dictionary<string, object>();

        private static Dictionary<string, object> Props(string name, string desc)
        {
            return new Dictionary<string, object>
            {
                { name, new { type = "string", description = desc } }
            };
        }

        private static Dictionary<string, object> Props(params (string name, string desc, bool optional)[] props)
        {
            var dict = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var (n, d, opt) in props)
            {
                dict[n] = new { type = "string", description = d };
                if (!opt) required.Add(n);
            }
            if (required.Count > 0)
                dict["__required"] = required.ToArray();
            return dict;
        }

        // 支持 array / 其他类型字段的 schema 构造
        private static Dictionary<string, object> PropsMixed(params (string name, string desc, bool optional, string type)[] props)
        {
            var dict = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var (n, d, opt, t) in props)
            {
                dict[n] = new { type = string.IsNullOrEmpty(t) ? "string" : t, description = d };
                if (!opt) required.Add(n);
            }
            if (required.Count > 0)
                dict["__required"] = required.ToArray();
            return dict;
        }
    }
}
