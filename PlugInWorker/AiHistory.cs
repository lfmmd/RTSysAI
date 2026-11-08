using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- History budget / trimming constants ----

        private const int MaxHistoryKeep = 100;
        private const int MaxRecentKeep = 30;
        private const int MaxToolResultLen = 16000;
        // lookup_command full=true 的文档可能很大（如 DATUM 74K）；安全关键参数表不能被 16K 截断。
        private const int MaxLookupResultLen = 96 * 1024;
        // microCompact：旧 tool_result 的 content 替换为占位符，保留 tool_use_id 不破坏配对
        private const int MaxRecentToolResults = 5;
        private const string ClearedToolResult = "[Old tool result content cleared]";
        // auto-compaction 与 Chat 主循环共用同一个 tokens 预算（AiService.ContextTokenBudget）。
        private const int HistoryTokenBudget = ContextTokenBudget;
        // 条数触发的 token 下限：count > MaxHistoryKeep 但 tokens 低于此值时不压缩。
        // 小历史（如十几K tokens / 百余条短消息）不该触发昂贵的压缩流程；
        // 真正要控的是「条数多 + token 也已显著增长」的历史，避免压缩失败时硬截断丢上下文。
        private const int CountTriggerTokenFloor = 100000;

        private void TrimHistory()
        {
            // CompactHistory 的锁契约要求调用者持有 _historyLock（内部会 Monitor.Exit/Enter）。
            lock (_historyLock)
            {
                TrimHistoryLocked();
            }
        }

        /// <summary>调用者必须持有 _historyLock。</summary>
        private void TrimHistoryLocked()
        {
            int estTokens = EstimateHistoryTokens();
            int count = _conversationHistory.Count;
            // 触发条件：① token 已达 budget（500K）；或 ② 条数 > 100 且 token 也已达 floor（100K）。
            // 仅条数超限但 token 很小（如 14K tokens / 101 条）不触发 —— 不为小巧的历史跑昂贵压缩。
            bool tokenTrigger = estTokens >= HistoryTokenBudget;
            bool countTrigger = count > MaxHistoryKeep && estTokens >= CountTriggerTokenFloor;
            if (!tokenTrigger && !countTrigger)
                return;

            OnSystemMessage?.Invoke(Lang.L($"⚠ 已触发历史裁剪: {count} 条消息, 约 {estTokens} tokens",
                                           $"⚠ TrimHistory triggered: {count} msgs, ~{estTokens} tokens"));

            // auto-compaction：将旧消息摘要压缩为一条，保留最近消息不变。
            if (CompactHistory())
                return;

            // 摘要失败 → 兜底硬截断，告知用户（避免静默丢上下文）
            OnSystemMessage?.Invoke(Lang.L(
                "⚠ 自动摘要失败，已回退到硬截断（最近 30 条消息已保留）",
                "⚠ Auto-summary failed, fell back to hard truncation (last 30 messages kept)"));

            // 摘要失败时兜底：截断旧消息
            // 优先找 user+string 消息（普通用户输入），兜底找任意 user 消息（tool_result），
            // 确保 messages 首条始终是 user 角色。
            int start = _conversationHistory.Count - MaxRecentKeep;
            if (start < 1) start = 1;

            int found = -1;
            int foundAnyUser = -1;
            for (int i = start; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                if (GetStringValue(msg, "role") == "user")
                {
                    // 跳过 user(tool_result)：对应的 assistant(tool_use) 会被一起截掉，
                    // 留下孤立 tool_result 触发 EnsureValidMessageSequence 反复修复。
                    if (IsUserToolResultMessage(msg)) continue;
                    if (foundAnyUser < 0) foundAnyUser = i;
                    if (msg["content"] is string)
                    {
                        found = i;
                        break;
                    }
                }
            }
            // 兜底：如果没找到 user+string，用任意 user 消息（确保不以 assistant 开头）
            if (found < 0) found = foundAnyUser;

            if (found <= 0) return;

            var trimmed = new List<Dictionary<string, object>>(
                _conversationHistory.GetRange(found, _conversationHistory.Count - found));
            _conversationHistory.Clear();
            _conversationHistory.AddRange(trimmed);

            // 硬截断可能产生孤立 tool_result（user 含 tool_result 但对应的 assistant tool_use
            // 已被截掉）— 调 EnsureValidMessageSequence 兜底清理，避免每次 API 请求触发修复刷屏
            EnsureValidMessageSequence(_conversationHistory);
        }

        /// Auto-compaction：调用 AI 将旧消息摘要为一条 user 消息，保留最近 MaxRecentKeep 条。
        /// 成功返回 true（历史已替换为摘要 + 最近消息）。
        /// 调用者必须持 _historyLock。内部在 API 调用前释放锁，调用后重新获取。
        private bool CompactHistory()
        {
            if (_conversationHistory.Count <= MaxRecentKeep + 2) return false;

            int compactEnd = _conversationHistory.Count - MaxRecentKeep;
            if (compactEnd < 2) return false;

            // 快照旧消息用于摘要（在锁内读取）
            var sb = new StringBuilder();
            for (int i = 0; i < compactEnd; i++)
            {
                var msg = _conversationHistory[i];
                var role = GetStringValue(msg, "role") ?? "?";
                object contentVal;
                if (!msg.TryGetValue("content", out contentVal)) continue;
                if (contentVal is string s)
                    sb.AppendFormat("[{0}]: {1}\n", role, s);
                else if (contentVal is List<Dictionary<string, object>> blocks)
                {
                    foreach (var b in blocks)
                    {
                        var c = GetStringValue(b, "content");
                        if (c == null) c = GetStringValue(b, "text");
                        if (c != null)
                        {
                            var type = GetStringValue(b, "type") ?? "";
                            var name = GetStringValue(b, "name") ?? "";
                            if (type == "tool_use")
                                sb.AppendFormat("[assistant/tool_use:{0}]: {1}\n", name, c);
                            else if (type == "tool_result")
                                sb.AppendFormat("[user/tool_result]: {0}\n", Truncate(c, 2000));
                            else
                                sb.AppendFormat("[{0}]: {1}\n", role, c);
                        }
                    }
                }
            }

            // 快照 _recentReadFiles（在锁内）
            var recentFiles = new List<Tuple<string, string>>(_recentReadFiles);

            if (sb.Length < 100) return false;

            var compactPrompt = string.Format(
                "Summarize the following conversation between a user and an AI assistant. " +
                "Preserve: 1) User's original request and intent 2) Key decisions and code changes made " +
                "3) Important error messages and fixes 4) Current state of work (what's done, what's pending). " +
                "Be concise but complete — this summary replaces the original messages.\n\n{0}",
                sb.ToString());

            // 释放锁 → API 调用（可能耗时数秒）
            // 安全性依赖:Chat 已串行化(_chatRunning),释锁期间不会有并发 Chat 修改 _conversationHistory,
            // 故此处的 Exit/Enter 配对不会产生竞态。若未来移除串行队列,需重审此处(改用 snapshot 副本 + 锁外调用)。
            System.Threading.Monitor.Exit(_historyLock);
            OnSystemMessage?.Invoke(Lang.L("正在自动压缩对话历史...",
                                           "Auto-compacting conversation history..."));
            string summary;
            try
            {
                summary = CallCompactApi(compactPrompt);
            }
            finally
            {
                System.Threading.Monitor.Enter(_historyLock);
            }
            if (string.IsNullOrEmpty(summary)) return false;

            try
            {
                // API 调用期间历史可能已变，重新计算 compactEnd
                compactEnd = _conversationHistory.Count - MaxRecentKeep;
                if (compactEnd < 2) return false;

                // 切点净化：若保留段以 user(tool_result) 开头，它引用的 assistant(tool_use)
                // 已被压进摘要，留下孤立 tool_result 会让 EnsureValidMessageSequence 每次
                // 请求都触发修复并刷屏。把这条 user(tool_result) 一起压进摘要，直到切点
                // 不再是孤立 tool_result。保留至少 1 条原始消息防止退化为纯摘要。
                while (compactEnd < _conversationHistory.Count - 1
                       && IsUserToolResultMessage(_conversationHistory[compactEnd]))
                {
                    compactEnd++;
                }

                var newHistory = new List<Dictionary<string, object>>();
                newHistory.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", "[Conversation summary — older messages compacted]\n\n" + summary }
                });
                newHistory.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", "Understood. I have the context from the summary above and will continue from where we left off." }
                });

                // 压缩后恢复最近读过的文件上下文
                if (recentFiles.Count > 0)
                {
                    var fileSb = new StringBuilder("[Recently read source files — restored after compaction]\n");
                    foreach (var f in recentFiles)
                        fileSb.AppendFormat("\n### {0}\n```\n{1}\n```\n", f.Item1, f.Item2);
                    newHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", fileSb.ToString() }
                    });
                    newHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "assistant" },
                        { "content", "Understood. I have the restored file contexts above." }
                    });
                }

                for (int i = compactEnd; i < _conversationHistory.Count; i++)
                    newHistory.Add(_conversationHistory[i]);

                _conversationHistory.Clear();
                _conversationHistory.AddRange(newHistory);

                OnSystemMessage?.Invoke(Lang.L($"已压缩为摘要。新历史: {_conversationHistory.Count} 条消息。",
                                               $"Compacted into summary. New history: {_conversationHistory.Count} msgs."));
                return true;
            }
            catch (Exception ex)
            {
                LogException("CompactHistory", ex);
                return false;
            }
        }

        private string CallCompactApi(string prompt)
        {
            // 复用 system blocks 以命中缓存，compact 调用也省 system prompt tokens
            var systemBlocks = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", BuildStablePrompt() },
                    { "cache_control", new { type = "ephemeral" } }
                }
            };
            if (_memoryEnabled)
            {
                var memText = LoadMemory();
                systemBlocks.Add(new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", string.IsNullOrEmpty(memText) ? "## Persistent Memory\n\n(No memory stored yet)" : "## Persistent Memory\n\n" + memText },
                    { "cache_control", new { type = "ephemeral" } }
                });
            }
            // compact 只是摘要，给 thinking 小 budget；max_tokens 须 > budget_tokens。
            const int compactThinkingBudget = 2048;
            const int compactMaxTokens = 6144;

            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "max_tokens", compactMaxTokens },
                { "system", systemBlocks },
                { "messages", new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", prompt }
                        }
                    }
                },
                { "stream", true }
            };

            // 与主对话一致：开 thinking 的模型须带 thinking 配置，否则 GLM 兼容端点会拒绝(400)或
            // 把 thinking 当首个 block 返回导致 text 取空。走流式 + 只累积 text_delta 跳过 thinking。
            if (_enableThinking)
            {
                body["thinking"] = new Dictionary<string, object>
                {
                    { "type", "enabled" },
                    { "budget_tokens", compactThinkingBudget }
                };
            }

            var json = SerializeRequest(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, NormalizeApiUrl(_apiUrl));
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Content = content;

            System.Net.Http.HttpResponseMessage response = null;
            try
            {
                response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogException("CompactHistory HTTP send", ex);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // 记录状态码 + 错误体，便于诊断 compact 失败（之前静默 return null 无法排查）。
                var errText = "<read failed>";
                try { errText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
                var code = (int)response.StatusCode;
                response.Dispose();
                LogException("CompactHistory HTTP", new Exception($"HTTP {code}: {errText}"));
                return null;
            }

            // 流式读取 SSE，累积 text_delta（跳过 thinking_delta / signature_delta）。
            var sb = new StringBuilder();
            try
            {
                using (response)
                using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var reader = new System.IO.StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!line.StartsWith("data:")) continue;
                        var data = line.Substring(5).TrimStart();
                        if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;
                        Dictionary<string, object> evt;
                        try { evt = _json.Deserialize<Dictionary<string, object>>(data); }
                        catch { continue; }
                        if (evt == null) continue;
                        if (GetStringValue(evt, "type") != "content_block_delta") continue;
                        var delta = GetDictValue(evt, "delta");
                        if (delta == null) continue;
                        if (GetStringValue(delta, "type") != "text_delta") continue;
                        var t = GetStringValue(delta, "text");
                        if (!string.IsNullOrEmpty(t)) sb.Append(t);
                    }
                }
            }
            catch (Exception ex)
            {
                LogException("CompactHistory stream read", ex);
                return sb.Length > 0 ? sb.ToString() : null;
            }

            return sb.ToString();
        }

        // 粗略估算历史 token：chars/4。仅用于裁剪触发判断，不要求精确。
        // 调用者必须持 _historyLock。
        private int EstimateHistoryTokens()
        {
            int chars = 0;
            foreach (var m in _conversationHistory)
            {
                if (!m.ContainsKey("content")) continue;
                var content = m["content"];
                if (content is string s)
                {
                    chars += s.Length;
                }
                else if (content is List<Dictionary<string, object>> blocks)
                {
                    foreach (var b in blocks)
                    {
                        var c = GetStringValue(b, "content");
                        if (c != null) chars += c.Length;
                        var t = GetStringValue(b, "text");
                        if (t != null) chars += t.Length;
                        var th = GetStringValue(b, "thinking");
                        if (th != null) chars += th.Length;
                    }
                }
            }
            return chars / 4;
        }

        private List<Dictionary<string, object>> BuildTrimmedMessages()
        {
            lock (_historyLock)
            {
            // 持久化修复：对 _conversationHistory 本身做一次 in-place 修复。
            // 之前只在请求副本 messages 上修，但没写回历史 → 历史中的坏数据（孤立 tool_result、
            // 跨消息重复 tool_use、首条非 user 等）会让每次请求都重复触发提示。
            // in-place 修复后历史变干净，下次 repaired=false，提示只出现一次。
            // 触发来源：旧版本遗留、LoadSession 加载的老 session 文件、取消时塞的 sentinel 等。
            EnsureValidMessageSequence(_conversationHistory);

            var messages = new List<Dictionary<string, object>>(_conversationHistory.Count);

            // 先建立 tool_use_id → tool_name 映射（来自 assistant 消息的 tool_use block）
            // 同时为 lookup_command 建立 tool_use_id → query 映射，用于去重。
            var toolNameById = new Dictionary<string, string>(StringComparer.Ordinal);
            var lookupQueryById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") == "tool_use")
                    {
                        var id = GetStringValue(b, "id");
                        var name = GetStringValue(b, "name");
                        if (id != null && name != null) toolNameById[id] = name;

                        if (string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase)
                            && id != null
                            && b.TryGetValue("input", out var inObj)
                            && inObj is Dictionary<string, object> inDict)
                        {
                            object q;
                            if (inDict.TryGetValue("query", out q) && q != null)
                                lookupQueryById[id] = q.ToString();
                        }
                    }
                }
            }

            // lookup_command 去重：同一 query+full+library 组合第一次出现保留完整内容，后续替换为引用占位符。
            // 只按 query 去重会导致：先查 brief → 后查 full 被误判为重复 → 完整 HTML 丢失。
            // 不区分 library 会导致：查 triobasic 的 MOVE → 再查 iec 的 MOVE 被误判为重复。
            var lookupMetaById = new Dictionary<string, Tuple<bool, string>>(StringComparer.Ordinal);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") != "tool_use") continue;
                    if (!string.Equals(GetStringValue(b, "name"), "lookup_command", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = GetStringValue(b, "id");
                    if (id == null) continue;
                    if (!(b.TryGetValue("input", out var inObj) && inObj is Dictionary<string, object> inDict)) continue;
                    var fullVal = GetStr(inDict, "full");
                    var isFull = string.Equals(fullVal, "true", StringComparison.OrdinalIgnoreCase);
                    var lib = GetStr(inDict, "library") ?? "";
                    lookupMetaById[id] = Tuple.Create(isFull, lib);
                }
            }

            var seenLookupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateLookupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "user") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") != "tool_result") continue;
                    var id = GetStringValue(b, "tool_use_id");
                    if (id == null) continue;
                    if (!lookupQueryById.TryGetValue(id, out var query)) continue;
                    Tuple<bool, string> meta;
                    if (!lookupMetaById.TryGetValue(id, out meta)) continue;
                    var key = query + "\t" + (meta.Item1 ? "full" : "brief") + "\t" + meta.Item2;
                    if (!seenLookupKeys.Add(key))
                        duplicateLookupIds.Add(id);
                }
            }

            // microCompact：扫描全部 user 消息里的 tool_result，按出现顺序收集 tool_use_id。
            // 保留最后 N 个完整内容，更早的 content 清空（保留 tool_use_id 防止 tool_use/tool_result 配对断裂）。
            // 工具结果（HTML 帮助页、文件内容）是请求 token 的大头，旧的清空能省 30%+。
            var allToolResultIds = new List<string>();
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "user") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") == "tool_result")
                    {
                        var id = GetStringValue(b, "tool_use_id");
                        if (id != null) allToolResultIds.Add(id);
                    }
                }
            }
            // 最近 N 个 ∪ lookup_command / read_skill 的所有结果（参考类内容，AI 写代码时会反复引用，
            // 清空会导致它再查/再读一次浪费 API + 中断代码连贯性）
            var keepRecent = new HashSet<string>(
                allToolResultIds.Skip(Math.Max(0, allToolResultIds.Count - MaxRecentToolResults)));
            foreach (var id in allToolResultIds)
            {
                if (toolNameById.TryGetValue(id, out var name)
                    && (string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "read_skill", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "read_source", StringComparison.OrdinalIgnoreCase)))
                {
                    keepRecent.Add(id);
                }
            }

            // read_source 历史去重：同一文件 + 同一参数范围只保留最新读取结果
            var readSourceById = new Dictionary<string, string>(StringComparer.Ordinal);
            var readSourceKeyById = new Dictionary<string, string>(StringComparer.Ordinal);
            var latestReadSourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") != "tool_use") continue;
                    if (!string.Equals(GetStringValue(b, "name"), "read_source", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = GetStringValue(b, "id");
                    if (id == null) continue;
                    if (!(b.TryGetValue("input", out var inObj) && inObj is Dictionary<string, object> inDict)) continue;
                    var progName = GetStr(inDict, "name");
                    if (progName == null) continue;
                    var startLine = GetStr(inDict, "startLine") ?? "";
                    var endLine = GetStr(inDict, "endLine") ?? "";
                    var key = progName.ToLowerInvariant() + "|" + startLine + "|" + endLine;
                    readSourceById[id] = progName;
                    readSourceKeyById[id] = key;
                    latestReadSourceId[key] = id; // last wins → latest
                }
            }
            var dupReadSourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in readSourceKeyById)
            {
                string latestId;
                if (latestReadSourceId.TryGetValue(kv.Value, out latestId) && latestId != kv.Key)
                    dupReadSourceIds.Add(kv.Key);
            }

            // 预计算 assistant 消息数量，用于判断哪些是"旧的"（清理 thinking、压缩 tool_use input）
            int totalAssistantMsgs = 0;
            foreach (var m in _conversationHistory)
                if (GetStringValue(m, "role") == "assistant") totalAssistantMsgs++;
            int assistantSeen = 0;
            // 只保留最近 1 条 assistant 的 thinking（= 当前活跃 turn 的思考链头），更早的 thinking 不进请求。
            // 对应 GLM clear_thinking=true 语义：不回传历史 reasoning_content，否则历史思考越积越多 →
            // 模型在旧推理上打转 →「思考越来越长停不下来」。Anthropic/DeepSeek 同样接受删除历史 thinking
            // （只禁止改 thinking content，删除整块合法）。之前 =3 会累积 3 轮思考，是循环思考根因。
            // 统一三家、不按端点区分（符合 thinking-unified 约定）。
            const int KeepRecentThinking = 1;

            for (int idx = 0; idx < _conversationHistory.Count; idx++)
            {
                var msg = _conversationHistory[idx];
                var copy = new Dictionary<string, object>(msg);
                var content = copy["content"];

                // user 消息：tool_result 去重 / microCompact / 截断
                if (copy["role"] as string == "user" && content is List<Dictionary<string, object>> blocks)
                {
                    var trimmedBlocks = new List<Dictionary<string, object>>();
                    foreach (var block in blocks)
                    {
                        if (GetStringValue(block, "type") == "tool_result")
                        {
                            var tb = new Dictionary<string, object>(block);
                            var id = GetStringValue(tb, "tool_use_id");
                            if (id != null && duplicateLookupIds.Contains(id))
                            {
                                var q = lookupQueryById[id];
                                tb["content"] = "[Duplicate of lookup_command(\"" + q
                                    + "\") — full content preserved at the first call earlier in this conversation. "
                                    + "Reference that occurrence instead of asking again.]";
                            }
                            else if (id != null && dupReadSourceIds.Contains(id))
                            {
                                var pn = readSourceById.ContainsKey(id) ? readSourceById[id] : "?";
                                tb["content"] = "[Earlier read_source(\"" + pn
                                    + "\") — latest read preserved later in this conversation]";
                            }
                            else if (id != null && !keepRecent.Contains(id))
                            {
                                tb["content"] = ClearedToolResult;
                            }
                            else
                            {
                                var c = GetStringValue(tb, "content");
                                int limit = (id != null && lookupQueryById.ContainsKey(id)) ? MaxLookupResultLen : MaxToolResultLen;
                                if (c != null && c.Length > limit)
                                    tb["content"] = SmartTruncate(c, limit);
                            }
                            trimmedBlocks.Add(tb);
                        }
                        else
                        {
                            trimmedBlocks.Add(block);
                        }
                    }
                    copy["content"] = trimmedBlocks;
                }

                // assistant: 清理旧 thinking、压缩已清空 tool_use input（cache_control 不在此处逐条打）
                if (GetStringValue(copy, "role") == "assistant" && content is List<Dictionary<string, object>> asstBlocks && asstBlocks.Count > 0)
                {
                    assistantSeen++;
                    bool isOld = totalAssistantMsgs > KeepRecentThinking
                        && assistantSeen <= totalAssistantMsgs - KeepRecentThinking;
                    var newBlocks = new List<Dictionary<string, object>>();
                    foreach (var b in asstBlocks)
                    {
                        var bType = GetStringValue(b, "type") ?? "";
                        // 旧 thinking block 完全移除
                        if (bType == "thinking" && isOld)
                            continue;
                        // 已清空结果的 tool_use → 压缩 input
                        if (bType == "tool_use" && isOld)
                        {
                            var toolId = GetStringValue(b, "id");
                            if (toolId != null && !keepRecent.Contains(toolId))
                            {
                                var compressed = new Dictionary<string, object>(b);
                                compressed["input"] = new Dictionary<string, object> { { "_summarized", true } };
                                newBlocks.Add(compressed);
                                continue;
                            }
                        }
                        newBlocks.Add(b);
                    }
                    if (newBlocks.Count == 0)
                    {
                        newBlocks.Add(new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", "[assistant message compacted]" }
                        });
                    }
                    copy["content"] = newBlocks;
                }

                    messages.Add(copy);
                }

                // 防御：确保 messages 满足 API 约束：
                //   1. 首条必须是 user 角色
                //   2. tool_result 必须紧跟在 assistant 的 tool_use 后面
                //   3. messages 不能为空数组
                // TrimHistory/CompactHistory 截断可能破坏这些约束。
                // 参考 claudecodefx 的 ensureToolResultPairing 做法进行修复。
                EnsureValidMessageSequence(messages);

                // 砍掉最后一条 assistant 末尾的 thinking 块（对齐 cc-haha filterTrailingThinkingFromLastAssistant）。
                // Anthropic 规范：assistant 消息不能以 thinking 结尾（API 400）。正常 [thinking,text]/[thinking,tool_use]
                // 的 thinking 在头部不受影响；仅清理「光想不说」的 [thinking] 或异常尾部 thinking，顺带减少无主 thinking 被回传。
                FilterTrailingThinkingFromLastAssistant(messages);

                // cache breakpoint 策略：Anthropic 每请求最多 4 个 breakpoint（system + tools 已占 2-3）。
                // messages 只给【最后一条 assistant】的末尾 block 打 1 个 breakpoint —— 缓存整个历史 prefix，
                // 下次新增消息后该 prefix 仍稳定 → 命中。旧版给每条 assistant 都打，长会话飙到几十个，
                // 超 Anthropic 4 上限的多余 breakpoint 被服务端忽略，反而命中率低。
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    if (GetStringValue(messages[i], "role") != "assistant") continue;
                    if (!(messages[i].TryGetValue("content", out var cObj)
                          && cObj is List<Dictionary<string, object>> aBlocks && aBlocks.Count > 0)) continue;
                    var ccBlock = new Dictionary<string, object>(aBlocks[aBlocks.Count - 1])
                    {
                        { "cache_control", new { type = "ephemeral" } }
                    };
                    aBlocks[aBlocks.Count - 1] = ccBlock;
                    break;
                }

                return messages;
            }
        }
        /// <summary>
        /// 砍掉 messages 中【最后一条 assistant 消息】末尾连续的 thinking/redacted_thinking 块。
        /// Anthropic 规范：assistant 消息不能以 thinking 块结尾（API 返回 400）。
        /// 适配自 claudecodefx filterTrailingThinkingFromLastAssistant（messages.ts:4897）：cc-haha 处理
        /// messages 数组末尾恰好是 assistant 的情况；ai 请求末尾恒为 user，故改为定位「最后一条
        /// role==assistant」。正常 [thinking,text]/[thinking,tool_use] 的 thinking 在头部，不受影响；
        /// 仅清理「光想不说」的纯 [thinking] 或异常尾部 thinking。砍光则插占位符（API 要求 assistant 非空）。
        /// </summary>
        private static void FilterTrailingThinkingFromLastAssistant(List<Dictionary<string, object>> messages)
        {
            // 从末尾往前找最后一条 assistant
            int lastAsstIdx = -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (GetStringValue(messages[i], "role") == "assistant") { lastAsstIdx = i; break; }
            }
            if (lastAsstIdx < 0) return;
            if (!(messages[lastAsstIdx].TryGetValue("content", out var cObj)
                  && cObj is List<Dictionary<string, object>> blocks) || blocks.Count == 0) return;

            // 末尾 block 不是 thinking/redacted_thinking → 无需处理
            var lastType = GetStringValue(blocks[blocks.Count - 1], "type") ?? "";
            if (lastType != "thinking" && lastType != "redacted_thinking") return;

            // 从末尾往前砍连续的 thinking 块
            int lastValidIdx = blocks.Count - 1;
            while (lastValidIdx >= 0)
            {
                var bt = GetStringValue(blocks[lastValidIdx], "type") ?? "";
                if (bt != "thinking" && bt != "redacted_thinking") break;
                lastValidIdx--;
            }

            var newBlocks = new List<Dictionary<string, object>>();
            if (lastValidIdx < 0)
            {
                // 全是 thinking → 插占位符（API 要求 assistant 非空且不能以 thinking 结尾）
                newBlocks.Add(new Dictionary<string, object> { { "type", "text" }, { "text", "[thinking-only message, content stripped]" } });
            }
            else
            {
                for (int i = 0; i <= lastValidIdx; i++) newBlocks.Add(blocks[i]);
            }
            messages[lastAsstIdx]["content"] = newBlocks;
        }

        /// <summary>
        /// 判断消息是否为含 tool_result block 的 user 消息。
        /// 用于 CompactHistory/TrimHistory 切点净化：这类消息若被切到保留段开头，
        /// 它引用的 assistant(tool_use) 已丢失，会成为孤立 tool_result。
        /// </summary>
        private static bool IsUserToolResultMessage(Dictionary<string, object> msg)
        {
            if (GetStringValue(msg, "role") != "user") return false;
            if (!(msg.TryGetValue("content", out var content) && content is List<Dictionary<string, object>> blocks)) return false;
            foreach (var b in blocks)
            {
                if (GetStringValue(b, "type") == "tool_result") return true;
            }
            return false;
        }

        /// <summary>
        /// 确保 messages 数组满足 API 约束：首条必须是 user，tool_result 必须紧跟 assistant 的 tool_use，
        /// 不能为空数组。参考 claudecodefx 的 ensureToolResultPairing 逻辑。
        /// </summary>
        private void EnsureValidMessageSequence(List<Dictionary<string, object>> messages)
        {
            if (messages.Count == 0) return;

            // 跨消息 tool_use ID 累积去重（防止两条 assistant 消息有相同 id 的 tool_use）
            var globalToolUseIds = new HashSet<string>(StringComparer.Ordinal);
            var repaired = false;

            // 第一遍：处理 assistant 消息 — 去重 tool_use ID，并记录有效的 tool_use ID
            var validToolUseIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> blocks)) continue;

                var filtered = new List<Dictionary<string, object>>();
                foreach (var b in blocks)
                {
                    if (GetStringValue(b, "type") == "tool_use")
                    {
                        var id = GetStringValue(b, "id");
                        if (id != null && globalToolUseIds.Contains(id))
                        {
                            // 跨消息重复的 tool_use ID → 丢弃
                            repaired = true;
                            continue;
                        }
                        if (id != null)
                        {
                            globalToolUseIds.Add(id);
                            validToolUseIds.Add(id);
                        }
                    }
                    filtered.Add(b);
                }

                if (filtered.Count != blocks.Count)
                {
                    // assistant 内容被清空了 → 插入占位文本
                    if (filtered.Count == 0)
                    {
                        filtered.Add(new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", "[Duplicate tool use removed]" }
                        });
                    }
                    m["content"] = filtered;
                }
            }

            // 第二遍：修复 user 消息 — 移除孤立和重复的 tool_result
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                var role = GetStringValue(m, "role");

                if (role == "user" && m["content"] is List<Dictionary<string, object>> blocks)
                {
                    // 检查是否有孤立的 tool_result（对应 tool_use 不在前面的 assistant 消息中）
                    var validBlocks = new List<Dictionary<string, object>>();
                    var seenResultIds = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var b in blocks)
                    {
                        if (GetStringValue(b, "type") == "tool_result")
                        {
                            var trId = GetStringValue(b, "tool_use_id");
                            // 去重：同一个 tool_use_id 只保留第一个
                            if (trId != null && seenResultIds.Contains(trId))
                            {
                                repaired = true;
                                continue;
                            }
                            if (trId != null) seenResultIds.Add(trId);

                            // 检查有没有对应的有效 tool_use
                            if (trId != null && validToolUseIds.Contains(trId))
                            {
                                validBlocks.Add(b);
                            }
                            else
                            {
                                repaired = true;
                            }
                        }
                        else
                        {
                            validBlocks.Add(b);
                        }
                    }

                    if (validBlocks.Count != blocks.Count)
                    {
                        if (validBlocks.Count == 0)
                        {
                            // 移除后没有内容了 → 替换为占位文本
                            m["content"] = "[Orphaned tool result removed due to conversation history trimming]";
                            repaired = true;
                        }
                        else
                        {
                            m["content"] = validBlocks;
                        }
                    }
                }
            }

            // 第二遍半：去重连续相同的 user 纯文本消息（自循环 / 旧脏数据 / restore 累积的产物）。
            // 仅处理 content is string 的 user 消息（不误伤 tool_result 的 user，其 content 是 List）。
            // "相邻"指物理位置相邻 —— 重复 user 之间若有 assistant 则不触发，故不误伤用户合理的真重发。
            {
                int j = 0;
                while (j < messages.Count - 1)
                {
                    var cur = messages[j];
                    var nxt = messages[j + 1];
                    if (GetStringValue(cur, "role") != "user" || GetStringValue(nxt, "role") != "user") { j++; continue; }
                    if (!(cur.TryGetValue("content", out var c1) && c1 is string s1) ||
                        !(nxt.TryGetValue("content", out var c2) && c2 is string s2)) { j++; continue; }
                    if (string.Equals(s1, s2, StringComparison.Ordinal))
                    {
                        messages.RemoveAt(j + 1);
                        repaired = true;
                        continue; // 不 j++：新的 messages[j+1] 可能仍与 cur 相同，一次性收敛三连/多连
                    }
                    j++;
                }
            }

            // 第三遍：处理 assistant 消息中有 tool_use 但后面没有对应 tool_result 的情况
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> blocks)) continue;

                var toolUseIds = new List<string>();
                foreach (var b in blocks)
                {
                    if (GetStringValue(b, "type") == "tool_use")
                    {
                        var id = GetStringValue(b, "id");
                        if (id != null) toolUseIds.Add(id);
                    }
                }
                if (toolUseIds.Count == 0) continue;

                // 找下一条 user 消息中的 tool_result id
                var resultIds = new HashSet<string>(StringComparer.Ordinal);
                if (i + 1 < messages.Count && GetStringValue(messages[i + 1], "role") == "user")
                {
                    var next = messages[i + 1];
                    if (next["content"] is List<Dictionary<string, object>> nextBlocks)
                    {
                        foreach (var b in nextBlocks)
                        {
                            if (GetStringValue(b, "type") == "tool_result")
                            {
                                var id = GetStringValue(b, "tool_use_id");
                                if (id != null) resultIds.Add(id);
                            }
                        }
                    }
                }

                // 找缺失的 tool_use id（有 tool_use 没有对应 tool_result）
                var missingIds = toolUseIds.Where(id => !resultIds.Contains(id)).ToList();
                if (missingIds.Count > 0)
                {
                    repaired = true;
                    // 插入合成的 tool_result
                    var synthBlocks = missingIds.Select(id => new Dictionary<string, object>
                    {
                        { "type", "tool_result" },
                        { "tool_use_id", id },
                        { "content", "[Tool result missing due to history trimming]" },
                        { "is_error", true }
                    }).ToList();

                    if (i + 1 < messages.Count && GetStringValue(messages[i + 1], "role") == "user")
                    {
                        // 下一条是 user 消息，在前面追加合成的 tool_result
                        var next = messages[i + 1];
                        if (next["content"] is List<Dictionary<string, object>> nextBlocks)
                        {
                            synthBlocks.AddRange(nextBlocks);
                            next["content"] = synthBlocks;
                        }
                        else
                        {
                            synthBlocks.Add(new Dictionary<string, object>
                            {
                                { "type", "text" },
                                { "text", next["content"]?.ToString() ?? "" }
                            });
                            next["content"] = synthBlocks;
                        }
                    }
                    else
                    {
                        // 需要插入一条新的 user 消息
                        messages.Insert(i + 1, new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", synthBlocks.Cast<Dictionary<string, object>>().ToList<Dictionary<string, object>>() }
                        });
                        i++; // 跳过刚插入的消息
                    }
                }
            }

            // 第四遍：确保首条是合法的 user 消息
            if (messages.Count > 0)
            {
                int firstValidUser = -1;
                for (int i = 0; i < messages.Count; i++)
                {
                    if (GetStringValue(messages[i], "role") == "user")
                    {
                        firstValidUser = i;
                        break;
                    }
                }

                if (firstValidUser > 0)
                {
                    OnSystemMessage?.Invoke(Lang.L($"⚠ 历史修复: 跳过开头 {firstValidUser} 条非 user 消息",
                                                   $"⚠ History repair: skipped {firstValidUser} leading non-user messages"));
                    messages.RemoveRange(0, firstValidUser);
                    repaired = true;
                }
                else if (firstValidUser < 0)
                {
                    // 全部是 assistant 消息 → 替换为一条占位 user 消息
                    OnSystemMessage?.Invoke(Lang.L("⚠ 历史修复: 未找到 user 消息，已插入占位消息",
                                                   "⚠ History repair: no user messages found, inserting placeholder"));
                    messages.Clear();
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", "[Conversation history was trimmed. Please continue helping the user.]" }
                    });
                    repaired = true;
                }
            }

            // 最终兜底：空数组 → 插入占位
            if (messages.Count == 0)
            {
                messages.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", "[Conversation history was trimmed. Please continue helping the user.]" }
                });
                repaired = true;
            }

            // thinking 块照原样回传（Anthropic 规范：pass back as you received it），不按 signature 有无清理。
            // 真 Anthropic 完成块自带 signature，GLM/DeepSeek 完成块结构性无 signature，照原样回传三家都正确。
            // 唯一的毒块（流中断的 partial thinking，无 content_block_stop、无 signature）已在 AiService.cs
            // flush 路径源头拦下，不进历史，故此处无需任何 signature 清理。

            if (repaired)
                OnSystemMessage?.Invoke(Lang.L("⚠ 历史修复: 消息序列已被修复",
                                               "⚠ History repair: message sequence was repaired"));
        }

        /// <summary>
        /// 将参数值归一化为小写字符串，解决 JavaScriptSerializer 对 bool/string/缺失的类型不一致问题。
        /// bool true → "true", string "True" → "true", 缺失 key → ""
        /// </summary>
        private static string NormParam(Dictionary<string, object> d, string key)
        {
            object val;
            if (!d.TryGetValue(key, out val) || val == null) return "";
            if (val is bool b) return b ? "true" : "false";
            return val.ToString().Trim().ToLowerInvariant();
        }

        /// <summary>
        /// P0: lookup_command 同会话去重。扫描 _conversationHistory，如果同一 query+library+full
        /// 组合已经成功执行过，返回紧凑引用而非重新加载完整 HTML。
        /// CompactHistory 之后旧 tool_use/tool_result 对被替换为摘要文本，扫描不会命中 → 自然回退到正常执行。
        /// </summary>
        private object TryDedupLookupCommand(Dictionary<string, object> input)
        {
            lock (_historyLock)
            {
            // 确保去重判断与工具实际行为一致。
            // 例如：full=true(bool) → GetStr="True" → 工具视为非 full → 去重也应视为非 full。
            var nQuery = GetStr(input, "query");
            var nFull = GetStr(input, "full");       // null / "true" / "True" / "false" ...
            var nLibrary = GetStr(input, "library");  // null / "iec" / "triobasic" ...
            if (string.IsNullOrEmpty(nQuery)) return null;

            for (int i = 0; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                if (GetStringValue(msg, "role") != "assistant") continue;
                if (!(msg["content"] is List<Dictionary<string, object>> blocks)) continue;

                foreach (var b in blocks)
                {
                    if (GetStringValue(b, "type") != "tool_use") continue;
                    if (!string.Equals(GetStringValue(b, "name"), "lookup_command",
                        StringComparison.OrdinalIgnoreCase)) continue;

                    object rawInput;
                    if (!b.TryGetValue("input", out rawInput)) continue;
                    var bInput = rawInput as Dictionary<string, object>;
                    if (bInput == null) continue;

                    var hQuery = GetStr(bInput, "query");
                    var hFull = GetStr(bInput, "full");
                    var hLibrary = GetStr(bInput, "library");

                    // query: 大小写不敏感匹配
                    if (!string.Equals(nQuery, hQuery, StringComparison.OrdinalIgnoreCase)) continue;
                    // full: full=true 可以替代不带 full 的查询（full 包含 brief 的全部信息）
                    // 允许命中：① 精确匹配 ② 当前非 full 但历史是 full（full→brief 方向去重）
                    // 不允许：当前是 full 但历史不是 full（brief 不能替代 full）
                    var hIsFull = string.Equals(hFull, "true", StringComparison.OrdinalIgnoreCase);
                    var nIsFull = string.Equals(nFull, "true", StringComparison.OrdinalIgnoreCase);
                    if (nIsFull && !hIsFull) continue;
                    // library: 大小写不敏感匹配
                    if (!string.Equals(nLibrary, hLibrary, StringComparison.OrdinalIgnoreCase)) continue;

                    // 找到相同参数的历史调用，检查其结果是否成功
                    var toolId = GetStringValue(b, "id");
                    if (toolId == null) continue;

                    // tool_result 紧跟在 tool_use 后面的 user 消息中（最多向下找 2 条）
                    for (int j = i + 1; j < _conversationHistory.Count && j <= i + 2; j++)
                    {
                        var rMsg = _conversationHistory[j];
                        if (GetStringValue(rMsg, "role") != "user") continue;
                        if (!(rMsg["content"] is List<Dictionary<string, object>> rBlocks)) continue;

                        foreach (var rb in rBlocks)
                        {
                            if (GetStringValue(rb, "type") != "tool_result") continue;
                            if (GetStringValue(rb, "tool_use_id") != toolId) continue;

                            var content = GetStringValue(rb, "content");
                            if (content != null && !content.Contains("\"error\":"))
                            {
                                return new
                                {
                                    results = new[]
                                    {
                                        new
                                        {
                                            name = nQuery,
                                            note = $"Already looked up '{nQuery}' (full={nFull}, library={nLibrary}) " +
                                                   "earlier in this conversation. The full result is preserved in the " +
                                                   "conversation history — reference that earlier tool_result instead " +
                                                   "of calling again."
                                        }
                                    }
                                };
                            }
                        }
                    }
                }
            }

            return null;
            }
        }

        // 智能截断：优先在 HTML heading / paragraph / table 边界处切，避免把语法表或参数说明截成半句。
        // 比纯字符 cut 慢一点但只对超长 tool_result 触发（lookup_command 帮助页为主）。
        private static readonly string[] _truncateBoundaries =
            { "</h2>", "</h3>", "</h4>", "</h5>", "</table>", "</ul>", "</ol>", "</p>" };

        private static string SmartTruncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            int searchStart = (int)(maxLen * 0.7);
            int best = -1;
            foreach (var b in _truncateBoundaries)
            {
                int idx = s.LastIndexOf(b, maxLen, StringComparison.OrdinalIgnoreCase);
                if (idx > searchStart && idx + b.Length > best)
                    best = idx + b.Length;
            }
            int cut = best > 0 ? best : maxLen;
            return s.Substring(0, cut) + "\n...[truncated " + cut + "/" + s.Length + " chars]";
        }
    }
}
