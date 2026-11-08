using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ============ TrioBASIC 代码校验（标识符白名单 + 签名语法）============
        // 双层防线：(1) 提取代码里所有"系统标识符候选"，凡是不在 TrioBASIC 索引/HTML
        // 文件名/关键字白名单里的，一律拒绝写入；(2) 解析 lookup_command 索引的 desc
        // 字段提取签名，校验调用点的参数数量 / 赋值目标合法性。
        // 设计取舍：不依赖小模型，纯 C# 静态扫描，零 API 开销，确定性 100%。

        private static HashSet<string> _triobasicIds;                       // 所有合法标识符（命令/函数/参数/关键字）
        private static Dictionary<string, CommandSignature> _signatures;    // 签名表（key 大写）
        private static volatile bool _validationIndexBuilt;
        private static readonly object _validationLock = new object();

        // 兜底关键字 — 控制流 / 类型 / 运算符，部分没有 HTML 文件
        private static readonly HashSet<string> _builtinKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if","then","else","elseif","endif","for","next","to","step","while","wend",
            "do","loop","until","repeat","gosub","return","goto","exit",
            "dim","const","global","local","integer","float","string","as",
            "print","input","and","or","not","mod","xor","shl","shr",
            "true","false","rem","on","select","case","end","using","with",
            "default",     // SELECT CASE DEFAULT
            "waits",       // 等待同步（区别于 WAIT UNTIL，无 HTML 单独条目）
            "until_io",    // WAIT UNTIL_IO 等
            // 常见用户变量前缀（避免假阳性）
            "i","j","k","x","y","z","t","n","cnt","idx","temp","tmp",
        };

        private class CommandSignature
        {
            public string Name;
            public int MinArgs = 0;
            public int? MaxArgs = null;     // null = 不限
            public bool ReturnsValue = false;
            public bool IsAssignable = false;
            public string RawSignature = "";
        }

        // desc 提取签名 — 三种模式：
        //   "value = NAME(args) ..."   函数返回值
        //   "NAME(args) = value ..."   可赋值函数（如 AIN_CONFIG）
        //   "NAME(args) ..."           命令/函数
        private static readonly Regex _reDescSigWithValue =
            new Regex(@"^\s*\w+\s*=\s*(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex _reDescSigCall =
            new Regex(@"^\s*(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex _reDescSigAssign =
            new Regex(@"^\s*(\w+)\s*\([^)]*\)\s*=", RegexOptions.Compiled);

        private static void EnsureValidationIndex()
        {
            if (_validationIndexBuilt) return;
            lock (_validationLock)
            {
                if (_validationIndexBuilt) return;
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sigs = new Dictionary<string, CommandSignature>(StringComparer.OrdinalIgnoreCase);

                // 1. 从 index.json 加载 RTBasic 条目（仅 rtbasic 目录）
                foreach (var entry in LoadIndex())
                {
                    if (string.IsNullOrEmpty(entry?.Name)) continue;
                    if (!string.Equals(entry.Lib, "rtbasic", StringComparison.OrdinalIgnoreCase)) continue;
                    ids.Add(entry.Name);
                    var sig = ParseSignature(entry.Name, entry.Sig ?? entry.Desc ?? "");
                    if (sig != null) sigs[entry.Name] = sig;
                }

                // 2. 扫描 skills/rtbasic/{zh,en} 下所有 .html 文件名（兜底 index.json 不全的情况，如关键字 IF/FOR/DIM）
                //    注意：仅扫 rtbasic 目录。IEC/PLCopen 的库（AO-printf 之类）不能混进 RTBasic 白名单，
                //    否则 AI 写 printf() 这种 IEC 函数会被误判为合法 RTBasic。
                foreach (var lang in new[] { "zh", "en" })
                {
                    var rtbasicDir = Path.Combine(SkillsDir, "rtbasic", lang);
                    if (!Directory.Exists(rtbasicDir)) continue;
                    foreach (var html in Directory.GetFiles(rtbasicDir, "*.html"))
                    {
                        var name = Path.GetFileNameWithoutExtension(html);
                        // 仅纳入合法 identifier 名（字母数字下划线，>=2 字符）
                        if (name.Length >= 2 && IsIdentifierLike(name))
                            ids.Add(name);
                    }
                }

                _triobasicIds = ids;
                _signatures = sigs;
                _validationIndexBuilt = true;
            }
        }

        private static bool IsIdentifierLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_') return false;
            foreach (var c in s)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        private static CommandSignature ParseSignature(string name, string desc)
        {
            var sig = new CommandSignature { Name = name };
            if (string.IsNullOrWhiteSpace(desc)) return sig;

            // regex 都用 ^ 锚定开头，直接对原 desc 调用即可。
            // 之前用 split('.') 取第一句反而把 `ANYBUS(..., parameters...)` 里的 `...` 误切断。

            // Pattern 1: value = NAME(args)
            var m = _reDescSigWithValue.Match(desc);
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                sig.ReturnsValue = true;
                SetArgCount(sig, m.Groups[2].Value);
                sig.RawSignature = ("value = " + name + "(" + m.Groups[2].Value + ")").Trim();
                return sig;
            }

            // Pattern 2: NAME(args) = value
            m = _reDescSigAssign.Match(desc);
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                sig.IsAssignable = true;
                var m2 = _reDescSigCall.Match(desc);
                if (m2.Success) SetArgCount(sig, m2.Groups[2].Value);
                sig.RawSignature = (name + "(" + (m2?.Groups[2].Value ?? "") + ") = value").Trim();
                return sig;
            }

            // Pattern 3: NAME(args)
            m = _reDescSigCall.Match(desc);
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                SetArgCount(sig, m.Groups[2].Value);
                sig.RawSignature = (name + "(" + m.Groups[2].Value + ")").Trim();
                return sig;
            }

            return sig;
        }

        private static void SetArgCount(CommandSignature sig, string argsStr)
        {
            if (string.IsNullOrWhiteSpace(argsStr))
            {
                sig.MinArgs = 0;
                sig.MaxArgs = 0;
                return;
            }
            // TrioBASIC 文档的可选参数有两种形式：
            //   "axis0[, axis1[, ...]]"   方括号
            //   "axis<, second axis>..."  尖括号（如 BASE(axis<,second axis>...)）
            // 第一个 '[' 或 '<' 之前的是必填，之后的全部可选。
            int bracketIdx = argsStr.IndexOf('[');
            int angleIdx = argsStr.IndexOf('<');
            int cut = -1;
            if (bracketIdx >= 0 && angleIdx >= 0) cut = Math.Min(bracketIdx, angleIdx);
            else if (bracketIdx >= 0) cut = bracketIdx;
            else if (angleIdx >= 0) cut = angleIdx;

            string requiredPart = cut >= 0 ? argsStr.Substring(0, cut) : argsStr;
            bool hasOptional = cut >= 0 || argsStr.Contains("...");

            var parts = requiredPart.Split(',');
            int required = 0;
            foreach (var p in parts)
            {
                var t = p.Trim().TrimEnd('[');
                if (string.IsNullOrEmpty(t)) continue;
                if (t == "..." || t == "…") continue;
                required++;
            }
            sig.MinArgs = required;
            sig.MaxArgs = hasOptional ? (int?)null : required;
        }

        // 已知「只读」函数 — 不能作为赋值左侧。系统变量（VR/TABLE/MPOS/DPOS 等）默认双向，不在本表。
        private static readonly HashSet<string> _knownReadOnly =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ABS","SIN","COS","TAN","ASIN","ACOS","ATAN","ATAN2",
                "SQRT","SQR","EXP","LOG","LN","POW","POWER",
                "INT","FLOAT","FRAC","ROUND","FIX","SIGN",
                "MAX","MIN","MOD",
                "RND","RANDOM",
                "INSTR","LEFT","RIGHT","MID","LEN","UPPER","LOWER","CHR","ASC","VAL","STR",
                "GET_PARM","AXIS_STATE","GET_VR_SCALE","GET_TABLE_SCALE",
            };

        // 校验代码 — 返回错误列表（空 = 通过）
        // Phase 1（全大写标识符白名单）已移除：误杀代价（AI 死循环）远大于漏杀（编译器兜底）。
        // 仅保留 Phase 2：函数调用签名校验，拦截 Sleep()/printf() 等幻觉命令。
        private static readonly Regex _reLineComment =
            new Regex(@"'[^\r\n]*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _reFuncCallSite =
            new Regex(@"\b([A-Za-z_][A-Za-z_0-9]*)\s*\(([^)]*)\)", RegexOptions.Compiled);
        // 用户自定义 SUB 声明（行首 SUB name / GLOBAL SUB name，可带参数括号）；调用点不能被当作未知命令。
        // 必须按行匹配：用 \s+ 会跨过换行，把 "END SUB\nSUB Next" 的第二个 SUB 关键字当成名字吞掉。
        private static readonly Regex _reSubDef =
            new Regex(@"^[ \t]*(?:GLOBAL[ \t]+)?SUB[ \t]+([A-Za-z_][A-Za-z_0-9]*)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        public static object ValidateTrioBasicCodePublic(string code)
        {
            var errs = ValidateTrioBasicCode(code);
            return new { ok = errs.Count == 0, errors = errs };
        }

        /// <summary>与 validate_basic 相同的权威校验，供写入前拦截复用，避免两套规则互相矛盾。</summary>
        public static List<string> ValidateTrioBasicCodeErrors(string code)
        {
            return ValidateTrioBasicCode(code);
        }

        private static List<string> ValidateTrioBasicCode(string code)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(code)) return errors;
            try { EnsureValidationIndex(); }
            catch { return errors; }

            var clean = _reLineComment.Replace(code, "");
            var lines = clean.Split('\n');
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>();
            var userSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _reSubDef.Matches(clean))
                userSubs.Add(m.Groups[1].Value);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (Match m in _reFuncCallSite.Matches(line))
                {
                    var funcName = m.Groups[1].Value;
                    var argsStr = m.Groups[2].Value;

                    // 用户自定义 SUB：调用签名由用户代码决定，不按参考库校验
                    if (userSubs.Contains(funcName)) continue;

                    // 已知函数 → 走签名校验
                    if (_signatures != null && _signatures.TryGetValue(funcName, out var sig))
                    {
                        int argCount = string.IsNullOrWhiteSpace(argsStr) ? 0
                            : argsStr.Split(',').Length;

                        if (argCount < sig.MinArgs)
                        {
                            var k = funcName.ToUpper() + "@L" + (i + 1) + "few";
                            if (seen.Add(k))
                                errors.Add(string.Format("Line {0}: {1} called with {2} arg(s), but signature requires ≥{3}: \"{4}\"",
                                    i + 1, funcName, argCount, sig.MinArgs, sig.RawSignature));
                        }
                        else if (sig.MaxArgs.HasValue && argCount > sig.MaxArgs.Value)
                        {
                            var k = funcName.ToUpper() + "@L" + (i + 1) + "many";
                            if (seen.Add(k))
                                errors.Add(string.Format("Line {0}: {1} called with {2} arg(s), but signature accepts ≤{3}: \"{4}\"",
                                    i + 1, funcName, argCount, sig.MaxArgs, sig.RawSignature));
                        }
                        else
                        {
                            var afterIdx = m.Index + m.Length;
                            var after = afterIdx < line.Length ? line.Substring(afterIdx).TrimStart() : "";
                            if (after.StartsWith("=") && _knownReadOnly.Contains(funcName))
                            {
                                var k = funcName.ToUpper() + "@L" + (i + 1) + "assign";
                                if (seen.Add(k))
                                    errors.Add(string.Format("Line {0}: cannot assign to {1}(...) — it's a read-only function, use as expression",
                                        i + 1, funcName));
                            }
                        }
                        continue;
                    }

                    // 未知调用：Name(...) 不在 _triobasicIds → 幻觉命令
                    if (_triobasicIds == null || !_triobasicIds.Contains(funcName))
                    {
                        // 控制流关键字（if/elseif/else/endif/while/wend/for/next/do/loop/...）没有单独
                        // HTML 文件，不在 _triobasicIds；ELSEIF (expr) 等会被正则当成函数调用误报，按
                        // _builtinKeywords 跳过（_builtinKeywords 含全部控制流 + 类型 + 运算符关键字）
                        if (_builtinKeywords.Contains(funcName))
                            continue;
                        var k = funcName.ToUpper() + "@unknown";
                        if (seen.Add(k))
                        {
                            unknown.Add(funcName.ToUpper());
                            if (!errors.Exists(e => e.StartsWith("Identifiers not in TrioBASIC reference")))
                                errors.Add("Identifiers not in TrioBASIC reference: " +
                                    string.Join(", ", unknown.OrderBy(x => x)));
                        }
                    }
                }
            }

            return errors;
        }
    }
}
