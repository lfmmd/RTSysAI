using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- Session-related instance fields ----

        private readonly List<Dictionary<string, object>> _conversationHistory = new List<Dictionary<string, object>>();
        private readonly object _historyLock = new object();
        private string _currentSessionId;
        private const int MaxRestoredFiles = 20;
        private const int MaxRestoredFileChars = 6000;
        private readonly List<Tuple<string, string>> _recentReadFiles = new List<Tuple<string, string>>();

        // 会话级编辑类许可：本对话内首次确认一个编辑/编译类工具（AutoAllowWriteTools）后置 true，
        // 后续同类工具直接放行（不逐次确认）；新对话/切换会话/清空时重置回 false。
        // 运行/上下传/变量写入类（run_program/upload/write_vr 等）不受此影响，仍每次确认。
        private bool _sessionEditApproved = false;

        // ---- Task / Todo 系统（AI 自跟踪多步任务）----
        private readonly List<TaskItem> _tasks = new List<TaskItem>();
        private readonly object _tasksLock = new object();
        private int _nextTaskId = 1;

        // ---- Plan Mode（先调研后动手）----
        // true 时所有 WriteTools 工具调用被拒绝；AI 必须用 read-only 工具完成调研，
        // 然后调 exit_plan_mode 让用户审批计划，审批后才能动手修改。
        private bool _planMode = false;
        public bool IsPlanMode => _planMode;
        public Action<bool> OnPlanModeChanged;

        internal class TaskItem
        {
            public int Id;
            public string Subject;
            public string Description;
            public string Status; // pending | in_progress | completed
            public DateTime CreatedAt;
            public DateTime? UpdatedAt;
        }

        /// <summary>
        /// 让 AI 跟踪多步任务的进度。每个任务有 id / subject / status。
        /// 任务不进入 conversation history（不污染上下文），但每轮由 BuildDynamicContext
        /// 注入 system prompt，让 AI 不依赖被压缩的 history 链也能看到当前进度——
        /// 直接防止失忆后重复建任务 / 重新规划（自循环根因，见 chat_history 日志）。
        /// 返回强类型 Dictionary 以便格式化时按名取字段（原匿名对象无法强类型访问）。
        /// </summary>
        internal List<Dictionary<string, object>> SnapshotTasks()
        {
            lock (_tasksLock)
            {
                var result = new List<Dictionary<string, object>>();
                foreach (var t in _tasks)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        { "id", t.Id },
                        { "subject", t.Subject },
                        { "description", t.Description },
                        { "status", t.Status },
                        { "created_at", t.CreatedAt.ToString("HH:mm:ss") },
                        { "updated_at", t.UpdatedAt.HasValue ? (object)t.UpdatedAt.Value.ToString("HH:mm:ss") : null }
                    });
                }
                return result;
            }
        }

        public int HistoryMessageCount { get { lock (_historyLock) { return _conversationHistory.Count; } } }
        public int HistoryTokenEstimate { get { lock (_historyLock) { return EstimateHistoryTokens(); } } }

        // ---- Session / History ----

        public string CurrentSessionId => _currentSessionId;

        public string StartNewSession()
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
                _recentReadFiles.Clear();
                _sessionEditApproved = false;
                _totalInputTokens = _totalOutputTokens = _totalCacheReadTokens = _totalCacheCreateTokens = 0;
                _currentMaxTokens = DefaultMaxTokens;
                _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            }
            // 新会话必须退出 Plan Mode：上一会话的 _planMode 残留会导致 write 工具仍被拒、
            // UI 横幅（OnPlanModeChanged 回调）不消失。OnPlanModeChanged 是 UI 回调，在 lock 外 invoke。
            if (_planMode)
            {
                _planMode = false;
                OnPlanModeChanged?.Invoke(false);
            }
            return _currentSessionId;
        }

        internal void RecordFileRead(string name, string content)
        {
            var summary = content.Length > MaxRestoredFileChars
                ? content.Substring(0, MaxRestoredFileChars) + "\n...[truncated]"
                : content;
            lock (_historyLock)
            {
                _recentReadFiles.RemoveAll(f => f.Item1 == name);
                _recentReadFiles.Add(Tuple.Create(name, summary));
                while (_recentReadFiles.Count > MaxRestoredFiles)
                    _recentReadFiles.RemoveAt(0);
            }
        }

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
                _currentSessionId = null;
                _sessionEditApproved = false;
            }
        }

        public void SaveSession(string displayMessagesJson)
        {
            if (string.IsNullOrEmpty(_currentSessionId)) return;
            var path = Path.Combine(HistoryDir, _currentSessionId + ".json");
            List<Dictionary<string, object>> historySnapshot;
            lock (_historyLock)
            {
                historySnapshot = new List<Dictionary<string, object>>(_conversationHistory);
            }
            // 解析 display messages JSON → object
            object displayMsgs = new object[0];
            try { if (!string.IsNullOrEmpty(displayMessagesJson)) displayMsgs = _json.Deserialize<object>(displayMessagesJson) ?? displayMsgs; } catch { }
            var data = new Dictionary<string, object>
            {
                { "id", _currentSessionId },
                { "updated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "messages", displayMsgs },
                { "history", historySnapshot }
            };
            try { File.WriteAllText(path, SerializeRequest(data)); } catch { }
        }

        public string LoadSession(string sessionId)
        {
            var path = Path.Combine(HistoryDir, sessionId + ".json");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);

            lock (_historyLock)
            {
                _currentSessionId = sessionId;
                _conversationHistory.Clear();
                _sessionEditApproved = false;
                try
                {
                    var data = _json.Deserialize<Dictionary<string, object>>(text);

                    // 加载原生 history（tool_use/tool_result 序列）。
                    if (data != null && data.TryGetValue("history", out var histObj)
                        && histObj is System.Collections.ArrayList al)
                    {
                        foreach (var item in al)
                        {
                            if (item is Dictionary<string, object> d)
                            {
                                NormalizeHistoryContent(d);
                                _conversationHistory.Add(d);
                            }
                        }
                    }

                    // 从 display messages 生成可读摘要，注入 history 开头
                    // AI 模型往往无法从 tool_use/tool_result 交换中理解对话上下文，
                    // 摘要帮助 AI 理解之前的对话内容。
                    if (data != null && data.TryGetValue("messages", out var msgsObj)
                        && msgsObj is System.Collections.ArrayList displayMsgs
                        && displayMsgs.Count > 0
                        && _conversationHistory.Count > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        // 明确告知模型：这是上一会话**已完成**的工作记录，仅作上下文，请勿重复执行。
                        // 否则模型会把"已完成的动作"当成待办从头重做 —— 这是自循环失忆 bug 的根因。
                        sb.AppendLine(Lang.L(
                            "[上一会话已完成的工作记录（仅作上下文，请勿重复执行其中已完成的动作，根据当前实际状态继续）]",
                            "[Previous session — COMPLETED work log, for context only. DO NOT re-execute the actions listed below. Continue based on the actual current state.]"));
                        sb.AppendLine();
                        foreach (var m in displayMsgs)
                        {
                            if (!(m is Dictionary<string, object> md)) continue;
                            var role = md.ContainsKey("role") ? md["role"]?.ToString() : "?";
                            var msg = md.ContainsKey("text") ? md["text"]?.ToString() : "";
                            if (string.IsNullOrEmpty(msg)) continue;
                            // System 类是工具调用记录（如"AI 请求执行: create_program..."）——保留并标为 Tool，
                            // 让模型看清哪些动作已做过，是防止重复执行的关键证据。
                            var label = role == "User" ? "User" : role == "AI" ? "AI" : "Tool";
                            var trimmed = msg.Length > 300 ? msg.Substring(0, 300) + "..." : msg;
                            sb.AppendLine($"{label}: {trimmed}");
                        }
                        _conversationHistory.Insert(0, new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", sb.ToString() }
                        });
                        _conversationHistory.Insert(1, new Dictionary<string, object>
                        {
                            { "role", "assistant" },
                            { "content", Lang.L(
                                "明白。以上是上一会话已完成的工作记录，我不会重复执行其中已完成的动作，将根据当前实际状态继续。",
                                "Understood. The above is a log of COMPLETED work from the previous session. I will NOT re-execute actions that are already done, and will continue based on the actual current state.") }
                        });
                    }
                }
                catch (Exception ex)
                {
                    try { AiService.LogException("LoadSession", ex); } catch { }
                }
            }
            return text;
        }

        public void DeleteSession(string sessionId)
        {
            var path = Path.Combine(HistoryDir, sessionId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }

        public List<SessionInfo> ListSessions()
        {
            var result = new List<SessionInfo>();
            if (!Directory.Exists(HistoryDir)) return result;
            foreach (var file in Directory.GetFiles(HistoryDir, "*.json").OrderByDescending(f => f))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var data = _json.Deserialize<Dictionary<string, object>>(text);
                    if (data == null) continue;
                    var id = Path.GetFileNameWithoutExtension(file);
                    object updated;
                    var timeStr = data.TryGetValue("updated", out updated) ? updated?.ToString() : id;
                    string preview = "";
                    object msgs;
                    if (data.TryGetValue("messages", out msgs) && msgs is System.Collections.ArrayList al && al.Count > 0)
                    {
                        foreach (var m in al)
                        {
                            var md = m as Dictionary<string, object>;
                            if (md != null && GetStr(md, "role") == "User")
                            {
                                preview = GetStr(md, "text") ?? "";
                                if (preview.Length > 50) preview = preview.Substring(0, 50) + "...";
                                break;
                            }
                        }
                    }
                    result.Add(new SessionInfo { Id = id, Time = timeStr, Preview = preview });
                }
                catch { }
            }
            return result;
        }

        /// JavaScriptSerializer 反序列化后 content 数组变成 ArrayList，
        /// 但代码中大量使用 `content is List&lt;Dictionary&lt;string,object&gt;&gt;` 类型检查。
        /// 加载时必须把 ArrayList 转回 List，否则所有 assistant 消息的 tool_use/text block 都会被跳过。
        private static void NormalizeHistoryContent(Dictionary<string, object> msg)
        {
            object content;
            if (!msg.TryGetValue("content", out content)) return;
            if (content is System.Collections.ArrayList al)
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var item in al)
                {
                    if (item is Dictionary<string, object> d)
                        list.Add(d);
                }
                msg["content"] = list;
            }
        }

        internal class SessionInfo
        {
            public string Id { get; set; }
            public string Time { get; set; }
            public string Preview { get; set; }
        }
    }
}
