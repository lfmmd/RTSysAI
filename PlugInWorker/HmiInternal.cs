using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RTSys.PlugIn
{
    /// <summary>
    /// RTSys HMI 内部对象模型桥接（进程内调用 ExtMainHmi.dll 导出符号）。
    ///
    /// 设计要点（依据 docs/ida_analysis/hmi_internal_capabilities_2026-11-09.md）：
    ///   1. 全部能力来自 RTSys 自身：ExtMainHmi.dll 的 mangled 导出名 + GetProcAddress，
    ///      不硬编码 RVA，不重新实现 .hmi 文件格式解析；
    ///   2. 对象模型（x86, 32 位）：
    ///        ZHmi_FileClass   +0x20 窗口链头（next 在 obj+0x0C，节点内嵌于 obj+4）
    ///        ZHmi_WindowClass +0x1C 控件链头
    ///        ZHmi_BaseObject  +0x18 特征链头
    ///        ZHmi_FeatureClass +0x20 int / +0x28 double / +0x30 CString*
    ///   3. 对象入口：ZHMI_GetEditObject() → obj+0x10 = 所属 ZHmi_FileClass*；
    ///      无选中对象时返回 null（上层给出可操作提示，不盲写）。
    ///   4. 成员函数用 ThisCall 委托调用（首个参数为 this）。
    ///   5. 字符串：CString 返回值 ABI 复杂，读走 GetFeatureString 返回 CString*，
    ///      写走 FindFeature + ZHmi_FeatureClass::SetString(const char*)（ANSI）。
    ///
    /// 线程约束：ExtMainHmi 的对象属于宿主 UI 线程，所有调用必须在 RTSys UI 线程执行。
    /// </summary>
    internal static class HmiInternal
    {
        // ---- ExtMainHmi.dll 导出符号（mangled，可原样传 GetProcAddress）----

        /// <summary>?ZHMI_GetEditObject@@YAPAVZHmi_BaseObject@@XZ</summary>
        private const string SymGetEditObject = "?ZHMI_GetEditObject@@YAPAVZHmi_BaseObject@@XZ";
        /// <summary>?FindFeature@ZHmi_BaseObject@@QAEPAVZHmi_FeatureClass@@HHH@Z</summary>
        private const string SymFindFeature = "?FindFeature@ZHmi_BaseObject@@QAEPAVZHmi_FeatureClass@@HHH@Z";
        /// <summary>?GetFeatureInt@ZHmi_BaseObject@@UAEHHHH@Z</summary>
        private const string SymGetFeatureInt = "?GetFeatureInt@ZHmi_BaseObject@@UAEHHHH@Z";
        /// <summary>?GetFeatureDouble@ZHmi_BaseObject@@UAENHHH@Z</summary>
        private const string SymGetFeatureDouble = "?GetFeatureDouble@ZHmi_BaseObject@@UAENHHH@Z";
        /// <summary>?GetFeatureString@ZHmi_BaseObject@@UAEPAVCString@@HHH@Z</summary>
        private const string SymGetFeatureString = "?GetFeatureString@ZHmi_BaseObject@@UAEPAV?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@HHH@Z";
        /// <summary>?SetString@ZHmi_FeatureClass@@QAEXPBD@Z</summary>
        private const string SymFeatureSetString = "?SetString@ZHmi_FeatureClass@@QAEXPBD@Z";
        /// <summary>?ReflashFeatureList@ZHmi_BaseObject@@UAEHXZ</summary>
        private const string SymReflashFeatureList = "?ReflashFeatureList@ZHmi_BaseObject@@UAEHXZ";
        /// <summary>?FindWindowW@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z</summary>
        private const string SymFindWindowW = "?FindWindowW@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z";
        /// <summary>?FindControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@H@Z</summary>
        private const string SymFindControl = "?FindControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@H@Z";
        /// <summary>?NewAndAddControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@HHHHHH@Z</summary>
        private const string SymNewAndAddControl = "?NewAndAddControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@HHHHHH@Z";
        /// <summary>?NewAndAddNopWindow@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z</summary>
        private const string SymNewAndAddWindow = "?NewAndAddNopWindow@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z";

        // ---- 对象布局偏移（x86；已由 IDA + 运行时探针双重确认）----
        /// <summary>ZHmi_BaseObject+0x10：所属 ZHmi_FileClass*</summary>
        internal const int OffOwnerFile = 0x10;
        /// <summary>ZHmi_BaseObject+0x18：特征链表头</summary>
        internal const int OffFeatureHead = 0x18;
        /// <summary>ZHmi_FileClass+0x20：窗口链表头</summary>
        internal const int OffWindowHead = 0x20;
        /// <summary>ZHmi_WindowClass+0x1C：控件链表头</summary>
        internal const int OffControlHead = 0x1C;
        /// <summary>链表节点的 next 指针所在位置（obj+0x0C，节点内嵌于 obj+4）</summary>
        internal const int OffItemNext = 0x0C;
        /// <summary>链表节点相对对象起点的偏移（节点 = obj+4）</summary>
        internal const int ItemNodeDelta = 4;
        /// <summary>对象类型 ID（u32 at obj+4）：窗口=11</summary>
        internal const int OffObjectType = 0x04;
        /// <summary>ZHmi_FeatureClass+0x20：int 值</summary>
        internal const int OffFeatureInt = 0x20;
        /// <summary>ZHmi_FeatureClass+0x28：double 值</summary>
        internal const int OffFeatureDouble = 0x28;
        /// <summary>ZHmi_FeatureClass+0x30：CString*</summary>
        internal const int OffFeatureString = 0x30;

        // ---- 已知特征 ID ----
        internal const int FeatureControlId = 1000;      // 控件编号
        internal const int FeatureControlName = 1001;    // 控件名称
        internal const int FeatureSize = 1002;           // 尺寸：sub0=宽 sub1=高
        internal const int FeaturePosition = 1003;       // 位置：sub0=X sub1=Y
        internal const int FeatureRepaintFlag = 1081;    // 需重绘/重生成图片
        internal const int FeaturePositionLock = 1270;   // 位置锁（非 0 时忽略 1002/1003 写入）
        internal const int FeatureWindowId = 20001;      // 窗口编号
        internal const int FeatureWindowName = 20003;    // 窗口名称
        internal const int FeatureDisplayText = 2000;    // 显示文本

        // ---- 委托类型（ThisCall：首参为 this）----

        // ZHMI_GetEditObject 是自由函数 __cdecl（?ZHMI_GetEditObject@@YA...），无 this 指针。
        // 若声明为 ThisCall，调用方会把垃圾 ECX 当 this，函数体忽略 ECX 所以表面可用；
        // 但 CLR 按 ThisCall 会在调用前置 ECX 并污染状态，导致 __cdecl 函数栈/寄存器不一致。
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetEditObjectFn();

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr FindFeatureFn(IntPtr self, int mainId, int subId, int index);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int GetFeatureIntFn(IntPtr self, int mainId, int subId, int index);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate double GetFeatureDoubleFn(IntPtr self, int mainId, int subId, int index);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr GetFeatureStringFn(IntPtr self, int mainId, int subId, int index);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int SetFeatureIntFn(IntPtr self, int mainId, int subId, int index, int value);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int SetFeatureDoubleFn(IntPtr self, int mainId, int subId, int index, double value);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int ReflashFeatureActionFn(IntPtr self, IntPtr feature);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void FeatureSetStringFn(IntPtr self, IntPtr ansiText);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int ReflashFeatureListFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr FindWindowWFn(IntPtr self, int windowNo);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr FindControlFn(IntPtr self, int controlId);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr NewAndAddControlFn(IntPtr self, int id, int type, int x, int y, int w, int h);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr NewAndAddWindowFn(IntPtr self, int id);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr updateRegion, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        // ---- 解析后的入口（懒加载，进程内缓存）----
        private static bool _resolved;
        private static string _resolveError;

        private static GetEditObjectFn _getEditObject;
        private static FindFeatureFn _findFeature;
        private static GetFeatureIntFn _getFeatureInt;
        private static GetFeatureDoubleFn _getFeatureDouble;
        private static GetFeatureStringFn _getFeatureString;
        private static ReflashFeatureActionFn _reflashFeatureAction;
        private static FeatureSetStringFn _featureSetString;
        private static ReflashFeatureListFn _reflashFeatureList;
        private static FindWindowWFn _findWindowW;
        private static FindControlFn _findControl;
        private static NewAndAddControlFn _newAndAddControl;
        private static NewAndAddWindowFn _newAndAddWindow;

        /// <summary>强制在宿主 UI 线程内执行（经 RTSysProjectModel.UiInvokeAsync）。</summary>
        internal static T UiInvoke<T>(Func<T> func)
        {
            return RTSysProjectModel.UiInvokeAsync(func);
        }

        /// <summary>ExtMainHmi.dll 是否已加载并成功解析导出符号。</summary>
        internal static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _resolved;
            }
        }

        /// <summary>解析失败时的原因（成功时为 null）。</summary>
        internal static string ResolveError
        {
            get
            {
                EnsureResolved();
                return _resolveError;
            }
        }

        private static void EnsureResolved()
        {
            if (_resolved || _resolveError != null) return;

            var module = GetModuleHandle("ExtMainHmi.dll");
            if (module == IntPtr.Zero)
            {
                _resolveError = "ExtMainHmi.dll 未加载（RTSys 未启动或未加载 HMI 模块）。";
                return;
            }

            try
            {
                _getEditObject = Bind<GetEditObjectFn>(module, SymGetEditObject);
                _findFeature = Bind<FindFeatureFn>(module, SymFindFeature);
                _getFeatureInt = Bind<GetFeatureIntFn>(module, SymGetFeatureInt);
                _getFeatureDouble = Bind<GetFeatureDoubleFn>(module, SymGetFeatureDouble);
                _getFeatureString = Bind<GetFeatureStringFn>(module, SymGetFeatureString);
                _reflashFeatureAction = Bind<ReflashFeatureActionFn>(module, "?ReflashFeatureAction@ZHmi_BaseObject@@UAEHPAVZHmi_FeatureClass@@@Z");
                _featureSetString = Bind<FeatureSetStringFn>(module, SymFeatureSetString);
                _reflashFeatureList = Bind<ReflashFeatureListFn>(module, SymReflashFeatureList);
                _findWindowW = Bind<FindWindowWFn>(module, SymFindWindowW);
                _findControl = Bind<FindControlFn>(module, SymFindControl);
                _newAndAddControl = Bind<NewAndAddControlFn>(module, SymNewAndAddControl);
                _newAndAddWindow = Bind<NewAndAddWindowFn>(module, SymNewAndAddWindow);
            }
            catch (Exception ex)
            {
                _resolveError = "解析 ExtMainHmi.dll 导出符号失败：" + ex.Message;
                return;
            }

            _resolved = true;
        }

        private static T Bind<T>(IntPtr module, string symbol) where T : class
        {
            var proc = GetProcAddress(module, symbol);
            if (proc == IntPtr.Zero)
                throw new InvalidOperationException("导出符号不存在：" + symbol);
            return Marshal.GetDelegateForFunctionPointer<T>(proc);
        }

        // ========== 对象入口 ==========

        /// <summary>
        /// 当前选中的 HMI 对象（ZHMI_GetEditObject）。无选中返回 IntPtr.Zero。
        /// </summary>
        internal static IntPtr GetEditObject()
        {
            EnsureResolved();
            if (!_resolved) return IntPtr.Zero;
            try { return _getEditObject(); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// 当前 HMI 文件对象（ZHmi_FileClass*）。无选中/无效时返回 Zero。
        /// 选中对象时取 obj+0x10；选中本身是文件时（vtable 指向 FileClass）
        /// 直接返回对象本身。文件对象类型 ID 为 10（见 HmiInternal 常量注释）。
        /// </summary>
        internal static IntPtr GetCurrentFileClass()
        {
            var obj = GetEditObject();
            if (obj == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                if (GetObjectType(obj) == 10) return obj;
                var file = Marshal.ReadIntPtr(obj, OffOwnerFile);
                return IsPlausible(file) ? file : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        // ========== 链表遍历 ==========

        /// <summary>
        /// 遍历对象链（窗口链 file+0x20 / 控件链 win+0x1C）。
        /// next 在 obj+0x0C（指向下一个节点 = nextObj+4），故 nextObj = next - 4。
        /// </summary>
        internal static List<IntPtr> EnumerateChain(IntPtr owner, int headOffset)
        {
            var result = new List<IntPtr>();
            if (!IsPlausible(owner)) return result;

            IntPtr head;
            try { head = Marshal.ReadIntPtr(owner, headOffset); }
            catch { return result; }
            if (!IsPlausible(head)) return result;

            var seen = new HashSet<int>();
            var cur = head;
            for (int i = 0; i < 4096; i++)
            {
                if (cur == IntPtr.Zero || !seen.Add(cur.ToInt32())) break;
                result.Add(cur);

                IntPtr next;
                try { next = Marshal.ReadIntPtr(cur, OffItemNext); }
                catch { break; }
                if (!IsPlausible(next)) break;
                var nextObj = new IntPtr(next.ToInt32() - ItemNodeDelta);
                if (nextObj == head) break;
                cur = nextObj;
            }
            return result;
        }

        /// <summary>
        /// 遍历对象自身的特征链（obj+0x18）。next 在节点+0x08。
        /// 返回 (mainId, subId, featurePtr) 列表。
        /// </summary>
        internal static List<FeatureEntry> EnumerateFeatures(IntPtr obj)
        {
            var result = new List<FeatureEntry>();
            if (!IsPlausible(obj)) return result;

            IntPtr head;
            try { head = Marshal.ReadIntPtr(obj, OffFeatureHead); }
            catch { return result; }
            if (!IsPlausible(head)) return result;

            var seen = new HashSet<int>();
            var cur = head;
            for (int i = 0; i < 4096; i++)
            {
                if (cur == IntPtr.Zero || !seen.Add(cur.ToInt32())) break;
                try
                {
                    int mainId = Marshal.ReadInt32(cur, 0x00);
                    int subId = Marshal.ReadByte(cur, 0x11);
                    result.Add(new FeatureEntry(mainId, subId, cur));
                }
                catch { break; }

                IntPtr next;
                try { next = Marshal.ReadIntPtr(cur, 0x08); }
                catch { break; }
                if (!IsPlausible(next) || next == head) break;
                cur = next;
            }
            return result;
        }

        // ========== 特征读写 ==========

        /// <summary>查找特征节点（FindFeature(mainId, subId, index)）。找不到返回 Zero。</summary>
        internal static IntPtr FindFeature(IntPtr obj, int mainId, int subId, int index = 0)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return IntPtr.Zero;
            try { return _findFeature(obj, mainId, subId, index); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>读整型特征值。特征不存在返回 null。</summary>
        internal static int? GetFeatureInt(IntPtr obj, int mainId, int subId, int index = 0)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return null;
            try
            {
                var feature = _findFeature(obj, mainId, subId, index);
                if (!IsPlausible(feature)) return null;
                return _getFeatureInt(obj, mainId, subId, index);
            }
            catch { return null; }
        }

        /// <summary>读浮点特征值。特征不存在返回 null。</summary>
        internal static double? GetFeatureDouble(IntPtr obj, int mainId, int subId, int index = 0)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return null;
            try
            {
                var feature = _findFeature(obj, mainId, subId, index);
                if (!IsPlausible(feature)) return null;
                return _getFeatureDouble(obj, mainId, subId, index);
            }
            catch { return null; }
        }

        /// <summary>
        /// 读字符串特征值（走 GetFeatureString 返回 CString*，再读其 m_pchData 宽字符串）。
        /// 特征不存在或字符串为空返回 null。
        /// </summary>
        internal static string GetFeatureString(IntPtr obj, int mainId, int subId, int index = 0)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return null;
            try
            {
                var feature = _findFeature(obj, mainId, subId, index);
                if (!IsPlausible(feature)) return null;

                var cstring = _getFeatureString(obj, mainId, subId, index);
                if (!IsPlausible(cstring)) return null;
                return ReadCString(cstring);
            }
            catch { return null; }
        }

        /// <summary>
        /// 写整型特征值（SetFeatureInt）。返回宿主返回码；0 表示成功。
        /// 宿主内置守卫由 RTSys 自身负责：1270 位置锁、1002/1003 负值钳 0、double 同步、1081 重绘标志。
        /// </summary>
        internal static int SetFeatureInt(IntPtr obj, int mainId, int subId, int index, int value)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return -1;
            try
            {
                var method = GetVirtualMethod(obj, 40);
                return method == IntPtr.Zero ? -1 : Marshal.GetDelegateForFunctionPointer<SetFeatureIntFn>(method)(obj, mainId, subId, index, value);
            }
            catch { return -1; }
        }

        /// <summary>写浮点特征值（SetFeatureDouble）。</summary>
        internal static int SetFeatureDouble(IntPtr obj, int mainId, int subId, int index, double value)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return -1;
            try
            {
                var method = GetVirtualMethod(obj, 44);
                return method == IntPtr.Zero ? -1 : Marshal.GetDelegateForFunctionPointer<SetFeatureDoubleFn>(method)(obj, mainId, subId, index, value);
            }
            catch { return -1; }
        }

        /// <summary>
        /// 写字符串特征值（FindFeature + ZHmi_FeatureClass::SetString(const char*)，ANSI）。
        /// 返回宿主返回码；0 表示成功。
        /// </summary>
        internal static int SetFeatureString(IntPtr obj, int mainId, int subId, int index, string value)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return -1;

            var feature = FindFeature(obj, mainId, subId, index);
            if (!IsPlausible(feature)) return -1;

            var bytes = Encoding.Default.GetBytes(value ?? "");
            var buf = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, buf, bytes.Length);
                Marshal.WriteByte(buf, bytes.Length, 0);
                _featureSetString(feature, buf);
                return 0;
            }
            catch { return -1; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>通知宿主刷新特征列表（ReflashFeatureList）。</summary>
        internal static void ReflashFeatureList(IntPtr obj)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(obj)) return;
            try { _reflashFeatureList(obj); }
            catch { }
        }

        /// <summary>运行宿主针对单个特征的属性变更回调。</summary>
        internal static void ReflashFeatureAction(IntPtr obj, int mainId, int subId, int index)
        {
            var feature = FindFeature(obj, mainId, subId, index);
            if (!IsPlausible(feature)) return;
            try { _reflashFeatureAction(obj, feature); }
            catch { }
        }

        /// <summary>重绘 HMI 编辑器 MDI 子窗口及其主窗口子树。</summary>
        internal static bool RefreshCanvas()
        {
            try
            {
                var main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (main == IntPtr.Zero) return false;

                IntPtr canvas = IntPtr.Zero;
                long largestArea = 0;
                EnumChildWindows(main, (hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    var title = new StringBuilder(512);
                    GetWindowTextW(hwnd, title, title.Capacity);
                    if (title.ToString().IndexOf("Hmi", StringComparison.OrdinalIgnoreCase) < 0 &&
                        title.ToString().IndexOf(".hmi", StringComparison.OrdinalIgnoreCase) < 0)
                        return true;
                    NativeRect rect;
                    if (!GetWindowRect(hwnd, out rect)) return true;
                    long area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
                    if (area > largestArea) { largestArea = area; canvas = hwnd; }
                    return true;
                }, IntPtr.Zero);

                if (canvas == IntPtr.Zero) return false;
                InvalidateRect(canvas, IntPtr.Zero, true);
                UpdateWindow(canvas);
                return RedrawWindow(main, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0080 | 0x0100 | 0x0400);
            }
            catch { return false; }
        }

        private static IntPtr GetVirtualMethod(IntPtr obj, int byteOffset)
        {
            var vtable = Marshal.ReadIntPtr(obj);
            return IsPlausible(vtable) ? Marshal.ReadIntPtr(vtable, byteOffset) : IntPtr.Zero;
        }

        // ========== 按编号查找 ==========

        /// <summary>按窗口编号查找窗口对象（FindWindowW）。</summary>
        internal static IntPtr FindWindow(IntPtr file, int windowNo)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(file)) return IntPtr.Zero;
            try { return _findWindowW(file, windowNo); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>按控件编号查找控件对象（FindControl）。</summary>
        internal static IntPtr FindControl(IntPtr window, int controlId)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(window)) return IntPtr.Zero;
            try { return _findControl(window, controlId); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>新建控件并挂链（NewAndAddControl(id,type,x,y,w,h)）。</summary>
        internal static IntPtr NewAndAddControl(IntPtr window, int id, int type, int x, int y, int w, int h)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(window)) return IntPtr.Zero;
            try { return _newAndAddControl(window, id, type, x, y, w, h); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>新建窗口并挂链（NewAndAddNopWindow(id)）。</summary>
        internal static IntPtr NewAndAddWindow(IntPtr file, int id)
        {
            EnsureResolved();
            if (!_resolved || !IsPlausible(file)) return IntPtr.Zero;
            try { return _newAndAddWindow(file, id); }
            catch { return IntPtr.Zero; }
        }

        // ========== 辅助 ==========

        /// <summary>取对象的类型 ID（u32 at obj+4）：11=窗口，600+=控件。</summary>
        internal static int GetObjectType(IntPtr obj)
        {
            if (!IsPlausible(obj)) return 0;
            try { return Marshal.ReadInt32(obj, OffObjectType); }
            catch { return 0; }
        }

        /// <summary>指针是否看起来像有效的用户态对象地址。</summary>
        internal static bool IsPlausible(IntPtr ptr)
        {
            var v = ptr.ToInt32();
            return v >= 0x10000 && (uint)v < 0x80000000u;
        }

        /// <summary>读取 MFC CString 对象（m_pchData 指向宽字符串，偏移 0）。</summary>
        private static string ReadCString(IntPtr cstringObj)
        {
            if (!IsPlausible(cstringObj)) return null;
            try
            {
                var data = Marshal.ReadIntPtr(cstringObj);
                if (!IsPlausible(data)) return null;
                return Marshal.PtrToStringUni(data);
            }
            catch { return null; }
        }

        /// <summary>特征条目。</summary>
        internal sealed class FeatureEntry
        {
            public int MainId;
            public int SubId;
            public IntPtr Ptr;

            public FeatureEntry(int mainId, int subId, IntPtr ptr)
            {
                MainId = mainId;
                SubId = subId;
                Ptr = ptr;
            }
        }
    }
}
