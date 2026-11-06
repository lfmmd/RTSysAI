using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

internal static class P
{
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr CreateWindowExA(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "child-blocked";
        Console.WriteLine("mode = " + mode);

        IntPtr parent = IntPtr.Zero;
        if (mode == "child-blocked")
        {
            // 父窗口在主线程；主线程随后被阻塞（模拟宿主 PlugIn_Create 阻塞）
            parent = CreateWindowExA(0, "STATIC", "DockBar", 0x40000000 | 0x10000000,
                0, 0, 800, 600, GetDesktopWindow(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            // 父窗口在独立线程，该线程持续泵消息
            var th = new Thread(() =>
            {
                parent = CreateWindowExA(0, "STATIC", "DockBar", 0x40000000 | 0x10000000,
                    0, 0, 800, 600, GetDesktopWindow(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                while (true)
                {
                    IntPtr msg; int wp; IntPtr lp;
                    uint r = 0;
                    // 简易泵：PeekMessage + DispatchMessage
                    r = PeekMessage(out msg, IntPtr.Zero, 0, 0, 1);
                    if (r != 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                    Thread.Sleep(5);
                }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            Thread.Sleep(500);
        }
        Console.WriteLine("parent = 0x" + parent.ToString("X"));

        var done = new ManualResetEventSlim(false);
        var ui = new Thread(() =>
        {
            try
            {
                var d = Dispatcher.CurrentDispatcher;
                var tcs = new TaskCompletionSource<bool>();
                tcs.SetResult(true);
                Dispatcher.Run();
            }
            catch (Exception e) { Console.WriteLine("[ui] ERR " + e.Message); }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        Thread.Sleep(300);

        var result = new ManualResetEventSlim(false);
        var uiThread = ui;
        var work = new Thread(() =>
        {
            try
            {
                uiThread.Join();
            }
            catch { }
        });
        work.IsBackground = true;
        work.Start();

        // 在 UI 线程上创建 HwndSource（模拟插件行为）
        Dispatcher d2 = null;
        var capture = new ManualResetEventSlim(false);
        var ui2 = new Thread(() =>
        {
            d2 = Dispatcher.CurrentDispatcher;
            capture.Set();
            Dispatcher.Run();
        });
        ui2.SetApartmentState(ApartmentState.STA);
        ui2.IsBackground = true;
        ui2.Start();
        capture.Wait();

        Console.WriteLine("[main] dispatching HwndSource creation...");
        d2.Invoke(() =>
        {
            try
            {
                var p = new HwndSourceParameters("probe")
                {
                    ParentWindow = parent,
                    WindowStyle = unchecked((int)0x40000000),
                    Width = 0,
                    Height = 0
                };
                var src = new HwndSource(p);
                Console.WriteLine("[ui] HwndSource created hwnd=0x" + src.Handle.ToString("X"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ui] HwndSource EXCEPTION: " + ex.GetType().Name + " " + ex.Message);
            }
            finally { result.Set(); }
        });

        bool ok = result.Wait(TimeSpan.FromSeconds(20));
        Console.WriteLine(ok ? "RESULT: HwndSource creation COMPLETED" : "RESULT: HwndSource creation HUNG (20s timeout)");
        return ok ? 0 : 1;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}
