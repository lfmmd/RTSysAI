using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RTSys.PlugIn
{
    // 轻量 research subagent：主模型调用 research 工具时，在独立 subMessages 列表里跑研究循环，
    // 复用 ExecuteTool 执行只读工具，跑完只把最后一条 assistant 文本结论回传主线。
    // 子 agent 期间查阅的命令文档/源码（大块 tool_result）从不进主 _conversationHistory —— 这是
    // 解决"永久保留的参考内容撑大主上下文"的核心（见 plan: replicated-skipping-adleman.md）。
    internal partial class AiService
    {
        private const int SubagentMaxTurns = 12;

        // 子 agent 执行标志：RunSubagent 期间为 true。ExecuteTool 据此跳过主线专属的 lookup 去重
        // （去重扫的是 _conversationHistory 主历史；子 agent 的调用在隔离 subMessages，既扫不到自己的、
        // 又会误命中主线历史返回误导性的 "reference earlier tool_result"。子 agent 靠 prompt + cap 兜底）。
        private bool _inSubagent = false;
        private const int SubagentToolResultCap = 16000;   // 单工具结果上限，防 full HTML 把子 agent 自己撑爆
        private const int SubagentTrimCap = 40;            // subMessages 条数兜底上限

        /// <summary>钳制子 agent 轮数到 [1, SubagentMaxTurns]，非法值（0/负/过大）回落默认。纯函数，便于单测。</summary>
        private static int ClampSubTurns(int n)
        {
            if (n <= 0 || n > SubagentMaxTurns) return SubagentMaxTurns;
            return n;
        }

        /// <summary>从 content blocks 提取所有 text 块，用空行连接（thinking/tool_use 忽略）。</summary>
        private static string ExtractText(List<Dictionary<string, object>> content)
        {
            var sb = new StringBuilder();
            foreach (var b in content)
            {
                if (GetStringValue(b, "type") == "text")
                {
                    var t = GetStringValue(b, "text");
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (sb.Length > 0) sb.Append("\n\n");
                        sb.Append(t);
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>轨迹摘要用：压扁换行/制表为单空格、合并连续空格、截断到 max 并加省略号。</summary>
        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t' || ch == ' ')
                {
                    if (!prevSpace) { sb.Append(' '); prevSpace = true; }
                }
                else { sb.Append(ch); prevSpace = false; }
            }
            var flat = sb.ToString().Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }

        /// <summary>构造 stub tool_result 列表（取消时补齐，保持 user/assistant 交替合法）。</summary>
        private static List<Dictionary<string, object>> BuildStubToolResults(
            List<Dictionary<string, object>> toolUses, string reason)
        {
            var r = new List<Dictionary<string, object>>();
            foreach (var tb in toolUses)
                r.Add(new Dictionary<string, object>
                {
                    { "type", "tool_result" },
                    { "tool_use_id", GetStringValue(tb, "id") ?? "" },
                    { "content", reason }
                });
            return r;
        }

        /// <summary>subMessages 条数兜底：超 cap 时保留首条 + 末尾，中间丢弃并维持 user/assistant 交替。</summary>
        private static void SubagentTrimIfNeeded(List<Dictionary<string, object>> msgs, int cap)
        {
            if (msgs.Count <= cap) return;
            var head = msgs[0];
            var tailCount = cap - 1;
            var tail = msgs.GetRange(msgs.Count - tailCount, tailCount);
            msgs.Clear();
            msgs.Add(head);
            // head(user) 后紧跟 tail[0](user) → 插 assistant 占位维持交替
            if (GetStringValue(tail[0], "role") == "user")
                msgs.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", "[earlier subagent turns trimmed]" } });
            foreach (var m in tail) msgs.Add(m);
        }

        /// <summary>
        /// 子 agent retry 包装（与 CallApiWithRetry 同结构，但调 CallApiOnce 且 suppressUiCallbacks=true，
        /// 失败静默返回 null，不弹 OnSystemMessage 刷屏）。
        /// </summary>
        private StreamResult CallSubagentWithRetry(
            List<Dictionary<string, object>> sys,
            List<Dictionary<string, object>> tools,
            List<Dictionary<string, object>> msgs,
            int maxTokens, bool enableThinking, int budgetTokens,
            string model,
            CancellationToken ct)
        {
            const int MaxAttempts = 3;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    return CallApiOnce(sys, tools, msgs, maxTokens, enableThinking, budgetTokens,
                                       suppressUiCallbacks: true, model, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (RetryableApiException ex)
                {
                    if (attempt >= MaxAttempts - 1) return null;
                    int delay = ex.RetryAfterSeconds.HasValue && ex.RetryAfterSeconds.Value > 0
                        ? Math.Min(ex.RetryAfterSeconds.Value * 1000, 30000)
                        : GetBackoffDelay(attempt);
                    SleepWithCancel(delay, ct);
                }
                catch (System.IO.IOException)
                {
                    if (attempt >= MaxAttempts - 1) return null;
                    SleepWithCancel(GetBackoffDelay(attempt), ct);
                }
            }
            return null;
        }

        /// <summary>
        /// 子 agent 方言上下文：子 agent 拿不到主线 BuildDynamicContext（不知当前项目方言），需显式告知，
        /// 否则 IEC 项目里 verify/review 不知用 library=iec、且无 IEC 防漂移锚点（与主线方言切换不对齐的盲区）。
        /// 只注入精炼锚点（方言感知 + library 指向 + 关键防漂移），不重复主线全套 20KB 规则 ——
        /// 子 agent 靠 lookup_command 查证，不需全套规则强化。dialect 未知则返回 null（保持旧行为）。
        /// 时序：RunSubagent 由主线工具执行阶段调用，此时 _activeDialect 已被本轮 CallApiStream 开头刷新。
        /// </summary>
        private string BuildSubagentDialectHint()
        {
            var dialect = string.IsNullOrEmpty(_activeDialect) ? "" : _activeDialect;
            if (dialect == "triobasic")
                return "## Current dialect: TrioBASIC\n\n" +
                       "You are working on TrioBASIC programs. Verify/research with lookup_command(query, library=\"triobasic\"). " +
                       "TrioBASIC is a niche BASIC dialect — it drifts to VB/VB.NET/QBasic/PowerBASIC. " +
                       "Do NOT trust your memory of \"BASIC\"; verify every command/declaration (DIM...AS type, motion MOVE/MOVEABS/CONNECT/WAITS, axis params SPEED/ACCEL/WDOG) against the reference.";
            if (dialect == "iec")
                return "## Current dialect: IEC ST\n\n" +
                       "You are working on IEC ST programs. Verify/research with lookup_command(query, library=\"iec\") and get_iec_task_detail. " +
                       "IEC uses its OWN TC_* motion blocks (TC_MOVEABS, TC_MOVECIRC, TC_CONNECT, TC_CAM...) — NEVER PLCOpen MC_* (MC_MoveAbsolute etc.). " +
                       "Domain FBs (PID, RAMP, ALARM_A, AVERAGE...) have exact pins — verify each via lookup.";
            return null;
        }

        /// <summary>
        /// 子 agent 主循环（agentType = research/review/debug/explore）。
        /// 三类差异：prompt（GetSubagentPrompt）+ schema 工具池（BuildSubagentToolDefinitions(agentType)）+
        /// thinking（review/debug 跟随全局开关；research/explore 始终关）。
        /// 独立 subMessages（局部，不碰 _conversationHistory），复用 ExecuteTool 执行只读工具（运行时拦截共用 SubagentReadTools 超集）。
        /// 返回 (conclusion, success)：success = 是否产出了文本结论（无产出 / API 全失败 → false，DispatchTool 据此返回 is_error）。
        /// 由 DispatchTool 同步调用（跑在主循环 Task.Run 线程）。
        /// </summary>
        private (string conclusion, bool success) RunSubagent(string task, string agentType, int maxTurns, CancellationToken ct)
        {
            maxTurns = ClampSubTurns(maxTurns);

            var subMessages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", task }
                }
            };
            var subTools = BuildSubagentToolDefinitions(agentType);
            var subSystem = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", GetSubagentPrompt(agentType) },
                    { "cache_control", new { type = "ephemeral" } }
                }
            };
            // 方言上下文注入：告知当前方言 + library 指向 + 关键防漂移锚点（补全子 agent 盲区）。
            // 放在 prompt 块之后、不加 cache_control —— _activeDialect 变化只影响本块及之后，prompt 块前缀缓存保留（同 memory 块）。
            var dialectHint = BuildSubagentDialectHint();
            if (!string.IsNullOrEmpty(dialectHint))
            {
                subSystem.Add(new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", dialectHint }
                });
            }
            // 注入持久化记忆：子 agent 拿不到主线历史/项目上下文/task 清单，memory 是它了解用户偏好
            // （语言、注释风格等）与项目约定的唯一补充来源。与主线一致（CallApiStream 的 memory 块）。
            // 放在 prompt 块之后且不加 cache_control——prompt 块保留前缀缓存断点，memory 变化不影响其缓存。
            if (_memoryEnabled)
            {
                var memText = LoadMemory();
                if (!string.IsNullOrEmpty(memText))
                {
                    subSystem.Add(new Dictionary<string, object>
                    {
                        { "type", "text" },
                        { "text", "## Persistent Memory (user preferences / project conventions — follow these in your findings)\n\n" + memText }
                    });
                }
            }
            // thinking：仅 review/debug/verify 跟随全局开关（分析/诊断/验证类，深度推理有价值）；research/explore 始终关（查文档/遍历，不需要）。
            bool subThinking = (agentType == "review" || agentType == "debug" || agentType == "verify") && _enableThinking;
            int subBudget = subThinking ? _budgetTokens : 0;
            // 模型分流（与 thinking 同分组）：research/explore（查文档/探索，简单）走轻模型；review/debug/verify（审查/诊断/验证，需推理）走主模型。轻模型留空则回退主。
            string subModel = (agentType == "research" || agentType == "explore") && !string.IsNullOrEmpty(_lightModel)
                ? _lightModel : _model;

            // 内部轨迹收集：每轮思考 + 工具调用 + 结果摘要 + 最终结论，跑完通过 OnResearchTrace 回传给
            // ChatPanel 折叠显示（用户可见 subagent 内部过程）。声明在 try 外，确保取消/异常路径 finally 也能回传已有部分。
            var trace = new StringBuilder();
            trace.AppendLine("[" + agentType + "] subagent trace (cap " + maxTurns + " turns)");

            bool savedShowToolStatus = _showToolStatus;
            _showToolStatus = false;   // 抑制 ExecuteTool 内部逐工具 OnToolStatus 刷屏
            OnResearchStart?.Invoke(agentType, maxTurns);   // 显示 ChatPanel 顶部进度条 banner
            _inSubagent = true;   // 标记子 agent 执行中：ExecuteTool 据此跳过主线专属的 lookup 去重
            try
            {
                string lastText = "";
                for (int turn = 0; turn < maxTurns; turn++)
                {
                    ct.ThrowIfCancellationRequested();

                    StreamResult result = CallSubagentWithRetry(subSystem, subTools, subMessages,
                        DefaultMaxTokens, enableThinking: subThinking, budgetTokens: subBudget, subModel, ct);

                    if (result == null)
                    {
                        trace.AppendLine();
                        trace.AppendLine("[API failed — partial trace above]");
                        return string.IsNullOrEmpty(lastText)
                            ? ("[" + agentType + " subagent: API failed before producing any findings]", false)
                            : (lastText + "\n\n[..." + agentType + " subagent: API failed, returning partial findings above...]", true);
                    }

                    // token 计入主线总消耗（子 agent 的消耗是真实成本，计入正确）
                    _totalInputTokens += result.InputTokens;
                    _totalOutputTokens += result.OutputTokens;
                    _totalCacheReadTokens += result.CacheReadTokens;
                    _totalCacheCreateTokens += result.CacheCreateTokens;

                    var turnText = ExtractText(result.Content);
                    if (!string.IsNullOrEmpty(turnText)) lastText = turnText;

                    // 轨迹：本轮思考（仅 review/debug/verify 开了 thinking；research/explore 此处为空跳过）。
                    bool turnHeadered = false;
                    foreach (var b in result.Content)
                    {
                        if (GetStringValue(b, "type") == "thinking")
                        {
                            var th = GetStringValue(b, "thinking");
                            if (!string.IsNullOrEmpty(th))
                            {
                                if (!turnHeadered) { trace.AppendLine(); trace.AppendLine("── turn " + (turn + 1) + " ──"); turnHeadered = true; }
                                trace.Append("💭 ").Append(Clip(th, 500)).AppendLine();
                            }
                        }
                    }

                    subMessages.Add(new Dictionary<string, object>
                    {
                        { "role", "assistant" },
                        { "content", result.Content }
                    });

                    var toolUses = result.Content
                        .Where(b => GetStringValue(b, "type") == "tool_use")
                        .ToList();

                    // 无 tool_use → 研究完成（最后一轮必是纯 text 结论）
                    if (toolUses.Count == 0 || result.StopReason != "tool_use")
                        break;

                    // 进度：结构化回调驱动 ChatPanel 顶部进度条（不再走 OnToolStatus，避免聊天流刷屏）
                    var names = string.Join(", ", toolUses.Select(b => GetStringValue(b, "name")));
                    OnResearchTurn?.Invoke(agentType, turn + 1, maxTurns, names);

                    // 轨迹：本轮工具调用（name + 入参摘要）
                    if (!turnHeadered) { trace.AppendLine(); trace.AppendLine("── turn " + (turn + 1) + " ──"); turnHeadered = true; }
                    foreach (var tb in toolUses)
                    {
                        var tn = GetStringValue(tb, "name");
                        var ti = GetDictValue(tb, "input") ?? new Dictionary<string, object>();
                        string inputBrief;
                        try { inputBrief = _json.Serialize(ti); } catch { inputBrief = ""; }
                        trace.Append("🔧 ").Append(tn).Append("  ").Append(Clip(inputBrief, 200)).AppendLine();
                    }

                    // 并行执行工具：白名单外的拒绝，单结果超限截断
                    var toolResultMap = new Dictionary<string, string>(StringComparer.Ordinal);
                    var tasks = new List<Task>();
                    foreach (var tb in toolUses)
                    {
                        var id = GetStringValue(tb, "id");
                        var tname = GetStringValue(tb, "name");
                        var tinput = GetDictValue(tb, "input") ?? new Dictionary<string, object>();
                        var cid = id;
                        tasks.Add(Task.Run(() =>
                        {
                            string r;
                            if (!SubagentReadTools.Contains(tname))
                                r = "Error: tool '" + tname + "' is not available in the research subagent (read-only tools only).";
                            else
                            {
                                try { r = ExecuteTool(tname, tinput); }
                                catch (Exception ex) { r = "Error: " + ex.Message; }
                            }
                            if (r != null && r.Length > SubagentToolResultCap)
                                r = r.Substring(0, SubagentToolResultCap) + "\n\n[...truncated in subagent...]";
                            lock (toolResultMap) { toolResultMap[cid] = r; }
                        }, ct));
                    }
                    try { Task.WaitAll(tasks.ToArray(), ct); }
                    catch (OperationCanceledException)
                    {
                        var stubs = BuildStubToolResults(toolUses, "[cancelled]");
                        subMessages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", stubs } });
                        throw;
                    }

                    var toolResults = new List<Dictionary<string, object>>();
                    foreach (var tb in toolUses)
                    {
                        var id = GetStringValue(tb, "id");
                        var execResult = toolResultMap.ContainsKey(id) ? toolResultMap[id] : "Error: tool execution lost";
                        trace.Append("   → ").Append(Clip(execResult, 500)).AppendLine();   // 轨迹：结果摘要
                        var entry = new Dictionary<string, object>
                        {
                            { "type", "tool_result" },
                            { "tool_use_id", id },
                            { "content", execResult }
                        };
                        if (IsToolError(execResult)) entry["is_error"] = true;
                        toolResults.Add(entry);
                    }
                    subMessages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", toolResults }
                    });

                    SubagentTrimIfNeeded(subMessages, SubagentTrimCap);
                }

                if (!string.IsNullOrEmpty(lastText))
                {
                    trace.AppendLine();
                    trace.AppendLine("── conclusion ──");
                    trace.AppendLine(Clip(lastText, 1500));
                }
                return string.IsNullOrEmpty(lastText)
                    ? ("[" + agentType + " subagent completed with no textual conclusion]", false)
                    : (lastText, true);
            }
            finally
            {
                _inSubagent = false;
                _showToolStatus = savedShowToolStatus;
                OnResearchEnd?.Invoke();   // 隐藏进度条 banner（含取消 / 异常路径）
                OnResearchTrace?.Invoke(agentType, trace.ToString());   // 折叠显示内部轨迹（取消/异常也回传已有部分）
            }
        }
    }
}
