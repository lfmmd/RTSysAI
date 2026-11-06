using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- Tool Execution ----

        private static readonly HashSet<string> SourceWriteTools = new HashSet<string>
        {
            "write_source", "patch_source"
        };

        private static readonly HashSet<string> WriteTools = new HashSet<string>
        {
            "write_source", "patch_source", "write_vr", "write_table",
            "create_program", "delete_program", "rename_program",
            "upload", "download", "compile_program", "run_program", "stop_program",
            "set_program_process", "write_iec_variables"
        };

        // 低风险写工具子集：程序编辑（增/删/改/重命名源码）+ 编译。纯工程文件操作，可重建、不碰实时控制器，
        // 自动放行免逐次确认（减少确认摩擦）。必须是 WriteTools 的子集——Plan Mode 仍靠 WriteTools 拦截全部写工具。
        // 运行/停止/上下传/写 VR·TABLE·IEC 等影响实时控制器状态的，不在本集合内，仍需人工逐次确认。
        private static readonly HashSet<string> AutoAllowWriteTools = new HashSet<string>
        {
            "write_source", "patch_source", "create_program", "delete_program", "rename_program", "compile_program"
        };

        // 纯 IO 工具：不需要 UI 线程（不访问 MP 项目模型），可绕过 DispatcherHelper.Invoke 直接调用。
        // 这些工具的 DispatchTool 路径是纯内存字典查询或纯文件读，线程安全。
        // 加它们到这个集合 → 可在 Task.Run 内真正并行执行。
        private static readonly HashSet<string> PureIoTools = new HashSet<string>
        {
            "lookup_command",    // 纯内存字典 + HTML 文件读
            "read_skill",        // 纯文件读
            "discover_skills",   // 纯文件读（LoadMdSkills）
            "task_create",       // 纯内存操作（_tasks list）
            "task_update",       // 纯内存操作
            "task_list",         // 纯内存操作
            "enter_plan_mode",   // 纯内存操作（_planMode 字段）
            "exit_plan_mode",    // 纯内存操作 + OnConfirmPlan 回调（UI 线程安全）
            "research",          // 子 agent：调 API + ExecuteTool（内部各自分流），不直接访问 MP 模型；避免主循环 Task.Run 内二次 Invoke 卡 UI（R3）
            "review", "debug", "explore", "verify"   // 同 research：5 种子 agent 共用同一套机制
        };

        private string ExecuteTool(string name, Dictionary<string, object> input)
        {
            try
            {
                // P0: lookup_command 同会话去重 — 避免重复查询同一命令的完整 HTML（每次 ~16KB）。
                // 仅主线生效：子 agent 的调用在隔离 subMessages，dedup 扫 _conversationHistory 既扫不到自己的、
                // 又会误命中主线历史返回误导性 note（子 agent 看不到那个 tool_result）。子 agent 靠 prompt + cap 兜底。
                if (!_inSubagent && string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase))
                {
                    var dedupResult = TryDedupLookupCommand(input);
                    if (dedupResult != null)
                        return _json.Serialize(dedupResult);
                }

                // Backup source code before write operations
                if (SourceWriteTools.Contains(name))
                {
                    var progName = GetStr(input, "name");
                    if (!string.IsNullOrEmpty(progName))
                        BackupSource(progName);
                }

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
                var resultStr = _json.Serialize(result);
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: {Truncate(resultStr, 300)}");
                return _json.Serialize(result);
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
                    var query = GetStr(input, "query");
                    if (string.IsNullOrEmpty(query))
                        return new { error = "query is required — describe what the subagent should investigate" };
                    int maxTurns = GetInt(input, "max_turns", 12);
                    var (conclusion, success) = RunSubagent(query, name, maxTurns, _currentCancellationToken);
                    // 子 agent 失败（API 错误 / 无文本产出）→ 返回 error 触发 tool_result.is_error，主模型据此重试或如实告知（不再把失败兜底文本伪装成结论）
                    if (!success)
                        return new { error = name + " subagent failed to produce a conclusion (API errors or no textual output). Consider retrying, possibly with a more specific query." };
                    return new { conclusion = conclusion };
                }
                case "get_status": return RTSysHandlers.GetStatus();
                case "list_programs": return RTSysHandlers.ListPrograms();
                case "read_source":
                {
                    var readResult = RTSysHandlers.ReadSource(GetStr(input, "name"), input);
                    var srcText = readResult is string s ? s : _json.Serialize(readResult);
                    RecordFileRead(GetStr(input, "name"), Truncate(srcText, MaxRestoredFileChars));
                    return readResult;
                }
                case "get_iec_task_detail": return RTSysHandlers.GetIecTaskDetail(GetStr(input, "name"));
                case "read_iec_variables": return RTSysHandlers.ReadIecVariables(GetStr(input, "name"), GetStr(input, "scope"));
                case "write_iec_variables": return RTSysHandlers.WriteIecVariables(GetStr(input, "name"), GetStr(input, "scope"), GetStr(input, "text"));
                case "write_source":
                {
                    var progName = GetStr(input, "name");
                    if (RTSysHandlers.GetProgramDialect(progName) == "rtbasic")
                    {
                        var code = GetStr(input, "sourceCode") ?? "";
                        var errs = ValidateRTBasicCode(code);
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
                                    errs.AddRange(ValidateRTBasicCode(newStr));
                                }
                            }
                        }
                        if (errs.Count > 0)
                            return new { error = "BLOCKED by RTBasic validation:\n  " + string.Join("\n  ", errs) };
                    }
                    return RTSysHandlers.PatchSource(progName, input);
                }
                case "read_vr": return RTSysHandlers.ReadVR(GetHexInt(input, "address"), GetInt(input, "count"));
                case "write_vr": return RTSysHandlers.WriteVR(GetHexInt(input, "address"), input);
                case "read_table": return RTSysHandlers.ReadTable(GetHexInt(input, "address"), GetInt(input, "count"));
                case "write_table": return RTSysHandlers.WriteTable(GetHexInt(input, "address"), input);
                case "list_axes": return RTSysHandlers.ListAxes();
                case "get_axis_detail": return RTSysHandlers.GetAxisDetail(GetInt(input, "index"));
                case "copy_program": return RTSysHandlers.CopyProgram(GetStr(input, "name"), input);
                case "read_sysvar": return RTSysHandlers.ReadSysVar(GetStr(input, "name"));
                case "write_sysvar": return RTSysHandlers.WriteSysVar(GetStr(input, "name"), input);
                case "list_digital_io": return RTSysHandlers.ListDigitalIO();
                case "read_digital_io": return RTSysHandlers.ReadDigitalIO(GetInt(input, "index"));
                case "write_digital_io": return RTSysHandlers.WriteDigitalIO(GetInt(input, "index"), input);
                case "list_analogue_io": return RTSysHandlers.ListAnalogueIO();
                case "read_analogue_io": return RTSysHandlers.ReadAnalogueIO(GetInt(input, "index"));
                case "write_analogue_io": return RTSysHandlers.WriteAnalogueIO(GetInt(input, "index"), input);
                case "list_breakpoints": return RTSysHandlers.ListBreakpoints(GetStr(input, "name"));
                case "set_breakpoint": return RTSysHandlers.SetBreakpoint(GetStr(input, "name"), input);
                case "clear_all_breakpoints": return RTSysHandlers.ClearAllBreakpoints(GetStr(input, "name"));
                case "list_processes": return RTSysHandlers.ListProcesses();
                case "get_process_variable": return RTSysHandlers.GetProcessVariable(GetInt(input, "pid"), GetStr(input, "program"), GetStr(input, "variable"));
                case "get_events": return RTSysHandlers.GetEvents(GetLong(input, "since"));
                case "read_drive_param": return RTSysHandlers.ReadDriveParam(GetInt(input, "axis"), GetHexInt(input, "address"), GetInt(input, "nd", 4));
                case "write_drive_param": return RTSysHandlers.WriteDriveParam(GetInt(input, "axis"), GetHexInt(input, "address"), input);
                case "scan_ethercat": return RTSysHandlers.ScanEtherCAT(GetInt(input, "slot", 0));
                case "read_ethercat_sdo":
                    return RTSysHandlers.EtherCATReadSDO(GetInt(input, "slot", 0),
                                                    (uint)GetLong(input, "position"),
                                                    (uint)GetHexLong(input, "index"),
                                                    (uint)GetHexLong(input, "subindex"),
                                                    GetStr(input, "type") ?? "uint16");
                case "write_ethercat_sdo": return RTSysHandlers.EtherCATWriteSDOFromDict(input);
                case "scan_msbus": return RTSysHandlers.ScanMsBus(GetInt(input, "slot", 0));
                case "list_remote_devices": return RTSysHandlers.ListRemoteDevices();
                case "list_robots": return RTSysHandlers.ListRobots();
                case "list_recipes": return RTSysHandlers.ListRecipes();
                case "list_alarms": return RTSysHandlers.ListAlarms();
                case "list_plugins": return RTSysHandlers.ListAttachedPlugins();
                case "open_project": return RTSysHandlers.OpenProject(input);
                case "list_project_items": return RTSysHandlers.ListProjectItems();
                case "list_descriptors": return RTSysHandlers.ListDescriptors();
                case "create_program": return RTSysHandlers.CreateProgram(input);
                case "delete_program": return RTSysHandlers.DeleteProgram(GetStr(input, "name"));
                case "rename_program": return RTSysHandlers.RenameProgram(GetStr(input, "name"), input);
                case "compile_program": return RTSysHandlers.Compile(GetStr(input, "name"));
                case "run_program": return RTSysHandlers.RunProgram(GetStr(input, "name"), input);
                case "stop_program": return RTSysHandlers.StopProgram(GetStr(input, "name"), input);
                case "upload": return RTSysHandlers.Upload(GetStr(input, "name"));
                case "download": return RTSysHandlers.Download(GetStr(input, "name"));
                case "save_project": return RTSysHandlers.SaveProject();
                case "open_program": return RTSysHandlers.OpenProgram(GetStr(input, "name"));
                case "get_program_process": return RTSysHandlers.GetProgramProcess(GetStr(input, "name"));
                case "set_program_process": return RTSysHandlers.SetProgramProcess(GetStr(input, "name"), input);
                case "lookup_command": return LookupCommand(GetStr(input, "query"), GetStr(input, "full"), GetStr(input, "library"));
                case "read_skill": return ReadSkill(GetStr(input, "name"));
                case "discover_skills": return DiscoverSkills(GetStr(input, "category"));
                case "search_code": return RTSysHandlers.SearchCode(GetStr(input, "query"));
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
                hint = "Plan Mode is now active. All write/modify tools (write_source, compile_program, run_program, write_vr, etc.) are blocked. " +
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

        private object TaskCreate(string subject, string description)
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

        private object TaskUpdate(int id, string status, string subject, string description)
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

        private object TaskList()
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
                return new List<Dictionary<string, object>>(_cachedToolDefs);
            }
        }

        // 子 agent 运行时拦截用的只读工具【超集】（所有只读工具，不含任何写工具，禁递归 research）。
        // 双重保险的"运行时"那一层：RunSubagent 对超集外的 tool_use 返回 error。
        // 各 agentType 的 schema 暴露面用 SubagentToolPools（按定位裁剪的子集）控制，但运行时拦截始终用此超集兜底。
        private static readonly HashSet<string> SubagentReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_status", "list_programs", "read_source", "get_iec_task_detail",
            "read_iec_variables", "read_vr", "read_table", "list_axes", "list_descriptors",
            "download", "get_program_process", "lookup_command", "read_skill", "discover_skills",
            "search_code", "get_axis_detail", "read_sysvar", "list_digital_io", "read_digital_io",
            "list_analogue_io", "read_analogue_io", "list_processes", "get_process_variable",
            "get_events", "list_breakpoints", "read_drive_param", "scan_ethercat", "read_ethercat_sdo",
            "scan_msbus", "list_remote_devices", "list_robots", "list_recipes", "list_alarms",
            "list_plugins", "list_project_items"
        };

        // 各 agentType 的 schema 工具池（按定位裁剪；严格 = 该 agent prompt 明确提到的工具，保证 schema 与 prompt 一致 ——
        // 子 agent 不会调用 prompt 没告诉它的工具）。research = 全超集（万能查文档，不削弱）；未知 type 回落超集。
        // 这里只控制 schema 暴露面（省 token + 提升选工具聚焦度）；运行时拦截仍由 SubagentReadTools 超集兜底。
        private static readonly Dictionary<string, HashSet<string>> SubagentToolPools =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "research", SubagentReadTools },
            { "review", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "read_source", "search_code", "get_iec_task_detail", "read_iec_variables",
                  "lookup_command", "list_programs", "list_axes", "read_vr", "read_sysvar", "get_status" } },
            { "debug", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "get_status", "list_axes", "get_axis_detail", "read_vr", "read_sysvar", "read_table",
                  "list_processes", "get_process_variable", "get_events", "read_drive_param",
                  "scan_ethercat", "read_ethercat_sdo", "read_source" } },
            { "explore", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "list_programs", "list_project_items", "read_source", "search_code",
                  "lookup_command", "get_iec_task_detail", "get_status", "list_axes", "list_processes" } },
            { "verify", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "read_source", "search_code", "get_iec_task_detail", "read_iec_variables", "lookup_command",
                  "get_status", "list_axes", "read_vr", "read_sysvar", "read_table",
                  "list_processes", "get_process_variable", "get_events" } },
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
                Tool("get_status", "Get controller connection status, product name, firmware version, project name", NoParams()),
                Tool("list_programs", "List all programs in the current MotionPerfect project", NoParams()),
                Tool("read_source", "Read source code of a program. The system auto-returns the FIRST CHUNK for files >200 lines or >8000 chars — the response includes totalLines and a hint when more chunks exist. You MUST check totalLines vs endLine and continue with startLine=endLine+1 to read subsequent chunks; NEVER assume the first response contains the whole file. For IEC tasks, use pouName to read a specific POU (default: first POU). IEC ST POU returns local VAR...END_VAR block (from POU's variable group) PREPENDED to the code body — write_source expects this same shape.", Props(
                    ("name", "Program name", false),
                    ("pouName", "For IEC tasks only: specific POU name to read. If omitted, reads the first POU. Use get_iec_task_detail to list available POUs.", true),
                    ("startLine", "Starting 1-based line number (optional, for pagination)", true),
                    ("endLine", "Ending 1-based line number (optional, for pagination)", true)
                )),
                Tool("get_iec_task_detail", "Get the internal structure of an IEC task: list of POUs (programs), task/global/retain variable blocks (VAR...END_VAR text), and the user data type table. Use this first when working with an IEC task to see its full contents.", Props("name", "IEC task name")),
                Tool("write_source", "Write full source code to a program (auto-backup, requires confirmation). For IEC tasks, use pouName to target a specific POU — POU is auto-created (as ST/Main) if it does not exist.", Props(
                    ("name", "Program name", false),
                    ("sourceCode", "Full source code to write", false),
                    ("pouName", "For IEC tasks only: target POU name. Auto-created if missing. If omitted, writes to first POU (or creates MAIN).", true)
                )),
                Tool("patch_source", "Apply text-level edits to an EXISTING program by finding and replacing exact text snippets (auto-backup, requires confirmation). REQUIRES the program to already exist — cannot create new programs (use write_source or create_program first). More reliable than line-number based editing. For IEC tasks, pouName must point to an EXISTING POU.", PropsMixed(
                    ("name", "Program name", false, "string"),
                    ("pouName", "For IEC tasks only: target POU name (must exist; defaults to first POU)", true, "string"),
                    ("operations", "Array of {old_string:exact text to find in source, new_string:replacement text}. old_string must be unique in the source. If old_string is empty, new_string is appended to the end.", false, "array")
                )),
                Tool("read_iec_variables", "Read IEC task variable block as VAR...END_VAR text. scope: task (task-local globals), global (controller-wide), retain (retained across power cycle).", Props(
                    ("name", "IEC task name", false),
                    ("scope", "Variable scope: task | global | retain", false)
                )),
                Tool("write_iec_variables", "Replace an IEC task variable block from VAR...END_VAR text (requires confirmation). Same scope semantics as read_iec_variables.", Props(
                    ("name", "IEC task name", false),
                    ("scope", "Variable scope: task | global | retain", false),
                    ("text", "Full VAR...END_VAR text to import (replaces existing variables in this scope)", false)
                )),
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
                Tool("list_axes", "List all configured axes on the controller", NoParams()),
                Tool("list_descriptors", "List available program type descriptors", NoParams()),
                Tool("create_program", "Create a new program in the project (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("type", "Program type: basic, text, etc.", true),
                    ("sourceCode", "Initial source code", true)
                )),
                Tool("delete_program", "Delete a program from the project (requires confirmation)", Props("name", "Program name")),
                Tool("compile_program", "Compile a program on the controller", Props("name", "Program name")),
                Tool("run_program", "Run a program on the controller (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("process", "Process number", true)
                )),
                Tool("stop_program", "Stop a running program (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("process", "Process number", true)
                )),
                Tool("upload", "Upload a program to the controller (requires confirmation)", Props("name", "Program name")),
                Tool("download", "Download a program from the controller", Props("name", "Program name")),
                Tool("save_project", "Save the current project", NoParams()),
                Tool("open_program", "Open a program in the editor. Call this before write_source or patch_source if the program is not already open.", Props("name", "Program name")),
                Tool("get_program_process", "Get process/auto-run settings for a program (isAutorun, autorunProcess, processAffinity)", Props("name", "Program name")),
                Tool("set_program_process", "Set process/auto-run settings for a program (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("isAutorun", "Whether program auto-runs on controller startup (true/false)", true),
                    ("autorunProcess", "Process number for auto-run", true),
                    ("processAffinity", "Process affinity (process slot to run on)", true)
                )),
                Tool("lookup_command", "Look up command/keyword reference. Default returns name + signature + description (lightweight). Use full=true for complete HTML. Use library to scope to triobasic/iec/plcopen.", Props(
                    ("query", "Command name or keyword to search (e.g. MOVE, MC_MoveAbsolute, FOR)", false),
                    ("full", "Set to \"true\" to load complete HTML documentation (heavier, use only when examples or detailed parameters are needed)", true),
                    ("library", "Scope search to: triobasic | iec | plcopen (default: search all libraries)", true)
                )),
                Tool("read_skill", "Load full markdown content of a skill listed in the 'Available Skills' section of the system prompt. BLOCKING REQUIREMENT: when the user's request matches a skill's 'Use when:' description, invoke read_skill BEFORE writing any code or generating other response about the task. NEVER mention or claim to follow a skill without calling this tool first. Note: skills whose full body is already shown in the system prompt (e.g. 'Safe Coding Rules (MANDATORY)') are pre-loaded — do NOT re-read those.", Props(
                    ("name", "Skill name from Available Skills", false)
                )),
                Tool("discover_skills", "List available markdown skills with their name, description, and when_to_use trigger. Use this FIRST when you are unsure which skills exist or which one matches the user's task — cheaper than calling read_skill by trial and error. After finding a relevant skill, call read_skill(name) to load its full body.", Props(
                    ("category", "Filter by category (general | triobasic | iec | plcopen). null/empty returns all skills.", true)
                )),
                Tool("search_code", "Search for a text pattern across all programs in the project. Returns matching lines with line numbers.", Props(
                    ("query", "Search text or pattern", false),
                    ("caseSensitive", "Whether search is case sensitive (default false)", true)
                )),
                Tool("get_axis_detail", "Get detailed info for a single axis (Type, IsEncoderType, DefaultFriendlyName, Motor type, plus all list_axes fields).", Props(
                    ("index", "Axis index (0-based)", false)
                )),
                Tool("copy_program", "Copy an existing program to a new name (requires confirmation)", Props(
                    ("name", "Source program name", false),
                    ("newName", "New program name", false),
                    ("storage", "Storage: internalStorage (default) or sdcardStorage", true)
                )),
                Tool("rename_program", "Rename an existing program (requires confirmation)", Props(
                    ("name", "Current program name", false),
                    ("newName", "New program name", false)
                )),
                Tool("read_sysvar", "Read any named controller system variable (e.g. PROCESS_RUNNING)", Props("name", "Variable name")),
                Tool("write_sysvar", "Write a named system variable (requires confirmation)", Props(
                    ("name", "Variable name", false),
                    ("value", "Value to write", false)
                )),
                Tool("list_digital_io", "List all digital IO lines", NoParams()),
                Tool("read_digital_io", "Read digital IO state (input + output) at index", Props("index", "IO line index")),
                Tool("write_digital_io", "Write a digital output (requires confirmation)", Props(
                    ("index", "IO line index", false),
                    ("value", "Bool value to write", false)
                )),
                Tool("list_analogue_io", "List all analogue IO lines", NoParams()),
                Tool("read_analogue_io", "Read analogue IO state at index", Props("index", "IO line index")),
                Tool("write_analogue_io", "Write an analogue output (requires confirmation)", Props(
                    ("index", "IO line index", false),
                    ("value", "Numeric value to write", false)
                )),
                Tool("list_processes", "List all running processes on the controller (pid, status, program, line)", NoParams()),
                Tool("get_process_variable", "Read a runtime variable from a running BASIC program", Props(
                    ("pid", "Process ID", false),
                    ("program", "Program (module) name", false),
                    ("variable", "Variable name", false)
                )),
                Tool("get_events", "Pull subscribed controller events since given UTC ticks (program_state, connection_state, async_message, io_changed, compile_state)", Props(
                    ("since", "UTC ticks; default 0 = all buffered events", true)
                )),
                Tool("list_breakpoints", "List breakpoints in a TrioBASIC program (line numbers)", Props("name", "Program name")),
                Tool("set_breakpoint", "Set or clear a breakpoint in a TrioBASIC program (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("line", "1-based line number", false),
                    ("enable", "true to set, false to clear (default true)", true)
                )),
                Tool("clear_all_breakpoints", "Remove all breakpoints in a TrioBASIC program (requires confirmation)", Props("name", "Program name")),
                Tool("read_drive_param", "Read a drive parameter via DRIVE_READ", Props(
                    ("axis", "Axis number", false),
                    ("address", "Hex drive parameter address", false),
                    ("nd", "Number of fraction digits (default 4)", true)
                )),
                Tool("write_drive_param", "Write a drive parameter via DRIVE_WRITE (requires confirmation)", Props(
                    ("axis", "Axis number", false),
                    ("address", "Hex drive parameter address", false),
                    ("value", "Value to write (number or string)", false)
                )),
                Tool("scan_ethercat", "Scan EtherCAT devices on a slot", Props(
                    ("slot", "Slot number (default 0)", true)
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
                Tool("scan_msbus", "Scan MS Bus modules on a slot", Props(
                    ("slot", "Slot (default 0)", true)
                )),
                Tool("list_remote_devices", "List configured remote device gateways (Modbus TCP/RTU) and their devices", NoParams()),
                Tool("list_robots", "List configured robots (index, name, model, type, axes)", NoParams()),
                Tool("list_recipes", "List Recipe project items", NoParams()),
                Tool("list_alarms", "List alarms from AlarmSupport project items", NoParams()),
                Tool("list_plugins", "Probe which controller-attached plugin services are available (IRobotService, RemoteDeviceManager)", NoParams()),
                Tool("open_project", "Open an existing project from a path (requires confirmation)", Props(
                    ("path", "Project file path", false)
                )),
                Tool("list_project_items", "List all project items with name/type/group", NoParams()),
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
                Tool("enter_plan_mode", "Enter Plan Mode: all write/modify tools (write_source, patch_source, compile_program, run_program, write_vr, write_table, etc.) are blocked. Use this at the start of a non-trivial task to investigate first with read-only tools, then present a plan via exit_plan_mode for user approval before making any changes. Avoid for trivial single-step requests, AND for batch same-operation-on-many-items tasks (fix/check all programs) — those are independent sub-tasks; use `task_create` + process one at a time instead.", NoParams()),
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
                ))
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
