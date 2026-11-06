using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Trio.CommunicationsLibrary;
using Trio.SharedLibrary;

namespace RTSys.PlugIn
{
    internal static class Handlers
    {
        private static readonly EventLog _events = new EventLog();
        private static IMainWindow MW => MPSingletons.MainWindow;
        private static IProject Project => MW?.Project;
        private static IController Controller => MPSingletons.Controller;

        // ---- Project ----
        public static object CreateProject()
        {
            var mw = MW;
            if (mw == null) return Error("No main window");
            var pm = mw.ProjectManager;
            if (pm == null) return Error("No project manager");

            var project = pm.CreateProject();
            return new { success = true, fileName = project?.FileName };
        }

        public static object SaveProject()
        {
            var proj = Project;
            if (proj == null || !proj.IsProject) return Error("No project loaded");

            var mw = MW;
            mw.ProjectManager.SaveProject(proj, ProjectComponent.ProjectFile | ProjectComponent.ItemsContent, true, true);
            return new { success = true };
        }

        // ---- Status ----
        public static object GetStatus()
        {
            var ctrl = Controller;
            var proj = Project;
            return new
            {
                connected = ctrl?.IsConnected ?? false,
                connectionState = ctrl?.ConnectionState.ToString() ?? "Unknown",
                productName = ctrl?.ProductName,
                firmwareVersion = ctrl?.FullVersionString,
                serialNumber = ctrl?.SerialNumber,
                projectName = proj?.FileName,
                isProject = proj?.IsProject ?? false,
                uiLanguage = System.Threading.Thread.CurrentThread.CurrentUICulture.Name
            };
        }

        // ---- Programs ----
        public static object ListPrograms()
        {
            var proj = Project;
            if (proj == null) return new { items = new object[0] };

            var items = proj.Items.Select(i =>
            {
                var result = new Dictionary<string, object>
                {
                    { "name", i.ItemName },
                    { "type", i.Type.ToString() },
                    { "itemType", i.Descriptor?.ItemType },
                    { "isEditable", i.IsEditableType },
                    { "isExecutable", i.IsExecutableType },
                    { "storage", i.Storage.ToString() },
                    { "fileName", i.FileName }
                };

                var procInfo = GetRunnableInfo(i);
                if (procInfo != null)
                {
                    result["isAutorun"] = procInfo.Item1;
                    result["autorunProcess"] = procInfo.Item2;
                    result["processAffinity"] = procInfo.Item3;
                }

                return result;
            }).ToList();

            return new { items };
        }

        public static object CreateProgram(Dictionary<string, object> body)
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var name = GetString(body, "name");
            if (string.IsNullOrEmpty(name)) return Error("name is required");

            var typeName = GetString(body, "type") ?? "basic";
            var sourceCode = GetString(body, "sourceCode");

            var programType = TypeFromString(typeName);
            var descriptor = RegistryProjectItemDescriptors.GetDescriptorByProgramType(programType, name, null);
            if (descriptor == null)
                descriptor = RegistryProjectItemDescriptors.FindDescriptor(name, null, null);
            if (descriptor == null) return Error($"Unknown type: {typeName}");

            var item = proj.CreateAndAddItem(name, descriptor, null, null);
            if (item == null) return Error("Failed to create item");

            if (!string.IsNullOrEmpty(sourceCode) && item is TextProjectItemBase textItem)
            {
                textItem.SaveSourceCode(sourceCode);

                var openError = EnsureDocumentOpen(item);
                if (openError != null)
                    return new { success = true, name = item.ItemName, warning = $"Created but editor not opened: {openError}" };

                if (!SetEditorTextSync(item, sourceCode))
                    return new { success = true, name = item.ItemName, warning = "Created and source saved, but editor view did not refresh. Reopen the program to see changes." };
            }

            return new { success = true, name = item.ItemName };
        }

        public static object DeleteProgram(string name)
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            proj.RemoveItem(item, true);
            return new { success = true };
        }

        public static object CopyProgram(string name, Dictionary<string, object> body)
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var newName = GetString(body, "newName");
            if (string.IsNullOrEmpty(newName)) return Error("newName is required");

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            if (proj.HasItem(newName)) return Error($"Name already exists: {newName}");

            var storageStr = GetString(body, "storage");
            ProgramStorage_t storage = ProgramStorage_t.internalStorage;
            if (!string.IsNullOrEmpty(storageStr))
            {
                try { storage = (ProgramStorage_t)Enum.Parse(typeof(ProgramStorage_t), storageStr, true); }
                catch { return Error($"Invalid storage: {storageStr}"); }
            }

            try
            {
                // IEC 项目项 CopyItem 走文件路径会失败；改为：新建同类型 IEC task + 复制源码
                if (IsIecItem(item))
                {
                    var descriptor = item.Descriptor;
                    var newItem = proj.CreateAndAddItem(newName, descriptor, null, null);
                    if (newItem == null) return Error("CreateAndAddItem returned null for IEC copy");

                    string src = null;
                    try { src = ReadIecSource(item); } catch { }
                    if (!string.IsNullOrEmpty(src))
                    {
                        try { WriteIecSource(newItem, src); } catch { }
                    }
                    return new { success = true, name = newItem.ItemName };
                }

                var copiedItem = proj.CopyItem(item, newName, storage, null);
                if (copiedItem == null) return Error("CopyItem returned null");
                return new { success = true, name = copiedItem.ItemName };
            }
            catch (Exception ex)
            {
                return Error($"Copy failed: {ex.Message}");
            }
        }

        public static object RenameProgram(string name, Dictionary<string, object> body)
        {
            var newName = GetString(body, "newName");
            if (string.IsNullOrEmpty(newName)) return Error("newName is required");

            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            proj.RenameItem(item, newName);
            return new { success = true, name = newName };
        }

        // ---- Source Code ----
        // IEC 任务详情：POU 列表 + 任务/全局/保持变量文本 + 用户数据类型表。
        // IEC 任务在 MP 里是一个包，内部含多个 POU（.prg）+ 任务变量 + 数据类型表。
        public static object GetIecTaskDetail(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");
            if (!IsIecItem(item)) return Error($"Not an IEC task: {name}");

            var t = item.GetType();
            var result = new Dictionary<string, object>
            {
                ["name"] = name,
                ["itemType"] = t.Name,  // ContainerTask / ContainerLibrary
            };

            // POUs (Programs) — name + language
            var pouList = new List<object>();
            var programsProp = t.GetProperty("Programs");
            var programs = programsProp?.GetValue(item) as System.Collections.IEnumerable;
            if (programs != null)
            {
                foreach (var pou in programs)
                {
                    var pt = pou.GetType();
                    var pouName = pt.GetProperty("ItemName")?.GetValue(pou) as string;
                    string lang = "";
                    var ctx = pt.GetProperty("SubItemContext")?.GetValue(pou);
                    if (ctx != null)
                    {
                        var desc = ctx.GetType().GetProperty("ProgramDescriptor")?.GetValue(ctx);
                        if (desc != null)
                        {
                            var langObj = desc.GetType().GetProperty("ProgramLanguage")?.GetValue(desc);
                            if (langObj != null) lang = langObj.ToString();
                        }
                    }
                    pouList.Add(new { name = pouName ?? "", language = lang });
                }
            }
            result["pous"] = pouList;

            // Variable groups — return VAR...END_VAR text (null if group absent)
            try { result["taskVariables"] = GetIecGroupText(t.GetProperty("TaskVariables")?.GetValue(item)); }
            catch (Exception ex) { result["taskVariablesError"] = ex.Message; }
            try { result["globalVariables"] = GetIecGroupText(t.GetProperty("GlobalVariables")?.GetValue(item)); }
            catch (Exception ex) { result["globalVariablesError"] = ex.Message; }
            try { result["retainVariables"] = GetIecGroupText(t.GetProperty("RetainVariables")?.GetValue(item)); }
            catch (Exception ex) { result["retainVariablesError"] = ex.Message; }

            // Data type table — name + kind (Struct/Union/Enum)
            var typeList = new List<object>();
            try
            {
                var typesEnum = t.GetProperty("Types")?.GetValue(item) as System.Collections.IEnumerable;
                if (typesEnum != null)
                {
                    foreach (var ty in typesEnum)
                    {
                        var tt = ty.GetType();
                        var typeName = tt.GetProperty("ItemName")?.GetValue(ty) as string
                                    ?? tt.GetProperty("DisplayName")?.GetValue(ty) as string
                                    ?? "";
                        var kind = tt.Name;
                        if (kind.StartsWith("IECObject")) kind = kind.Substring("IECObject".Length);
                        typeList.Add(new { name = typeName, kind = kind });
                    }
                }
            }
            catch (Exception ex) { result["dataTypesError"] = ex.Message; }
            result["dataTypes"] = typeList;

            return result;
        }

        // 读 IEC 任务的三类变量组：task (Global 内部组) / global (Controller 跨任务) / retain (保持)
        public static object ReadIecVariables(string name, string scope)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");
            if (!IsIecItem(item)) return Error($"Not an IEC task: {name}");

            object group;
            try { group = GetIecVariableGroup(item, scope); }
            catch (Exception ex) { return Error(ex.Message); }
            if (group == null) return Error($"Scope '{scope}' has no variable group in this task.");

            var text = GetIecGroupText(group);
            var groupName = group.GetType().GetProperty("DisplayName")?.GetValue(group) as string ?? scope;
            return new
            {
                name = name,
                scope = scope,
                groupName = groupName,
                text = text ?? "",
            };
        }

        // 写 IEC 任务的三类变量组（VAR...END_VAR 文本）
        public static object WriteIecVariables(string name, string scope, string text)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");
            if (!IsIecItem(item)) return Error($"Not an IEC task: {name}");
            if (text == null) return Error("text is required");

            object group;
            try { group = GetIecVariableGroup(item, scope); }
            catch (Exception ex) { return Error(ex.Message); }
            if (group == null) return Error($"Scope '{scope}' has no variable group in this task.");

            // STParser 用 "VAR\r\n" / "END_VAR\r\n" 匹配（VAR_START_TAGS），需要 CRLF。
            // 这里只 normalize 一次，避免下面 invoke 重复处理。
            string normalized = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

            object result = null;
            DispatcherHelper.Invoke(() =>
            {
                try
                {
                    // signature: ImportVariables(string text, IList<ParserException> warnings, bool renameVariablesInFiles)
                    var warnings = new System.Collections.ArrayList();
                    var importMethod = group.GetType().GetMethod("ImportVariables",
                        new[] { typeof(string), typeof(System.Collections.IList), typeof(bool) });
                    if (importMethod == null)
                        throw new InvalidOperationException("ImportVariables method not found on group");
                    importMethod.Invoke(group, new object[] { normalized, warnings, false });
                    var ok = warnings.Count == 0;
                    var warnList = new List<string>();
                    foreach (var w in warnings)
                    {
                        var msg = w?.GetType().GetProperty("Message")?.GetValue(w) as string ?? w?.ToString() ?? "";
                        warnList.Add(msg);
                    }
                    result = new
                    {
                        success = true,
                        name = name,
                        scope = scope,
                        warnings = warnList,
                        warningCount = warnList.Count,
                    };
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException?.Message ?? ex.Message;
                    result = new { success = false, error = inner };
                }
            });
            return result;
        }

        // 取 IEC 任务里指定 scope 的变量组对象（反射）
        private static object GetIecVariableGroup(IProjectItem item, string scope)
        {
            if (string.IsNullOrEmpty(scope))
                throw new ArgumentException("scope is required (task|global|retain)");
            var t = item.GetType();
            string propName;
            switch (scope.ToLowerInvariant())
            {
                case "task": propName = "TaskVariables"; break;
                case "global": propName = "GlobalVariables"; break;
                case "retain": propName = "RetainVariables"; break;
                default:
                    throw new ArgumentException($"Unknown scope '{scope}'. Use one of: task, global, retain");
            }
            return t.GetProperty(propName)?.GetValue(item);
        }

        public static object ReadSource(string name, Dictionary<string, object> body = null)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var itemType = item.Type.ToString();
            var dialect = GetProgramDialect(name);

            // 先尝试标准 TextProjectItemBase 路径（TrioBASIC / Text）
            var textItem = item as TextProjectItemBase;
            string source;
            int lineOffset = 0;
            if (textItem != null)
            {
                // 优先从已打开的编辑器读取 — 编辑器文本与 IDE 行号一致
                var editorText = TryGetEditorText(item);
                if (!string.IsNullOrEmpty(editorText))
                {
                    source = editorText;
                }
                else
                {
                    source = textItem.LoadSourceCode();
                    // LoadSourceCode() 可能包含 IDE 编辑器不计入行号的头部行
                    lineOffset = DetectSourceHeaderOffset(source, name);
                }
            }
            else if (IsIecItem(item))
            {
                string pouName = body != null ? GetString(body, "pouName") : null;
                try { source = ReadIecSource(item, pouName); }
                catch (Exception ex) { return Error($"IEC source read failed: {ex.Message}"); }
            }
            else
            {
                return Error($"Program is not a text/IEC item: {name}");
            }

            var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // 剥离 LoadSourceCode() 中 IDE 不计入行号的头部行
            if (lineOffset > 0 && lineOffset < lines.Length)
            {
                lines = lines.Skip(lineOffset).ToArray();
                source = string.Join("\n", lines);
            }

            int totalLines = lines.Length;

            int? startOpt = body != null ? GetInt(body, "startLine") : null;
            int? endOpt = body != null ? GetInt(body, "endLine") : null;

            // 显式分页：返回指定行范围
            if (startOpt.HasValue || endOpt.HasValue)
            {
                int start = Math.Max(1, startOpt ?? 1);
                if (start > totalLines) return Error($"startLine {start} exceeds total lines {totalLines}");
                int end = Math.Min(totalLines, endOpt ?? totalLines);
                if (end < start) end = start;
                var slice = string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));
                return new
                {
                    sourceCode = slice,
                    startLine = start,
                    endLine = end,
                    totalLines,
                    remainingLines = Math.Max(0, totalLines - end),
                    truncated = false,
                    type = itemType,
                    dialect
                };
            }

            // 无分页参数：完整返回。但若文件特别大，自动截到前 400 行或 12000 字符（避免触发 MaxToolResultLen），并提示 AI 用 startLine 分页
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
                var slice = string.Join("\n", lines.Take(end));
                return new
                {
                    sourceCode = slice,
                    startLine = 1,
                    endLine = end,
                    totalLines,
                    remainingLines = Math.Max(0, totalLines - end),
                    truncated = true,
                    hint = $"Large file ({totalLines} lines, {source.Length} chars). To read the next chunk, call read_source with startLine={end + 1}.",
                    type = itemType,
                    dialect
                };
            }

            return new { sourceCode = source, totalLines, type = itemType, dialect };
        }

        public static object OpenProgram(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var mw = MW;
            if (mw == null) return Error("No main window");

            if (IsIecItem(item))
            {
                try
                {
                    OpenIecEditor(item);
                    return new { success = true };
                }
                catch (Exception ex)
                {
                    return Error($"Failed to open IEC editor: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            try
            {
                string error = null;
                DispatcherHelper.Invoke(() =>
                {
                    var doc = mw.OpenProgramEditor((IItemBase)(object)item, null, null, true);
                    if (doc == null)
                        error = "OpenProgramEditor returned null";
                });
                if (error != null)
                    return Error(error);
                return new { success = true };
            }
            catch (Exception ex)
            {
                return Error($"Failed to open: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string EnsureDocumentOpen(IProjectItem item)
        {
            var mw = MW;
            if (mw == null) return "No main window";
            try
            {
                var openDoc = mw.HasOpenedDocumentFor(item);
                if (openDoc != null) return null;

                var doc = mw.OpenProgramEditor((IItemBase)(object)item, null, null, true);
                if (doc != null) return null;

                return "OpenProgramEditor returned null";
            }
            catch (Exception ex)
            {
                return $"EnsureDocumentOpen: {ex.GetType().Name}: {ex.Message}";
            }
        }

        public static object WriteSource(string name, Dictionary<string, object> body)
        {
            var sourceCode = GetString(body, "sourceCode") ?? "";

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var textItem = item as TextProjectItemBase;
            if (textItem != null)
            {
                var mw = MW;
                object result = null;
                DispatcherHelper.Invoke(() =>
                {
                    textItem.SaveSourceCode(sourceCode);
                    var openError = EnsureDocumentOpen(item);
                    if (openError != null)
                    {
                        result = new { success = false, error = openError };
                        return;
                    }

                    var refreshed = SetEditorTextSync(item, sourceCode);
                    result = refreshed
                        ? (object)new { success = true }
                        : new { success = true, warning = "Source saved to project, but editor view did not refresh. Reopen the program to see changes." };
                });
                return result ?? Error("Write failed");
            }

            if (IsIecItem(item))
            {
                string pouName = GetString(body, "pouName");
                object iecResult = null;
                DispatcherHelper.Invoke(() =>
                {
                    try
                    {
                        WriteIecSource(item, sourceCode, pouName);
                        iecResult = new { success = true };
                    }
                    catch (Exception ex)
                    {
                        iecResult = new { success = false, error = $"IEC write failed: {ex.Message}" };
                    }
                });
                return iecResult ?? Error("IEC write failed");
            }

            return Error($"Program is not a text/IEC item: {name}");
        }

        public static object PatchSource(string name, Dictionary<string, object> body)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var textItem = item as TextProjectItemBase;
            bool isIec = textItem == null && IsIecItem(item);
            if (textItem == null && !isIec)
                return Error($"Program is not a text/IEC item: {name}");

            var ops = GetArray(body, "operations");
            if ((ops == null || ops.Length == 0) &&
                body.TryGetValue("operations", out var rawOps) && rawOps is string opsStr && !string.IsNullOrEmpty(opsStr))
            {
                try
                {
                    var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                    ops = js.Deserialize<object[]>(opsStr);
                }
                catch { }
            }
            if (ops == null || ops.Length == 0) return Error("operations is required");

            string source;
            string pouName = isIec ? GetString(body, "pouName") : null;
            try
            {
                source = textItem != null ? textItem.LoadSourceCode() : ReadIecSource(item, pouName);
            }
            catch (Exception ex) { return Error($"Read source failed: {ex.Message}"); }

            // 规范化行尾为 \n
            source = source.Replace("\r\n", "\n").Replace("\r", "\n");

            // 解析操作列表：每个操作含 old_string（要查找的原文本）和 new_string（替换为）
            var editOps = ops.Select(op => op as Dictionary<string, object>)
                .Where(d => d != null)
                .Select(d => new
                {
                    oldString = GetString(d, "old_string") ?? GetString(d, "oldContent") ?? "",
                    newString = GetString(d, "new_string") ?? GetString(d, "content") ?? ""
                })
                .ToList();

            var appliedOps = new List<object>();
            var currentSource = source;

            foreach (var op in editOps)
            {
                if (string.IsNullOrEmpty(op.oldString))
                {
                    // old_string 为空 → 在文件末尾追加 new_string
                    currentSource = currentSource.TrimEnd('\n') + "\n" + op.newString + "\n";
                    appliedOps.Add(new { status = "appended" });
                    continue;
                }

                // 在源码中查找 old_string（精确 → 大小写不敏感 → Trim 逐行模糊）
                var found = FindText(currentSource, op.oldString);
                int pos = found.pos;
                int matchLen = found.length;
                if (pos < 0)
                {
                    appliedOps.Add(new { status = "skipped", reason = "old_string not found" });
                    continue;
                }

                // 检查唯一性：old_string 不应在源码中出现多次
                var matchCount = CountOccurrences(currentSource, op.oldString);
                if (matchCount > 1)
                {
                    appliedOps.Add(new { status = "skipped", reason = $"old_string matched {matchCount} times, need more context to be unique" });
                    continue;
                }

                // 用 matchLen 而非 op.oldString.Length 切片：Trim 模糊分支下源码行可能带
                // 尾随/前导空白，实际匹配块比 old_string 长，用 oldString.Length 会切在中间留残留。
                currentSource = currentSource.Substring(0, pos) + op.newString + currentSource.Substring(pos + matchLen);
                appliedOps.Add(new { status = "replaced" });
            }

            object result = null;
            DispatcherHelper.Invoke(() =>
            {
                try
                {
                    bool refreshed = true;
                    if (textItem != null)
                    {
                        textItem.SaveSourceCode(currentSource);
                        EnsureDocumentOpen(item);
                        refreshed = SetEditorTextSync(item, currentSource);
                    }
                    else
                    {
                        WriteIecSource(item, currentSource, pouName);
                    }
                    result = refreshed
                        ? (object)new { success = true, sourceCode = currentSource, operations = appliedOps }
                        : new { success = true, sourceCode = currentSource, operations = appliedOps, warning = "Source saved to project, but editor view did not refresh. Reopen the program to see changes." };
                }
                catch (Exception ex)
                {
                    result = new { success = false, error = ex.Message };
                }
            });
            return result ?? Error("Patch failed");
        }

        // 在 source 中查找 search，返回 (起始偏移, 匹配块的实际长度)。
        // matchLength 与 search.Length 可能不同：Trim 模糊分支下源码行带尾随/前导空白时
        // 实际匹配块更长。调用方必须用 matchLength 切片，否则会切在匹配块中间留下残留。
        // 未找到返回 (-1, 0)。
        private static (int pos, int length) FindText(string source, string search)
        {
            // 1) 精确匹配（含 \r\n 已规范化为 \n）
            int idx = source.IndexOf(search, StringComparison.Ordinal);
            if (idx >= 0) return (idx, search.Length);

            // 2) 大小写不敏感匹配（TrioBASIC 语义）：TrioBASIC 编译器大小写不敏感，
            //    AI 记成全小写/全大写都合法。若此处仍用 Ordinal，会把 cVR_ vs cvr_ 判成不同
            //    字符串 → old_string 永远 not found → AI 反复重试（见 chat_history 20260621_202830
            //    msg#68-70 连续 3 次失败根因）。CountOccurrences 必须同步用 OrdinalIgnoreCase，
            //    否则唯一性检查会与查找语义割裂。长度与 search 相同（仅大小写不同）。
            idx = source.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return (idx, search.Length);

            // 3) Trim 后逐行模糊匹配（处理行尾/行首空白差异）
            var srcLines = source.Split('\n');
            var searchLines = search.Split('\n');

            // 计算第 i 行在 source 中的起始偏移 + 第 i..i+n 行块的总长度（含中间 \n，不含末尾 \n）
            int LineOffset(int i) { int p = 0; for (int k = 0; k < i; k++) p += srcLines[k].Length + 1; return p; }
            int BlockLength(int i, int n)
            {
                int len = 0;
                for (int k = 0; k < n; k++) len += srcLines[i + k].Length + 1;  // +1 为 \n
                return len > 0 ? len - 1 : 0;  // 去掉末尾多算的 \n
            }

            // 3a) TrimEnd 逐行（仅容忍行尾空白）
            for (int i = 0; i <= srcLines.Length - searchLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < searchLines.Length; j++)
                {
                    if (!string.Equals(srcLines[i + j].TrimEnd(), searchLines[j].TrimEnd(), StringComparison.OrdinalIgnoreCase))
                    { match = false; break; }
                }
                if (match)
                {
                    // 旧实现 source.IndexOf(srcLines[i], source.Length - source.Length) 是 bug：
                    // length-length 恒为 0，等价于「从文件开头找该行文本首次出现位置」，
                    // 多行 old_string 且该行文本在文件中重复出现时会返回错误偏移 → 替换错位置。
                    return (LineOffset(i), BlockLength(i, searchLines.Length));
                }
            }

            // 3b) Trim 逐行（两端都 trim，容忍缩进差异）
            for (int i = 0; i <= srcLines.Length - searchLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < searchLines.Length; j++)
                {
                    if (!string.Equals(srcLines[i + j].Trim(), searchLines[j].Trim(), StringComparison.OrdinalIgnoreCase))
                    { match = false; break; }
                }
                if (match)
                {
                    return (LineOffset(i), BlockLength(i, searchLines.Length));
                }
            }

            return (-1, 0);
        }

        // 计数 old_string 在 source 中出现的次数（用于唯一性检查）。
        // 必须与 FindText 用同一套比较语义：OrdinalIgnoreCase，否则会出现
        // 「FindText 找到 → pos>=0，但 CountOccurrences=0」的不一致。
        private static int CountOccurrences(string source, string search)
        {
            int count = 0, pos = 0;
            while ((pos = source.IndexOf(search, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                pos += search.Length;
            }
            return count;
        }

        private static bool SetEditorTextSync(IProjectItem item, string text)
        {
            var mw = MW;
            // 立即设置：文档已打开且视觉树就绪时直接成功
            var openDoc = mw?.HasOpenedDocumentFor(item);
            if (openDoc != null && TrySetEditorText(openDoc, text))
                return true;

            // 文档刚打开 / 长时间未操作后视觉树未就绪：pump dispatcher 让视觉树构建后重试。
            // 原实现仅 pump 一次 Loaded，长间隔下单次常拿不到控件 → 静默失败；改为多次 pump。
            for (int attempt = 0; attempt < 4; attempt++)
            {
                bool done = false;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        openDoc = mw?.HasOpenedDocumentFor(item);
                        if (openDoc != null && TrySetEditorText(openDoc, text))
                            done = true;
                    }));
                if (done) return true;
            }
            return false;
        }

        private static bool TrySetEditorText(IDocumentContainer doc, string text)
        {
            try
            {
                var content = doc.Content as System.Windows.FrameworkElement;
                if (content == null) return false;
                var found = FindControlWithTextProperty(content);
                if (found == null) return false;
                found.GetType().GetProperty("Text").SetValue(found, text, null);
                return true;
            }
            catch { return false; }
        }

        private static string TryGetEditorText(IProjectItem item)
        {
            try
            {
                return DispatcherHelper.Invoke(() =>
                {
                    var mw = MW;
                    if (mw == null) return null;
                    var doc = mw.HasOpenedDocumentFor(item);
                    if (doc == null) return null;
                    var content = doc.Content as System.Windows.FrameworkElement;
                    if (content == null) return null;
                    var found = FindControlWithTextProperty(content);
                    if (found == null) return null;
                    return found.GetType().GetProperty("Text")?.GetValue(found) as string;
                }) as string;
            }
            catch { return null; }
        }

        // 检测 LoadSourceCode() 返回的源码中 IDE 不计入行号的头部行数
        // TrioBASIC 程序的 LoadSourceCode() 可能包含 1~2 行头部（如程序名注释 + 空行），
        // 这些行在 IDE 编辑器中不计入行号，导致 ReadSource 报告的行号与 IDE 偏移。
        private static int DetectSourceHeaderOffset(string source, string programName)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(programName)) return 0;
            var firstNewline = source.IndexOfAny(new[] { '\r', '\n' });
            var firstLine = firstNewline >= 0 ? source.Substring(0, firstNewline) : source;
            var trimmed = firstLine.TrimStart('\'', ' ', '\t');

            // 第一行是程序名（纯名或注释形式如 'ProgramName）→ 头部偏移
            if (!string.Equals(trimmed, programName, StringComparison.OrdinalIgnoreCase))
                return 0;

            // 找到第二行，检查是否为空行
            if (firstNewline < 0) return 1;
            var rest = source.Substring(firstNewline).TrimStart('\r', '\n');
            if (rest.Length == 0 || rest[0] == '\r' || rest[0] == '\n')
                return 2;

            return 1;
        }

        private static System.Windows.DependencyObject FindControlWithTextProperty(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                var name = child.GetType().Name;
                // Look for AvalonEdit TextEditor or ProgramEditor
                if (name == "TextEditor" || name == "TextArea")
                {
                    var tp = child.GetType().GetProperty("Text");
                    if (tp != null && tp.CanWrite) return child;
                }
                // ProgramEditor itself might have Text
                if (name == "ProgramEditor")
                {
                    var tp = child.GetType().GetProperty("Text");
                    if (tp != null && tp.CanWrite) return child;
                    // Search inside ProgramEditor for TextEditor
                    var inner = FindControlWithTextProperty(child);
                    if (inner != null) return inner;
                    return null;
                }
                var result = FindControlWithTextProperty(child);
                if (result != null) return result;
            }
            return null;
        }

        // ---- Transfer ----
        public static object Upload(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            if (IsIecItem(item))
            {
                try
                {
                    InvokeContainerTaskMethod(item, "Upload");
                    return new { success = true };
                }
                catch (Exception ex)
                {
                    return Error("IEC upload failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }

            var ctrl = Controller;
            var context = new TransferToControllerContext((TransferToControllerFlags)2, null);
            bool ok = ctrl.TransferToController(item, context);
            return ok ? new { success = true } : Error(context.error?.ErrorString ?? "Transfer failed");
        }

        public static object Download(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            try
            {
                Controller.TransferFromController(item);
                return new { success = true };
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        // ---- Compile / Run / Stop ----
        public static object Compile(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            if (IsIecItem(item))
            {
                try
                {
                    // 找 Compile 重载：优先带 (bool, out IEnumerable<>) 的版本，能拿到错误明细
                    var itemType = item.GetType();
                    var compileMethods = itemType.GetMethods().Where(m => m.Name == "Compile").ToList();
                    System.Reflection.MethodInfo verboseMethod = null;
                    foreach (var m in compileMethods)
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(bool) && ps[1].IsOut && ps[1].ParameterType.IsByRef)
                        {
                            verboseMethod = m; break;
                        }
                    }

                    if (verboseMethod == null)
                    {
                        var okObj = InvokeContainerTaskMethod(item, "Compile");
                        bool ok0 = okObj is bool b0 && b0;
                        return new { success = ok0, isCompiled = ok0, diag = "verbose Compile overload not found" };
                    }

                    var args = new object[] { false, null };
                    var ok = DispatcherHelper.Invoke(() => verboseMethod.Invoke(item, args));
                    bool success = ok is bool bv && bv;
                    var errs = args[1] as System.Collections.IEnumerable;
                    var errList = new List<object>();
                    if (errs != null)
                    {
                        foreach (var e in errs)
                        {
                            var et = e.GetType();
                            var msgProp = et.GetProperty("Info") ?? et.GetProperty("Message") ?? et.GetProperty("Text") ?? et.GetProperty("ErrorText");
                            var lineProp = et.GetProperty("LineNumber") ?? et.GetProperty("Line") ?? et.GetProperty("CodeLineBase");
                            var typeProp = et.GetProperty("Type");
                            errList.Add(new
                            {
                                message = msgProp?.GetValue(e)?.ToString(),
                                line = lineProp != null ? Convert.ToInt32(lineProp.GetValue(e)) : 0,
                                type = typeProp?.GetValue(e)?.ToString()
                            });
                        }
                    }
                    return new { success = success, isCompiled = success, errors = errList };
                }
                catch (Exception ex)
                {
                    return Error("IEC compile failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }

            if (item.RemoteState == null) return Error("No remote state");
            // V5.6/V5.7 双版本：反射调用 CompileProgram，兼容单个错误(V5.6)/List(V5.7)，字段名两版一致。
            var compileErrs = CompileApiCompat.InvokeCompileProgramErrors(Controller, item.RemoteState);
            if (compileErrs.Count > 0)
            {
                return new
                {
                    success = false,
                    // 首条作为快速摘要，全部错误见 errors（不丢错误）
                    error = compileErrs[0].Text ?? $"Compile error #{compileErrs[0].Number}",
                    errors = compileErrs.Select(e => new
                    {
                        lineNumber = e.Line,
                        errorNumber = e.Number,
                        errorText = e.Text,
                        isWarning = e.IsWarning,
                        programName = e.ProgramName
                    })
                };
            }
            return new { success = true };
        }

        public static object RunProgram(string name, Dictionary<string, object> body = null)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            int? processNum = body != null ? GetInt(body, "process") : null;
            int proc = processNum ?? 0;

            if (IsIecItem(item))
            {
                try
                {
                    InvokeContainerTaskMethod(item, "Run", proc);
                    return new { success = true };
                }
                catch (Exception ex)
                {
                    return Error("IEC run failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }

            if (item.RemoteState == null) return Error("No remote state");
            if (processNum.HasValue)
                Controller.RunProgram(item.RemoteState, processNum.Value);
            else
                Controller.RunProgram(item.RemoteState);
            return new { success = true };
        }

        public static object StopProgram(string name, Dictionary<string, object> body = null)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            int? processNum = body != null ? GetInt(body, "process") : null;
            int proc = processNum ?? 0;

            if (IsIecItem(item))
            {
                try
                {
                    InvokeContainerTaskMethod(item, "Stop", proc);
                    return new { success = true };
                }
                catch (Exception ex)
                {
                    return Error("IEC stop failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }

            if (item.RemoteState == null) return Error("No remote state");
            if (processNum.HasValue)
                Controller.StopProgram(item.RemoteState, processNum.Value);
            else
                Controller.StopProgram(item.RemoteState);
            return new { success = true };
        }

        // ---- Program Process Settings ----

        public static object GetProgramProcess(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var procInfo = GetRunnableInfo(item);
            if (procInfo == null)
                return Error("This program does not support process settings (not a runnable item or no remote state)");

            return new
            {
                name = item.ItemName,
                isAutorun = procInfo.Item1,
                autorunProcess = procInfo.Item2,
                processAffinity = procInfo.Item3
            };
        }

        public static object SetProgramProcess(string name, Dictionary<string, object> body)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var target = FindRunnableTarget(item);
            if (target == null)
                return Error("This program does not support process settings");

            try
            {
                var type = target.GetType().GetInterface("IRunnableItem");

                if (body.ContainsKey("isAutorun"))
                {
                    var prop = type.GetProperty("IsAutorun");
                    prop.SetValue(target, Convert.ToBoolean(body["isAutorun"]), null);
                }
                if (body.ContainsKey("autorunProcess"))
                {
                    var prop = type.GetProperty("AutorunProcess");
                    prop.SetValue(target, Convert.ToInt32(body["autorunProcess"]), null);
                }
                if (body.ContainsKey("processAffinity"))
                {
                    var prop = type.GetProperty("ProcessAffinity");
                    prop.SetValue(target, Convert.ToInt32(body["processAffinity"]), null);
                }

                // Read back
                var procInfo = GetRunnableInfo(item);
                return new
                {
                    success = true,
                    isAutorun = procInfo.Item1,
                    autorunProcess = procInfo.Item2,
                    processAffinity = procInfo.Item3
                };
            }
            catch (Exception ex)
            {
                return Error($"Failed to set process: {ex.Message}");
            }
        }

        private static object FindRunnableTarget(IProjectItem item)
        {
            try
            {
                // Try item itself first (RoboTool pattern)
                if (item.GetType().GetInterface("IRunnableItem") != null)
                    return item;
                // Then try RemoteState (IEC pattern)
                if (item.RemoteState != null && item.RemoteState.GetType().GetInterface("IRunnableItem") != null)
                    return item.RemoteState;
            }
            catch { }
            return null;
        }

        private static Tuple<bool, int, int> GetRunnableInfo(IProjectItem item)
        {
            try
            {
                var target = FindRunnableTarget(item);
                if (target == null) return null;

                var type = target.GetType().GetInterface("IRunnableItem");
                if (type == null) return null;

                var isAutorun = (bool)type.GetProperty("IsAutorun").GetValue(target);
                var autorunProcess = (int)type.GetProperty("AutorunProcess").GetValue(target);
                var processAffinity = (int)type.GetProperty("ProcessAffinity").GetValue(target);
                return Tuple.Create(isAutorun, autorunProcess, processAffinity);
            }
            catch { return null; }
        }

        // ---- VR ----
        public static object ReadVR(int address, int count)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var values = new List<double?>();
            for (int i = 0; i < count; i++)
            {
                double val;
                if (VR.ExecuteGet(queue, address + i, out val, context))
                    values.Add(val);
                else
                    values.Add(null);
            }
            return new { values };
        }

        public static object WriteVR(int address, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            double value = GetDouble(body, "value");
            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            bool ok = VR.ExecuteSet(queue, address, value, context);
            return ok ? new { success = true } : Error("Write VR failed");
        }

        // ---- TABLE ----
        public static object ReadTable(int address, int count)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var values = new List<double?>();

            for (int i = 0; i < count; i++)
            {
                var range = new TABLE.RangeGet_t(address + i, 1, 1, -1);
                var buf = new double?[1];
                if (TABLE.ExecuteGet(queue, range, buf, context) && buf[0].HasValue)
                    values.Add(buf[0].Value);
                else
                    values.Add(null);
            }
            return new { values };
        }

        public static object WriteTable(int address, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var vals = GetArray(body, "values");
            if (vals == null || vals.Length == 0) return Error("values array is required");

            var doubles = vals.Select(v => Convert.ToDouble(v)).ToArray();
            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var range = new TABLE.RangeSet_t(address, doubles);
            bool ok = TABLE.ExecuteSet(queue, range, context);
            return ok ? new { success = true } : Error("Write TABLE failed");
        }

        // ---- Descriptors ----
        public static object ListDescriptors()
        {
            var descs = RegistryProjectItemDescriptors.Descriptors.Select(d => new
            {
                friendlyName = d.FriendlyName,
                type = d.Type.ToString(),
                itemType = d.ItemType,
                defaultStorage = d.DefaultStorage.ToString()
            }).ToList();

            return new { descriptors = descs };
        }

        // ---- Chat Panel ----
        public static object OpenChat()
        {
            // RTSys 下由宿主 DockBar 承载 ChatPanel，无需通过 MP 工具窗口打开
            return Error("OpenChat not supported on RTSys (chat panel is embedded by host)");
        }

        // ---- Axes ----
        public static object ListAxes()
        {
            var ctrl = Controller;
            if (ctrl == null) return Error("Not connected to controller");

            var axes = ctrl.Axes;
            if (axes == null) return new { axes = new object[0] };

            var result = axes.Select(a => new
            {
                index = a.Index,
                name = a.FriendlyName,
                typeName = a.TypeName,
                slot = a.Slot,
                type = a.Type.ToString(),
                isActive = a.IsActive,
                isInError = a.IsInError,
                isInWarning = a.IsInWarning,
                axisStatus = $"0x{a.AxisStatus:X}",
                driveStatus = a.DriveStatus.HasValue ? $"0x{a.DriveStatus:X}" : null,
                error = a.IsInError ? a.ErrorDescription : null
            }).ToList();

            return new { axes = result };
        }

        public static object GetAxisDetail(int index)
        {
            var ctrl = Controller;
            if (ctrl == null) return Error("Not connected to controller");

            var axes = ctrl.Axes;
            if (axes == null) return Error("No axes available");

            var axis = axes.FirstOrDefault(a => a.Index == index);
            if (axis == null) return Error($"Axis {index} not found");

            return new
            {
                index = axis.Index,
                name = axis.FriendlyName,
                defaultName = axis.DefaultFriendlyName,
                nameWithIndex = axis.FriendlyNameWithIndex,
                type = axis.Type.ToString(),
                typeName = axis.TypeName,
                isEncoderType = axis.IsEncoderType,
                slot = axis.Slot,
                isActive = axis.IsActive,
                isInError = axis.IsInError,
                isInWarning = axis.IsInWarning,
                axisStatus = $"0x{axis.AxisStatus:X}",
                driveStatus = axis.DriveStatus.HasValue ? $"0x{axis.DriveStatus:X}" : null,
                robotStatus = $"0x{axis.RobotStatus:X}",
                errorDescription = axis.IsInError ? axis.ErrorDescription : null,
                motorType = axis.Motor?.GetType().Name
            };
        }

        // ---- System Variables / SysVars ----
        public static object GetSystemVariables()
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var sv = Controller.SysVars;
            if (sv == null) return Error("SysVars not available");

            return new
            {
                wdog = sv.WDog,
                motionError = sv.MotionError.ToString(),
                servoPeriod = sv.ServoPeriod,
                unitError = sv.UnitError,
                systemError = sv.SystemError.ToString(),
                flashStatus = sv.FlashStatus
            };
        }

        public static object ReadSysVar(string name)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            if (string.IsNullOrEmpty(name)) return Error("name is required");

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            string value;
            if (!SystemVariable.ExecuteGet(queue, name, out value, context))
                return Error($"Failed to read system variable: {name}");

            double num;
            bool isNumeric = double.TryParse(value, System.Globalization.NumberStyles.Float,
                                              System.Globalization.CultureInfo.InvariantCulture, out num);
            return new
            {
                name = name,
                rawValue = value,
                numericValue = isNumeric ? (double?)num : null
            };
        }

        public static object WriteSysVar(string name, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            if (string.IsNullOrEmpty(name)) return Error("name is required");

            if (!body.ContainsKey("value")) return Error("value is required");
            string valueStr;
            var v = body["value"];
            if (v is bool b) valueStr = b ? "1" : "0";
            else if (v is double d || v is float || v is int || v is long)
                valueStr = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            else valueStr = v?.ToString() ?? "";

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            if (!SystemVariable.ExecuteSet(queue, name, valueStr, context))
                return Error($"Failed to write system variable: {name}");

            return new { success = true, name = name, value = valueStr };
        }

        // ---- Digital / Analogue IO ----
        public static object ListDigitalIO()
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            var lines = Controller.DigitalIOLines;
            if (lines == null) return new { lines = new object[0] };

            var result = lines.Select(l => new
            {
                index = l.Index,
                name = l.NameOrIndex,
                friendlyName = l.FriendlyName,
                bankHardware = l.Bank?.hardware.ToString(),
                bankDirection = l.Bank?.direction.ToString(),
                bankIsDigital = l.Bank?.isDigital
            }).ToList();
            return new { lines = result };
        }

        public static object ReadDigitalIO(int index)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();

            bool inState = false, opState = false;
            bool inOk = IN.Execute(queue, index, out inState, context);
            bool opOk = READ_OP.Execute(queue, index, out opState, context);

            return new
            {
                index = index,
                input = inOk ? (bool?)inState : null,
                output = opOk ? (bool?)opState : null
            };
        }

        public static object WriteDigitalIO(int index, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            if (!body.ContainsKey("value")) return Error("value is required (bool)");
            bool value;
            var v = body["value"];
            if (v is bool b) value = b;
            else if (v is double d) value = d != 0;
            else if (v is int iv) value = iv != 0;
            else if (!bool.TryParse(v?.ToString(), out value)) return Error("value must be bool");

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            if (!OP.Execute(queue, index, value, context))
                return Error($"Failed to write digital output {index}");
            return new { success = true, index = index, output = value };
        }

        public static object ListAnalogueIO()
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            var lines = Controller.AnalogueIOLines;
            if (lines == null) return new { lines = new object[0] };

            var result = lines.Select(l => new
            {
                index = l.Index,
                name = l.NameOrIndex,
                friendlyName = l.FriendlyName,
                bankDirection = l.Bank?.direction.ToString()
            }).ToList();
            return new { lines = result };
        }

        public static object ReadAnalogueIO(int index)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var info = new SystemVariable.InfoWithParam_t("AIN", index);
            if (!SystemVariable.ExecuteGet(queue, info, null, 0, context))
                return Error($"Failed to read analogue input {index}");
            int raw;
            int.TryParse(info.value, out raw);

            var infoOut = new SystemVariable.InfoWithParam_t("AOUT", index);
            int rawOut = 0;
            bool outOk = SystemVariable.ExecuteGet(queue, infoOut, null, 0, context);
            if (outOk) int.TryParse(infoOut.value, out rawOut);

            return new
            {
                index = index,
                inputRaw = raw,
                inputText = info.value,
                outputRaw = outOk ? (int?)rawOut : null,
                outputText = outOk ? infoOut.value : null
            };
        }

        public static object WriteAnalogueIO(int index, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            if (!body.ContainsKey("value")) return Error("value is required (number)");
            double v;
            var o = body["value"];
            if (o is double d) v = d;
            else if (o is int iv) v = iv;
            else if (!double.TryParse(o?.ToString(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out v))
                return Error("value must be numeric");

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            int ivalue = (int)v;
            if (!SystemVariable.ExecuteSet(queue, $"AOUT({index})", ivalue.ToString(), context))
                return Error($"Failed to write analogue output {index}");
            return new { success = true, index = index, outputRaw = ivalue };
        }

        // ---- Breakpoints (TrioBASIC programs) ----
        public static object ListBreakpoints(string name)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            if (IsIecItem(item))
            {
                var list = new List<object>();
                var mgr = GetIecBreakpointManager(item);
                if (mgr != null)
                {
                    var allProp = mgr.GetType().GetProperty("AllBreakpoints");
                    var all = allProp?.GetValue(mgr) as System.Collections.IEnumerable;
                    if (all != null)
                    {
                        foreach (var bp in all)
                        {
                            int line = 0;
                            var lineProp = bp.GetType().GetProperty("LineNumber") ?? bp.GetType().GetProperty("Line");
                            if (lineProp != null) { try { line = Convert.ToInt32(lineProp.GetValue(bp)); } catch { } }
                            list.Add(new { line });
                        }
                    }
                }
                return new { name = name, breakpoints = list };
            }

            var prog = item as Program;
            if (prog == null) return Error($"Not a breakpoint-capable BASIC program: {name}");

            var bps = prog.Breakpoints;
            var basicList = bps.Select(b => new { line = b.Line }).ToList();
            return new { name = name, breakpoints = basicList };
        }

        public static object SetBreakpoint(string name, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            if (IsIecItem(item))
                return Error("IEC breakpoints must be set via MP UI (line→CodeElement resolution not supported yet)");

            var prog = item as Program;
            if (prog == null) return Error($"Not a breakpoint-capable BASIC program: {name}");

            if (!body.ContainsKey("line")) return Error("line is required (1-based)");
            int line;
            try { line = Convert.ToInt32(body["line"]); }
            catch { return Error("line must be an integer"); }

            bool enable = true;
            if (body.ContainsKey("enable") && body["enable"] is bool eb) enable = eb;

            var ctrl = Controller;
            if (enable) ctrl.SetBreakpoint(prog, line);
            else ctrl.RemoveBreakpoint(prog, line);
            return new { success = true, name = name, line = line, enabled = enable };
        }

        public static object ClearAllBreakpoints(string name)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            if (IsIecItem(item))
            {
                try
                {
                    var mgr = GetIecBreakpointManager(item);
                    mgr?.GetType().GetMethod("DeleteAllBreakpoints")?.Invoke(mgr, null);
                    return new { success = true, name = name };
                }
                catch (Exception ex)
                {
                    return Error("IEC clear breakpoints failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }

            var prog = item as Program;
            if (prog == null) return Error($"Not a breakpoint-capable BASIC program: {name}");

            Controller.RemoveAllBreakpoints(prog);
            return new { success = true, name = name };
        }

        // 拿 ContainerTask._bkpManager (private BreakpointManager)
        private static object GetIecBreakpointManager(IProjectItem item)
        {
            if (item == null) return null;
            var t = item.GetType();
            var field = t.GetField("_bkpManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(item);
        }

        // ---- Running processes & process variables ----
        public static object ListProcesses()
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            List<PROCESS.ProcessEntry> entries;
            if (!PROCESS.Execute(queue, out entries, context))
                return Error("Failed to query process list");

            var result = entries.Select(e => new
            {
                pid = e.processNumber,
                status = e.processStatus.ToString(),
                program = e.programName,
                line = e.programLineNumber,
                type = e.processType.ToString(),
                moduleName = e.moduleName
            }).ToList();
            return new { processes = result };
        }

        public static object GetProcessVariable(int pid, string program, string variable)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            if (string.IsNullOrEmpty(variable)) return Error("variable name is required");
            if (string.IsNullOrEmpty(program)) return Error("program name is required");

            var proc = Controller.FindProcess(pid);
            if (proc == null) return Error($"No running process with pid {pid}");
            if (proc.IsDead) return Error($"Process {pid} is dead");

            var pv = proc.GetVariable(program, string.Empty, variable, null,
                                       checkIfValid: false, valueOnlyInStepMode: false, activeModifier: null);
            if (pv == null) return Error($"Variable not found: {variable}");

            return new
            {
                pid = pid,
                program = program,
                variable = variable,
                variablePath = pv.VariablePath,
                variableType = pv.VariableType,
                value = pv.HasRuntimeValue ? pv.RuntimeValue : null,
                isValid = pv.IsRuntimeValueValid
            };
        }

        // ---- Drive parameters (DRIVE_READ / DRIVE_WRITE) ----
        public static object ReadDriveParam(int axis, int address, int numFractionDigits = 4)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            string value;
            if (!DRIVE_READ.Execute(queue, address, axis, numFractionDigits, out value, context))
                return Error($"Failed DRIVE_READ axis {axis} addr ${address:X}");

            double num;
            bool isNumeric = double.TryParse(value, System.Globalization.NumberStyles.Float,
                                              System.Globalization.CultureInfo.InvariantCulture, out num);
            return new
            {
                axis = axis,
                address = $"0x{address:X}",
                rawValue = value,
                numericValue = isNumeric ? (double?)num : null
            };
        }

        public static object WriteDriveParam(int axis, int address, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;
            if (!body.ContainsKey("value")) return Error("value is required");
            string valueStr;
            var v = body["value"];
            if (v is double d || v is float || v is int || v is long)
                valueStr = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            else valueStr = v?.ToString() ?? "";

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            if (!DRIVE_WRITE.Execute(queue, address, axis, valueStr, context))
                return Error($"Failed DRIVE_WRITE axis {axis} addr ${address:X}");

            return new { success = true, axis = axis, address = $"0x{address:X}", value = valueStr };
        }

        // ---- EtherCAT ----
        public static object ScanEtherCAT(int slot)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var errors = new List<ETHERCAT.ErrorInfo_t>();
            IEnumerable<ETHERCAT.DeviceInfo_t> devices;
            if (!ETHERCAT.ExecuteInitProtocol(queue, slot, out devices, errors, context))
                return Error("EtherCAT scan failed");

            var deviceList = (devices?.Select(d => new
            {
                position = d.position,
                alias = d.alias,
                vendorId = d.vendorID.HasValue ? $"0x{d.vendorID.Value:X}" : null,
                productId = d.productID.HasValue ? $"0x{d.productID.Value:X}" : null,
                productName = d.productName,
                configured = d.configured,
                axisCount = d.axes?.Length ?? 0
            }).ToList()).Cast<object>().ToList();
            if (deviceList.Count == 0 && devices == null) deviceList = new List<object>();

            return new { slot = slot, devices = deviceList, errors = errors.Select(e => e?.ToString()).ToList() };
        }

        public static object EtherCATReadSDO(int slot, uint position, uint index, uint subindex, string typeStr)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var dt = ParseCODataType(typeStr);
            if (!dt.HasValue) return Error($"Invalid type: {typeStr}");

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            object value;
            if (!CO.ExecuteCANOpenRead(queue, slot, position, index, subindex, dt.Value, out value, context))
                return Error($"SDO read failed: position {position}, index 0x{index:X}, subindex {subindex}");

            return new
            {
                slot = slot,
                position = position,
                index = $"0x{index:X}",
                subindex = subindex,
                type = dt.Value.ToString(),
                value = value
            };
        }

        public static object EtherCATWriteSDO(int slot, uint position, uint index, uint subindex, string typeStr, object value)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var dt = ParseCODataType(typeStr);
            if (!dt.HasValue) return Error($"Invalid type: {typeStr}");

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            if (!CO.ExecuteCANOpenWrite(queue, slot, position, index, subindex, dt.Value, value, context))
                return Error($"SDO write failed: position {position}, index 0x{index:X}, subindex {subindex}");

            return new
            {
                success = true,
                slot = slot,
                position = position,
                index = $"0x{index:X}",
                subindex = subindex,
                value = value
            };
        }

        private static CO.DataType? ParseCODataType(string s)
        {
            if (string.IsNullOrEmpty(s)) return CO.DataType.Uint16;
            switch (s.ToLowerInvariant())
            {
                case "bool": return CO.DataType.Bool;
                case "int8": return CO.DataType.Int8;
                case "int16": return CO.DataType.Int16;
                case "int32": return CO.DataType.Int32;
                case "uint8": return CO.DataType.Uint8;
                case "uint16": return CO.DataType.Uint16;
                case "uint32": return CO.DataType.Uint32;
                case "real32": case "float": return CO.DataType.Real32;
                case "real64": case "double": return CO.DataType.Real64;
                case "string": return CO.DataType.String;
                default: return null;
            }
        }

        public static object EtherCATWriteSDOFromDict(Dictionary<string, object> body)
        {
            int slot = GetInt(body, "slot") ?? 0;
            uint pos = GetUInt(body, "position") ?? 0;
            uint idx = GetUInt(body, "index") ?? 0;
            uint sub = GetUInt(body, "subindex") ?? 0;
            var t = GetString(body, "type") ?? "uint16";
            if (!body.ContainsKey("value")) return Error("value is required");
            return EtherCATWriteSDO(slot, pos, idx, sub, t, body["value"]);
        }

        // ---- MS Bus ----
        public static object ScanMsBus(int slot)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var queue = Controller.CommandQueue;
            var context = new SimpleCommunicationsContext();
            IEnumerable<MS_BUS.DeviceInfo_t> devices;
            if (!MS_BUS.ExecuteStartNetwork(queue, slot, out devices, context))
                return Error("MS Bus start/scan failed");

            var list = (devices?.Select(d => new
            {
                position = d.position,
                type = d.type.ToString(),
                fpgaVersion = d.fpgaVersion,
                fwVersion = d.fwVersion,
                hwVersion = d.hwVersion
            }).ToList()).Cast<object>().ToList();
            if (list.Count == 0 && devices == null) list = new List<object>();

            MS_BUS.State_e state;
            if (MS_BUS.ExecuteGetBusState(queue, slot, out state, context))
                return new { slot = slot, state = state.ToString(), devices = list };
            return new { slot = slot, state = (string)null, devices = list };
        }

        // ---- Remote Devices / Modbus ----
        public static object ListRemoteDevices()
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var ctrl = Controller;
            var mgr = ctrl?.GetAttachedObject("RemoteDeviceManager") as IRemoteDeviceManager;
            if (mgr == null) return new { gateways = new object[0] };

            var list = new List<object>();
            foreach (var gw in mgr.Gateways ?? Enumerable.Empty<IRemoteDeviceGateway>())
            {
                var devices = new List<object>();
                foreach (var dev in gw.Devices ?? Enumerable.Empty<IRemoteDevice>())
                {
                    devices.Add(new
                    {
                        address = dev.Address,
                        vendorId = $"0x{dev.VendorId:X}",
                        productId = $"0x{dev.ProductId:X}",
                        firmwareVersion = dev.FirmwareVersion,
                        displayName = dev.DisplayName
                    });
                }
                list.Add(new
                {
                    name = gw.DisplayName,
                    description = gw.Description,
                    configuration = gw.Configuration,
                    typeName = gw.GetType().Name,
                    devices = devices
                });
            }
            return new { gateways = list };
        }

        // ---- Alarms ----
        public static object ListAlarms()
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var alarmsItems = proj.Items
                .Where(i => (i.GetType().FullName ?? "").Contains("AlarmsItem"))
                .ToList();

            if (alarmsItems.Count == 0) return new { alarms = new object[0] };

            var allAlarms = new List<object>();
            foreach (var item in alarmsItems)
            {
                var dataProp = item.GetType().GetProperty("Data");
                var data = dataProp?.GetValue(item, null);
                if (data == null) continue;

                var alarmsProp = data.GetType().GetProperty("Alarms");
                var alarms = alarmsProp?.GetValue(data, null) as System.Collections.IEnumerable;
                if (alarms == null) continue;

                foreach (var a in alarms)
                {
                    var t = a.GetType();
                    allAlarms.Add(new
                    {
                        item = item.ItemName,
                        name = GetPropRawValue(t.GetProperty("Name")?.GetValue(a, null)),
                        code = GetPropRawValue(t.GetProperty("Code")?.GetValue(a, null)),
                        conditionRaise = GetPropRawValue(t.GetProperty("ConditionRaise")?.GetValue(a, null)),
                        conditionClear = GetPropRawValue(t.GetProperty("ConditionClear")?.GetValue(a, null)),
                        indicator = GetPropRawValue(t.GetProperty("Indicator")?.GetValue(a, null)),
                        alarmClass = GetPropRawValue(t.GetProperty("Class")?.GetValue(a, null)),
                        description = GetPropRawValue(t.GetProperty("Description")?.GetValue(a, null))
                    });
                }
            }
            return new { alarms = allAlarms };
        }

        private static string GetPropRawValue(object dataValue)
        {
            if (dataValue == null) return null;
            var rp = dataValue.GetType().GetProperty("RawValue");
            var val = rp?.GetValue(dataValue, null);
            return val?.ToString();
        }

        // ---- Recipes ----
        public static object ListRecipes()
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var recipeItems = proj.Items
                .Where(i => (i.GetType().FullName ?? "").Contains("RecipeItem"))
                .Select(i => new
                {
                    name = i.ItemName,
                    typeName = i.GetType().Name,
                    group = i.Group?.DisplayName
                })
                .ToList();
            return new { recipes = recipeItems };
        }

        // ---- Robots ----
        public static object ListRobots()
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var svc = Controller?.GetAttachedObject("IRobotService") as IRobotService;
            if (svc == null) return new { robots = new object[0], available = false };

            var defs = svc.GetRobots() ?? new RobotDefinitionData[0];
            var list = defs.Select(r => new
            {
                index = r.Index,
                name = r.Name,
                modelRef = r.ModelRef,
                robotType = r.RobotType.ToString(),
                axes = r.MCAxes
            }).ToList();
            return new { robots = list, available = true, count = svc.RobotCount };
        }

        // ---- Oscilloscope ----
        public static object OpenOscilloscope()
        {
            var mw = MW;
            if (mw == null) return Error("No main window");
            try
            {
                var tool = mw.OpenToolWindow("Oscilloscope");
                return new { success = true, opened = tool != null };
            }
            catch (Exception ex)
            {
                return Error($"Failed to open oscilloscope: {ex.Message}");
            }
        }

        // ---- Project management extensions ----
        public static object OpenProject(Dictionary<string, object> body)
        {
            var mw = MW;
            if (mw == null) return Error("No main window");
            var path = GetString(body, "path");
            if (string.IsNullOrEmpty(path)) return Error("path is required");
            if (!System.IO.File.Exists(path)) return Error($"File not found: {path}");

            try
            {
                mw.ProjectManager.LoadProject(path, mw);
                return new { success = true, fileName = Project?.FileName };
            }
            catch (Exception ex)
            {
                return Error($"Failed to open project: {ex.Message}");
            }
        }

        public static object ListProjectItems()
        {
            var proj = Project;
            if (proj == null || !proj.IsProject) return Error("No project loaded");

            var items = proj.Items.Select(i => new
            {
                name = i.ItemName,
                typeName = i.GetType().Name,
                group = i.Group?.DisplayName,
                itemType = i.Descriptor?.ItemType
            }).ToList();
            return new { items = items, fileName = proj.FileName };
        }

        // ---- Plugin availability probe ----
        public static object ListAttachedPlugins()
        {
            var ctrl = Controller;
            var result = new Dictionary<string, object>();
            string[] ctrlServices = { "IRobotService", "RemoteDeviceManager" };
            foreach (var name in ctrlServices)
                result[name] = Probe(ctrl?.GetAttachedObject(name));
            return new { attachedObjects = result };
        }

        private static object Probe(object o)
        {
            if (o == null) return new { available = false };
            return new { available = true, typeName = o.GetType().FullName };
        }

        private static uint? GetUInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                uint r;
                if (val is double dd) return (uint)dd;
                if (val is int di) return (uint)di;
                if (val is long dl) return (uint)dl;
                if (uint.TryParse(val.ToString(), System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.AllowHexSpecifier,
                                  System.Globalization.CultureInfo.InvariantCulture, out r))
                    return r;
            }
            return null;
        }

        public static object SearchCode(string query, bool caseSensitive)
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");
            if (string.IsNullOrEmpty(query)) return Error("query is required");

            var results = new List<object>();
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var maxResults = 50;

            foreach (var item in proj.Items)
            {
                if (results.Count >= maxResults) break;

                var textItem = item as TextProjectItemBase;
                string source = null;
                int lineOffset = 0;
                if (textItem != null)
                {
                    // 优先从编辑器读取（行号与 IDE 一致）
                    source = TryGetEditorText(item);
                    if (string.IsNullOrEmpty(source))
                    {
                        try { source = textItem.LoadSourceCode(); } catch { }
                        if (source != null)
                            lineOffset = DetectSourceHeaderOffset(source, item.ItemName);
                    }
                }
                else if (IsIecItem(item))
                {
                    // IEC 项目项：仅当文档已打开时搜索（避免打开所有 IEC 编辑器导致性能问题）
                    var openDoc = MW?.HasOpenedDocumentFor(item);
                    if (openDoc != null)
                    {
                        try { source = ReadIecSource(item); } catch { }
                    }
                }

                if (source == null) continue;

                var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                if (lineOffset > 0 && lineOffset < lines.Length)
                    lines = lines.Skip(lineOffset).ToArray();
                for (int i = 0; i < lines.Length; i++)
                {
                    if (results.Count >= maxResults) break;
                    if (lines[i].IndexOf(query, comparison) >= 0)
                    {
                        results.Add(new
                        {
                            program = item.ItemName,
                            line = i + 1,
                            text = lines[i].Trim()
                        });
                    }
                }
            }

            return new { query, matchCount = results.Count, results };
        }

        // ---- Helpers ----
        private static object RequireDirectConnection()
        {
            var ctrl = Controller;
            if (ctrl == null) return Error("Not connected to controller");
            var state = ctrl.ConnectionState;
            if (state == ConnectionState.Disconnected || state == ConnectionState.External)
                return Error("Not connected to controller");
            if (state == ConnectionState.ConnTool)
                return Error("Controller is in Tool mode — switch to Direct or Sync mode for this operation");
            return null; // ok
        }

        private static IProjectItem FindItem(string name)
        {
            var proj = Project;
            if (proj == null) return null;
            return proj.Items.FirstOrDefault(i =>
                string.Equals(i.ItemName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static ProgramType_t TypeFromString(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "basic": return ProgramType_t.basic;
                case "encryptedbasic": return ProgramType_t.encryptedBasic;
                case "library":
                case "basiclibrary": return ProgramType_t.basicLibrary;
                case "text":
                case "textfile": return ProgramType_t.textFile;
                case "mcconfig": return ProgramType_t.mcConfig;
                case "iecprogram": return ProgramType_t.iecProgram;
                case "ieclibrary": return ProgramType_t.iecLibrary;
                case "hmidesign": return ProgramType_t.hmiDesign;
                case "hmilibrary": return ProgramType_t.hmiLibrary;
                case "robot": return ProgramType_t.robot;
                case "robotbasic": return ProgramType_t.robotBasic;
                case "realtimelibrary":
                case "realtimebasiclibrary": return ProgramType_t.realtimeBasicLibrary;
                default: return ProgramType_t.basic;
            }
        }

        private static object Error(string msg) => new { success = false, error = msg };

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null) return val.ToString();
            return null;
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
                return Convert.ToDouble(val);
            return 0;
        }

        private static int? GetInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result))
                    return result;
            }
            return null;
        }

        private static object[] GetArray(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val))
            {
                // string 也是 IEnumerable<char> — 必须排除，否则会被拆成字符数组
                if (val is string) return null;
                if (val is System.Collections.IEnumerable en)
                    return en.Cast<object>().ToArray();
            }
            return null;
        }

        // ---- IEC Source Helpers ----
        // IEC 项目项（ContainerTask / ContainerLibrary / IECObjectPOU）不继承 TextProjectItemBase。
        // 通过反射在已加载的 IECProjectProvider.MPPlugIn 程序集中查找类型并调用方法。

        private static bool IsIecItem(IProjectItem item)
        {
            if (item == null) return false;
            var fullName = item.GetType().FullName ?? "";
            return fullName.StartsWith("Trio.PlugIns.IEC61131_3");
        }

        public static string GetProgramDialect(string name)
        {
            var item = FindItem(name);
            if (item == null) return "unknown";
            if (IsIecItem(item)) return "iec";
            var t = item.Type.ToString().ToLowerInvariant();
            if (t == "basic" || t == "encryptedbasic" || t == "basiclibrary"
                || t == "textfile" || t == "robotbasic" || t == "realtimebasiclibrary")
                return "triobasic";
            return "unknown";
        }

        // 通过反射调用 ContainerTask 上的公开方法（Compile/Upload/Run/Stop 等），并在 UI 线程执行
        private static object InvokeContainerTaskMethod(IProjectItem item, string methodName, params object[] args)
        {
            if (item == null) throw new InvalidOperationException("item is null");
            var t = item.GetType();
            var types = (args == null || args.Length == 0) ? Type.EmptyTypes : args.Select(a => a == null ? typeof(object) : a.GetType()).ToArray();
            var method = t.GetMethod(methodName, types);
            if (method == null)
            {
                // 模糊匹配兜底（处理 int? 装箱等情形）
                var candidates = t.GetMethods().Where(m => m.Name == methodName && m.GetParameters().Length == types.Length).ToList();
                if (candidates.Count == 1) method = candidates[0];
                else throw new InvalidOperationException($"{methodName}({string.Join(",", types.Select(x => x.Name))}) not found on {t.Name}");
            }
            return DispatcherHelper.Invoke(() => method.Invoke(item, args ?? new object[0]));
        }

        // 打开 IEC 编辑器（ItemViewHibernation 路径，复用 ReadIecSource 内部模式）
        private static void OpenIecEditor(IProjectItem item)
        {
            var subItemPou = EnsureIecPou(item);
            var asm = LoadIecAssembly();
            var hibType = asm?.GetType("Trio.PlugIns.IEC61131_3.Views.ItemViewHibernation")
                         ?? throw new InvalidOperationException("ItemViewHibernation type not found");

            DispatcherHelper.Invoke(() =>
            {
                try
                {
                    var hib = System.Activator.CreateInstance(hibType, subItemPou);
                    hibType.GetMethod("OpenItemWindow", Type.EmptyTypes)?.Invoke(hib, null);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("OpenItemWindow failed: " + (ex.InnerException?.Message ?? ex.Message), ex);
                }
            });

            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        }

        // 从 IEC 项目项（ContainerTask / ContainerLibrary）拿到第一个可编辑 POU 的 SubItem_POU
        private static object TryGetFirstIecPou(object iecItem)
        {
            if (iecItem == null) return null;
            var programsProp = iecItem.GetType().GetProperty("Programs");
            if (programsProp == null) return null;
            var programs = programsProp.GetValue(iecItem) as System.Collections.IEnumerable;
            if (programs == null) return null;
            foreach (var pou in programs)
            {
                var ctxProp = pou.GetType().GetProperty("SubItemContext");
                var ctx = ctxProp?.GetValue(pou);
                if (ctx != null) return ctx;
            }
            return null;
        }

        // 按 POU 名查找（大小写不敏感）。返回匹配 POU 的 SubItem_POU context，找不到返回 null。
        private static object FindIecPou(object iecItem, string pouName)
        {
            if (iecItem == null || string.IsNullOrEmpty(pouName)) return null;
            var programsProp = iecItem.GetType().GetProperty("Programs");
            if (programsProp == null) return null;
            var programs = programsProp.GetValue(iecItem) as System.Collections.IEnumerable;
            if (programs == null) return null;
            var q = pouName.Trim().ToUpperInvariant();
            foreach (var pou in programs)
            {
                var name = pou.GetType().GetProperty("ItemName")?.GetValue(pou) as string;
                if (name != null && name.Trim().ToUpperInvariant() == q)
                {
                    var ctx = pou.GetType().GetProperty("SubItemContext")?.GetValue(pou);
                    if (ctx != null) return ctx;
                }
            }
            return null;
        }

        // 取 IECObjectGroup.Text（VAR...END_VAR 文本）。group 为 null 时返回 null。
        private static string GetIecGroupText(object group)
        {
            if (group == null) return null;
            try { return group.GetType().GetProperty("Text")?.GetValue(group) as string; }
            catch { return null; }
        }

        // 若 IEC 任务还没有 POU，调用 AddNewProgram 添加一个默认 ST POU（名为 MAIN），返回其 SubItem_POU。
        // 注意：pouName 为空 → "任意 POU 即可"（有就返第一个，没有就建 MAIN）。
        //       pouName 非空 → "必须名匹配"（找到则返，找不到新建），否则 write_source(pouName="NEW")
        //       会错误地把内容塞进第一个已有 POU（Round 4 修复）。
        private static object EnsureIecPou(object iecItem, string pouName = null)
        {
            if (string.IsNullOrEmpty(pouName))
            {
                var existing0 = TryGetFirstIecPou(iecItem);
                if (existing0 != null) return existing0;
                pouName = "MAIN";
            }
            else
            {
                var matched = FindIecPou(iecItem, pouName);
                if (matched != null) return matched;
            }

            // 反射拿 POULanguage.ST(0) 和 POUType.Main(0)
            var asm = LoadIecAssembly();
            if (asm == null) throw new InvalidOperationException("IEC assembly not loaded");
            var pouLangType = asm.GetType("Trio.PlugIns.IEC61131_3.Models.POULanguage")
                              ?? asm.GetType("Trio.PlugIns.IEC61131_3.POULanguage");
            var pouTypeType = asm.GetType("Trio.PlugIns.IEC61131_3.Models.POUType")
                              ?? asm.GetType("Trio.PlugIns.IEC61131_3.POUType");
            if (pouLangType == null || pouTypeType == null)
                throw new InvalidOperationException("POULanguage/POUType enums not found");
            object langST = Enum.Parse(pouLangType, "ST");
            object typeMain = Enum.Parse(pouTypeType, "Main");

            object newPou = null;
            DispatcherHelper.Invoke(() =>
            {
                try
                {
                    var addMethod = iecItem.GetType().GetMethod("AddNewProgram");
                    if (addMethod == null) throw new InvalidOperationException("AddNewProgram not found");
                    // signature: (name, comment, desc, POULanguage, POUType, SubItem_POU context, VirtualFolder folder, bool editVariables, bool openEditor, bool addInOut, bool isHidden, IECObjectPOU parent)
                    // 关键：folder 不能传 null！IECObjectPOU 构造函数会 Folder=folder → value?.Add(this)。
                    // MP 的项目树绑定 _folderRoot.ProjectItems，folder=null 时新 POU 不会注册到树里
                    // （只在 _programs 内存集合里），导致 POU 编辑器能开但项目树不显示。Round 4 修复。
                    object folder = iecItem.GetType().GetMethod("EnsureDefaultProgramFolder")
                                        ?.Invoke(iecItem, new object[] { false, false });  // isSubProgram=false, isUdfb=false → RootFolder
                    newPou = addMethod.Invoke(iecItem, new object[] { pouName, "", "", langST, typeMain, null, folder, false, false, false, false, null });
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("AddNewProgram failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            });
            if (newPou == null) throw new InvalidOperationException("AddNewProgram returned null");

            // 拿 SubItemContext
            var ctxProp = newPou.GetType().GetProperty("SubItemContext");
            var ctx = ctxProp?.GetValue(newPou);
            if (ctx == null) throw new InvalidOperationException("New POU has no SubItemContext");
            return ctx;
        }

        // 按 POU 名查找；找不到则新建一个 ST/Main POU（与 EnsureIecPou 默认行为一致）。
        // 用于 write_source：缺则建，已有则覆盖语义。
        private static object EnsureIecPouByName(object iecItem, string pouName)
        {
            if (string.IsNullOrEmpty(pouName)) return EnsureIecPou(iecItem);
            var existing = FindIecPou(iecItem, pouName);
            if (existing != null) return existing;
            return EnsureIecPou(iecItem, pouName);
        }

        private static System.Reflection.Assembly LoadIecAssembly()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetName().Name == "IECProjectProvider.MPPlugIn")
                        return asm;
                }
                catch { }
            }
            return null;
        }

        private static System.Reflection.MethodInfo FindItemViewControlMethod()
        {
            var asm = LoadIecAssembly();
            if (asm == null) return null;
            var t = asm.GetType("Trio.PlugIns.IEC61131_3.Views.ItemViewHibernation");
            if (t == null) return null;
            return t.GetMethod("ItemViewControl", Type.EmptyTypes);
        }

        private static string ReadIecSource(IProjectItem item, string pouName = null)
        {
            var mw = MW;
            if (mw == null) throw new InvalidOperationException("No main window");

            object subItemPou;
            if (string.IsNullOrEmpty(pouName))
                subItemPou = TryGetFirstIecPou(item);
            else
                subItemPou = FindIecPou(item, pouName);
            if (subItemPou == null)
            {
                throw new InvalidOperationException(string.IsNullOrEmpty(pouName)
                    ? "IEC task has no POU"
                    : $"POU '{pouName}' not found in IEC task. Use get_iec_task_detail to list POUs.");
            }

            var asm = LoadIecAssembly();
            var hibType = asm?.GetType("Trio.PlugIns.IEC61131_3.Views.ItemViewHibernation");
            if (hibType == null) throw new InvalidOperationException("ItemViewHibernation type not found");

            object hib = null;
            DispatcherHelper.Invoke(() =>
            {
                try
                {
                    hib = System.Activator.CreateInstance(hibType, subItemPou);
                    var openMethod = hibType.GetMethod("OpenItemWindow", Type.EmptyTypes);
                    openMethod?.Invoke(hib, null);
                }
                catch { }
            });

            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => { }));
            System.Threading.Thread.Sleep(150);

            if (hib == null) throw new InvalidOperationException("Failed to construct ItemViewHibernation");

            object ctrl = null;
            DispatcherHelper.Invoke(() =>
            {
                try
                {
                    var ctrlMethod = hibType.GetMethod("ItemViewControl");
                    ctrl = ctrlMethod?.Invoke(hib, null);
                }
                catch { }
            });
            if (ctrl == null) throw new InvalidOperationException("ItemViewControl() returned null");

            var viewProp = ctrl.GetType().GetProperty("View");
            var view = viewProp?.GetValue(ctrl);
            if (view == null) throw new InvalidOperationException("View is null");

            var textProp = view.GetType().GetProperty("Text");
            if (textProp == null) throw new InvalidOperationException("View has no Text property");
            var body = textProp.GetValue(view) as string ?? "";

            // ST 局部变量存在 POU 自己的 Group 里（不在 .src 代码体）。
            // 前置 Group.Text (VAR...END_VAR) 让 read+write 对称：
            // write_source 会用 STCodeGenerator.SplitCode 把这段拆出来 ImportVariables。
            try
            {
                var program = subItemPou.GetType().GetProperty("Program")?.GetValue(subItemPou);
                var group = program?.GetType().GetProperty("Group")?.GetValue(program);
                var groupText = (group?.GetType().GetProperty("Text")?.GetValue(group) as string ?? "").TrimEnd();
                if (groupText.Length > 0)
                    return groupText + "\n\n" + body;
            }
            catch { }
            return body;
        }

        private static bool WriteIecSource(IProjectItem item, string sourceCode, string pouName = null)
        {
            // write_source 语义：缺则建（与 EnsureIecPouByName 一致）
            object subItemPou = string.IsNullOrEmpty(pouName)
                ? EnsureIecPou(item)
                : EnsureIecPouByName(item, pouName);

            var asm = LoadIecAssembly();
            if (asm == null) throw new InvalidOperationException("IEC assembly not loaded");

            var hibType = asm.GetType("Trio.PlugIns.IEC61131_3.Views.ItemViewHibernation");
            if (hibType == null) throw new InvalidOperationException("ItemViewHibernation type not found");

            // 1. SplitCode: 分离 VAR...END_VAR 和代码体
            // STCodeGenerator 是 internal 类，命名空间是 Models（非 CodeGenerators）
            // 注意：SplitCode 内部用 "VAR\r\n" / "END_VAR\r\n" 索引（VAR_START_TAGS），
            // 仅匹配 Windows CRLF。LLM 输出常是 \n-only，必须先归一化，否则 vars 为空、
            // VAR 块残留在 code 体里 → MP 的"局部变量"页空白且编译失败。
            string normalized = (sourceCode ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            string code = normalized, vars = "";
            var stType = asm.GetTypes().FirstOrDefault(t => t.Name == "STCodeGenerator");
            if (stType != null)
            {
                var split = stType.GetMethod("SplitCode", new[] { typeof(string), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() });
                if (split != null)
                {
                    try
                    {
                        var args = new object[] { normalized, null, null };
                        split.Invoke(null, args);
                        code = (string)args[1]; vars = (string)args[2];
                    }
                    catch { code = normalized; }
                }
            }

            string lastErr = null;
            DispatcherHelper.Invoke(() =>
            {
                try
                {
                    // 2. ImportVariables 到 POU 所属 Group
                    if (!string.IsNullOrEmpty(vars))
                    {
                        var programProp = subItemPou.GetType().GetProperty("Program");
                        var program = programProp?.GetValue(subItemPou);
                        var groupProp = program?.GetType().GetProperty("Group");
                        var group = groupProp?.GetValue(program);
                        if (group != null)
                        {
                            var importMethod = group.GetType().GetMethod("ImportVariables");
                            importMethod?.Invoke(group, new object[] { vars, null, false });
                        }
                    }

                    // 3. 打开编辑器 + 设 Text + SaveContents
                    var hib = System.Activator.CreateInstance(hibType, subItemPou);
                    hibType.GetMethod("OpenItemWindow", Type.EmptyTypes).Invoke(hib, null);

                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(() => { }));
                    System.Threading.Thread.Sleep(150);

                    var ctrl = hibType.GetMethod("ItemViewControl").Invoke(hib, null);
                    if (ctrl == null) throw new InvalidOperationException("ItemViewControl() returned null");
                    var view = ctrl.GetType().GetProperty("View").GetValue(ctrl);
                    if (view == null) throw new InvalidOperationException("View is null");

                    var textProp = view.GetType().GetProperty("Text");
                    if (textProp == null || !textProp.CanWrite) throw new InvalidOperationException("View Text not writable");
                    textProp.SetValue(view, code);

                    var isModifiedProp = view.GetType().GetProperty("IsModified");
                    if (isModifiedProp != null && isModifiedProp.CanWrite)
                        isModifiedProp.SetValue(view, true);

                    var saveMethod = view.GetType().GetMethod("SaveContents");
                    saveMethod?.Invoke(view, new object[] { null });
                }
                catch (Exception ex)
                {
                    lastErr = ex.InnerException?.Message ?? ex.Message;
                }
            });

            if (lastErr != null) throw new InvalidOperationException("IEC write failed: " + lastErr);
            return true;
        }

        private static System.Windows.DependencyObject FindIecTextEditor(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                var t = child.GetType();
                var name = t.Name;
                var baseName = t.BaseType?.Name;
                // IEC ST 编辑器是 STEditor（继承 TextEditorBase → AvalonEdit.TextEditor）
                if (name == "STEditor" || name == "TextEditor" || name == "TextArea"
                    || baseName == "TextEditor" || baseName == "TextEditorBase")
                {
                    var tp = t.GetProperty("Text");
                    if (tp != null) return child;
                }
                var result = FindIecTextEditor(child);
                if (result != null) return result;
            }
            return null;
        }

        private static System.Windows.DependencyObject FindAncestorOfType(System.Windows.DependencyObject element, string typeName)
        {
            var current = element;
            while (current != null)
            {
                if (current.GetType().Name == typeName) return current;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ---- Event log ----
        public static object GetEvents(long sinceTicks)
        {
            _events.EnsureSubscribed();
            var since = new DateTime(sinceTicks, DateTimeKind.Utc);
            var entries = _events.GetSince(since);
            return new
            {
                serverTimeUtc = DateTime.UtcNow.Ticks,
                events = entries
            };
        }
    }

    internal class EventLog
    {
        private const int CAPACITY = 500;
        private readonly ConcurrentQueue<EventEntry> _queue = new ConcurrentQueue<EventEntry>();
        private readonly object _subscribeLock = new object();
        private bool _subscribed;
        private DateTime _lastPurge;

        private IController _controller;
        private ProgramStateChangedHandler _programHandler;
        private OnConnectionStateChange _connHandler;
        private AsyncControllerEventMessageHandler _asyncHandler;
        private EventHandler _ioHandler;
        private EventHandler<COMPILEStateEventArgs> _compileHandler;

        public void EnsureSubscribed()
        {
            if (_subscribed) return;
            lock (_subscribeLock)
            {
                if (_subscribed) return;
                Subscribe();
                _subscribed = true;
            }
        }

        private void Subscribe()
        {
            var ctrl = MPSingletons.Controller;
            if (ctrl == null) return;
            _controller = ctrl;

            _programHandler = (e) => Add("program_state", new
            {
                processId = e?._processID,
                program = e?._programName,
                line = e?._programLine,
                error = e?._programError,
                errorMsg = e?._programErrorMsg,
                state = (e as ProgramStateEvent)?._programState.ToString()
            });
            ctrl.ProgramStateChanged += _programHandler;

            _connHandler = (s, e) => Add("connection_state", new { newState = e?.NewState.ToString(), oldState = e?.OldState.ToString() });
            ctrl.ConnectionStateChanged += _connHandler;

            _asyncHandler = (sender, msg) => Add("async_message", new { message = msg });
            ctrl.AsyncMessage += _asyncHandler;

            _ioHandler = (s, e) => Add("io_changed", new { });
            ctrl.IOLinesChanged += _ioHandler;

            _compileHandler = (s, e) =>
            {
                // V5.6/V5.7 双版本：反射读取编译错误（V5.6=ErrorCode/ErrorLine/ErrorDescription，V5.7=isError+Errors）。
                var errs = CompileApiCompat.ReadCompileEventErrors(e, out bool isError);
                Add("compile_state", new
                {
                    program = e?.ProgramName,
                    isError,
                    errors = errs.Select(x => new { line = x.Line, code = x.Code, text = x.Text }),
                    compiledSize = e?.CompiledSize
                });
            };
            ctrl.CompileStateChanged += _compileHandler;
        }

        public void Add(string kind, object payload)
        {
            var now = DateTime.UtcNow;
            _queue.Enqueue(new EventEntry { timestampUtc = now.Ticks, kind = kind, payload = payload });

            // Periodic purge of old entries (every 10s)
            if ((now - _lastPurge).TotalSeconds > 10)
            {
                _lastPurge = now;
                while (_queue.Count > CAPACITY && _queue.TryDequeue(out _)) { }
            }
        }

        public List<object> GetSince(DateTime since)
        {
            return _queue
                .Where(e => new DateTime(e.timestampUtc, DateTimeKind.Utc) > since)
                .Select(e => (object)new { timestampUtc = e.timestampUtc, kind = e.kind, payload = e.payload })
                .ToList();
        }

        private struct EventEntry
        {
            public long timestampUtc;
            public string kind;
            public object payload;
        }
    }
}
