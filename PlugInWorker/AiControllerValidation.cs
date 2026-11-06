using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Trio.CommunicationsLibrary;
using Trio.SharedLibrary;
using Trio.SharedLibrary.ControllerServices;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- RTBasic 静态语法校验（正则 + 白名单，无需控制器连接）----

        private static HashSet<string> _rtBasicTokenNames;
        private static bool _rtBasicTokenNamesLoaded;
        private static readonly object _rtBasicTokenLock = new object();

        // 预编译正则（_reLineComment 由 AiValidation.cs 定义，此处复用同一分部类成员）
        private static readonly Regex _reDimStatement = new Regex(
            @"^\s*(DIM|LOCAL|GLOBAL)\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _reDimVar = new Regex(
            @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\((\d+)\))?\s*(?:AS\s+(FLOAT|INTEGER|STRING|BOOLEAN))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _reForbiddenVBNet = new Regex(
            @"\b(Function|Sub|Class|Module|Imports|Option\s+Explicit|Try|Catch|Finally|Throw|End\s+(Function|Sub|Class|Module|Try|If|Select))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _reForbiddenDimBare = new Regex(
            @"^\s*(DIM|LOCAL|GLOBAL)\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:\(\s*\d+\s*\))?(?!\s+AS\s+(?:FLOAT|INTEGER|STRING|BOOLEAN))\s*(?:,|$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _reMotionCommand = new Regex(
            @"\b(MOVER_JABS|MOVER_PABS|FORCE_SPEED|BASE)\s*\(([^)]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _reIdentifier = new Regex(
            @"\b([A-Za-z_][A-Za-z0-9_]*)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 从技能文档构建 RTBasic 关键字白名单（替代 TokenTable）。
        /// </summary>
        private static HashSet<string> GetRTBasicTokenNames()
        {
            if (_rtBasicTokenNamesLoaded) return _rtBasicTokenNames;
            lock (_rtBasicTokenLock)
            {
                if (_rtBasicTokenNamesLoaded) return _rtBasicTokenNames;

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    // 控制流
                    "IF", "THEN", "ELSE", "ELSEIF", "ENDIF",
                    "FOR", "TO", "STEP", "NEXT",
                    "WHILE", "WEND",
                    "DO", "LOOP", "UNTIL",
                    "GOSUB", "RETURN", "GOTO",
                    "EXIT",
                    "SELECT", "CASE", "END", "DEFAULT",
                    "ON", "ERROR",

                    // 声明与类型
                    "DIM", "LOCAL", "GLOBAL", "CONST",
                    "AS", "FLOAT", "INTEGER", "STRING", "BOOLEAN",

                    // 逻辑/比较/位运算
                    "AND", "OR", "NOT", "MOD", "XOR", "SHL", "SHR",
                    "TRUE", "FALSE",

                    // I/O 与系统
                    "PRINT", "INPUT", "REM",
                    "VR", "TABLE", "AXIS", "OP", "IN", "OUT",
                    "SERVO", "WDOG", "BASE", "SPEED", "ACCEL", "DECEL",
                    "CREEP", "FE_LIMIT", "UNITS", "DPOS", "MPOS",
                    "RUN", "STOP", "CANCEL", "RAPIDSTOP",
                    "WAIT", "IDLE", "IN_POS", "UNTIL",

                    // 运动指令
                    "MOVE", "MOVEABS", "MOVECIRC", "MOVELINK",
                    "MOVER_JABS", "MOVER_PABS",
                    "FORWARD", "REVERSE",
                    "DATUM", "HOME",
                    "CONNECT", "CAM", "CAMBOX", "GEAR",
                    "FORCE_SPEED", "FORCE_TORQUE",

                    // 数学/字符串/位操作内建函数
                    "ABS", "SQR", "SQRT", "SIN", "COS", "TAN", "ATN", "ATAN",
                    "EXP", "LOG", "LN", "INT", "FIX", "CINT", "SGN",
                    "RND", "RAND", "MAX", "MIN", "LIMIT",
                    "STR", "VAL", "LEN", "MID", "LEFT", "RIGHT", "INSTR",
                    "CHR", "ASC", "HEX", "BIN",
                    "BIT", "BIT_SET", "BIT_CLR", "BIT_TST",

                    // 系统变量/参数
                    "DP", "VP_MODE", "ATYPE", "PSWITCH", "COMPARE",
                    "SERIAL", "COMMS",

                    // 标签/行号相关
                    "LABEL"
                };

                _rtBasicTokenNames = names;
                _rtBasicTokenNamesLoaded = true;
                return names;
            }
        }

        /// <summary>
        /// RTBasic 静态语法校验入口。
        /// 返回错误列表；空列表表示通过。
        /// </summary>
        public static List<string> ValidateRTBasicCode(string code)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(code)) return errors;

            var tokenNames = GetRTBasicTokenNames();
            var cleanCode = _reLineComment.Replace(code, "");

            // 1. 禁止 VB.NET 语法
            foreach (Match m in _reForbiddenVBNet.Matches(cleanCode))
            {
                errors.Add($"禁止的 VB.NET 语法: '{m.Value}' (位置 {m.Index})");
            }

            // 2. DIM/LOCAL/GLOBAL 必须带 AS type
            foreach (Match m in _reForbiddenDimBare.Matches(cleanCode))
            {
                errors.Add($"变量声明缺少 AS 类型: '{m.Value.Trim()}' (行约 {cleanCode.Substring(0, m.Index).Count(c => c == '\n') + 1})");
            }

            // 3. 详细逐行检查 DIM 语法、数组、保留字、运动指令参数
            var lines = cleanCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var controlStack = new Stack<(string keyword, int line)>();
            var dimVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                int lineNo = i + 1;

                // DIM 语句详细解析
                var dimMatch = _reDimStatement.Match(line);
                if (dimMatch.Success)
                {
                    var declPart = dimMatch.Groups[2].Value;
                    foreach (var token in declPart.Split(','))
                    {
                        var varToken = token.Trim();
                        var varMatch = _reDimVar.Match(varToken);
                        if (!varMatch.Success)
                        {
                            errors.Add($"Line {lineNo}: 无效的变量声明 '{varToken}' — 应为 'name AS TYPE' 或 'name AS TYPE(size)'");
                            continue;
                        }
                        string varName = varMatch.Groups[1].Value;
                        string arraySize = varMatch.Groups[2].Value;
                        string typeName = varMatch.Groups[3].Value;

                        if (string.IsNullOrEmpty(typeName))
                        {
                            errors.Add($"Line {lineNo}: 变量 '{varToken}' 缺少 AS 类型说明");
                            continue;
                        }

                        if (tokenNames.Contains(varName))
                        {
                            errors.Add($"Line {lineNo}: 变量名 '{varName}' 与保留字冲突");
                        }
                        dimVars.Add(varName);
                    }
                    continue;
                }

                // 控制流配对检查
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("IF ") && upper.Contains(" THEN"))
                {
                    controlStack.Push(("IF", lineNo));
                }
                else if (upper == "ENDIF")
                {
                    if (controlStack.Count == 0 || controlStack.Peek().keyword != "IF")
                        errors.Add($"Line {lineNo}: ENDIF 缺少匹配的 IF");
                    else
                        controlStack.Pop();
                }
                else if (upper.StartsWith("FOR ") && upper.Contains(" TO "))
                {
                    controlStack.Push(("FOR", lineNo));
                }
                else if (upper.StartsWith("NEXT"))
                {
                    if (controlStack.Count == 0 || controlStack.Peek().keyword != "FOR")
                        errors.Add($"Line {lineNo}: NEXT 缺少匹配的 FOR");
                    else
                        controlStack.Pop();
                }
                else if (upper.StartsWith("WHILE "))
                {
                    controlStack.Push(("WHILE", lineNo));
                }
                else if (upper == "WEND")
                {
                    if (controlStack.Count == 0 || controlStack.Peek().keyword != "WHILE")
                        errors.Add($"Line {lineNo}: WEND 缺少匹配的 WHILE");
                    else
                        controlStack.Pop();
                }
                else if (upper.StartsWith("DO "))
                {
                    controlStack.Push(("DO", lineNo));
                }
                else if (upper == "LOOP")
                {
                    if (controlStack.Count == 0 || (controlStack.Peek().keyword != "DO" && controlStack.Peek().keyword != "DO WHILE" && controlStack.Peek().keyword != "DO UNTIL"))
                        errors.Add($"Line {lineNo}: LOOP 缺少匹配的 DO");
                    else
                        controlStack.Pop();
                }
                else if (upper.StartsWith("GOSUB "))
                {
                    controlStack.Push(("GOSUB", lineNo));
                }
                else if (upper == "RETURN")
                {
                    if (controlStack.Count == 0 || controlStack.Peek().keyword != "GOSUB")
                        errors.Add($"Line {lineNo}: RETURN 缺少匹配的 GOSUB");
                    else
                        controlStack.Pop();
                }

                // 运动指令参数数量检查
                foreach (Match mc in _reMotionCommand.Matches(line))
                {
                    string cmd = mc.Groups[1].Value.ToUpperInvariant();
                    string args = mc.Groups[2].Value;
                    int argCount = args.Split(',').Count(a => !string.IsNullOrWhiteSpace(a));

                    int expected = cmd switch
                    {
                        "MOVER_JABS" => 6,
                        "MOVER_PABS" => 6,
                        "FORCE_SPEED" => 1,
                        "BASE" => -1, // 0 或多个，不强制
                        _ => -1
                    };

                    if (expected > 0 && argCount != expected)
                    {
                        errors.Add($"Line {lineNo}: {cmd} 需要 {expected} 个参数，实际给了 {argCount}");
                    }
                }

                // 保留字冲突检查（非关键字上下文中的标识符）
                foreach (Match id in _reIdentifier.Matches(line))
                {
                    string ident = id.Groups[1].Value;
                    if (ident.Length <= 1) continue;
                    if (char.IsDigit(ident[0])) continue;

                    // 跳过已知控制流/类型/声明关键字
                    string upperIdent = ident.ToUpperInvariant();
                    if (new[] { "IF", "THEN", "ELSE", "ELSEIF", "ENDIF", "FOR", "TO", "STEP", "NEXT",
                                "WHILE", "WEND", "DO", "LOOP", "UNTIL", "GOSUB", "RETURN", "GOTO",
                                "EXIT", "SELECT", "CASE", "END", "DEFAULT", "ON", "ERROR",
                                "DIM", "LOCAL", "GLOBAL", "CONST", "AS", "FLOAT", "INTEGER", "STRING", "BOOLEAN",
                                "AND", "OR", "NOT", "MOD", "XOR", "SHL", "SHR", "TRUE", "FALSE",
                                "PRINT", "INPUT", "REM", "WAIT", "IDLE", "IN_POS", "UNTIL",
                                "LABEL" }.Contains(upperIdent))
                        continue;

                    // 已声明的变量名跳过
                    if (dimVars.Contains(ident)) continue;

                    // 若是保留字但未在白名单中允许作标识符，报错
                    if (tokenNames.Contains(upperIdent))
                    {
                        errors.Add($"Line {lineNo}: 标识符 '{ident}' 与系统保留字冲突");
                    }
                }
            }

            // 栈未清空 = 结构未闭合
            while (controlStack.Count > 0)
            {
                var (kw, ln) = controlStack.Pop();
                errors.Add($"Line {ln}: {kw} 缺少对应的结束语句");
            }

            return errors;
        }

        // ========== 兼容原有接口签名（保留供旧调用点） ==========

        /// <summary>
        /// 兼容旧接口：基于 TokenTable 校验（现直接委托给 RTBasic 静态校验）。
        /// </summary>
        private static List<string> ValidateWithTokenTable(string code)
        {
            // 旧逻辑依赖控制器连接；新版本改为离线静态校验
            return ValidateRTBasicCode(code);
        }

        /// <summary>
        /// 兼容旧接口：控制器逐行验证（RTSys 无此 API，返回空列表）。
        /// </summary>
        private static List<string> ValidateByController(string code)
        {
            return new List<string>();
        }

        /// <summary>
        /// RTSys 无逐行验证 API，固定返回 false。
        /// </summary>
        internal static bool ShouldUseControllerValidation()
        {
            return false;
        }
    }
}