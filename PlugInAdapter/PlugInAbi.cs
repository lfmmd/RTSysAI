// ============================================================================
// PlugInAbi.cs —— RTSys.exe Plug_in\ 第三方插件接口 ABI 定义
//
// ⚠️ 唯一 ABI 定义点：拿到新的反汇编结论后，只需修改本文件。
//
// 来源：IDA 反汇编 RTSys.exe（宿主为 x86 原生 C++ / MFC，imagebase 0x400000）。
// 全部导出为 __cdecl（宿主侧 add esp,N 清栈），插件条目结构 28 字节/条：
//   +0  HMODULE      DLL 模块句柄
//   +4  CPluginView* 宿主侧停靠栏对象（PlugIn_Create 的第 1 参数，变体 A）
//   +8  CWnd*        PlugIn_Create 返回的插件窗口指针
//   +12 wchar_t*     DLL 文件名
//   +16 wchar_t*     别名/标题
//   +24 wchar_t*     插件组/子目录名
//
// 关键约束：
//   1. 全部导出必须 __cdecl。.NET Framework 4.8 托管 DLL 无法直接原生导出
//      （无 UnmanagedCallersOnly），需 DllExport（方案 A）或 C++/CLI shim（方案 B）。
//   2. 所有 native 边界必须 try/catch，托管异常绝不允许穿透到 native。
// ============================================================================
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RTSys.PlugIn
{
    #region ---- 宿主侧不透明类型（仅用作指针，不解引用）----

    /// <summary>宿主侧 CPluginView（BCGPDockingControlBar 派生，sizeof = 0x3D8）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CPluginView
    {
        public IntPtr reserved0;
        public IntPtr reserved1;
        // 其余字段由宿主私有，不解引用
    }

    /// <summary>宿主主框架窗口 CBCGPMDIFrameWnd。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CBCGPMDIFrameWnd
    {
        public IntPtr reserved;
    }

    /// <summary>宿主返回的插件窗口（CWnd*）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CWnd
    {
        public IntPtr reserved;
    }

    #endregion

    #region ---- 导出函数委托类型（宿主调用插件时使用）----

    /// <summary>
    /// void __cdecl PlugIn_GetHandle(void** pHostContext);
    /// 插件把宿主上下文指针写入 *pHostContext。宿主随后把它当 void* 传给
    /// Scope_GetMainConHandle / ECAT_GetMainConHandle。
    /// [ABI-TODO] 已由 IDA 确认；x86 下 IntPtr 即 void** 的地址。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PlugIn_GetHandle_t(IntPtr pHostContext);

    /// <summary>
    /// bool __cdecl PlugIn_SetDockBar(void);
    /// 零参数能力查询。返回 true → 宿主创建 CPluginView 并走 PlugIn_Create(CPluginView*)；
    /// 返回 false → 宿主走 PlugIn_Create(CBCGPMDIFrameWnd*) 独立窗口路径。
    /// [ABI-TODO] 已由 IDA 确认。C++ 的 bool 为 1 字节，故返回类型标注 I1。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool PlugIn_SetDockBar_t();

    /// <summary>
    /// bool __cdecl PlugIn_SetAutoSize(void);
    /// 零参数能力查询。返回值存入 CPluginView+0x3D4（自动尺寸标志）。
    /// [ABI-TODO] 已由 IDA 确认。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public delegate bool PlugIn_SetAutoSize_t();

    /// <summary>
    /// CWnd* __cdecl PlugIn_Create(void* pHost);
    /// 变体 A: pHost = 宿主 CPluginView*（PlugIn_SetDockBar 返回 true 时）
    /// 变体 B: pHost = 主框架 CBCGPMDIFrameWnd*（PlugIn_SetDockBar 返回 false 时）
    /// 返回 CWnd* 存入插件条目 +8；宿主随后 ShowWindow / MoveWindow / GetClientRect。
    /// [ABI-TODO] 已由 IDA 确认签名；变体 A 的 pHost 究竟是 CPluginView* 还是其 HWND
    ///            仍需在宿主实测时确认，PlugInHostAdapter.Create 内按 HWND 处理并做降级。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr PlugIn_Create_t(IntPtr pHost);

    /// <summary>
    /// void __cdecl PlugIn_Reserved(int reserved);
    /// 宿主恒传 0，返回值忽略。收尾/握手通知或保留扩展位。
    /// [ABI-TODO] 已由 IDA 确认。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PlugIn_Reserved_t(int reserved);

    /// <summary>
    /// void __cdecl PlugIn_Destroy(void* pViewOrWindow);
    /// 必须在 AfxFreeLibrary 之前调用。参数为 CPluginView* 或 PlugIn_Create 返回的 CWnd*。
    /// [ABI-TODO] 已由 IDA 确认（第 6 个导出，宿主在 0x46A1C0 / 0x46A57C / 0x504282 处调用）。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PlugIn_Destroy_t(IntPtr pViewOrWindow);

    #endregion

    #region ---- 导出名常量（GetProcAddress 用）----

    public static class PlugInExports
    {
        public const string GetHandle = "PlugIn_GetHandle";
        public const string SetDockBar = "PlugIn_SetDockBar";
        public const string SetAutoSize = "PlugIn_SetAutoSize";
        public const string Create = "PlugIn_Create";
        public const string Reserved = "PlugIn_Reserved";
        public const string Destroy = "PlugIn_Destroy";
    }

    #endregion

    #region ---- 宿主消息常量与自检工具 ----

    public static class PlugInAbi
    {
        /// <summary>宿主初始化完成后投递到主窗口的消息。</summary>
        public const uint WM_HOST_INITIALIZED = 0x52D;

        /// <summary>宿主控制器信息消息。</summary>
        public const uint WM_HOST_CONTROLLER_INFO = 0xFB8;

        // delegate 类型与 PlugInNativeExports 中导出方法的对应关系（签名一致性自检）
        private static readonly Type[] DelegateTypes =
        {
            typeof(PlugIn_GetHandle_t),
            typeof(PlugIn_SetDockBar_t),
            typeof(PlugIn_SetAutoSize_t),
            typeof(PlugIn_Create_t),
            typeof(PlugIn_Reserved_t),
            typeof(PlugIn_Destroy_t)
        };

        private static readonly string[] ExportMethodNames =
        {
            "GetHandle",
            "SetDockBar",
            "SetAutoSize",
            "Create",
            "Reserved",
            "Destroy"
        };

        /// <summary>
        /// 校验 PlugInAbi 中的 delegate 与 PlugInNativeExports 中的导出方法参数是否一致。
        /// 返回 null 表示一致，否则返回不一致描述。修改 ABI 后请调用此方法自检。
        /// </summary>
        public static string VerifySignatureConsistency()
        {
            Type exportsType = typeof(PlugInNativeExports);
            for (int i = 0; i < DelegateTypes.Length; i++)
            {
                ParameterInfo[] delegateParams = DelegateTypes[i].GetMethod("Invoke").GetParameters();
                MethodInfo exportMethod = exportsType.GetMethod(
                    ExportMethodNames[i], BindingFlags.Public | BindingFlags.Static);

                if (exportMethod == null)
                    return "找不到导出方法 " + ExportMethodNames[i];

                ParameterInfo[] exportParams = exportMethod.GetParameters();
                if (delegateParams.Length != exportParams.Length)
                {
                    return string.Format("{0}: 参数个数不一致，delegate={1}，export={2}",
                        ExportMethodNames[i], delegateParams.Length, exportParams.Length);
                }

                for (int p = 0; p < delegateParams.Length; p++)
                {
                    if (delegateParams[p].ParameterType != exportParams[p].ParameterType)
                    {
                        return string.Format("{0}: 第 {1} 个参数类型不一致，delegate={2}，export={3}",
                            ExportMethodNames[i], p + 1,
                            delegateParams[p].ParameterType, exportParams[p].ParameterType);
                    }
                }
            }

            return null;
        }
    }

    #endregion
}
