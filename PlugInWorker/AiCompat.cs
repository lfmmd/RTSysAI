using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Trio.SharedLibrary;

namespace RTSys.PlugIn
{
    // V5.6 / V5.7 编译 API 双版本兼容（反射）。
    // V5.7 重构了编译错误模型：COMPILEStateEventArgs 改 isError+Errors、CompileProgram 返回 List、Parser_BAS 换命名空间。
    // 本插件按 V5.7 DLL 编译，运行时用反射探测实际 API 形态，让同一个 DLL 同时跑在 V5.6 与 V5.7 上
    //（V5.6 缺 isError/Errors，V5.7 缺 ErrorCode/ErrorLine/ErrorDescription）。
    // 独立静态类，供 Handlers / EventLog / AiService 三处共用。
    internal static class CompileApiCompat
    {
        internal struct CompileErrInfo
        {
            public int Line, Number;
            public bool IsWarning;
            public string Code, Text, ProgramName;
        }

        // 编译事件：COMPILEStateEventArgs → 统一错误列表（站点：OnCompileStateChanged / EventLog.compile_state）。
        // V5.6: 无 isError，读 ErrorCode/ErrorLine/ErrorDescription（ErrorCode==0 视为无错）。
        // V5.7: 读 isError + Errors（每条 Line/Text/Code）。
        internal static List<CompileErrInfo> ReadCompileEventErrors(COMPILEStateEventArgs e, out bool isError)
        {
            isError = false;
            var list = new List<CompileErrInfo>();
            if (e == null) return list;
            var t = e.GetType();
            var isErrorProp = t.GetProperty("isError");
            if (isErrorProp != null)
            {
                isError = isErrorProp.GetValue(e) is bool b && b;
                if (!isError) return list;
                var errs = t.GetProperty("Errors")?.GetValue(e) as IEnumerable;
                if (errs != null)
                {
                    foreach (var x in errs)
                    {
                        if (x == null) continue;
                        var xt = x.GetType();
                        list.Add(new CompileErrInfo
                        {
                            Line = PropInt(xt, x, "Line"),
                            Code = PropStr(xt, x, "Code"),
                            Text = PropStr(xt, x, "Text")
                        });
                    }
                }
            }
            else
            {
                int errorCode = PropInt(t, e, "ErrorCode");
                isError = errorCode != 0;
                if (!isError) return list;
                list.Add(new CompileErrInfo
                {
                    Line = PropInt(t, e, "ErrorLine"),
                    Code = errorCode.ToString(),
                    Text = PropStr(t, e, "ErrorDescription")
                });
            }
            return list;
        }

        // CompileProgram：反射调用 + 兼容单个错误(V5.6) / List(V5.7)（站点：Handlers.CompileProgram）。
        private static MethodInfo _compileProgramMethod;

        private static MethodInfo GetCompileProgramMethod(object ctrl)
        {
            if (_compileProgramMethod != null) return _compileProgramMethod;
            foreach (var m in ctrl.GetType().GetMethods())
            {
                if (m.Name == "CompileProgram" && m.GetParameters().Length == 1)
                {
                    _compileProgramMethod = m;
                    return m;
                }
            }
            return null;
        }

        internal static List<CompileErrInfo> InvokeCompileProgramErrors(object controller, object remoteState)
        {
            var list = new List<CompileErrInfo>();
            if (controller == null || remoteState == null) return list;
            var mi = GetCompileProgramMethod(controller);
            if (mi == null) return list;
            object result;
            try { result = mi.Invoke(controller, new object[] { remoteState }); }
            catch { return list; }
            return ReadCompileResultErrors(result);
        }

        // V5.6 返回单个 TrioBasicError（null=无错）；V5.7 返回 List<TrioBasicError>（空=无错）。
        // 字段名 lineNumber/errorNumber/errorText/isWarning/programName 两版一致。
        internal static List<CompileErrInfo> ReadCompileResultErrors(object result)
        {
            var list = new List<CompileErrInfo>();
            if (result == null) return list;
            IEnumerable seq = (result is string) ? null : result as IEnumerable;
            var items = seq != null ? seq.Cast<object>() : new[] { result };
            foreach (var x in items)
            {
                if (x == null) continue;
                var xt = x.GetType();
                list.Add(new CompileErrInfo
                {
                    Line = PropInt(xt, x, "lineNumber"),
                    Number = PropInt(xt, x, "errorNumber"),
                    IsWarning = PropBool(xt, x, "isWarning"),
                    Text = PropStr(xt, x, "errorText"),
                    ProgramName = PropStr(xt, x, "programName")
                });
            }
            return list;
        }

        // Parser_BAS.EnumTokens：静态调用跨命名空间兼容（站点：AiService.ValidateWithTokenTable）。
        // V5.6: Trio.SharedLibrary.Parser_BAS；V5.7: Trio.SharedLibrary.CodeCompletion.BAS.Parser_BAS。
        // EnumTokens 签名与 EnumTokenDelegate 两版完全一致，只差命名空间。
        private static Type _parserBasType;
        private static MethodInfo _enumTokensMethod;
        private static Type _enumTokenDelegateType;

        private static void InitParserBas()
        {
            if (_parserBasType != null) return;
            var asm = typeof(COMPILEStateEventArgs).Assembly;
            _parserBasType = asm.GetType("Trio.SharedLibrary.CodeCompletion.BAS.Parser_BAS")
                            ?? asm.GetType("Trio.SharedLibrary.Parser_BAS");
            _enumTokenDelegateType = _parserBasType?.GetNestedType("EnumTokenDelegate");
            _enumTokensMethod = _parserBasType?.GetMethod("EnumTokens", BindingFlags.Public | BindingFlags.Static);
        }

        internal static void EnumTokensCompat(string text, int startAt, int endAt,
            Func<string, bool, int, int, int, int, bool> onFound)
        {
            InitParserBas();
            if (_enumTokensMethod == null || _enumTokenDelegateType == null) return; // 命名空间都找不到 → 跳过分词（降级）
            try
            {
                // 把 lambda 重新绑到运行时的 EnumTokenDelegate 类型（签名一致即可）。
                var del = Delegate.CreateDelegate(_enumTokenDelegateType, onFound.Target, onFound.Method);
                _enumTokensMethod.Invoke(null, new object[] { text, startAt, endAt, del });
            }
            catch { /* 分词失败 → 降级，不阻断校验流程 */ }
        }

        // 反射属性读取小工具。
        private static int PropInt(Type t, object o, string n)
        {
            var v = t.GetProperty(n)?.GetValue(o);
            return v == null ? 0 : Convert.ToInt32(v);
        }
        private static bool PropBool(Type t, object o, string n)
        {
            return t.GetProperty(n)?.GetValue(o) is bool b && b;
        }
        private static string PropStr(Type t, object o, string n)
        {
            return t.GetProperty(n)?.GetValue(o)?.ToString();
        }
    }
}
