using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal static class RTSysHandlers
    {
        private static ZMotionHandle _handle;
        private static readonly object _handleLock = new object();
        private static string _cachedIp;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        // ========== 连接管理 ==========

        private static async Task<ZMotionHandle> GetHandleAsync()
        {
            if (_handle != null && !_handle.IsInvalid)
                return _handle;

            lock (_handleLock)
            {
                if (_handle != null && !_handle.IsInvalid)
                    return _handle;

                string ip = GetRTSysIpFromConfig();
                if (string.IsNullOrEmpty(ip))
                    throw new InvalidOperationException("RTSysIP not configured. Set it in AI settings.");

                _cachedIp = ip;
            }

            _handle = await ZMotionHandle.ConnectAsync(_cachedIp);
            return _handle;
        }

        private static string GetRTSysIpFromConfig()
        {
            try
            {
                if (File.Exists(AiService.ConfigPath))
                {
                    var text = File.ReadAllText(AiService.ConfigPath);
                    var cfg = _json.Deserialize<Dictionary<string, object>>(text);
                    object val;
                    if (cfg.TryGetValue("RTSysIP", out val) && val != null)
                        return val.ToString().Trim();
                }
            }
            catch { }
            return null;
        }

        public static void SetRTSysIp(string ip)
        {
            try
            {
                var cfg = new Dictionary<string, object>();
                if (File.Exists(AiService.ConfigPath))
                {
                    var text = File.ReadAllText(AiService.ConfigPath);
                    cfg = _json.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
                }
                cfg["RTSysIP"] = ip ?? "";
                File.WriteAllText(AiService.ConfigPath, _json.Serialize(cfg));
                _cachedIp = ip;

                lock (_handleLock)
                {
                    if (_handle != null && !_handle.IsInvalid)
                    {
                        _handle.DisposeSafe();
                        _handle = null;
                    }
                }
            }
            catch { }
        }

        // ========== 统一错误包装 ==========

        private static object Error(string msg) => new { success = false, error = msg };

        private static async Task<object> SafeAsync(Func<Task<object>> func)
        {
            try
            {
                return await Task.Run(func);
            }
            catch (ZMotionException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private static object Safe(Func<object> func)
        {
            try
            {
                return Task.Run(func).GetAwaiter().GetResult();
            }
            catch (ZMotionException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private static object Safe(Func<Task<object>> func)
        {
            try
            {
                return Task.Run(func).GetAwaiter().GetResult();
            }
            catch (ZMotionException ex)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        // ========== 核心接口：保持与原 Handlers 相同签名 ==========

        // ---- Status ----
        public static object GetStatus()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var status = await handle.GetFullStatusAsync();
                return new
                {
                    connected = true,
                    controller = "RTSys",
                    firmware = status.FirmwareVersion?.ToString() ?? "unknown",
                    project = "RTSys Controller",
                    ip = _cachedIp,
                    axisCount = status.AxisCount,
                    state = status.State
                };
            });
        }

        // ---- Programs ----
        public static object ListPrograms()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var programs = await handle.ListProgramsAsync();
                var items = programs.Select(p => new
                {
                    name = p,
                    type = "basic",
                    dialect = "rtbasic",
                    isEditable = true,
                    isExecutable = true,
                    storage = "controller"
                }).ToList();
                return new { items };
            });
        }

        public static object ReadSource(string name, Dictionary<string, object> body = null)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string source = await handle.ReadProgramAsync(name);
                var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                int totalLines = lines.Length;
                int? startOpt = body != null ? GetInt(body, "startLine") : null;
                int? endOpt = body != null ? GetInt(body, "endLine") : null;

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
                        type = "basic",
                        dialect = "rtbasic"
                    };
                }

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
                        hint = $"Large file ({totalLines} lines, {source.Length} chars). Use startLine={end + 1} to read next chunk.",
                        type = "basic",
                        dialect = "rtbasic"
                    };
                }

                return new { sourceCode = source, totalLines, type = "basic", dialect = "rtbasic" };
            });
        }

        public static object WriteSource(string name, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var sourceCode = GetString(body, "sourceCode") ?? "";
                var validationErrors = ValidateRtBasicCode(sourceCode);
                if (validationErrors.Count > 0)
                    return new { success = false, error = "BLOCKED by RTBasic validation:\n  " + string.Join("\n  ", validationErrors) };

                var handle = await GetHandleAsync();
                var (success, error) = await handle.CompileAndDownloadAsync(sourceCode, name);
                return success ? new { success = true } : new { success = false, error = error };
            });
        }

        public static object CompileProgram(string name)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string source = await handle.ReadProgramAsync(name);
                var validationErrors = ValidateRtBasicCode(source);
                if (validationErrors.Count > 0)
                    return new { success = false, errors = validationErrors.Select(e => new { line = 0, text = e }) };

                var (success, error) = await handle.CompileAndDownloadAsync(source, name);
                if (!success)
                    return new { success = false, error = error, errors = new[] { new { line = 0, text = error } } };
                return new { success = true };
            });
        }

        public static object RunProgram(string name, Dictionary<string, object> body = null)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                await handle.RunProgramAsync(name);
                return new { success = true };
            });
        }

        public static object StopProgram(string name, Dictionary<string, object> body = null)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                await handle.StopProgramAsync();
                return new { success = true };
            });
        }

        // ---- VR ----
        public static object ReadVR(int address, int count)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var values = await handle.ReadVRAsync(address, count);
                return new { values = values.Select(v => (double?)v).ToList() };
            });
        }

        public static object WriteVR(int address, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                double value = GetDouble(body, "value");
                await handle.WriteVRAsync(address, new[] { (int)value });
                return new { success = true };
            });
        }

        // ---- TABLE ----
        public static object ReadTable(int address, int count)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var values = await handle.ReadTableAsync(address, count);
                return new { values = values.Select(v => (double?)v).ToList() };
            });
        }

        public static object WriteTable(int address, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var vals = GetArray(body, "values");
                if (vals == null || vals.Length == 0) return Error("values array is required");
                var ints = vals.Select(v => Convert.ToInt32(v)).ToArray();
                await handle.WriteTableAsync(address, ints);
                return new { success = true };
            });
        }

        // ---- Axes ----
        public static object GetAxisStates()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var states = await handle.GetAllAxisStatesAsync();
                var result = states.Select((s, i) => new
                {
                    index = i,
                    position = s.position,
                    targetPos = s.targetPos,
                    velocity = s.velocity,
                    error = s.error,
                    status = $"0x{s.status:X}",
                    servoStatus = s.servoStatus,
                    homeStatus = s.homeStatus,
                    limitStatus = s.limitStatus,
                    alarmCode = s.alarmCode
                }).ToList();
                return new { axes = result };
            });
        }

        public static object ListAxes()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                byte count = handle.GetAxisCount();
                var result = new List<object>();
                for (byte i = 0; i < count; i++)
                {
                    result.Add(new { index = i, name = $"Axis{i}", typeName = "RTSys", slot = 0 });
                }
                return new { axes = result };
            });
        }

        public static object GetAxisDetail(int index)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var state = await handle.GetAxisStateAsync((byte)index);
                return new
                {
                    index = index,
                    name = $"Axis{index}",
                    type = "RTSys",
                    position = state.position,
                    targetPos = state.targetPos,
                    velocity = state.velocity,
                    error = state.error,
                    status = $"0x{state.status:X}",
                    servoStatus = state.servoStatus,
                    homeStatus = state.homeStatus,
                    limitStatus = state.limitStatus,
                    alarmCode = state.alarmCode,
                    torque = state.torque,
                    temperature = state.temperature
                };
            });
        }

        // ---- Digital IO ----
        public static object GetIn(int index)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                bool value = await handle.GetInAsync((short)index);
                return new { index = index, value = value };
            });
        }

        public static object SetOutput(int index, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                bool value = GetBool(body, "value");
                await handle.SetOutputAsync((short)index, value);
                return new { success = true };
            });
        }

        public static object GetOut(int index)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                // ZMotionAPI 没有直接读输出的独立函数，用 GetInAll/GetOutAll 替代
                var all = await handle.GetOutAllAsync();
                int byteIdx = index / 8;
                int bitIdx = index % 8;
                bool value = byteIdx < all.Length && (all[byteIdx] & (1 << bitIdx)) != 0;
                return new { index = index, value = value };
            });
        }

        public static object GetInAll()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var data = await handle.GetInAllAsync();
                return new { data = data };
            });
        }

        public static object GetOutAll()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var data = await handle.GetOutAllAsync();
                return new { data = data };
            });
        }

        public static object SetOutAll(Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var vals = GetArray(body, "values");
                if (vals == null) return Error("values array required");
                var bytes = vals.Select(v => Convert.ToByte(v)).ToArray();
                await handle.SetOutAllAsync(bytes);
                return new { success = true };
            });
        }

        // ---- Register ----
        public static object RegisterRead(int regType, int address, int count)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var data = await handle.RegisterReadAsync(regType, address, count);
                return new { data = data };
            });
        }

        public static object RegisterWrite(int regType, int address, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var vals = GetArray(body, "values");
                if (vals == null) return Error("values array required");
                var bytes = vals.Select(v => Convert.ToByte(v)).ToArray();
                await handle.RegisterWriteAsync(regType, address, bytes);
                return new { success = true };
            });
        }

        // ---- Direct Command ----
        public static object DirectCommand(string cmd)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string response = await handle.DirectCommandAsync(cmd);
                return response;
            });
        }

        // ---- Search Code (local cache only - not applicable for RTSys, return empty) ----
        public static object SearchCode(string query)
        {
            return new { query = query, matchCount = 0, results = new object[0] };
        }

        // ---- Project Management (stubs for compatibility) ----
        public static object OpenProject(Dictionary<string, object> body)
        {
            return Error("RTSys does not support project files. Use controller programs directly.");
        }

        public static object SaveProject()
        {
            return Error("RTSys does not support project files.");
        }

        public static object ListProjectItems()
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                var programs = await handle.ListProgramsAsync();
                var items = programs.Select(p => new
                {
                    name = p,
                    typeName = "Program",
                    group = "Controller",
                    itemType = "basic"
                }).ToList();
                return new { items = items, fileName = "RTSys Controller" };
            });
        }

        // ---- Dialect ----
        public static object GetProgramDialect(string name)
        {
            return "rtbasic";
        }

        // ---- IEC (not supported) ----
        public static object ReadIecVariables(string name, string scope)
        {
            return Error("IEC variables not supported on RTSys");
        }

        public static object WriteIecVariables(string name, string scope, string text)
        {
            return Error("IEC variables not supported on RTSys");
        }

        public static object GetIecTaskDetail(string name)
        {
            return Error("IEC tasks not supported on RTSys");
        }

        // ---- Motion/Drive/EtherCAT (delegate to ZMotionExtensions) ----
        public static object ReadDriveParam(int axis, int address, int numFractionDigits = 4)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                int value = await handle.EcatSdoReadAsync(0, (short)axis, address, 0, 7); // UINT32
                return new { axis = axis, address = $"0x{address:X}", rawValue = value.ToString(), numericValue = value };
            });
        }

        public static object WriteDriveParam(int axis, int address, Dictionary<string, object> body)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string valueStr = GetString(body, "value") ?? "";
                int value = Convert.ToInt32(valueStr);
                await handle.EcatSdoWriteAsync(0, (short)axis, address, 0, 7, value);
                return new { success = true };
            });
        }

        public static object ScanEtherCAT(int slot)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                // 简化：仅返回占位
                return new { slot = slot, devices = new object[0], message = "Use ZMotionExtensions for full EtherCAT scan" };
            });
        }

        public static object EtherCATReadSDO(int slot, uint position, uint index, uint subindex, string typeStr)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                int dataType = ParseCODataType(typeStr);
                int value = await handle.EcatSdoReadAsync((short)slot, (short)position, (int)index, (int)subindex, dataType);
                return new { slot = slot, position = position, index = $"0x{index:X}", subindex = subindex, type = typeStr, value = value };
            });
        }

        public static object EtherCATWriteSDO(int slot, uint position, uint index, uint subindex, string typeStr, object value)
        {
            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                int dataType = ParseCODataType(typeStr);
                int intValue = Convert.ToInt32(value);
                await handle.EcatSdoWriteAsync((short)slot, (short)position, (int)index, (int)subindex, dataType, intValue);
                return new { success = true };
            });
        }

        // ---- Helpers ----

        private static List<string> ValidateRtBasicCode(string code)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(code)) return errors;

            var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // 关键字白名单
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MOVE", "MOVEABS", "MOVEREL", "MOVEVEL", "JOG", "HOME",
                "SPEED", "ACCEL", "DECEL", "JERK",
                "IF", "THEN", "ELSE", "ENDIF",
                "FOR", "TO", "STEP", "NEXT",
                "WHILE", "WEND",
                "REPEAT", "UNTIL",
                "GOSUB", "RETURN", "GOTO",
                "WAIT", "DELAY", "PAUSE",
                "VR", "TABLE", "IN", "OUT", "OP",
                "AXIS", "AXES",
                "SERVO", "ENABLE", "DISABLE",
                "ERROR", "ONERROR", "OFFERROR",
                "DIM", "AS", "CONST",
                "PRINT", "INPUT",
                "REM", "'",
                "TRUE", "FALSE", "ON", "OFF"
            };

            // 危险模式检测
            var dangerousPatterns = new[]
            {
                new { Pattern = @"\b(DELETE|FORMAT|ERASE|CLEAR)\s+(FLASH|MEMORY|ALL)\b", Msg = "Dangerous flash/memory operation" },
                new { Pattern = @"\bZMC_\w+", Msg = "Direct DLL calls not allowed in RTBasic" },
                new { Pattern = @"\bASM\b|\bASSEMBLY\b", Msg = "Inline assembly not allowed" }
            };

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("'") || line.StartsWith("REM")) continue;

                // 检查危险模式
                foreach (var dp in dangerousPatterns)
                {
                    if (Regex.IsMatch(line, dp.Pattern, RegexOptions.IgnoreCase))
                        errors.Add($"Line {i + 1}: {dp.Msg}");
                }

                // 简单语法检查：未闭合的 IF/FOR/WHILE
                // 这里仅做基础检查，完整语法由控制器编译器验证
            }

            return errors;
        }

        private static int ParseCODataType(string s)
        {
            if (string.IsNullOrEmpty(s)) return 7; // UINT16
            switch (s.ToLowerInvariant())
            {
                case "bool": return 1;
                case "int8": return 2;
                case "uint8": return 3;
                case "int16": return 4;
                case "uint16": return 5;
                case "int32": return 6;
                case "uint32": return 7;
                case "real32": case "float": return 8;
                case "real64": case "double": return 9;
                case "string": return 10;
                default: return 7;
            }
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null) return val.ToString();
            return null;
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
                return Convert.ToDouble(val);
            return 0;
        }

        private static int? GetInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result))
                    return result;
            }
            return null;
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
            {
                if (val is bool b1) return b1;
                if (val is double dbl) return dbl != 0;
                if (val is int i) return i != 0;
                bool b2;
                if (bool.TryParse(val.ToString(), out b2)) return b2;
            }
            return false;
        }

        private static object[] GetArray(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true)
            {
                if (val is string) return null;
                if (val is System.Collections.IEnumerable en)
                    return en.Cast<object>().ToArray();
            }
            return null;
        }

        private static uint? GetUInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d?.TryGetValue(key, out val) == true && val != null)
            {
                uint result;
                if (uint.TryParse(val.ToString(), out result))
                    return result;
            }
            return null;
        }

        public static object PatchSource(string name, Dictionary<string, object> body)
        {
            var ops = GetArray(body, "operations");
            if (ops == null || ops.Length == 0) return Error("operations is required");

            return Safe(async () =>
            {
                var handle = await GetHandleAsync();
                string currentSource = await handle.ReadProgramAsync(name);
                currentSource = currentSource.Replace("\r\n", "\n").Replace("\r", "\n");

                var editOps = ops.Select(op => op as Dictionary<string, object>)
                    .Where(d => d != null)
                    .Select(d => new
                    {
                        oldString = GetString(d, "old_string") ?? GetString(d, "oldContent") ?? "",
                        newString = GetString(d, "new_string") ?? GetString(d, "content") ?? ""
                    })
                    .ToList();

                foreach (var op in editOps)
                {
                    if (string.IsNullOrEmpty(op.oldString))
                    {
                        currentSource = currentSource.TrimEnd('\n') + "\n" + op.newString + "\n";
                        continue;
                    }
                    int pos = currentSource.IndexOf(op.oldString);
                    if (pos < 0)
                        pos = currentSource.IndexOf(op.oldString, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        return Error("old_string not found: " + op.oldString.Substring(0, Math.Min(op.oldString.Length, 40)));
                    currentSource = currentSource.Substring(0, pos) + op.newString + currentSource.Substring(pos + op.oldString.Length);
                }

                var (success, error) = await handle.CompileAndDownloadAsync(currentSource, name);
                if (!success)
                    return Error(error ?? "CompileAndDownload failed");
                return new { success = true };
            });
        }

        // ---- AI tools compatibility stubs (not supported on RTSys) ----
        public static object Compile(string name)
        {
            return CompileProgram(name);
        }

        public static object CopyProgram(string name, Dictionary<string, object> body)
        {
            return Error("CopyProgram not supported on RTSys (no project files).");
        }

        public static object CreateProgram(Dictionary<string, object> body)
        {
            return Error("CreateProgram not supported on RTSys (no project files). Use WriteSource directly.");
        }

        public static object DeleteProgram(string name)
        {
            return Error("DeleteProgram not supported on RTSys (no project files).");
        }

        public static object RenameProgram(string name, Dictionary<string, object> body)
        {
            return Error("RenameProgram not supported on RTSys (no project files).");
        }

        public static object Download(string name)
        {
            return Error("Download not supported on RTSys (no project files).");
        }

        public static object Upload(string name)
        {
            return Error("Upload not supported on RTSys (no project files).");
        }

        public static object OpenProgram(string name)
        {
            return Error("OpenProgram not supported on RTSys (no editor integration).");
        }

        public static object GetProgramProcess(string name)
        {
            return Error("GetProgramProcess not supported on RTSys.");
        }

        public static object SetProgramProcess(string name, Dictionary<string, object> body)
        {
            return Error("SetProgramProcess not supported on RTSys.");
        }

        public static object ListBreakpoints(string name)
        {
            return Error("Breakpoints not supported on RTSys.");
        }

        public static object SetBreakpoint(string name, Dictionary<string, object> body)
        {
            return Error("Breakpoints not supported on RTSys.");
        }

        public static object ClearAllBreakpoints(string name)
        {
            return Error("Breakpoints not supported on RTSys.");
        }

        public static object ListProcesses()
        {
            return Error("Process listing not supported on RTSys.");
        }

        public static object GetProcessVariable(int pid, string program, string variable)
        {
            return Error("Process variables not supported on RTSys.");
        }

        public static object GetEvents(long sinceTicks)
        {
            return Error("Event log not supported on RTSys.");
        }

        public static object ListDescriptors()
        {
            return Error("Program type descriptors not supported on RTSys.");
        }

        public static object ListAttachedPlugins()
        {
            return new { plugins = new object[0] };
        }

        public static object ListRecipes()
        {
            return Error("Recipes not supported on RTSys.");
        }

        public static object ListRobots()
        {
            return Error("Robots not supported on RTSys.");
        }

        public static object ListRemoteDevices()
        {
            return Error("Remote devices not supported on RTSys.");
        }

        public static object ListAlarms()
        {
            return Error("Alarms not supported on RTSys.");
        }

        public static object ListDigitalIO()
        {
            return Error("Digital IO not supported on RTSys (no direct hardware access).");
        }

        public static object ReadDigitalIO(int index)
        {
            return Error("Digital IO not supported on RTSys.");
        }

        public static object WriteDigitalIO(int index, Dictionary<string, object> body)
        {
            return Error("Digital IO not supported on RTSys.");
        }

        public static object ListAnalogueIO()
        {
            return Error("Analogue IO not supported on RTSys.");
        }

        public static object ReadAnalogueIO(int index)
        {
            return Error("Analogue IO not supported on RTSys.");
        }

        public static object WriteAnalogueIO(int index, Dictionary<string, object> body)
        {
            return Error("Analogue IO not supported on RTSys.");
        }

        public static object ScanMsBus(int slot)
        {
            return Error("MSBus scan not supported on RTSys.");
        }

        public static object EtherCATWriteSDOFromDict(Dictionary<string, object> body)
        {
            // Expecting body with: slot, position, index, subindex, type, value
            if (body == null) return Error("body required");
            int slot = GetInt(body, "slot") ?? 0;
            uint position = GetUInt(body, "position") ?? 0;
            uint index = GetUInt(body, "index") ?? 0;
            uint subindex = GetUInt(body, "subindex") ?? 0;
            string type = GetString(body, "type") ?? "uint16";
            object value = body.ContainsKey("value") ? body["value"] : null;
            if (value == null) return Error("value required");
            return EtherCATWriteSDO(slot, position, index, subindex, type, value);
        }

        public static object ReadSysVar(string name)
        {
            return Error("System variables not directly accessible on RTSys. Use VR/TABLE.");
        }

        public static object WriteSysVar(string name, Dictionary<string, object> body)
        {
            return Error("System variables not directly writable on RTSys.");
        }
    }
}