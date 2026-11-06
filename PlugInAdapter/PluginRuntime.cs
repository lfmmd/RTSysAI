// ============================================================================
// PluginRuntime.cs —— RTSys 托管运行时
//
// 职责：
//   1. 启动 HTTP API 服务（通过注入的委托，对外暴露业务接口）
//   2. 创建 WPF 视图并嵌入到宿主提供的 pHost 窗口中
//   3. 通过 ZMotionAPI P/Invoke zmotion.dll 实现控制器通讯（绕过宿主 hostCtx 模式）
//   4. 提供 MainWindowHandle 供 PlugIn_Create 返回给宿主
//
// 说明：本类不直接依赖任何 RTSysAI 具体类型。启动/停止 HTTP 服务
//      与 UI 视图创建均通过构造函数注入的委托完成，由集成方提供实现。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RTSys.PlugIn
{
    public sealed class PluginRuntime : IDisposable
    {
        private readonly IntPtr _pHost;
        private readonly bool _dockBarSupported;
        private readonly bool _autoSize;
        private IntPtr _mainWindowHandle = IntPtr.Zero;
        private bool _disposed;

        // 注入的委托，由集成方提供具体实现
        private readonly Action _startService;   // 启动 HTTP API
        private readonly Action _stopService;    // 停止 HTTP API
        private readonly Func<FrameworkElement> _viewFactory;  // 创建 UI 视图

        private Window _wpfHostWindow;

        public IntPtr MainWindowHandle => _mainWindowHandle;

        public PluginRuntime(IntPtr pHost, bool dockBarSupported, bool autoSize,
            Func<FrameworkElement> viewFactory, Action startService, Action stopService)
        {
            _pHost = pHost;
            _dockBarSupported = dockBarSupported;
            _autoSize = autoSize;
            _viewFactory = viewFactory;
            _startService = startService;
            _stopService = stopService;
        }

        public void Initialize()
        {
            // STA 线程已在 PlugInHostAdapter.PlugIn_Create 中启动，
            // 此处直接在当前线程初始化 WPF + API 服务。
            InitWpf();
            InitApiServer();
        }

        private void InitWpf()
        {
            // 确保当前线程有 Dispatcher
            if (Dispatcher.CurrentDispatcher == null)
                throw new InvalidOperationException("PluginRuntime 必须在 STA 线程（带 Dispatcher）上初始化");

            // 创建承载 WPF 内容的宿主窗口
            _wpfHostWindow = new Window
            {
                Title = "AI 助手",
                Width = 800,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                ResizeMode = _autoSize ? ResizeMode.CanResize : ResizeMode.NoResize,
            };

            // 将托管窗口嵌入宿主提供的 pHost（HWND 或 CPluginView*）
            // 若 pHost 是宿主 CPluginView*，需通过 HwndHost 转换；若 pHost 是主框架，
            // 则作为独立子窗口。此处按 HWND 处理（C++/CLI shim 层负责转换）。
            if (_pHost != IntPtr.Zero)
            {
                try
                {
                    // 将 pHost 当作父窗口 HWND，创建子窗口
                    _wpfHostWindow.WindowStyle = WindowStyle.None;
                    _wpfHostWindow.ShowActivated = false;

                    // 使用 HwndSource 把 WPF 内容承载到宿主 HWND
                    var sourceParams = new HwndSourceParameters("RTSys_WpfHost")
                    {
                        ParentWindow = _pHost,
                        WindowStyle = 0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
                        Width = (int)_wpfHostWindow.Width,
                        Height = (int)_wpfHostWindow.Height,
                    };
                    var source = new HwndSource(sourceParams);
                    var rootVisual = _viewFactory != null ? _viewFactory() : _wpfHostWindow.Content as Visual;
                    source.RootVisual = rootVisual;
                    _mainWindowHandle = source.Handle;
                }
                catch
                {
                    // 承载失败则退回独立窗口
                    _wpfHostWindow.Show();
                    _mainWindowHandle = new WindowInteropHelper(_wpfHostWindow).Handle;
                }
            }
            else
            {
                _wpfHostWindow.Show();
                _mainWindowHandle = new WindowInteropHelper(_wpfHostWindow).Handle;
            }
        }

        private void InitApiServer()
        {
            // 启动 HTTP API 服务（通过注入的委托，由集成方提供具体实现）
            _startService?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stopService?.Invoke();

            _wpfHostWindow?.Close();
            _wpfHostWindow = null;
        }
    }
}
