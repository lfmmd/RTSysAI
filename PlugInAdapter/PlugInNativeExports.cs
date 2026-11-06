using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RTSys.PlugIn
{
    #region ---- 托管导出实现（由纯原生 shim 经反射调用）----

    /// <summary>
    /// 插件导出逻辑（托管实现）。
    ///
    /// .NET Framework 4.8 没有 [UnmanagedCallersOnly]（.NET 5+ 才有），托管 DLL 本身
    /// 无法直接导出原生函数。因此采用「纯原生 shim + CLR COM 宿主」方案：
    ///   - 纯原生 DLL（PlugInShimNative.dll）导出 6 个 __cdecl 原生符号；
    ///   - 首次调用时启动 CLR，通过 IDispatch 晚期绑定反射调用本类的静态方法。
    ///
    /// 本类的方法名 / 参数签名必须与 PlugInAbi.cs 中的 delegate 严格一致，
    /// 修改 ABI 时两处同步修改。
    ///
    /// 所有方法一律 try/catch，异常绝不穿透到 native 边界，并在 catch 中写 Trace 日志。
    /// </summary>
    public static class PlugInNativeExports
    {
        // 参数与 PlugInAbi.cs 中的 delegate 严格一一对应；修改 ABI 时两处同步修改。
        // [ABI-TODO] 以下签名已由 IDA 确认；若 RTSys 后续版本改动 ABI，
        //              仅需在 PlugInAbi.cs 改 delegate，并在此同步改方法签名。

        /// <summary>void __cdecl PlugIn_GetHandle(void** pHostContext)</summary>
        public static void GetHandle(
            IntPtr pHostContext // [ABI-TODO] void**：宿主上下文输出地址（x86 下 IntPtr 即 void** 地址）
        )
        {
            try
            {
                Trace.WriteLine("[PlugIn] GetHandle(pHostContext=0x" + pHostContext.ToString("X") + ")");
                PlugInHostAdapter adapter = PlugInHostAdapter.GetOrCreate();
                if (adapter != null && pHostContext != IntPtr.Zero)
                {
                    adapter.WriteHostContext(pHostContext);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] GetHandle 异常: " + ex);
            }
        }

        /// <summary>bool __cdecl PlugIn_SetDockBar(void)</summary>
        public static bool SetDockBar()
        {
            try
            {
                Trace.WriteLine("[PlugIn] SetDockBar()");
                PlugInHostAdapter adapter = PlugInHostAdapter.GetExisting();
                if (adapter == null) return false;
                return adapter.SetDockBarCore();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] SetDockBar 异常: " + ex);
                return false;
            }
        }

        /// <summary>bool __cdecl PlugIn_SetAutoSize(void)</summary>
        public static bool SetAutoSize()
        {
            try
            {
                Trace.WriteLine("[PlugIn] SetAutoSize()");
                PlugInHostAdapter adapter = PlugInHostAdapter.GetExisting();
                if (adapter == null) return false;
                return adapter.SetAutoSizeCore();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] SetAutoSize 异常: " + ex);
                return false;
            }
        }

        /// <summary>CWnd* __cdecl PlugIn_Create(void* pHost)</summary>
        public static IntPtr Create(
            IntPtr pHost // [ABI-TODO] void*：宿主 CPluginView* 或 CBCGPMDIFrameWnd*（适配器内按 HWND 处理并降级）
        )
        {
            try
            {
                Trace.WriteLine("[PlugIn] Create(pHost=0x" + pHost.ToString("X") + ")");
                PlugInHostAdapter adapter = PlugInHostAdapter.GetOrCreate();
                if (adapter == null) return IntPtr.Zero;
                return adapter.CreateCore(pHost);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Create 异常: " + ex);
                return IntPtr.Zero;
            }
        }

        /// <summary>void __cdecl PlugIn_Reserved(int reserved)</summary>
        public static void Reserved(
            int reserved // [ABI-TODO] int：宿主恒传 0
        )
        {
            try
            {
                Trace.WriteLine("[PlugIn] Reserved(reserved=" + reserved + ")");
                PlugInHostAdapter adapter = PlugInHostAdapter.GetExisting();
                if (adapter != null)
                {
                    adapter.ReservedCore(reserved);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Reserved 异常: " + ex);
            }
        }

        /// <summary>void __cdecl PlugIn_Destroy(void* pViewOrWindow)</summary>
        public static void Destroy(
            IntPtr pViewOrWindow // [ABI-TODO] void*：CPluginView* 或 PlugIn_Create 返回的 CWnd*
        )
        {
            try
            {
                Trace.WriteLine("[PlugIn] Destroy(pViewOrWindow=0x" + pViewOrWindow.ToString("X") + ")");
                PlugInHostAdapter.ReleaseGlobal();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Destroy 异常: " + ex);
            }
        }

        // ---------- 兼容入口：供 C++/CLI shim（方案 B）直接调用 ----------
        // 纯原生 shim 走 IDispatch 反射调用上方 GetHandle/SetDockBar/...；
        // 以下 Managed_* 保留给 C++/CLI 路线，二者语义完全等价。

        public static void Managed_GetHandle(IntPtr pHostContext)
        {
            GetHandle(pHostContext);
        }

        public static bool Managed_SetDockBar()
        {
            return SetDockBar();
        }

        public static bool Managed_SetAutoSize()
        {
            return SetAutoSize();
        }

        public static IntPtr Managed_Create(IntPtr pHost)
        {
            return Create(pHost);
        }

        public static void Managed_Reserved(int reserved)
        {
            Reserved(reserved);
        }

        public static void Managed_Destroy(IntPtr pViewOrWindow)
        {
            Destroy(pViewOrWindow);
        }
    }

    #endregion

    #region ---- 纯原生 shim 入口（ICLRRuntimeHost::ExecuteInDefaultAppDomain）----

    /// <summary>
    /// 供纯原生 shim 通过 <c>ICLRRuntimeHost::ExecuteInDefaultAppDomain</c> 调用的入口。
    ///
    /// ExecuteInDefaultAppDomain 的约束：
    ///   - 托管方法必须是 public static；
    ///   - 只能带一个 string 参数（可为 null）；
    ///   - 返回值只能是 int（映射为 DWORD）。
    ///
    /// 因此指针参数以 "0x%08X" 字符串编码传入，int 返回值承载布尔结果 / HWND。
    /// 六个入口与 PlugInAbi.cs 的 delegate 语义一一对应。
    /// </summary>
    public static class PlugInNativeBridge
    {
        /// <summary>解析 "0xXXXXXXXX" 形式的指针参数。</summary>
        private static IntPtr ParsePointer(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return IntPtr.Zero;
            uint value = 0;
            if (!uint.TryParse(arg.Replace("0x", ""),
                               System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out value))
            {
                return IntPtr.Zero;
            }
            return new IntPtr((int)value);
        }

        /// <summary>对应 PlugIn_GetHandle：把宿主上下文写入 *pHostContext。</summary>
        public static int GetHandle(string arg)
        {
            try
            {
                PlugInNativeExports.GetHandle(ParsePointer(arg));
                return 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Bridge.GetHandle 异常: " + ex);
                return 0;
            }
        }

        /// <summary>对应 PlugIn_SetDockBar（返回 1 表示支持 DockBar）。</summary>
        public static int SetDockBar(string arg)
        {
            try
            {
                return PlugInNativeExports.SetDockBar() ? 1 : 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Bridge.SetDockBar 异常: " + ex);
                return 0;
            }
        }

        /// <summary>对应 PlugIn_SetAutoSize（返回 1 表示支持自动尺寸）。</summary>
        public static int SetAutoSize(string arg)
        {
            try
            {
                return PlugInNativeExports.SetAutoSize() ? 1 : 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Bridge.SetAutoSize 异常: " + ex);
                return 0;
            }
        }

        /// <summary>
        /// 对应 PlugIn_Create：创建 ChatPanel + 启动 ApiServer + HwndSource 嵌入宿主 HWND，
        /// 返回插件窗口句柄（x86 下低 32 位有效）。
        /// </summary>
        public static int Create(string arg)
        {
            try
            {
                IntPtr hwnd = PlugInNativeExports.Create(ParsePointer(arg));
                return hwnd.ToInt32();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Bridge.Create 异常: " + ex);
                return 0;
            }
        }

        /// <summary>对应 PlugIn_Reserved：转发保留接口调用。</summary>
        public static int Reserved(string arg)
        {
            try
            {
                int reserved = 0;
                if (!string.IsNullOrEmpty(arg))
                {
                    int.TryParse(arg, out reserved);
                }
                PlugInNativeExports.Reserved(reserved);
                return 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Bridge.Reserved 异常: " + ex);
                return 0;
            }
        }

        /// <summary>对应 PlugIn_Destroy：停止 ApiServer + 释放 UI。</summary>
        public static int Destroy(string arg)
        {
            try
            {
                PlugInNativeExports.Destroy(ParsePointer(arg));
                return 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PlugIn] Bridge.Destroy 异常: " + ex);
                return 0;
            }
        }
    }

    #endregion
}
