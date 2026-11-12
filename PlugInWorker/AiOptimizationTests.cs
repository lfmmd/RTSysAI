using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        /// <summary>
        /// 运行 P0/P1 优化的全部回归测试。返回 (pass, fail, details)。
        /// 不需要控制器连接，纯逻辑验证。
        /// </summary>
        public static (int pass, int fail, string report) RunOptimizationTests()
        {
            var results = new List<(string name, bool ok, string detail)>();

            // ========== P0: TryDedupLookupCommand 测试 ==========
            var svc = new AiService();
            // 清空历史确保干净起点
            svc._conversationHistory.Clear();

            // --- P0-1: 首次调用不应去重 ---
            {
                var input = MakeLookupInput("MOVE", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-1 首次调用不去重", result == null,
                    result == null ? "OK: 返回 null，正常执行" : "FAIL: 不应去重但返回了结果"));
            }

            // 模拟历史：assistant 调用了 lookup_command("MOVE", full=true) → user 收到成功结果
            var toolId1 = "toolu_001";
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", toolId1, new Dictionary<string, object>
            {
                { "query", "MOVE" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult(toolId1, "{\"results\":[{\"name\":\"MOVE\",\"html\":\"...16KB...\"}]}"));

            // --- P0-2: 相同参数再次调用应去重 ---
            {
                var input = MakeLookupInput("MOVE", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                var json = result != null ? _json.Serialize(result) : "";
                bool ok = result != null && json.Contains("Already looked up");
                results.Add(("P0-2 相同参数去重", ok,
                    ok ? "OK: 返回引用提示" : $"FAIL: result={json}"));
            }

            // --- P0-3: 不同 query 不去重 ---
            {
                var input = MakeLookupInput("CONNECT", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-3 不同query不去重", result == null,
                    result == null ? "OK: CONNECT 首次调用正常" : "FAIL: 不应匹配 MOVE"));
            }

            // --- P0-4: 不同 full 参数不去重 ---
            {
                var input = MakeLookupInput("MOVE", "false", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-4 不同full不去重", result == null,
                    result == null ? "OK: full=false ≠ full=true" : "FAIL: full 参数不同不应去重"));
            }

            // --- P0-5: 不同 library 不去重 ---
            {
                var input = MakeLookupInput("MOVE", "true", "iec");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-5 不同library不去重", result == null,
                    result == null ? "OK: library=iec ≠ (empty)" : "FAIL: library 不同不应去重"));
            }

            // --- P0-6: 大小写不敏感匹配 ---
            {
                var input = MakeLookupInput("move", "True", "");
                var result = svc.TryDedupLookupCommand(input);
                var json = result != null ? _json.Serialize(result) : "";
                bool ok = result != null && json.Contains("Already looked up");
                results.Add(("P0-6 大小写不敏感", ok,
                    ok ? "OK: move/True 匹配 MOVE/true" : "FAIL: 大小写应不敏感"));
            }

            // --- P0-7: 之前返回 error 的不去重 ---
            svc._conversationHistory.Clear();
            var toolId2 = "toolu_002";
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", toolId2, new Dictionary<string, object>
            {
                { "query", "FOOBAR" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult(toolId2, "{\"error\":\"No matching command found for 'FOOBAR'\"}"));
            {
                var input = MakeLookupInput("FOOBAR", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-7 error结果不去重", result == null,
                    result == null ? "OK: 允许重试失败的查询" : "FAIL: error 结果不应缓存"));
            }

            // --- P0-8: CompactHistory 后不再匹配 ---
            svc._conversationHistory.Clear();
            var toolId3 = "toolu_003";
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", toolId3, new Dictionary<string, object>
            {
                { "query", "HOME" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult(toolId3, "{\"results\":[{\"name\":\"HOME\"}]}"));
            // 模拟 CompactHistory：用摘要替换旧消息
            svc._conversationHistory.Clear();
            svc._conversationHistory.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", "[Conversation summary] User asked about HOME command..." }
            });
            {
                var input = MakeLookupInput("HOME", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-8 压缩后不去重", result == null,
                    result == null ? "OK: 摘要文本不含 tool_use 结构，不匹配" : "FAIL: 压缩后应正常执行"));
            }

            // --- P0-9: 多个 tool_use 在同一 assistant 消息中 ---
            svc._conversationHistory.Clear();
            svc._conversationHistory.Add(MakeAssistantToolUse(new[]
            {
                ("lookup_command", "toolu_010", new Dictionary<string, object> { { "query", "BASE" }, { "full", "false" } }),
                ("lookup_command", "toolu_011", new Dictionary<string, object> { { "query", "SPEED" }, { "full", "false" } }),
                ("get_status", "toolu_012", new Dictionary<string, object>())
            }));
            svc._conversationHistory.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", new List<Dictionary<string, object>>
                {
                    MakeToolResultBlock("toolu_010", "{\"results\":[{\"name\":\"BASE\"}]}"),
                    MakeToolResultBlock("toolu_011", "{\"results\":[{\"name\":\"SPEED\"}]}"),
                    MakeToolResultBlock("toolu_012", "{\"status\":\"ok\"}")
                }}
            });
            {
                // BASE 应去重
                var r1 = svc.TryDedupLookupCommand(MakeLookupInput("BASE", "false", ""));
                // SPEED 应去重
                var r2 = svc.TryDedupLookupCommand(MakeLookupInput("SPEED", "false", ""));
                // DPOS 首次不应去重
                var r3 = svc.TryDedupLookupCommand(MakeLookupInput("DPOS", "false", ""));
                bool ok = r1 != null && r2 != null && r3 == null;
                results.Add(("P0-9 多tool_use混合", ok,
                    ok ? "OK: BASE/SPEED 去重，DPOS 不去重" :
                    $"FAIL: BASE={r1 != null}, SPEED={r2 != null}, DPOS={r3 == null}"));
            }

            // --- P0-10: 非 lookup_command 的 tool_use 不干扰 ---
            svc._conversationHistory.Clear();
            svc._conversationHistory.Add(MakeAssistantToolUse("read_source", "toolu_020", new Dictionary<string, object>
            {
                { "name", "MAIN" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult("toolu_020", "{\"sourceCode\":\"...\"}"));
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", "toolu_021", new Dictionary<string, object>
            {
                { "query", "WAITS" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult("toolu_021", "{\"results\":[{\"name\":\"WAITS\"}]}"));
            {
                var r = svc.TryDedupLookupCommand(MakeLookupInput("WAITS", "true", ""));
                bool ok = r != null && _json.Serialize(r).Contains("Already looked up");
                results.Add(("P0-10 非lookup不干扰", ok,
                    ok ? "OK: WAITS 正确匹配到 lookup_command 而非 read_source" : "FAIL"));
            }

            // ========== P1: GetToolDefinitions 缓存测试 ==========
            // --- P1-1: 返回正确数量的 tools ---
            // 当前注册的 tool 总数：65 基础 + research/review/debug/explore（4 个子 agent 工具）= 69
            {
                var tools1 = GetToolDefinitions();
                bool ok = tools1.Count == 70;
                results.Add(("P1-1 tool数量=70", ok,
                    ok ? "OK: 70 个 tool (65 + research/review/debug/explore/verify 子 agent 工具)" : $"FAIL: 实际 {tools1.Count} 个"));
            }

            // --- P1-2: 多次调用返回新 list 实例 ---
            {
                var list1 = GetToolDefinitions();
                var list2 = GetToolDefinitions();
                bool ok = !ReferenceEquals(list1, list2);
                results.Add(("P1-2 每次返回新List", ok,
                    ok ? "OK: 浅拷贝，不是同一引用" : "FAIL: 返回了同一个 List 对象"));
            }

            // --- P1-3: 修改返回的 list 不影响缓存 ---
            {
                var list1 = GetToolDefinitions();
                var origCount = list1.Count;
                list1.Clear();
                var list2 = GetToolDefinitions();
                bool ok = list2.Count == origCount;
                results.Add(("P1-3 修改list不影响缓存", ok,
                    ok ? $"OK: Clear 后重新获取仍有 {origCount} 个" : $"FAIL: 缓存被污染，只剩 {list2.Count} 个"));
            }

            // --- P1-4: 添加 cache_control 不影响缓存 ---
            {
                var tools = GetToolDefinitions();
                if (tools.Count > 0)
                {
                    // 模拟 CallApiStream 的操作
                    var lastTool = new Dictionary<string, object>(tools[tools.Count - 1])
                    {
                        { "cache_control", new { type = "ephemeral" } }
                    };
                    tools[tools.Count - 1] = lastTool;
                }
                var tools2 = GetToolDefinitions();
                var lastTool2 = tools2[tools2.Count - 1];
                bool hasCacheCtrl = lastTool2.ContainsKey("cache_control");
                bool ok = !hasCacheCtrl;
                results.Add(("P1-4 cache_control不污染缓存", ok,
                    ok ? "OK: 缓存的 tool 没有 cache_control" : "FAIL: 缓存被 cache_control 污染了"));
            }

            // --- P1-5: tool schema 完整性 ---
            {
                var tools = GetToolDefinitions();
                bool allValid = true;
                var badTools = new List<string>();
                foreach (var t in tools)
                {
                    if (!t.ContainsKey("name") || !t.ContainsKey("description") || !t.ContainsKey("input_schema"))
                    {
                        allValid = false;
                        badTools.Add(GetStr(t, "name") ?? "(no name)");
                    }
                }
                results.Add(("P1-5 schema完整性", allValid,
                    allValid ? "OK: 全部 tool 含 name+description+input_schema" :
                    $"FAIL: 以下 tool 缺字段: {string.Join(", ", badTools)}"));
            }

            // ========== Phase 1: 工具并行 / 重试 / 循环 / Skills / Task / Plan Mode ==========

            // --- Phase1-1: PureIoTools 分流集合 ---
            {
                var pureExpected = new[] { "lookup_command", "read_skill", "discover_skills",
                    "task_create", "task_update", "task_list",
                    "enter_plan_mode", "exit_plan_mode" };
                var nonPureExpected = new[] { "read_source", "write_source", "list_programs",
                    "compile_program", "run_program", "write_vr", "get_status" };
                bool allPure = pureExpected.All(n => PureIoTools.Contains(n));
                bool allNonPure = nonPureExpected.All(n => !PureIoTools.Contains(n));
                bool ok = allPure && allNonPure;
                results.Add(("Phase1-1 PureIoTools分流", ok,
                    ok ? $"OK: {pureExpected.Length} 个纯IO工具 + write/read_source 仍在 UI 线程" :
                    $"FAIL: pure={string.Join(",", pureExpected.Where(n => !PureIoTools.Contains(n)))} nonPure={string.Join(",", nonPureExpected.Where(n => PureIoTools.Contains(n)))}"));
            }

            // --- Phase1-2: Phase 1 新增 7 个工具已注册 ---
            {
                var tools = GetToolDefinitions();
                var names = new HashSet<string>(tools.Select(t => GetStr(t, "name")), StringComparer.OrdinalIgnoreCase);
                var expected = new[] { "discover_skills", "task_create", "task_update", "task_list", "enter_plan_mode", "exit_plan_mode" };
                var missing = expected.Where(n => !names.Contains(n)).ToList();
                bool ok = missing.Count == 0;
                results.Add(("Phase1-2 工具已注册", ok,
                    ok ? "OK: 全部注册" : $"FAIL: 缺少 {string.Join(", ", missing)}"));
            }

            // --- Phase1-3: MaxTurns=50 + TokenBudgetLimit=400K ---
            {
                bool ok = MaxTurns == 50 && TokenBudgetLimit == 400_000;
                results.Add(("Phase1-3 循环退出常量", ok,
                    ok ? $"OK: MaxTurns={MaxTurns}, TokenBudget={TokenBudgetLimit}" :
                    $"FAIL: MaxTurns={MaxTurns}, TokenBudget={TokenBudgetLimit}"));
            }

            // --- Phase1-4: RetryableApiException 类型 + GetBackoffDelay 指数退避 ---
            {
                var ex1 = new RetryableApiException("test", 503, null);
                var ex2 = new RetryableApiException("net", null, null);
                bool typeOk = ex1.StatusCode == 503 && ex2.StatusCode == null;
                bool backoffOk = GetBackoffDelay(0) == 1000 && GetBackoffDelay(1) == 2000 && GetBackoffDelay(2) == 4000;
                bool ok = typeOk && backoffOk;
                results.Add(("Phase1-4 Retryable+Backoff", ok,
                    ok ? "OK: StatusCode 携带 + 1s/2s/4s 指数退避" : $"FAIL: type={typeOk}, backoff={backoffOk}"));
            }

            // --- Phase1-5: Task/Todo CRUD ---
            {
                var svc2 = new AiService();
                // task_create
                var created = svc2.TaskCreate("Test subject", "Test description");
                var createdJson = _json.Serialize(created);
                bool createOk = createdJson.Contains("\"id\":1") && createdJson.Contains("\"status\":\"pending\"");
                // task_update status 不合法
                var badUpdate = svc2.TaskUpdate(1, "invalid_status", null, null);
                bool badUpdateOk = _json.Serialize(badUpdate).Contains("error");
                // task_update 合法
                var updated = svc2.TaskUpdate(1, "in_progress", null, null);
                bool updateOk = _json.Serialize(updated).Contains("\"status\":\"in_progress\"");
                // task_list
                var list = svc2.TaskList();
                var listJson = _json.Serialize(list);
                bool listOk = listJson.Contains("\"count\":1") && listJson.Contains("\"in_progress\":1");
                bool ok = createOk && badUpdateOk && updateOk && listOk;
                results.Add(("Phase1-5 Task CRUD", ok,
                    ok ? "OK: create/update/list/error 全覆盖" :
                    $"FAIL: create={createOk} badUpdate={badUpdateOk} update={updateOk} list={listOk}"));
            }

            // --- Phase1-6: Plan Mode 拦截写工具 + 未挂 OnConfirmPlan 自动批准 ---
            {
                var svc3 = new AiService();
                // 初始非 plan mode
                bool initOk = !svc3.IsPlanMode;
                // enter plan mode
                var entered = svc3.EnterPlanMode();
                bool enterOk = _json.Serialize(entered).Contains("plan_mode\":true") && svc3.IsPlanMode;
                // write_source 在 plan mode 应被拦截
                var writeBlocked = svc3.ExecuteTool("write_source", new Dictionary<string, object>
                {
                    { "name", "TEST" }, { "sourceCode", "X" }
                });
                bool blockedOk = writeBlocked.Contains("BLOCKED: Plan Mode");
                // read_source 在 plan mode 应允许（不在 WriteTools，仍会调 DispatchTool 报"未连接"或类似）
                // 但我们不验证 read_source 实际结果，只验证不被 BLOCKED 拦截
                var readAttempt = svc3.ExecuteTool("read_source", new Dictionary<string, object>
                {
                    { "name", "NONEXISTENT_PROGRAM" }
                });
                bool readNotBlocked = !readAttempt.Contains("BLOCKED: Plan Mode");
                // exit plan mode 未挂 OnConfirmPlan → 自动批准
                var exited = svc3.ExitPlanMode("My plan to fix axis 2");
                bool exitOk = _json.Serialize(exited).Contains("\"approved\":true") && !svc3.IsPlanMode;
                bool ok = initOk && enterOk && blockedOk && readNotBlocked && exitOk;
                results.Add(("Phase1-6 Plan Mode", ok,
                    ok ? "OK: 拦截写工具 + 不拦截读 + 自动批准退出" :
                    $"FAIL: init={initOk} enter={enterOk} blocked={blockedOk} readNotBlocked={readNotBlocked} exit={exitOk}"));
            }

            // --- Phase1-7: TrimHistory 修复孤立 tool_result ---
            {
                var svc4 = new AiService();
                svc4._conversationHistory.Clear();
                // 构造一个"中间被截掉 assistant tool_use 留下孤立 tool_result"的场景：
                // 历史 = [user 文本, assistant tool_use A, user tool_result A, user tool_result B (孤立)]
                // 直接调 EnsureValidMessageSequence 验证它清理孤立 tool_result
                svc4._conversationHistory.Add(new Dictionary<string, object>
                {
                    { "role", "user" }, { "content", "hello" }
                });
                svc4._conversationHistory.Add(MakeAssistantToolUse("lookup_command", "toolu_A", new Dictionary<string, object>
                {
                    { "query", "MOVE" }, { "full", "false" }
                }));
                svc4._conversationHistory.Add(MakeUserToolResult("toolu_A", "{}"));
                // 孤立的 tool_result：tool_use_id 不存在于任何 assistant
                svc4._conversationHistory.Add(MakeUserToolResult("toolu_ORPHAN", "orphan content"));
                int before = svc4._conversationHistory.Count;
                svc4.EnsureValidMessageSequence(svc4._conversationHistory);
                // 验证：孤立 tool_result 的 content 被清空（EnsureValidMessageSequence 不会移除整条消息，
                // 但会清掉孤立 tool_result 块；孤立块被丢弃后，user 消息的 content 可能变空）
                bool foundOrphanContent = false;
                foreach (var m in svc4._conversationHistory)
                {
                    if (!(m["content"] is List<Dictionary<string, object>> blocks)) continue;
                    foreach (var b in blocks)
                    {
                        var c = GetStr(b, "content");
                        if (c == "orphan content") foundOrphanContent = true;
                    }
                }
                bool ok = !foundOrphanContent;
                results.Add(("Phase1-7 TrimHistory清理孤立", ok,
                    ok ? "OK: EnsureValidMessageSequence 清理了孤立 tool_result" :
                    $"FAIL: 孤立 tool_result 仍存在 (history before={before} after={svc4._conversationHistory.Count})"));
            }

            // --- Phase2-1: 去重连续相同 user 纯文本（自循环 bug 现场：4 条 → 1 条）---
            {
                var svc5 = new AiService();
                svc5._conversationHistory.Clear();
                for (int k = 0; k < 4; k++)
                    svc5._conversationHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "user" }, { "content", "同时创建3个demo" }
                    });
                svc5.EnsureValidMessageSequence(svc5._conversationHistory);
                int same = 0;
                foreach (var m in svc5._conversationHistory)
                    if (m.TryGetValue("content", out var c) && c is string s && s == "同时创建3个demo") same++;
                bool ok = same == 1;
                results.Add(("Phase2-1 连续相同user去重", ok,
                    ok ? "OK: 4 条连续相同 user 收敛为 1 条" :
                    $"FAIL: 收敛后仍有 {same} 条相同 user (count={svc5._conversationHistory.Count})"));
            }

            // --- Phase2-2: 不误伤"中间隔了 AI 回复"的真重发（两条都应保留）---
            {
                var svc6 = new AiService();
                svc6._conversationHistory.Clear();
                svc6._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "你好" } });
                svc6._conversationHistory.Add(MakeAssistantText("你好！有什么可以帮你？"));
                svc6._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "你好" } });
                svc6.EnsureValidMessageSequence(svc6._conversationHistory);
                int n = 0;
                foreach (var m in svc6._conversationHistory)
                    if (m.TryGetValue("content", out var c) && c is string s && s == "你好") n++;
                bool ok = n == 2;
                results.Add(("Phase2-2 不误伤真重发", ok,
                    ok ? "OK: 中间隔 AI 的相同 user 都保留" :
                    $"FAIL: 误删了真重发 user (剩余 {n} 条)"));
            }

            // --- Phase2-3: 不误伤 user(tool_result)（content 是 List，is string 守卫）---
            {
                var svc7 = new AiService();
                svc7._conversationHistory.Clear();
                svc7._conversationHistory.Add(MakeAssistantToolUse("lookup_command", "toolu_X",
                    MakeLookupInput("MOVE", "false", null)));
                svc7._conversationHistory.Add(MakeUserToolResult("toolu_X", "{\"results\":[{}]}"));
                svc7._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "dup" } });
                svc7._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "dup" } });
                int before = svc7._conversationHistory.Count;
                svc7.EnsureValidMessageSequence(svc7._conversationHistory);
                // tool_result 必须保留（is string 守卫挡住）；两条相邻 "dup" 收敛为 1 条
                bool toolResultKept = false;
                int dupCount = 0;
                foreach (var m in svc7._conversationHistory)
                {
                    if (m.TryGetValue("content", out var c))
                    {
                        if (c is List<Dictionary<string, object>>) toolResultKept = true;
                        else if (c is string s && s == "dup") dupCount++;
                    }
                }
                bool ok = toolResultKept && dupCount == 1;
                results.Add(("Phase2-3 不误伤tool_result", ok,
                    ok ? "OK: tool_result 保留 + 相邻纯文本收敛" :
                    $"FAIL: toolResultKept={toolResultKept} dupCount={dupCount} (before={before} after={svc7._conversationHistory.Count})"));
            }

            // ========== Phase-Thinking: 照原样回传（signature 有无都不清理） ==========

            // --- Phase-Thinking-1: 带 signature 的 thinking 块保留（不被防御规则误删）---
            {
                var svc8 = new AiService();
                svc8._conversationHistory.Clear();
                svc8._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "hi" } });
                svc8._conversationHistory.Add(MakeAssistantBlocks(
                    ThinkingBlock("planning...", "sig_abc123"),
                    TextBlock("done")));
                svc8.EnsureValidMessageSequence(svc8._conversationHistory);
                int thinkingN = CountBlocks(svc8._conversationHistory, "thinking");
                bool ok = thinkingN == 1;
                results.Add(("Phase-Thinking-1 带signature保留", ok,
                    ok ? "OK: 带 signature 的 thinking 块保留" : $"FAIL: thinking 块数={thinkingN}（应=1）"));
            }

            // --- Phase-Thinking-2: 无 signature 的 thinking 块保留（照原样回传，不清理）---
            {
                var svc9 = new AiService();
                svc9._conversationHistory.Clear();
                svc9._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "hi" } });
                svc9._conversationHistory.Add(MakeAssistantBlocks(
                    ThinkingBlock("reasoning without sig", null),   // 无 signature（GLM/DeepSeek 完成块结构性无 sig）
                    TextBlock("done")));
                svc9.EnsureValidMessageSequence(svc9._conversationHistory);
                int thinkingN = CountBlocks(svc9._conversationHistory, "thinking");
                bool ok = thinkingN == 1;
                results.Add(("Phase-Thinking-2 无signature保留", ok,
                    ok ? "OK: 无 signature 的 thinking 块照原样保留" : $"FAIL: thinking 块数={thinkingN}（应=1）"));
            }

            // --- Phase-Thinking-3: redacted_thinking 块保留（无 signature 但用 data，不被误删）---
            {
                var svc10 = new AiService();
                svc10._conversationHistory.Clear();
                svc10._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "hi" } });
                svc10._conversationHistory.Add(MakeAssistantBlocks(
                    RedactedBlock("base64data==")));
                svc10.EnsureValidMessageSequence(svc10._conversationHistory);
                int redactedN = CountBlocks(svc10._conversationHistory, "redacted_thinking");
                bool ok = redactedN == 1;
                results.Add(("Phase-Thinking-3 redacted保留", ok,
                    ok ? "OK: redacted_thinking 保留（不被无 signature 规则误删）" : $"FAIL: redacted 块数={redactedN}（应=1）"));
            }

            // --- Phase-Thinking-4: thinking(sig)+tool_use 配对完整（移除毒 thinking 不破坏 tool 配对）---
            {
                var svc11 = new AiService();
                svc11._conversationHistory.Clear();
                svc11._conversationHistory.Add(new Dictionary<string, object> { { "role", "user" }, { "content", "hi" } });
                svc11._conversationHistory.Add(MakeAssistantBlocks(
                    ThinkingBlock("think", "sig_xyz"),
                    new Dictionary<string, object> { { "type", "tool_use" }, { "id", "toolu_T4" }, { "name", "lookup_command" }, { "input", new Dictionary<string, object> { { "query", "MOVE" } } } }));
                svc11._conversationHistory.Add(MakeUserToolResult("toolu_T4", "{}"));
                svc11.EnsureValidMessageSequence(svc11._conversationHistory);
                int thinkingN = CountBlocks(svc11._conversationHistory, "thinking");
                int toolUseN = CountBlocks(svc11._conversationHistory, "tool_use");
                bool ok = thinkingN == 1 && toolUseN == 1;
                results.Add(("Phase-Thinking-4 thinking+tool配对", ok,
                    ok ? "OK: thinking(sig) 保留 + tool_use 配对完整" : $"FAIL: thinking={thinkingN} tool_use={toolUseN}"));
            }

            // --- IsToolError: 区分工具执行失败与成功结果（供 tool_result.is_error 标记）---
            {
                // 失败 → true
                bool t1 = AiService.IsToolError("Error: object reference not set");                 // ExecuteTool 异常
                bool t2 = AiService.IsToolError("BLOCKED: Plan Mode is active. ...");               // Plan Mode 拒绝
                bool t3 = AiService.IsToolError("User rejected this operation.");                   // 用户拒绝
                bool t4 = AiService.IsToolError("{\"error\":\"BLOCKED by TrioBASIC validation\"}");  // 验证拦截
                bool t5 = AiService.IsToolError("{\"error\":\"Unknown tool: foo\"}");                // 未知工具 / 工具内部 error
                // 成功 / 正常结果 → false（关键：不误判）
                bool f1 = AiService.IsToolError("{\"success\":false,\"errors\":[\"line 5: undefined\"]}"); // compile 编译报错 ≠ 工具失败
                bool f2 = AiService.IsToolError("{\"name\":\"MAIN\",\"source\":\"...err handler...\"}");    // read_source 文本含 err
                bool f3 = AiService.IsToolError("{\"programs\":[\"MAIN\",\"SUB\"]}");                // list_programs
                bool f4 = AiService.IsToolError("");                                                // 空
                bool ok = t1 && t2 && t3 && t4 && t5 && !f1 && !f2 && !f3 && !f4;
                results.Add(("Phase-IsToolError 工具失败判定", ok,
                    ok ? "OK: 异常/拒绝/拦截=true，编译报错/正常结果=false" :
                    $"FAIL: t1-5={t1}/{t2}/{t3}/{t4}/{t5} f1-4={f1}/{f2}/{f3}/{f4}"));
            }

            // --- Phase-Loop-1: thinking 逐字重复循环检测（旧版只看 text 会漏，修复后应触发）---
            {
                var st = default(LoopState);
                bool triggered = false;
                const string dup = "让我分析这个运动控制问题。首先考虑加速度限制，再考虑";
                for (int turn = 0; turn < 4; turn++)
                {
                    var c = new List<Dictionary<string, object>>
                    {
                        ThinkingBlock(dup, null),                // 每轮 thinking 逐字相同
                        TextBlock($"第{turn}步：检查参数")        // text 每轮不同（旧版据此漏判）
                    };
                    st = EvaluateLoopTurn(c, st, 3, out triggered, out _);
                }
                results.Add(("Phase-Loop-1 thinking循环检测", triggered,
                    triggered ? "OK: thinking 连续逐字重复第4轮触发终止" : "FAIL: thinking 循环未被检测（旧 text-only 回归）"));
            }

            // --- Phase-Loop-2: text 循环仍触发（防回归）---
            {
                var st = default(LoopState);
                bool triggered = false;
                for (int turn = 0; turn < 4; turn++)
                {
                    var c = new List<Dictionary<string, object>> { TextBlock("好的，我来处理") }; // 无 thinking，text 每轮相同
                    st = EvaluateLoopTurn(c, st, 3, out triggered, out _);
                }
                results.Add(("Phase-Loop-2 text循环不回归", triggered,
                    triggered ? "OK: text 循环仍能检测" : "FAIL: text 检测被破坏"));
            }

            // --- Phase-Loop-3: text 与 thinking 都逐轮变化 → 不误触发 ---
            {
                var st = default(LoopState);
                bool triggered = false;
                for (int turn = 0; turn < 4; turn++)
                {
                    var c = new List<Dictionary<string, object>>
                    {
                        ThinkingBlock($"思考方向{turn}...", null),
                        TextBlock($"执行第{turn}步")
                    };
                    st = EvaluateLoopTurn(c, st, 3, out triggered, out _);
                }
                results.Add(("Phase-Loop-3 正常多步不误触发", !triggered,
                    !triggered ? "OK: 内容逐轮变化不触发" : "FAIL: 正常多步推进被误判为循环"));
            }

            // --- Phase-Loop-4: 纯 tool_use 轮（无 text 无 thinking）不参与计数 ---
            {
                var st = default(LoopState);
                bool triggered = false;
                var toolOnly = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "type", "tool_use" }, { "id", "t1" }, { "name", "list_programs" }, { "input", new Dictionary<string, object>() } }
                };
                for (int turn = 0; turn < 4; turn++)
                    st = EvaluateLoopTurn(toolOnly, st, 3, out triggered, out _);
                results.Add(("Phase-Loop-4 纯tool轮不误判", !triggered,
                    !triggered ? "OK: 纯 tool_use 轮不触发" : "FAIL: 纯工具调用轮被误判"));
            }

            // --- Phase-Filter-1: 纯 thinking assistant 砍尾插占位（对齐 cc-haha）---
            {
                var messages = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "role", "user" }, { "content", "hi" } },
                    MakeAssistantBlocks(ThinkingBlock("光想不说，没产出", null))
                };
                FilterTrailingThinkingFromLastAssistant(messages);
                var blocks = (List<Dictionary<string, object>>)messages[1]["content"];
                bool ok = blocks.Count == 1 && GetStringValue(blocks[0], "type") == "text";
                results.Add(("Phase-Filter-1 纯thinking砍尾插占位", ok,
                    ok ? "OK: 纯 thinking → 占位 text" : $"FAIL: blocks.Count={blocks.Count}（应=1，占位 text）"));
            }

            // --- Phase-Filter-2: [thinking,text] 头部 thinking 不受影响 ---
            {
                var messages = new List<Dictionary<string, object>>
                {
                    MakeAssistantBlocks(ThinkingBlock("plan", null), TextBlock("done"))
                };
                FilterTrailingThinkingFromLastAssistant(messages);
                var blocks = (List<Dictionary<string, object>>)messages[0]["content"];
                bool ok = blocks.Count == 2
                          && GetStringValue(blocks[0], "type") == "thinking"
                          && GetStringValue(blocks[1], "type") == "text";
                results.Add(("Phase-Filter-2 头部thinking不动", ok,
                    ok ? "OK: [thinking,text] 保留" : $"FAIL: blocks.Count={blocks.Count}（应=2，头部 thinking 不应被砍）"));
            }

            // --- Phase-Filter-3: [text, thinking, thinking] 末尾连续 thinking 被砍，留 [text] ---
            {
                var messages = new List<Dictionary<string, object>>
                {
                    MakeAssistantBlocks(TextBlock("hi"), ThinkingBlock("t1", null), ThinkingBlock("t2", null))
                };
                FilterTrailingThinkingFromLastAssistant(messages);
                var blocks = (List<Dictionary<string, object>>)messages[0]["content"];
                bool ok = blocks.Count == 1
                          && GetStringValue(blocks[0], "type") == "text"
                          && GetStringValue(blocks[0], "text") == "hi";
                results.Add(("Phase-Filter-3 末尾连续thinking砍除", ok,
                    ok ? "OK: 砍尾后留 [text(hi)]" : $"FAIL: blocks.Count={blocks.Count}（应=1）"));
            }

            // ========== P-S: research 子 agent（Subagent）==========

            // --- P-S1: ExtractText —— 提取 text 块用空行连接，忽略 thinking/tool_use ---
            {
                var content = new List<Dictionary<string, object>>
                {
                    ThinkingBlock("inner reasoning", null),
                    TextBlock("结论 A"),
                    new Dictionary<string, object> { { "type", "tool_use" }, { "id", "x" }, { "name", "lookup_command" }, { "input", new Dictionary<string, object>() } },
                    TextBlock("结论 B")
                };
                string extracted = AiService.ExtractText(content);
                bool ok = extracted == "结论 A\n\n结论 B";
                results.Add(("P-S1 ExtractText提取文本", ok,
                    ok ? "OK: 两段 text 用空行连接，thinking/tool_use 被忽略" : $"FAIL: extracted=[{extracted}]"));
            }

            // --- P-S2: BuildStubToolResults —— tool_use 列表 → stub tool_result 列表（取消时补齐交替）---
            {
                var toolUses = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "type", "tool_use" }, { "id", "toolu_1" }, { "name", "lookup_command" }, { "input", new Dictionary<string, object>() } },
                    new Dictionary<string, object> { { "type", "tool_use" }, { "id", "toolu_2" }, { "name", "read_source" }, { "input", new Dictionary<string, object>() } }
                };
                var stubs = AiService.BuildStubToolResults(toolUses, "[cancelled]");
                bool ok = stubs.Count == 2
                          && GetStringValue(stubs[0], "tool_use_id") == "toolu_1"
                          && GetStringValue(stubs[0], "content") == "[cancelled]"
                          && GetStringValue(stubs[0], "type") == "tool_result";
                results.Add(("P-S2 BuildStubToolResults", ok,
                    ok ? "OK: 2 个 stub，id/content/type 正确" : $"FAIL: count={stubs.Count}"));
            }

            // --- P-S3: SubagentReadTools 白名单 —— 只读、不含写工具、禁递归 research ---
            {
                var mustContain = new[] { "lookup_command", "read_source", "read_skill", "search_code", "get_status", "list_programs" };
                var mustNotContain = new[] { "write_source", "patch_source", "write_vr", "write_table",
                    "create_program", "delete_program", "compile_program", "run_program", "upload",
                    "enter_plan_mode", "exit_plan_mode", "research" };   // 禁递归
                bool hasReads = mustContain.All(n => SubagentReadTools.Contains(n));
                bool noWrites = mustNotContain.All(n => !SubagentReadTools.Contains(n));
                bool ok = hasReads && noWrites;
                results.Add(("P-S3 白名单只读+禁递归", ok,
                    ok ? $"OK: 含核心只读工具，排除所有写/控制/research（{SubagentReadTools.Count} 个）" :
                    $"FAIL: hasReads={hasReads} noWrites={noWrites}"));
            }

            // --- P-S4: BuildSubagentToolDefinitions —— 过滤白名单 + 末项 cache_control + 不污染主缓存 ---
            {
                var subSvc = new AiService();
                var subTools = subSvc.BuildSubagentToolDefinitions("research");
                var allTools = GetToolDefinitions();
                int expected = allTools.Count(t => SubagentReadTools.Contains(GetStr(t, "name")));
                bool countOk = subTools.Count == expected && expected > 0;
                bool lastHasCache = subTools[subTools.Count - 1].ContainsKey("cache_control");
                bool mainNotPolluted = !allTools[allTools.Count - 1].ContainsKey("cache_control");
                bool allWhitelisted = subTools.All(t => SubagentReadTools.Contains(GetStr(t, "name")));
                bool ok = countOk && lastHasCache && mainNotPolluted && allWhitelisted;
                results.Add(("P-S4 子agent工具集隔离", ok,
                    ok ? $"OK: {expected} 个只读工具，末项 cache_control，主缓存未污染" :
                    $"FAIL: countOk={countOk}(got {subTools.Count}/exp {expected}) lastHasCache={lastHasCache} mainNotPolluted={mainNotPolluted} allWhitelisted={allWhitelisted}"));
            }

            // --- P-S5: ClampSubTurns —— 钳制到 [1,12]，非法（0/负/过大）回落 12 ---
            {
                bool ok = ClampSubTurns(0) == 12
                          && ClampSubTurns(-5) == 12
                          && ClampSubTurns(13) == 12
                          && ClampSubTurns(100) == 12
                          && ClampSubTurns(1) == 1
                          && ClampSubTurns(6) == 6
                          && ClampSubTurns(12) == 12;
                results.Add(("P-S5 ClampSubTurns钳制", ok,
                    ok ? "OK: 非法→12，[1,12] 原样" :
                    $"FAIL: Clamp(0)={ClampSubTurns(0)} (13)={ClampSubTurns(13)} (6)={ClampSubTurns(6)}"));
            }

            // --- P-S6: 取消传播 —— ct 已取消时 RunSubagent 立即抛 OperationCanceledException，不调 API ---
            {
                var subSvc2 = new AiService();
                int apiCalls = 0;
                subSvc2._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                { apiCalls++; return new StreamResult { Content = new List<Dictionary<string, object>>(), StopReason = "end_turn" }; };
                var cts = new CancellationTokenSource();
                cts.Cancel();
                bool threw = false;
                try { subSvc2.RunSubagent("test", "research", 5, cts.Token); }
                catch (OperationCanceledException) { threw = true; }
                bool ok = threw && apiCalls == 0;
                results.Add(("P-S6 取消立即传播", ok,
                    ok ? "OK: ct 已取消 → 抛 OperationCanceledException，未调 API" :
                    $"FAIL: threw={threw} apiCalls={apiCalls}"));
            }

            // --- P-S7: 白名单运行时拦截 —— model 返回 write_source tool_use，被拒为 "not available"，不崩、主线历史不污染 ---
            {
                var subSvc3 = new AiService();
                int apiCalls = 0;
                subSvc3._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                {
                    apiCalls++;
                    if (apiCalls == 1)
                        return new StreamResult
                        {
                            Content = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "tool_use" }, { "id", "toolu_w1" }, { "name", "write_source" },
                                    { "input", new Dictionary<string, object> { { "name", "X" }, { "sourceCode", "Y" } } }
                                }
                            },
                            StopReason = "tool_use"
                        };
                    return new StreamResult
                    {
                        Content = new List<Dictionary<string, object>> { TextBlock("研究完成：拦截生效") },
                        StopReason = "end_turn"
                    };
                };
                var (ret, success) = subSvc3.RunSubagent("test write block", "research", 5, CancellationToken.None);
                bool ok = apiCalls == 2 && success && ret.Contains("研究完成") && subSvc3._conversationHistory.Count == 0;
                results.Add(("P-S7 白名单运行时拦截", ok,
                    ok ? "OK: write_source 被拒 → 跑完终止、success=true，主线历史未污染" :
                    $"FAIL: apiCalls={apiCalls} success={success} ret=[{ret}] mainHist={subSvc3._conversationHistory.Count}"));
            }

            // --- P-S8: 无 tool_use 立即退出 —— model 返回纯 text（无 tool_use），RunSubagent 单轮返回 ---
            {
                var subSvc4 = new AiService();
                int apiCalls = 0;
                subSvc4._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                {
                    apiCalls++;
                    return new StreamResult
                    {
                        Content = new List<Dictionary<string, object>> { TextBlock("直接给结论") },
                        StopReason = "end_turn"
                    };
                };
                var (ret, success) = subSvc4.RunSubagent("simple", "research", 5, CancellationToken.None);
                bool ok = apiCalls == 1 && success && ret == "直接给结论";
                results.Add(("P-S8 无tool_use单轮退出", ok,
                    ok ? "OK: 无 tool_use → 单轮返回 text、success=true" :
                    $"FAIL: apiCalls={apiCalls} success={success} ret=[{ret}]"));
            }

            // --- P-S9: research 工具已注册 + 纯IO分流 + query 空校验 ---
            {
                var tools = GetToolDefinitions();
                var names = new HashSet<string>(tools.Select(t => GetStr(t, "name")), StringComparer.OrdinalIgnoreCase);
                bool registered = names.Contains("research");
                bool isPure = PureIoTools.Contains("research");
                var subSvc5 = new AiService();
                var dispatchResult = subSvc5.DispatchTool("research", new Dictionary<string, object> { { "max_turns", 3 } });
                bool queryGuard = _json.Serialize(dispatchResult).Contains("query is required");
                bool ok = registered && isPure && queryGuard;
                results.Add(("P-S9 research注册+分流+空校验", ok,
                    ok ? "OK: research 已注册、属 PureIoTools、query 空时拒绝" :
                    $"FAIL: registered={registered} isPure={isPure} queryGuard={queryGuard}"));
            }

            // --- P-S10: GetSubagentPrompt 4 种 agent type 都非空且互不相同 ---
            {
                var p_research = AiService.GetSubagentPrompt("research");
                var p_review   = AiService.GetSubagentPrompt("review");
                var p_debug    = AiService.GetSubagentPrompt("debug");
                var p_explore  = AiService.GetSubagentPrompt("explore");
                var p_verify   = AiService.GetSubagentPrompt("verify");
                var prompts = new[] { p_research, p_review, p_debug, p_explore, p_verify };
                bool allNonEmpty = prompts.All(p => !string.IsNullOrEmpty(p) && p.Length > 50);
                int distinctCount = new HashSet<string>(prompts).Count;
                bool fallbackOk = AiService.GetSubagentPrompt("nonexistent") == p_research;   // 未知 type 安全回落 research
                bool ok = allNonEmpty && distinctCount == 5 && fallbackOk;
                results.Add(("P-S10 五种agent prompt", ok,
                    ok ? "OK: research/review/debug/explore/verify 5 个 prompt 非空且互不相同，未知 type 回落 research" :
                    $"FAIL: allNonEmpty={allNonEmpty} distinctCount={distinctCount} fallbackOk={fallbackOk}"));
            }

            // --- P-S11: review/debug/explore 已注册 ---
            {
                var tools = GetToolDefinitions();
                var names = new HashSet<string>(tools.Select(t => GetStr(t, "name")), StringComparer.OrdinalIgnoreCase);
                bool hasReview  = names.Contains("review");
                bool hasDebug   = names.Contains("debug");
                bool hasExplore = names.Contains("explore");
                bool hasVerify  = names.Contains("verify");
                bool ok = hasReview && hasDebug && hasExplore && hasVerify;
                results.Add(("P-S11 新agent已注册", ok,
                    ok ? "OK: review/debug/explore/verify 都在 GetToolDefinitions 中" :
                    "FAIL: review=" + hasReview + " debug=" + hasDebug + " explore=" + hasExplore + " verify=" + hasVerify));
            }

            // --- P-S12: review/debug/explore 属于 PureIoTools（与 research 同机制，避免主循环 Task.Run 内二次 Invoke）---
            {
                bool hasReview  = PureIoTools.Contains("review");
                bool hasDebug   = PureIoTools.Contains("debug");
                bool hasExplore = PureIoTools.Contains("explore");
                bool hasVerify  = PureIoTools.Contains("verify");
                bool ok = hasReview && hasDebug && hasExplore && hasVerify;
                results.Add(("P-S12 新agent属PureIo", ok,
                    ok ? "OK: review/debug/explore/verify 都在 PureIoTools 中" :
                    "FAIL: review=" + hasReview + " debug=" + hasDebug + " explore=" + hasExplore + " verify=" + hasVerify));
            }

            // --- P-S13: review/debug/explore/verify query 空校验（DispatchTool 返回 "query is required"）---
            {
                var subSvc = new AiService();
                bool reviewGuard  = _json.Serialize(subSvc.DispatchTool("review",  new Dictionary<string, object>())).Contains("query is required");
                bool debugGuard   = _json.Serialize(subSvc.DispatchTool("debug",   new Dictionary<string, object>())).Contains("query is required");
                bool exploreGuard = _json.Serialize(subSvc.DispatchTool("explore", new Dictionary<string, object>())).Contains("query is required");
                bool verifyGuard  = _json.Serialize(subSvc.DispatchTool("verify",  new Dictionary<string, object>())).Contains("query is required");
                bool ok = reviewGuard && debugGuard && exploreGuard && verifyGuard;
                results.Add(("P-S13 新agent空query拒绝", ok,
                    ok ? "OK: review/debug/explore/verify query 为空时返回 query is required" :
                    "FAIL: review=" + reviewGuard + " debug=" + debugGuard + " explore=" + exploreGuard + " verify=" + verifyGuard));
            }

            // --- P-S14: review 子 agent 白名单运行时拦截 —— write_source tool_use 被拒，不崩、主线历史不污染 ---
            {
                var subSvc3 = new AiService();
                int apiCalls = 0;
                subSvc3._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                {
                    apiCalls++;
                    if (apiCalls == 1)
                        return new StreamResult
                        {
                            Content = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "tool_use" }, { "id", "toolu_rw1" }, { "name", "write_source" },
                                    { "input", new Dictionary<string, object> { { "name", "X" }, { "sourceCode", "Y" } } }
                                }
                            },
                            StopReason = "tool_use"
                        };
                    return new StreamResult
                    {
                        Content = new List<Dictionary<string, object>> { TextBlock("审查完成：拦截生效") },
                        StopReason = "end_turn"
                    };
                };
                var (ret, success) = subSvc3.RunSubagent("review program X", "review", 5, CancellationToken.None);
                bool ok = apiCalls == 2 && success && ret.Contains("审查完成") && subSvc3._conversationHistory.Count == 0;
                results.Add(("P-S14 review拦截write_source", ok,
                    ok ? "OK: review 子 agent write_source 被拒 → 跑完终止、success=true，主线历史未污染" :
                    "FAIL: apiCalls=" + apiCalls + " success=" + success + " ret=[" + ret + "] mainHist=" + subSvc3._conversationHistory.Count));
            }

            // --- P-S15: per-type 工具池正确性（research=超集；review/debug/explore 各裁剪且均为超集子集，无写工具泄漏）---
            {
                bool researchIsSuperset = SubagentToolPools["research"].SetEquals(SubagentReadTools);
                bool allPoolsSubsetOfSuperset = SubagentToolPools.Values.All(pool => pool.All(n => SubagentReadTools.Contains(n)));
                bool reviewTrimmed  = SubagentToolPools["review"].Count  < SubagentReadTools.Count;
                bool debugTrimmed   = SubagentToolPools["debug"].Count   < SubagentReadTools.Count;
                bool exploreTrimmed = SubagentToolPools["explore"].Count < SubagentReadTools.Count;
                bool verifyTrimmed  = SubagentToolPools["verify"].Count  < SubagentReadTools.Count;
                // 特征工具裁剪边界：read_drive_param（实时驱动参数）只在 research 超集 + debug（诊断需读驱动），不在 review/explore/verify
                bool driveInDebug      =  SubagentToolPools["debug"].Contains("read_drive_param");
                bool driveNotInReview  = !SubagentToolPools["review"].Contains("read_drive_param");
                bool driveNotInExplore = !SubagentToolPools["explore"].Contains("read_drive_param");
                bool driveNotInVerify  = !SubagentToolPools["verify"].Contains("read_drive_param");
                // read_skill（读技能文档）只在 research 超集；review/debug/explore/verify 查代码/实时状态，不查 skill 文档
                bool skillOnlyResearch = SubagentToolPools["research"].Contains("read_skill")
                                         && !SubagentToolPools["review"].Contains("read_skill")
                                         && !SubagentToolPools["debug"].Contains("read_skill")
                                         && !SubagentToolPools["explore"].Contains("read_skill")
                                         && !SubagentToolPools["verify"].Contains("read_skill");
                // verify 池兼顾代码 + 实时状态（独立验证需两者）：含 read_source（代码）且 get_events（实时事件）
                bool verifyCodeAndLive = SubagentToolPools["verify"].Contains("read_source") && SubagentToolPools["verify"].Contains("get_events");
                // BuildSubagentToolDefinitions("review") 输出 ⊆ review 池（按池过滤，不泄漏池外工具）
                var subSvc15 = new AiService();
                var reviewOut = subSvc15.BuildSubagentToolDefinitions("review");
                bool reviewOutSubset = reviewOut.Count > 0 && reviewOut.All(t => SubagentToolPools["review"].Contains(GetStr(t, "name")));
                bool ok = researchIsSuperset && allPoolsSubsetOfSuperset && reviewTrimmed && debugTrimmed && exploreTrimmed && verifyTrimmed
                          && driveInDebug && driveNotInReview && driveNotInExplore && driveNotInVerify && skillOnlyResearch && verifyCodeAndLive && reviewOutSubset;
                results.Add(("P-S15 per-type工具池", ok,
                    ok ? "OK: research=超集，review/debug/explore/verify 各裁剪且为超集子集（无写工具泄漏），BuildSubagentToolDefinitions 按池过滤" :
                    $"FAIL: researchSuperset={researchIsSuperset} allPoolsSubset={allPoolsSubsetOfSuperset} review/dbg/exp/vrf trimmed={reviewTrimmed}/{debugTrimmed}/{exploreTrimmed}/{verifyTrimmed} drive(dbg/rev/exp/vrf)={driveInDebug}/{driveNotInReview}/{driveNotInExplore}/{driveNotInVerify} skillOnly={skillOnlyResearch} verifyCodeAndLive={verifyCodeAndLive} reviewOutSubset={reviewOutSubset}"));
            }

            // --- P-S16: 失败语义（item 1）—— API 全失败 / 无文本产出 → RunSubagent 返回 success=false ---
            {
                // (a) CallApiOnce 返回 null（API 全失败）→ success=false，conclusion 含 "API failed"
                var svcA = new AiService();
                svcA._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) => null;
                var (concA, okA) = svcA.RunSubagent("fail case", "research", 5, CancellationToken.None);
                bool aFailed = !okA && concA.Contains("API failed");
                // (b) 跑完但无 text（只 thinking、StopReason=end_turn → 直接退出，lastText 空）→ success=false
                var svcB = new AiService();
                int callsB = 0;
                svcB._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                {
                    callsB++;
                    return new StreamResult
                    {
                        Content = new List<Dictionary<string, object>> { ThinkingBlock("only thinking no text", null) },
                        StopReason = "end_turn"
                    };
                };
                var (concB, okB) = svcB.RunSubagent("no text", "research", 5, CancellationToken.None);
                bool bFailed = !okB && callsB == 1 && concB.Contains("no textual conclusion");
                bool ok = aFailed && bFailed;
                results.Add(("P-S16 失败语义success=false", ok,
                    ok ? "OK: API 全失败/无文本产出 → success=false（DispatchTool 据此返回 is_error，不再把失败伪装成结论）" :
                    $"FAIL: aFailed={aFailed}(okA={okA} concA=[{concA}]) bFailed={bFailed}(okB={okB} callsB={callsB} concB=[{concB}])"));
            }

            // --- P-S17: thinking 按 agentType（item 3）—— 全局强度时 review/debug/verify 跟随，research/explore 始终 off ---
            {
                // 注意：AiService 构造函数调 LoadConfig() 会从磁盘 config 覆盖静态 _reasoningEffort，
                // 故必须在 new AiService() 【之后】设置测试值，RunSubagent 才读得到。
                string Capture(string type, string effort)
                {
                    var capSvc = new AiService();          // LoadConfig 在此运行，覆盖静态字段
                    _reasoningEffort = effort;             // 构造之后再覆盖，确保 RunSubagent 读到测试值
                    string etVal = null;
                    capSvc._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct2) =>
                    {
                        if (etVal == null) etVal = re;
                        return new StreamResult { Content = new List<Dictionary<string, object>> { TextBlock("done") }, StopReason = "end_turn" };
                    };
                    capSvc.RunSubagent("t", type, 3, CancellationToken.None);
                    return etVal;
                }
                string savedEffort = _reasoningEffort;
                try
                {
                    // 全局 high：review/debug/verify → high；research/explore → off
                    bool onOk  = Capture("review", "high") == "high" && Capture("debug", "high") == "high" && Capture("verify", "high") == "high";
                    bool offOk = Capture("research", "high") == "off" && Capture("explore", "high") == "off";
                    // 全局 off：review 也应 off
                    bool globalOffOk = Capture("review", "off") == "off";
                    bool ok = onOk && offOk && globalOffOk;
                    results.Add(("P-S17 thinking按agentType", ok,
                        ok ? "OK: 全局 high→review/debug/verify 跟随 high；research/explore off；全局 off→全 off" :
                        $"FAIL: review/debug/verify/research/explore/global-off effort 不符合预期"));
                }
                finally { _reasoningEffort = savedEffort; }
            }

            // --- P-S18: verify prompt 含 PASS/FAIL/PARTIAL 三态判定 + 明确只读（不编译）—— 验证语义核心 ---
            {
                var p = AiService.GetSubagentPrompt("verify");
                bool hasPass    = p.Contains("VERDICT: PASS");
                bool hasFail    = p.Contains("VERDICT: FAIL");
                bool hasPartial = p.Contains("VERDICT: PARTIAL");
                bool noCompile  = p.Contains("do NOT compile");   // 保持只读：明确禁止编译
                bool ok = hasPass && hasFail && hasPartial && noCompile;
                results.Add(("P-S18 verify判定格式", ok,
                    ok ? "OK: verify prompt 含 PASS/FAIL/PARTIAL 三态判定 + 明确只读（不编译）" :
                    $"FAIL: pass={hasPass} fail={hasFail} partial={hasPartial} noCompile={noCompile}"));
            }

            // --- P-S19: 模型按 agentType 分流（research/explore→轻；review/debug/verify→主；轻留空回退主）---
            {
                // (a) research + lightModel 非空 → 走轻模型
                var svcA = new AiService();
                svcA._model = "main-m";
                svcA._lightModel = "light-m";
                string capA = null;
                svcA._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                { capA = model; return new StreamResult { Content = new List<Dictionary<string, object>>(), StopReason = "end_turn" }; };
                svcA.RunSubagent("t", "research", 3, CancellationToken.None);
                bool okA = capA == "light-m";

                // (b) research + lightModel 空 → 回退主模型
                var svcB = new AiService();
                svcB._model = "main-m";
                svcB._lightModel = "";
                string capB = null;
                svcB._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                { capB = model; return new StreamResult { Content = new List<Dictionary<string, object>>(), StopReason = "end_turn" }; };
                svcB.RunSubagent("t", "research", 3, CancellationToken.None);
                bool okB = capB == "main-m";

                // (c) verify + lightModel 非空 → 仍走主模型（review/debug/verify 不降级到轻）
                var svcC = new AiService();
                svcC._model = "main-m";
                svcC._lightModel = "light-m";
                string capC = null;
                svcC._callApiOnceOverride = (sys, tools, msgs, mt, re, suppress, model, ct) =>
                { capC = model; return new StreamResult { Content = new List<Dictionary<string, object>>(), StopReason = "end_turn" }; };
                svcC.RunSubagent("t", "verify", 3, CancellationToken.None);
                bool okC = capC == "main-m";

                bool ok = okA && okB && okC;
                results.Add(("P-S19 模型按agentType分流", ok,
                    ok ? "OK: research/explore→轻(空回退主)；review/debug/verify→主" :
                    "FAIL: a=" + (capA ?? "<null>") + " b=" + (capB ?? "<null>") + " c=" + (capC ?? "<null>")));
            }

            // --- P-S20: 写工具风险分级 —— AutoAllowWriteTools 是 WriteTools 子集（Plan Mode 仍拦截全部写），
            //            仅含程序编辑/编译类（免确认）；运行/上下传/变量写入类保留人工确认 ---
            {
                var autoExpected = new[] { "write_source", "patch_source", "create_program", "delete_program", "rename_program", "compile_program" };
                var mustConfirm = new[] { "run_program", "stop_program", "set_program_process", "upload", "download", "write_vr", "write_table", "write_iec_variables" };
                bool allAuto = autoExpected.All(n => AutoAllowWriteTools.Contains(n));
                bool subset = AutoAllowWriteTools.All(n => WriteTools.Contains(n));
                bool mustNotAuto = mustConfirm.All(n => !AutoAllowWriteTools.Contains(n));
                bool ok = allAuto && subset && mustNotAuto;
                results.Add(("P-S20 写工具风险分级", ok,
                    ok ? "OK: 程序编辑/编译免确认；运行/上下传/变量写入保留人工确认" :
                    "FAIL: auto=" + string.Join(",", autoExpected.Where(n => !AutoAllowWriteTools.Contains(n))) +
                    " subset=" + (subset ? "ok" : string.Join(",", AutoAllowWriteTools.Where(n => !WriteTools.Contains(n)))) +
                    " leaked=" + string.Join(",", mustConfirm.Where(n => AutoAllowWriteTools.Contains(n)))));
            }

            // --- P-S21: 编辑类会话级许可重置语义 —— 本对话首次确认后放行，新对话/清空重置回 false ---
            //            （0.3.23：编辑/编译类从「永久免确认」改为「会话级首次许可」，每轮对话一道人工闸）---
            {
                var svcS21 = new AiService();
                svcS21.StartNewSession();
                bool a = !svcS21._sessionEditApproved;          // 初始/新会话 = false
                svcS21._sessionEditApproved = true;             // 模拟本对话已许可编辑类
                svcS21.StartNewSession();
                bool b = !svcS21._sessionEditApproved;          // 新会话重置
                svcS21._sessionEditApproved = true;
                svcS21.ClearHistory();
                bool c = !svcS21._sessionEditApproved;          // 清空重置
                bool ok = a && b && c;
                results.Add(("P-S21 编辑类许可会话重置", ok,
                    ok ? "OK: 初始/StartNewSession/ClearHistory 后 _sessionEditApproved=false（每轮对话重新要首次许可）" :
                    "FAIL: a=" + a + " b=" + b + " c=" + c));
            }

            // ========== 报告 ==========
            var sb = new StringBuilder();
            sb.AppendLine("=== RTSysAI Optimization Tests ===");
            sb.AppendLine();
            int pass = results.Count(r => r.ok);
            int fail = results.Count(r => !r.ok);
            foreach (var (name, ok, detail) in results)
            {
                sb.AppendFormat("{0}  {1}  — {2}\n", ok ? "PASS" : "FAIL", name, detail);
            }
            sb.AppendLine();
            sb.AppendFormat("结果: {0} passed, {1} failed / {2} total\n", pass, fail, results.Count);

            return (pass, fail, sb.ToString());
        }

        // ---- Test helpers ----

        private static Dictionary<string, object> MakeLookupInput(string query, string full, string library)
        {
            var d = new Dictionary<string, object> { { "query", query } };
            if (full != null) d["full"] = full;
            if (library != null && library.Length > 0) d["library"] = library;
            return d;
        }

        private static Dictionary<string, object> MakeAssistantToolUse(
            string toolName, string toolId, Dictionary<string, object> input)
        {
            return new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        { "type", "tool_use" },
                        { "id", toolId },
                        { "name", toolName },
                        { "input", input }
                    }
                }}
            };
        }

        private static Dictionary<string, object> MakeAssistantToolUse(
            (string name, string id, Dictionary<string, object> input)[] tools)
        {
            var blocks = new List<Dictionary<string, object>>();
            foreach (var (name, id, input) in tools)
            {
                blocks.Add(new Dictionary<string, object>
                {
                    { "type", "tool_use" },
                    { "id", id },
                    { "name", name },
                    { "input", input }
                });
            }
            return new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", blocks }
            };
        }

        private static Dictionary<string, object> MakeUserToolResult(string toolId, string content)
        {
            return new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", new List<Dictionary<string, object>>
                {
                    MakeToolResultBlock(toolId, content)
                }}
            };
        }

        private static Dictionary<string, object> MakeToolResultBlock(string toolId, string content)
        {
            return new Dictionary<string, object>
            {
                { "type", "tool_result" },
                { "tool_use_id", toolId },
                { "content", content }
            };
        }

        private static Dictionary<string, object> MakeAssistantText(string text)
        {
            return new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "type", "text" }, { "text", text } }
                }}
            };
        }

        // ---- Thinking 测试辅助 ----

        private static Dictionary<string, object> MakeAssistantBlocks(params Dictionary<string, object>[] blocks)
        {
            return new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", new List<Dictionary<string, object>>(blocks) }
            };
        }

        // signature == null → 不加 signature 字段（模拟 GLM/DeepSeek 完成块结构性无 sig）；非空 → 加字段（真 Anthropic 完成块）。
        private static Dictionary<string, object> ThinkingBlock(string text, string signature)
        {
            var b = new Dictionary<string, object> { { "type", "thinking" }, { "thinking", text } };
            if (signature != null) b["signature"] = signature;
            return b;
        }

        private static Dictionary<string, object> TextBlock(string text) =>
            new Dictionary<string, object> { { "type", "text" }, { "text", text } };

        private static Dictionary<string, object> RedactedBlock(string data) =>
            new Dictionary<string, object> { { "type", "redacted_thinking" }, { "data", data } };

        private static int CountBlocks(List<Dictionary<string, object>> messages, string type)
        {
            int n = 0;
            foreach (var m in messages)
            {
                if (!(m.TryGetValue("content", out var c) && c is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                    if (GetStringValue(b, "type") == type) n++;
            }
            return n;
        }
    }
}
