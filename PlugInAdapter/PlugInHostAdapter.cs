using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

#region ---- Win32：虚拟分配“假 CWnd”内存块（宿主期望 MFC CWnd*）----

/// <summary>
/// 宿主把 PlugIn_Create 的返回值当作 MFC CWnd* 使用：
///   - [返回值+0x20] 作为 m_hWnd（ShowWindow/MoveWindow/GetClientRect/GetWindowRect/SetFocus/PreTranslateMessage）
///   - [返回值+0x00] 作为 vptr，并在 PreTranslateMessage（vtable slot 69）做虚调用 [vptr+0x114]。
/// .NET 托管代码无法构造原生 vtable，因此由本方法在可执行内存中分配“假 CWnd”块：
///   +0x00: vptr 指向同块内的 vtable（70 个 slot，全部指向返回 FALSE 的 stub）
///   +0x20: 真实 HWND
/// 宿主在 PlugIn_Destroy 后即 AfxFreeLibrary，不再解引用本块，故 Destroy 时可安全释放。
/// </summary>
internal static class FakeCWnd
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, uint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint MEM_RELEASE = 0x8000;

    // stub: xor eax, eax; ret 4   (__thiscall BOOL 成员函数，1 个 stack 参数，返回 FALSE)
    private static readonly byte[] s_nullStub = { 0x33, 0xC0, 0xC2, 0x04, 0x00 };

    /// <summary>分配“假 CWnd”块，返回块地址（即宿主应收到的 CWnd*）。失败返回 IntPtr.Zero。</summary>
    public static IntPtr Allocate(IntPtr hwnd)
    {
        // 该布局按 32 位指针设计；x64 下 vtable 与 stub 的偏移不成立，直接拒绝。
        if (IntPtr.Size != 4) return IntPtr.Zero;

        const int blockSize = 0x300;
        IntPtr block = VirtualAlloc(IntPtr.Zero, (uint)blockSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (block == IntPtr.Zero) return IntPtr.Zero;

        IntPtr vtable = new IntPtr(block.ToInt64() + 0x100); // vtable 放在块内
        IntPtr stub = new IntPtr(block.ToInt64() + 0x280);   // stub 码放在 vtable 之后（70×4 结束于 +0x218）

        // 写 stub 码并刷新指令缓存（DEP 下执行前必需）
        Marshal.Copy(s_nullStub, 0, stub, s_nullStub.Length);
        FlushInstructionCache(GetCurrentProcess(), stub, (uint)s_nullStub.Length);

        // vtable：70 个 slot（涵盖 slot 69 PreTranslateMessage），全部指向 null stub
        for (int i = 0; i < 70; i++)
            Marshal.WriteIntPtr(vtable, i * IntPtr.Size, stub);

        // 假 CWnd 对象字段
        Marshal.WriteIntPtr(block, 0x00, vtable); // vptr
        Marshal.WriteIntPtr(block, 0x20, hwnd);   // m_hWnd

        // 写入完成后降为只读+可执行，避免宿主/第三方改写 vtable 或 stub。
        uint oldProtect;
        VirtualProtect(block, (UIntPtr)blockSize, PAGE_EXECUTE_READ, out oldProtect);

        return block;
    }

    public static void Free(IntPtr block)
    {
        if (block == IntPtr.Zero) return;
        VirtualFree(block, 0, MEM_RELEASE);
    }
}

#endregion


namespace RTSys.PlugIn
{
    #region ---- Win32 辅助（窗口有效性 / 尺寸跟随）----

    internal static class NativeWindow
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        // 用 ReadProcessMemory 读取宿主对象字段：无效地址只会返回失败，
        // 不会像 Marshal.ReadIntPtr 那样触发访问违例。
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            out IntPtr lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
    }

    #endregion

    #region ---- 插件运行时接口（与外部 PlugInBase 语义对齐）----

    /// <summary>
    /// 插件运行时接口：语义对齐外部依赖 <c>Trio.SharedLibrary.PlugInBase</c>。
    ///
    /// 对齐点：
    /// - <see cref="OnInitialize"/>  ↔ PlugInBase.Initialize() → protected OnInitialize()
    /// - <see cref="OnDispose"/>     ↔ PlugInBase.Dispose()    → protected OnDispose()
    /// - <see cref="GetView"/>       ↔ 插件对外提供的用户界面（MotionPerfect 侧为 ChatPanel）
    ///
    /// 与 PlugInBase 的差异（由 RTSys 的精确 ABI 决定）：
    /// - <see cref="SetDockBar"/> / <see cref="SetAutoSize"/> 是「零参数能力查询」，
    ///   返回 true 表示支持停靠 / 自动尺寸，而非接收宿主指针。
    /// - <see cref="Create"/> 返回插件窗口句柄（CWnd*），宿主会写入插件条目 +8。
    /// </summary>
    public interface IPlugInRuntime : IDisposable
    {
        /// <summary>插件实例句柄（GCHandle.ToIntPtr(this)），供 native 侧定位实例。</summary>
        IntPtr Handle { get; }

        /// <summary>宿主调用：完成初始化（启动 UI、启动 HTTP 服务、注册 Factory）。</summary>
        void OnInitialize();

        /// <summary>宿主调用：释放资源（停止 HTTP 服务、反注册 Factory）。</summary>
        void OnDispose();

        /// <summary>取得插件视图（用户界面）。</summary>
        FrameworkElement GetView();

        /// <summary>能力查询：是否支持停靠栏（DockBar）。</summary>
        bool SetDockBar();

        /// <summary>能力查询：是否支持自动尺寸。</summary>
        bool SetAutoSize();

        /// <summary>
        /// 宿主调用：创建插件视图。
        /// </summary>
        /// <param name="pHost">宿主对象指针（CPluginView* 或 CBCGPMDIFrameWnd*）。</param>
        /// <returns>插件窗口句柄（CWnd*）；失败返回 <see cref="IntPtr.Zero"/>。</returns>
        IntPtr Create(IntPtr pHost);

        /// <summary>宿主调用：保留扩展接口（宿主恒传 0）。</summary>
        void Reserved(int reserved);

        /// <summary>处理宿主投递的消息（0x52D 初始化完成 / 0xFB8 控制器信息 等）。</summary>
        void HandleHostMessage(uint msg, IntPtr wParam, IntPtr lParam);
    }

    #endregion

    #region ---- 宿主适配器 ----

    /// <summary>
    /// PlugInHostAdapter：插件与原生宿主之间的桥梁。
    ///
    /// - 单例模式：宿主在一个模块内多次调用 5~6 个导出，复用同一实例。
    /// - GCHandle 生命周期：分配后固定托管实例，避免 GC 回收导致 native 指针失效；
    ///   卸载时由 <see cref="ReleaseGlobal"/> 释放。
    /// - 异常保护：所有 native 边界（导出方法）一律 try/catch，绝不让托管异常穿透到 native。
    /// - 宿主消息：<see cref="HandleHostMessage"/> 处理 0x52D / 0xFB8。
    /// </summary>
    public class PlugInHostAdapter : IPlugInRuntime
    {
        private static readonly object _staticLock = new object();
        private static PlugInHostAdapter _instance;
        private static GCHandle _instanceHandle;
        // 进程内 HTTP API（可选，enableHttpApi=true 时启用），在宿主启动调用导出时即启动，
        // 不依赖 AI 面板是否打开。
        private static ApiServer _httpServer;
        private static readonly object _httpLock = new object();

        private readonly object _lock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        protected IntPtr _hostContext = IntPtr.Zero;
        private IntPtr _pluginWindow = IntPtr.Zero;
        // 宿主把 PlugIn_Create 返回值当作 MFC CWnd* 使用，取 [+0x20] 作为 m_hWnd。
        // 我们自己分配的“假 CWnd”内存块，其 +0x20 存放真实 HWND，在 PlugIn_Destroy 时释放。
        private IntPtr _fakeCWndBlock = IntPtr.Zero;
        private IntPtr _dockBarPtr = IntPtr.Zero;
        private bool _dockBarSupported;
        private bool _autoSizeSupported;
        private FrameworkElement _view;
        /// <summary>当前嵌入的视图（派生类用于主题刷新等）。</summary>
        protected FrameworkElement View => _view;

        // 持久的 UI 线程（STA + Dispatcher）。WPF 控件的消息泵必须持续运行，
        // 否则 ChatPanel 的 Dispatcher.BeginInvoke 回调会静默丢失。
        private Thread _uiThread;
        private Dispatcher _uiDispatcher;

        // 嵌入宿主后用于跟随父窗口尺寸的轮询定时器
        private DispatcherTimer _resizeTimer;
        private IntPtr _embeddedChildHwnd;
        private IntPtr _embeddedParentHwnd;
        // 记录宿主父窗口上一次的可见状态。RTSys 关闭插件面板时只隐藏父窗口、
        // 不调用 PlugIn_Destroy，因此必须在定时器里检测可见性变化来自行清理 UI。
        private bool _wasVisible;

        // 嵌入式 WPF 窗口源与降级独立窗口。必须持有字段引用，
        // 否则 OnDispose 时无从释放，导致宿主关闭插件后窗口/定时器残留。
        private HwndSource _hwndSource;
        private Window _fallbackWindow;

        #region ---- 单例存取 ----

        /// <summary>获取或创建全局单例。</summary>
        public static PlugInHostAdapter GetOrCreate()
        {
            lock (_staticLock)
            {
                if (_instance == null)
                {
                    // 具体插件实现：注入 ChatPanel 视图工厂与 ApiServer 启停委托
                    _instance = new RTSysPlugInAdapterImpl();
                    // 分配 GCHandle 防止托管实例被 GC 回收
                    _instanceHandle = GCHandle.Alloc(_instance);
                    Trace.WriteLine("[PlugIn] 适配器已创建, handle=0x" + GCHandle.ToIntPtr(_instanceHandle).ToString("X"));
                    EnsureHttpApiStarted();
                }
                return _instance;
            }
        }

        /// <summary>取得当前单例（不存在则返回 null）。</summary>
        public static PlugInHostAdapter GetExisting()
        {
            lock (_staticLock)
            {
                return _instance;
            }
        }

        /// <summary>释放全局单例（宿主调用 PlugIn_Destroy / AfxFreeLibrary 之前调用）。</summary>
        public static void ReleaseGlobal()
        {
            lock (_staticLock)
            {
                if (_instance == null) return;

                try
                {
                    _instance.OnDispose();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[PlugIn] ReleaseGlobal 异常: " + ex);
                }

                if (_instanceHandle.IsAllocated)
                {
                    _instanceHandle.Free();
                }
                _instance = null;
                StopHttpApi();
                Trace.WriteLine("[PlugIn] 适配器已释放");
            }
        }

        /// <summary>
        /// 在宿主启动调用插件导出（PlugIn_GetHandle / SetDockBar / SetAutoSize）时启动 HTTP API。
        /// 这样即使 AI 面板未打开，外部工具/自动化也能调用 /api/programs、/api/status 等路由。
        /// 仅当 config.json 的 enableHttpApi=true 或环境变量 RTSYS_AI_HTTP=1 时启用。
        /// </summary>
        internal static void EnsureHttpApiStarted()
        {
            if (!RTSysPlugInAdapterImpl.HttpApiEnabled()) return;
            lock (_httpLock)
            {
                if (_httpServer != null) return;
                try
                {
                    var server = new ApiServer();
                    server.Start();
                    _httpServer = server;
                    PlugInLog.Log("EnsureHttpApiStarted() ApiServer.Start() OK on port " + server.Port);
                }
                catch (Exception ex)
                {
                    PlugInLog.LogException("EnsureHttpApiStarted", ex);
                }
            }
        }

        internal static void StopHttpApi()
        {
            lock (_httpLock)
            {
                try { _httpServer?.Stop(); } catch { }
                _httpServer = null;
            }
        }

        #endregion

        #region ---- IPlugInRuntime 实现 ----

        public IntPtr Handle
        {
            get
            {
                lock (_staticLock)
                {
                    return _instanceHandle.IsAllocated ? GCHandle.ToIntPtr(_instanceHandle) : IntPtr.Zero;
                }
            }
        }

        public void OnInitialize()
        {
            PlugInLog.Log("OnInitialize() enter");
            lock (_lock)
            {
                if (_isInitialized || _isDisposed)
                {
                    PlugInLog.Log("OnInitialize() skipped (already initialized or disposed)");
                    return;
                }

                Trace.WriteLine("[PlugIn] OnInitialize");
                try
                {
                    // 子类在此启动 HTTP 服务、注册 Factory、加载资源字典
                    PlugInLog.Log("OnInitialize() calling InitializeCore()...");
                    InitializeCore();
                    PlugInLog.Log("OnInitialize() InitializeCore() OK");
                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    PlugInLog.LogException("OnInitialize", ex);
                    Trace.WriteLine("[PlugIn] OnInitialize 异常: " + ex);
                    throw;
                }
            }
            PlugInLog.Log("OnInitialize() exit");
        }

        public void OnDispose()
        {
            // 即使插件未完成初始化（例如只挂上了 WndProc 钩子），也必须恢复宿主窗口过程，
            // 否则宿主继续运行时会调用到已卸载程序集中的托管委托。
            try { RTSysProjectModel.Shutdown(); } catch { }

            bool needUiCleanup = false;
            lock (_lock)
            {
                if (!_isInitialized || _isDisposed) return;

                Trace.WriteLine("[PlugIn] OnDispose");
                try
                {
                    DisposeCore();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("[PlugIn] OnDispose 异常: " + ex);
                }
                finally
                {
                    _isDisposed = true;
                    _isInitialized = false;
                    _view = null;
                    _pluginWindow = IntPtr.Zero;
                    needUiCleanup = true;
                    // 释放“假 CWnd”内存块（宿主在 PlugIn_Destroy 之后即 AfxFreeLibrary，不再解引用）
                    // 必须用 VirtualFree 释放：该块由 FakeCWnd.Allocate 的 VirtualAlloc 分配，
                    // 用 Marshal.FreeHGlobal 释放会破坏进程堆（0xc0000374）。
                    if (_fakeCWndBlock != IntPtr.Zero)
                    {
                        FakeCWnd.Free(_fakeCWndBlock);
                        _fakeCWndBlock = IntPtr.Zero;
                    }
                }
            }

            // 锁外清理 UI 资源：Dispatcher.Invoke 可能等待 UI 线程，
            // 若持有 _lock 而 UI 线程正在 GetView() 里等同一把锁，会死锁。
            if (needUiCleanup)
            {
                CleanupUiResources();
            }
        }

        /// <summary>
        /// 释放嵌入窗口与尺寸跟随定时器。
        /// HwndSource / Window 都是 UI 线程亲和对象，必须在其 Dispatcher 上销毁；
        /// 若 UI 线程已退出，则退化为直接 Dispose（此时宿主进程通常正在卸载，尽力而为）。
        /// </summary>
        private void CleanupUiResources()
        {
            try
            {
                // 先关闭插件打开的所有对话框（设置/历史/记忆/关于）。
                // 这些对话框在 RTSys 下没有 owner，插件销毁时不会自动关闭，
                // 不显式关闭会一直浮在宿主窗口之上。
                var view = _view as ChatPanel;
                if (view != null)
                {
                    RunOnUiThread(() => view.CloseAllDialogs());
                }

                if (_resizeTimer != null)
                {
                    _resizeTimer.Stop();
                    _resizeTimer = null;
                }
                _embeddedParentHwnd = IntPtr.Zero;
                _embeddedChildHwnd = IntPtr.Zero;

                var source = _hwndSource;
                _hwndSource = null;
                if (source != null)
                {
                    RunOnUiThread(() => source.Dispose());
                }

                var window = _fallbackWindow;
                _fallbackWindow = null;
                if (window != null)
                {
                    RunOnUiThread(() => window.Close());
                }

                PlugInLog.Log("CleanupUiResources() 已释放 HwndSource/Window/SizeFollow");
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("CleanupUiResources", ex);
            }
        }

        /// <summary>在 UI 线程上执行动作；UI 线程已退出时直接在当前线程执行。</summary>
        private void RunOnUiThread(Action action)
        {
            if (action == null) return;
            try
            {
                var dispatcher = _uiDispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    dispatcher.Invoke(DispatcherPriority.Normal, action);
                }
                else
                {
                    action();
                }
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("RunOnUiThread", ex);
            }
        }

        public FrameworkElement GetView()
        {
            lock (_lock)
            {
                if (_view == null)
                {
                    PlugInLog.Log("GetView() calling CreateViewCore()...");
                    _view = CreateViewCore();
                    if (_view == null)
                    {
                        PlugInLog.Log("GetView() failed: CreateViewCore returned null");
                    }
                    else
                    {
                        PlugInLog.Log("GetView() view created OK");
                    }
                }
                return _view;
            }
        }

        public bool SetDockBar()
        {
            lock (_lock)
            {
                _dockBarSupported = true;
                Trace.WriteLine("[PlugIn] SetDockBar() -> true");
                return true;
            }
        }

        public bool SetAutoSize()
        {
            lock (_lock)
            {
                _autoSizeSupported = true;
                Trace.WriteLine("[PlugIn] SetAutoSize() -> true");
                return true;
            }
        }

        public IntPtr Create(IntPtr pHost)
        {
            PlugInLog.Log("Create() enter pHost=0x" + pHost.ToString("X"));
            Trace.WriteLine("[PlugIn] Create(pHost=0x" + pHost.ToString("X") + ")");
            try
            {
                lock (_lock)
                {
                    if (_isDisposed)
                    {
                        PlugInLog.Log("Create() disposed -> return 0");
                        return IntPtr.Zero;
                    }
                    // 1) 确保已初始化（OnInitialize：启动 HTTP 服务等）
                    if (!_isInitialized)
                    {
                        PlugInLog.Log("Create() calling OnInitialize()...");
                        OnInitialize();
                    }
                    _hostContext = pHost;
                    try
                    {
                        IntPtr hWndHost = ReadHostHwnd(pHost);
                        PlugInLog.Log("Create() pHost=0x" + pHost.ToString("X")
                            + " resolved hWndHost(offset+0x20)=0x" + hWndHost.ToString("X"));
                    }
                    catch
                    {
                        PlugInLog.Log("Create() pHost=0x" + pHost.ToString("X")
                            + " resolved hWndHost(offset+0x20)=<read failed>");
                    }
                    PlugInLog.Log("Create() hostContext stored");
                }

                // 2) 确保有一个持久的 STA UI 线程（带 Dispatcher）承载 WPF 视图。
                //    PlugIn_Create 由 native 线程调用（通常是 MTA 或无 COM 初始化），
                //    而 WPF UserControl 创建 / HwndSource 嵌入必须在 STA 线程执行，
                //    且该线程必须持续运行消息泵，否则 ChatPanel 的 Dispatcher 回调会丢失。
                PlugInLog.Log("Create() calling EnsureUiThread()...");
                EnsureUiThread();
                PlugInLog.Log("Create() EnsureUiThread() OK");

                // 3) 把 UI 创建与挂载调度到 UI 线程执行，同步等待完成
                //    注意：RTSys 每次点击插件菜单都会重新调用 PlugIn_Create，但不会先调用
                //    PlugIn_Destroy。若直接再挂一次会泄漏旧的 HwndSource / 定时器 / FakeCWnd。
                //    这里在重新挂载前先清理上一套资源（保留 _view 复用，避免丢失会话界面）。
                IntPtr pluginWindow;
                if (Thread.CurrentThread == _uiThread)
                {
                    DetachPreviousView();
                    pluginWindow = CreateAndAttachView(pHost);
                }
                else
                {
                    var tcs = new TaskCompletionSource<IntPtr>();
                    _uiDispatcher.Invoke(() =>
                    {
                        try
                        {
                            DetachPreviousView();
                            pluginWindow = CreateAndAttachView(pHost);
                            tcs.SetResult(pluginWindow);
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    });
                    pluginWindow = tcs.Task.GetAwaiter().GetResult();
                }

                if (pluginWindow == IntPtr.Zero)
                {
                    PlugInLog.Log("Create() failed: CreateAndAttachView returned HWND=0");
                    Trace.WriteLine("[PlugIn] Create 失败: 无法取得窗口句柄");
                    return IntPtr.Zero;
                }

                lock (_lock)
                {
                    _pluginWindow = pluginWindow;
                    // 【宿主 ABI 适配】分配“假 CWnd”块：+0x00=vptr→vtable（slot69 stub 返回 FALSE），
                    // +0x20=真实 HWND。宿主期望收到 CWnd*，故返回块地址而非裸 HWND。
                    try
                    {
                        _fakeCWndBlock = FakeCWnd.Allocate(pluginWindow);
                    }
                    catch { _fakeCWndBlock = IntPtr.Zero; }
                    PlugInLog.Log("Create() fakeCWndBlock=0x" + _fakeCWndBlock.ToString("X"));
                    PlugInLog.Log("Create() OK hwnd=0x" + pluginWindow.ToString("X"));
                    return _fakeCWndBlock != IntPtr.Zero ? _fakeCWndBlock : pluginWindow;
                }
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("Create", ex);
                Trace.WriteLine("[PlugIn] Create 异常: " + ex);
                return IntPtr.Zero;
            }
        }

        /// <summary>启动并保持一个持久的 STA UI 线程（首次 Create 时调用一次）。</summary>
        private void EnsureUiThread()
        {
            PlugInLog.Log("EnsureUiThread() enter");
            lock (_lock)
            {
                if (_uiThread != null && _uiThread.IsAlive)
                {
                    PlugInLog.Log("EnsureUiThread() UI thread already alive");
                    return;
                }

                var tcs = new TaskCompletionSource<bool>();
                var uiThread = new Thread(() =>
                {
                    try
                    {
                        _uiDispatcher = Dispatcher.CurrentDispatcher;
                        PlugInLog.Log("EnsureUiThread() UI thread Dispatcher ready");
                        tcs.SetResult(true);
                        System.Windows.Threading.Dispatcher.Run();
                    }
                    catch (Exception ex)
                    {
                        PlugInLog.LogException("EnsureUiThread() UI thread start", ex);
                        tcs.SetException(ex);
                    }
                });
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.IsBackground = true;
                uiThread.Start();

                // 等待线程的 Dispatcher 就绪
                PlugInLog.Log("EnsureUiThread() waiting for Dispatcher...");
                tcs.Task.GetAwaiter().GetResult();
                _uiThread = uiThread;
                PlugInLog.Log("EnsureUiThread() exit OK");
            }
        }

        /// <summary>
        /// 在 UI 线程上执行：创建视图并挂到宿主 HWND。返回窗口句柄。
        /// </summary>
        private IntPtr CreateAndAttachView(IntPtr pHost)
        {
            PlugInLog.Log("CreateAndAttachView() enter pHost=0x" + pHost.ToString("X"));
            FrameworkElement view = GetView();
            if (view == null)
            {
                PlugInLog.Log("CreateAndAttachView() failed: GetView returned null");
                Trace.WriteLine("[PlugIn] Create 失败: GetView 返回 null");
                return IntPtr.Zero;
            }
            PlugInLog.Log("CreateAndAttachView() GetView OK, calling AttachView()...");
            var hwnd = AttachView(view, pHost);
            PlugInLog.Log("CreateAndAttachView() AttachView returned hwnd=0x" + hwnd.ToString("X"));
            return hwnd;
        }

        public void Reserved(int reserved)
        {
            Trace.WriteLine("[PlugIn] Reserved(reserved=" + reserved + ")");
            OnReservedCore(reserved);
        }

        public void HandleHostMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            Trace.WriteLine("[PlugIn] HostMessage: 0x" + msg.ToString("X")
                + " wParam=0x" + wParam.ToString("X")
                + " lParam=0x" + lParam.ToString("X"));

            switch (msg)
            {
                case PlugInAbi.WM_HOST_INITIALIZED:        // 0x52D 初始化完成
                    OnHostInitialized(wParam, lParam);
                    break;

                case PlugInAbi.WM_HOST_CONTROLLER_INFO:     // 0xFB8 控制器信息
                    OnHostControllerInfo(wParam, lParam);
                    break;

                default:
                    OnHostMessage(msg, wParam, lParam);
                    break;
            }
        }

        public void Dispose()
        {
            OnDispose();
        }

        #endregion

        #region ---- 供导出层调用的内部入口 ----

        /// <summary>PlugIn_GetHandle：把宿主上下文写入 *pHostContext。</summary>
        internal void WriteHostContext(IntPtr pHostContext)
        {
            if (pHostContext == IntPtr.Zero) return;
            Marshal.WriteIntPtr(pHostContext, _hostContext);
        }

        /// <summary>PlugIn_SetDockBar：能力查询。</summary>
        internal bool SetDockBarCore()
        {
            return SetDockBar();
        }

        /// <summary>PlugIn_SetAutoSize：能力查询。</summary>
        internal bool SetAutoSizeCore()
        {
            return SetAutoSize();
        }

        /// <summary>PlugIn_Create：创建视图并返回窗口句柄。</summary>
        internal IntPtr CreateCore(IntPtr pHost)
        {
            _hostContext = pHost;
            return Create(pHost);
        }

        /// <summary>PlugIn_Reserved：转发。</summary>
        internal void ReservedCore(int reserved)
        {
            Reserved(reserved);
        }

        #endregion

        #region ---- 子类扩展点 ----

        /// <summary>核心初始化逻辑（子类重写）：启动 HTTP 服务、注册 Factory。</summary>
        protected virtual void InitializeCore()
        {
        }

        /// <summary>核心释放逻辑（子类重写）：停止 HTTP 服务、反注册 Factory。</summary>
        protected virtual void DisposeCore()
        {
        }

        /// <summary>创建插件视图（子类重写）。</summary>
        protected virtual FrameworkElement CreateViewCore()
        {
            return null;
        }

        /// <summary>保留接口回调（子类重写）。</summary>
        protected virtual void OnReservedCore(int reserved)
        {
        }

        /// <summary>宿主初始化完成（0x52D）。</summary>
        protected virtual void OnHostInitialized(IntPtr wParam, IntPtr lParam)
        {
        }

        /// <summary>宿主控制器信息（0xFB8）。</summary>
        protected virtual void OnHostControllerInfo(IntPtr wParam, IntPtr lParam)
        {
        }

        /// <summary>其他宿主消息。</summary>
        protected virtual void OnHostMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
        }

        /// <summary>
        /// 把 WPF 视图挂到宿主窗口。默认用 HwndSource 创建子窗口；
        /// 若 pHost 不是合法 HWND（如宿主传入 C++ 对象指针而非窗口句柄），
        /// 则降级为独立顶层窗口。
        ///
        /// 注意：宿主 RTSys 传入的 pHost 是 CPluginView* 对象指针，其 HWND 存储在
        /// 对象偏移 +0x20 处（CPluginView::OnSize / sub_50AA70 确认）。本方法先
        /// 解析出该 HWND 作为 HwndSourceParameters.ParentWindow，确保插件窗口
        /// 正确嵌入宿主 DockBar。
        /// </summary>
        protected virtual IntPtr AttachView(FrameworkElement view, IntPtr pHost)
        {
            if (view == null) return IntPtr.Zero;

            // pHost 可能是 CPluginView*：HWND 在对象偏移 +0x20。用 ReadProcessMemory
            // 安全读取，任何失败都降级为直传 pHost（保留旧行为作为兜底）。
            IntPtr hWndHost = IntPtr.Zero;
            bool hostResolvedFromOffset = false;
            try
            {
                hWndHost = ReadHostHwnd(pHost);
                hostResolvedFromOffset = hWndHost != IntPtr.Zero;
            }
            catch
            {
                hWndHost = IntPtr.Zero;
                hostResolvedFromOffset = false;
            }
            if (!hostResolvedFromOffset)
            {
                hWndHost = pHost;
            }
            PlugInLog.Log("AttachView() pHost=0x" + pHost.ToString("X")
                + " hWndHost=0x" + hWndHost.ToString("X")
                + " resolvedFromOffset=" + hostResolvedFromOffset);

            // 只有 hWndHost 是合法 HWND 时才尝试嵌入为子窗口；
            // 否则直接走独立窗口，避免 WS_CHILD 传非窗口句柄导致 HwndSource 创建失败。
            bool hostIsWindow = hWndHost != IntPtr.Zero && NativeWindow.IsWindow(hWndHost);

            try
            {
                if (hostIsWindow)
                {
                    var sourceParams = new HwndSourceParameters("RTSys_View")
                    {
                        ParentWindow = hWndHost,
                        WindowStyle = unchecked((int)0x40000000),  // WS_CHILD
                        Width = 0,
                        Height = 0
                    };
                    PlugInLog.Log("AttachView() creating HwndSource...");
                    var source = new HwndSource(sourceParams);
                    _hwndSource = source;
                    PlugInLog.Log("AttachView() HwndSource created, hwnd=0x" + source.Handle.ToString("X"));
                    source.RootVisual = view;
                    PlugInLog.Log("AttachView() RootVisual assigned");
                    StartSizeFollow(hWndHost, source.Handle);
                    return source.Handle;
                }
                PlugInLog.Log("AttachView() hWndHost 不是合法 HWND，跳过 HwndSource 子窗口");
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("AttachView", ex);
                Trace.WriteLine("[PlugIn] AttachView 失败, 降级为独立窗口: " + ex.Message);
            }

            // 独立顶层窗口（降级路径）
            try
            {
                PlugInLog.Log("AttachView() 尝试独立窗口降级...");
                var window = new Window
                {
                    Content = view,
                    Width = 800,
                    Height = 600,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = false
                };
                _fallbackWindow = window;
                window.Show();
                return new WindowInteropHelper(window).Handle;
            }
            catch (Exception ex2)
            {
                PlugInLog.LogException("AttachView fallback", ex2);
                Trace.WriteLine("[PlugIn] AttachView 降级也失败: " + ex2);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 读取宿主对象（pHost 为 CPluginView*）偏移 +0x20 处的 HWND。
        /// 返回 IntPtr.Zero 表示读取失败。
        /// </summary>
        private static IntPtr ReadHostHwnd(IntPtr pHost)
        {
            if (pHost == IntPtr.Zero) return IntPtr.Zero;
            IntPtr process = Process.GetCurrentProcess().Handle;
            IntPtr hwnd;
            UIntPtr read;
            if (!NativeWindow.ReadProcessMemory(process, new IntPtr(pHost.ToInt64() + 0x20),
                out hwnd, (UIntPtr)IntPtr.Size, out read) || read != (UIntPtr)IntPtr.Size)
            {
                return IntPtr.Zero;
            }
            return hwnd;
        }

        /// <summary>开始跟随宿主客户区尺寸（仅用于 HwndSource 嵌入子窗口）。</summary>
        private void StartSizeFollow(IntPtr parentHwnd, IntPtr childHwnd)
        {
            try
            {
                _embeddedParentHwnd = parentHwnd;
                _embeddedChildHwnd = childHwnd;
                _resizeTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _resizeTimer.Tick += (s, e) => TickSizeFollow();
                _resizeTimer.Start();
                TickSizeFollow();
                PlugInLog.Log("SizeFollow() 已启动，父窗口=0x" + parentHwnd.ToString("X") + " 子窗口=0x" + childHwnd.ToString("X"));
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("SizeFollow start", ex);
            }
        }

        /// <summary>把嵌入子窗口尺寸同步到宿主客户区。</summary>
        private void TickSizeFollow()
        {
            try
            {
                if (_embeddedParentHwnd == IntPtr.Zero || _embeddedChildHwnd == IntPtr.Zero) return;

                // 宿主关闭插件面板时不会调用 PlugIn_Destroy，只会隐藏/销毁 CPluginView 窗口。
                // 这里检测父窗口状态，变化时主动关闭插件打开的所有对话框，避免残留在后台。
                if (!NativeWindow.IsWindow(_embeddedParentHwnd))
                {
                    if (_wasVisible)
                    {
                        _wasVisible = false;
                        PlugInLog.Log("SizeFollow() 父窗口已销毁，关闭所有对话框");
                        CloseDialogsSafe();
                    }
                    return;
                }

                bool visible = NativeWindow.IsWindowVisible(_embeddedParentHwnd);
                if (visible != _wasVisible)
                {
                    _wasVisible = visible;
                    PlugInLog.Log("SizeFollow() 父窗口可见性变化: " + visible);
                    if (!visible)
                    {
                        CloseDialogsSafe();
                    }
                }
                if (!visible) return;

                OnSizeFollowTick();

                NativeWindow.RECT rc;
                if (!NativeWindow.GetClientRect(_embeddedParentHwnd, out rc)) return;
                if (rc.Width <= 0 || rc.Height <= 0) return;

                NativeWindow.SetWindowPos(_embeddedChildHwnd, IntPtr.Zero, 0, 0,
                    rc.Width, rc.Height,
                    NativeWindow.SWP_NOZORDER | NativeWindow.SWP_NOACTIVATE | NativeWindow.SWP_SHOWWINDOW);
            }
            catch { /* size follow best-effort only */ }
        }

        /// <summary>SizeFollow 定时器每次 tick（150ms）回调，派生类可挂载低频检查。</summary>
        protected virtual void OnSizeFollowTick() { }

        /// <summary>关闭 ChatPanel 打开的所有对话框（在 UI 线程上执行）。</summary>
        private void CloseDialogsSafe()
        {
            try
            {
                var panel = _view as ChatPanel;
                if (panel == null) return;
                RunOnUiThread(() => panel.CloseAllDialogs());
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("CloseDialogsSafe", ex);
            }
        }

        /// <summary>
        /// 在重新创建视图前清理上一套挂载资源（必须在 UI 线程执行）。
        ///
        /// RTSys 每次点击插件菜单都会重新调用 PlugIn_Create，但不先调用 PlugIn_Destroy，
        /// 因此必须显式释放旧的 HwndSource / 定时器 / FakeCWnd 内存块，否则每次点开
        /// 插件都会泄漏一套资源，旧对话框也会残留在后台。
        ///
        /// 注意：保留 _view（ChatPanel 实例）以便复用，避免丢失当前会话界面。
        /// </summary>
        private void DetachPreviousView()
        {
            try
            {
                PlugInLog.Log("DetachPreviousView() enter");
                CloseDialogsSafe();

                // 1) 停止尺寸跟随定时器
                if (_resizeTimer != null)
                {
                    try { _resizeTimer.Stop(); } catch { }
                    _resizeTimer = null;
                }
                _embeddedParentHwnd = IntPtr.Zero;
                _embeddedChildHwnd = IntPtr.Zero;
                _wasVisible = false;

                // 2) 释放旧的 HwndSource（先摘掉 RootVisual 再 Dispose）
                var source = _hwndSource;
                _hwndSource = null;
                if (source != null)
                {
                    try { source.RootVisual = null; } catch { }
                    try { source.Dispose(); } catch { }
                    PlugInLog.Log("DetachPreviousView() 旧 HwndSource 已释放");
                }

                // 3) 关闭降级独立窗口
                var fallback = _fallbackWindow;
                _fallbackWindow = null;
                if (fallback != null)
                {
                    try { fallback.Close(); } catch { }
                    PlugInLog.Log("DetachPreviousView() 旧降级窗口已关闭");
                }

                // 4) 释放旧 FakeCWnd 内存块（宿主已不再引用该块）
                if (_fakeCWndBlock != IntPtr.Zero)
                {
                    try { FakeCWnd.Free(_fakeCWndBlock); } catch { }
                    _fakeCWndBlock = IntPtr.Zero;
                }
                _pluginWindow = IntPtr.Zero;
                PlugInLog.Log("DetachPreviousView() exit");
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("DetachPreviousView", ex);
            }
        }

        #endregion
    }

    #endregion

    #region ---- 具体插件实现示例 ----

    /// <summary>
    /// RTSys 插件实现：把 TrioAI 业务接到 RTSys 宿主桥接层。
    ///
    /// 接线（单进程模式）：
    /// - CreateViewCore() → new ChatPanel()
    /// - ChatPanel 直接调用进程内 AiService.Chat()，不经过 HTTP
    /// - HTTP API 默认关闭；config.json 的 enableHttpApi=true（或环境变量 RTSYS_AI_HTTP=1）
    ///   时在宿主进程内启动 ApiServer，供外部工具/自动化调用 /api/programs 等路由。
    /// </summary>
    public class RTSysPlugInAdapterImpl : PlugInHostAdapter
    {
        // 复用 SizeFollow 定时器轮询宿主主题色：每 ThemeCheckEveryTicks 次 tick（150ms * 40 = 6s）
        // 才真正读取一次（3 个 int），其余 tick 只做整数自增；主题变化时才重建 UI。
        private const int ThemeCheckEveryTicks = 40;
        private int _themeCheckTick;
        private System.Windows.Media.Color _appliedThemeBg;
        private System.Windows.Media.Color _appliedThemeFg;
        private System.Windows.Media.Color _appliedThemeAc;
        private bool _hasAppliedTheme;

        public RTSysPlugInAdapterImpl()
        {
        }

        protected override void OnSizeFollowTick()
        {
            if (++_themeCheckTick < ThemeCheckEveryTicks) return;
            _themeCheckTick = 0;

            try
            {
                System.Windows.Media.Color bg, fg, ac;
                if (!TryGetHostThemeColors(_hostContext, out bg, out fg, out ac, false)) return;
                if (_hasAppliedTheme && bg == _appliedThemeBg && fg == _appliedThemeFg && ac == _appliedThemeAc) return;

                _appliedThemeBg = bg;
                _appliedThemeFg = fg;
                _appliedThemeAc = ac;
                _hasAppliedTheme = true;

                var panel = View as ChatPanel;
                if (panel == null) return;

                PlugInLog.Log("CheckThemeChanged() 宿主主题色变化，重建插件 UI: bg=(" + bg.R + "," + bg.G + "," + bg.B
                    + ") fg=(" + fg.R + "," + fg.G + "," + fg.B
                    + ") accent=(" + ac.R + "," + ac.G + "," + ac.B + ")");
                panel.RebuildForTheme(bg, fg, ac);
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("CheckThemeChanged", ex);
            }
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();
            // HTTP API 已在 GetOrCreate() 中随宿主启动导出一起启动（由 HttpApiEnabled 控制），
            // 不依赖 AI 面板是否打开，此处不重复启动。
        }

        protected override void DisposeCore()
        {
            try { RTSysProjectModel.Shutdown(); } catch { }
            base.DisposeCore();
        }

        /// <summary>
        /// HTTP API 开关：环境变量优先，其次 config.json 的 enableHttpApi。
        /// 默认关闭，避免普通使用时的端口占用/对外暴露。
        /// </summary>
        internal static bool HttpApiEnabled()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("RTSYS_AI_HTTP") == "1") return true;

                var cfgPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RTSysAI", "config.json");
                if (!File.Exists(cfgPath)) return false;

                var cfg = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath));
                object val;
                if (cfg != null && cfg.TryGetValue("enableHttpApi", out val))
                {
                    bool b;
                    if (bool.TryParse(val?.ToString(), out b)) return b;
                }
            }
            catch { }
            return false;
        }

        protected override FrameworkElement CreateViewCore()
        {
            PlugInLog.Log("CreateViewCore() calling new ChatPanel()...");
            try
            {
                // 先读宿主主题色写入全局调色板，再构造面板：
                // ChatPanel 构造函数里的 BuildUI() 直接用调色板取值，
                // 构造之后再改色只能刷新到少数控件，工具栏/气泡/对话框仍是旧色。
                try
                {
                    System.Windows.Media.Color bg, fg, ac;
                    if (TryGetHostThemeColors(_hostContext, out bg, out fg, out ac))
                    {
                        ChatPanel.ConfigurePalette(bg, fg, ac);
                        PlugInLog.Log("CreateViewCore() 使用宿主主题色: bg=(" + bg.R + "," + bg.G + "," + bg.B
                            + ") fg=(" + fg.R + "," + fg.G + "," + fg.B
                            + ") accent=(" + ac.R + "," + ac.G + "," + ac.B + ")");
                    }
                    else
                    {
                        bg = System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E);
                        fg = System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0);
                        ac = System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD7);
                        ChatPanel.ConfigurePalette(bg, fg, ac);
                        PlugInLog.Log("CreateViewCore() 使用兜底深色主题（未从宿主读取到主题色）");
                    }
                    _appliedThemeBg = bg;
                    _appliedThemeFg = fg;
                    _appliedThemeAc = ac;
                    _hasAppliedTheme = true;
                }
                catch (Exception themeEx)
                {
                    PlugInLog.LogException("CreateViewCore ConfigurePalette", themeEx);
                }
                var view = new ChatPanel();
                // Backup handler for UI thread unhandled exceptions (CreateViewCore runs on UI thread),
                // preventing them from crashing the RTSys process.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
                {
                    try { PlugInLog.Log("UI thread unhandled exception: " + e.Exception); } catch { }
                    e.Handled = true;
                };
                PlugInLog.Log("CreateViewCore() ChatPanel created OK");
                return view;
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("CreateViewCore new ChatPanel()", ex);
                PlugInLog.Log("CreateViewCore() ChatPanel unavailable, using simple fallback UserControl");
                return new SimpleFallbackPanel();
            }
        }

        #region ---- 宿主主题色读取 ----

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        /// <summary>
        /// 宿主主题色查询函数（x86 __cdecl，ExtMainHmi.dll 导出）：
        ///   int ZHMI_GetMainThemeColor(COLORREF** ppBackground, COLORREF** ppForeground, COLORREF** ppAccent)
        ///
        /// 函数把 DLL 内三个主题色全局变量的地址写回参数，返回值恒为 0，不能当作成功/失败标志。
        /// 反汇编确认（ExtMainHmi.dll +0x3D30）：
        ///   mov eax,[ebp+8]  / mov [eax],0x1005D0A4   ; *ppBackground = &g_bg
        ///   mov eax,[ebp+0Ch]/ mov [eax],0x1005D0A0   ; *ppForeground = &g_fg
        ///   mov eax,[ebp+10h]/ mov [eax],0x1005D09C   ; *ppAccent     = &g_accent
        ///   xor eax,eax / ret                          ; 返回 0
        ///
        /// COLORREF 是 0x00BBGGRR 的 DWORD（低字节 R）。
        /// 注意：ExtRTSysScope.dll / ExtRTSysEtherCAT.dll 的 Scope/ECAT_GetMainThemeColor
        /// 是“宿主调用插件”的回调（参数为 r,g,b 值），不是插件查询宿主的接口，不能用于读取。
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ZhmiGetMainThemeColor_t(IntPtr ppBackground, IntPtr ppForeground, IntPtr ppAccent);

        private const string ExtMainHmiModule = "ExtMainHmi.dll";
        private const string ZhmiGetMainThemeColorExport = "?ZHMI_GetMainThemeColor@@YAHPAPAK00@Z";

        /// <summary>
        /// 从宿主进程读取当前主题色（背景 / 前景 / 强调色）。
        /// ExtMainHmi.dll 随 RTSys.exe 加载，插件与宿主同进程，可直接查导出并调用。
        /// 失败时返回 false，调用方使用兜底值。
        /// <paramref name="verbose"/> 为 false 时不写日志（用于定时静默轮询，避免刷屏）。
        /// </summary>
        protected virtual bool TryGetHostThemeColors(
            IntPtr hostContext,
            out System.Windows.Media.Color background,
            out System.Windows.Media.Color foreground,
            out System.Windows.Media.Color accent,
            bool verbose = true)
        {
            background = System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E);
            foreground = System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0);
            accent = System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD7);

            try
            {
                IntPtr mod = GetModuleHandle(ExtMainHmiModule);
                if (mod == IntPtr.Zero)
                {
                    if (verbose) PlugInLog.Log("TryGetHostThemeColors: " + ExtMainHmiModule + " 未加载");
                    return false;
                }
                IntPtr fn = GetProcAddress(mod, ZhmiGetMainThemeColorExport);
                if (fn == IntPtr.Zero)
                {
                    if (verbose) PlugInLog.Log("TryGetHostThemeColors: " + ExtMainHmiModule + " 未导出 ZHMI_GetMainThemeColor");
                    return false;
                }
                var getThemeColor = Marshal.GetDelegateForFunctionPointer<ZhmiGetMainThemeColor_t>(fn);

                // 参数是“接收全局变量地址”的指针槽位，不是颜色缓冲区。
                IntPtr ppBg = Marshal.AllocHGlobal(IntPtr.Size);
                IntPtr ppFg = Marshal.AllocHGlobal(IntPtr.Size);
                IntPtr ppAc = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(ppBg, IntPtr.Zero);
                    Marshal.WriteIntPtr(ppFg, IntPtr.Zero);
                    Marshal.WriteIntPtr(ppAc, IntPtr.Zero);

                    // 返回值恒为 0，不检查。
                    getThemeColor(ppBg, ppFg, ppAc);

                    IntPtr pBg = Marshal.ReadIntPtr(ppBg);
                    IntPtr pFg = Marshal.ReadIntPtr(ppFg);
                    IntPtr pAc = Marshal.ReadIntPtr(ppAc);
                    if (pBg == IntPtr.Zero || pFg == IntPtr.Zero || pAc == IntPtr.Zero)
                    {
                        if (verbose) PlugInLog.Log("TryGetHostThemeColors: 宿主返回空的主题色指针");
                        return false;
                    }

                    background = ColorRefToColor((uint)Marshal.ReadInt32(pBg));
                    foreground = ColorRefToColor((uint)Marshal.ReadInt32(pFg));
                    accent = ColorRefToColor((uint)Marshal.ReadInt32(pAc));

                    if (verbose)
                    {
                        PlugInLog.Log(string.Format(
                            "TryGetHostThemeColors: bg=({0},{1},{2}) fg=({3},{4},{5}) accent=({6},{7},{8})",
                            background.R, background.G, background.B,
                            foreground.R, foreground.G, foreground.B,
                            accent.R, accent.G, accent.B));
                    }
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(ppBg);
                    Marshal.FreeHGlobal(ppFg);
                    Marshal.FreeHGlobal(ppAc);
                }
            }
            catch (Exception ex)
            {
                if (verbose) PlugInLog.LogException("TryGetHostThemeColors", ex);
                return false;
            }
        }

        /// <summary>Win32 COLORREF (0x00BBGGRR) → WPF Color。</summary>
        private static System.Windows.Media.Color ColorRefToColor(uint colorRef)
        {
            byte r = (byte)(colorRef & 0xFF);
            byte g = (byte)((colorRef >> 8) & 0xFF);
            byte b = (byte)((colorRef >> 16) & 0xFF);
            return System.Windows.Media.Color.FromRgb(r, g, b);
        }

        #endregion

        /// <summary>最小化的 fallback 视图：不依赖外部宿主程序集，仅用于验证嵌入链路。</summary>
        private sealed class SimpleFallbackPanel : System.Windows.Controls.UserControl
        {
            public SimpleFallbackPanel()
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30));
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text = "RTSys AI 插件（fallback 视图）：完整聊天面板依赖的宿主程序集未部署到插件目录，已降级。\n插件嵌入链路正常，ApiServer 服务已启动。",
                    Foreground = System.Windows.Media.Brushes.White,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    MaxWidth = 600
                };
                Content = tb;
            }
        }
    }

    #endregion
}
