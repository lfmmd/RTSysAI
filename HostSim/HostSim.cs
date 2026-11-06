// HostSim.cs —— 忠实复刻 RTSys.exe 宿主对插件的调用序列（基于 IDA sub_50AA70 / 0x4ADCA0）
//
// 加载真实原生 shim DLL（RTSysPlugInNative.dll），按宿主真实顺序调用 6 个导出，
// 并对返回的 CWnd* 重放宿主对 m_hWnd(+0x20) 的全部解引用，以及 PreTranslateMessage
// vtable[69] 虚调用（call [*(pPlugin+0x0)+0x114]，ecx=plugin，栈参 &MSG）。
//
// 用法: HostSim.exe <shim.dll 路径>
using System;
using System.Runtime.InteropServices;

internal static class HostSim
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetHandle_t(IntPtr pHostContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SetDockBar_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SetAutoSize_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr Create_t(IntPtr pHost);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Reserved_t(int reserved);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Destroy_t(IntPtr pViewOrWindow);

    // 宿主 vtable[69] 虚调用：x86 __thiscall，ecx=this(CWnd*)，栈参=MSG*，返回 BOOL
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool VCall69_t(IntPtr pThis, IntPtr pMsg);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight,
        [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr CreateWindowExA(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int X, int Y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    // 宿主模拟的 CPluginView：对象偏移 +0x20 存放 DockBar HWND
    [StructLayout(LayoutKind.Sequential)]
    private struct FakeCPluginView
    {
        public IntPtr pad0, pad1, pad2, pad3, pad4, pad5, pad6, pad7; // 0x00..0x1F
        public IntPtr hWndDockBar;                                    // 0x20
    }

    private static int Main(string[] args)
    {
        string dllPath = args.Length > 0 ? args[0] :
            @"%USERPROFILE%\Desktop\RTSys\Plug_in\AIPlugin\RTSysPlugInNative.dll";

        Console.WriteLine("[host] loading shim: " + dllPath);
        IntPtr hMod = LoadLibrary(dllPath);
        if (hMod == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: LoadLibrary err=" + Marshal.GetLastWin32Error());
            return 1;
        }

        IntPtr gp(string name)
        {
            IntPtr p = GetProcAddress(hMod, name);
            if (p == IntPtr.Zero) throw new Exception("missing export " + name);
            return p;
        }

        GetHandle_t GetHandle = (GetHandle_t)Marshal.GetDelegateForFunctionPointer(gp("PlugIn_GetHandle"), typeof(GetHandle_t));
        SetDockBar_t SetDockBar = (SetDockBar_t)Marshal.GetDelegateForFunctionPointer(gp("PlugIn_SetDockBar"), typeof(SetDockBar_t));
        SetAutoSize_t SetAutoSize = (SetAutoSize_t)Marshal.GetDelegateForFunctionPointer(gp("PlugIn_SetAutoSize"), typeof(SetAutoSize_t));
        Create_t Create = (Create_t)Marshal.GetDelegateForFunctionPointer(gp("PlugIn_Create"), typeof(Create_t));
        Reserved_t Reserved = (Reserved_t)Marshal.GetDelegateForFunctionPointer(gp("PlugIn_Reserved"), typeof(Reserved_t));
        Destroy_t Destroy = (Destroy_t)Marshal.GetDelegateForFunctionPointer(gp("PlugIn_Destroy"), typeof(Destroy_t));

        // 1) 创建模拟 DockBar 窗口（合法 HWND，供 HwndSource ParentWindow）
        IntPtr hDockBar = CreateWindowExA(0, "STATIC", "DockBar",
            0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
            0, 0, 800, 600,
            GetDesktopWindow(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hDockBar == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: CreateWindow DockBar");
            FreeLibrary(hMod);
            return 1;
        }
        Console.WriteLine("[host] DockBar HWND=0x" + hDockBar.ToString("X"));

        // 2) 构造模拟 CPluginView：+0x20 = DockBar HWND
        FakeCPluginView view = new FakeCPluginView { hWndDockBar = hDockBar };
        IntPtr pHost = Marshal.AllocHGlobal(Marshal.SizeOf(view));
        Marshal.StructureToPtr(view, pHost, false);

        IntPtr hostCtx = pHost;
        GetHandle(hostCtx);
        Console.WriteLine("[host] GetHandle -> wrote 0x" + Marshal.ReadIntPtr(hostCtx).ToString("X"));

        bool dock = SetDockBar();
        Console.WriteLine("[host] SetDockBar -> " + dock);
        bool autosize = SetAutoSize();
        Console.WriteLine("[host] SetAutoSize -> " + autosize);

        // ---- 核心：PlugIn_Create，宿主期望收到 CWnd*（+0x20=m_hWnd，+0=vptr）----
        IntPtr pPlugin = Create(pHost);
        Console.WriteLine("[host] PlugIn_Create -> 0x" + pPlugin.ToString("X"));
        if (pPlugin == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: Create returned NULL");
            Marshal.FreeHGlobal(pHost);
            DestroyWindow(hDockBar);
            FreeLibrary(hMod);
            return 1;
        }

        // 读取 m_hWnd = pPlugin+0x20（即宿主每一步所做的）
        IntPtr m_hWnd = Marshal.ReadIntPtr(pPlugin, 0x20);
        Console.WriteLine("[host] plugin m_hWnd(+0x20) = 0x" + m_hWnd.ToString("X"));

        // 3) ShowWindow(m_hWnd, 5)
        ShowWindow(m_hWnd, 5);
        Console.WriteLine("[host] ShowWindow OK");

        // 4) GetClientRect(m_hWnd, &Rect)
        RECT rc;
        GetClientRect(m_hWnd, out rc);
        Console.WriteLine("[host] GetClientRect OK rect={0},{1} {2}x{3}", rc.Left, rc.Top,
            rc.Right - rc.Left, rc.Bottom - rc.Top);

        // 5) MoveWindow(m_hWnd, left, top, w, h, TRUE)
        MoveWindow(m_hWnd, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, true);
        Console.WriteLine("[host] MoveWindow OK");

        // 6) GetWindowRect(m_hWnd, &rect_)
        RECT rc2;
        GetWindowRect(m_hWnd, out rc2);
        Console.WriteLine("[host] GetWindowRect OK");

        // 7) GetClientRect(m_hWnd, &rect__1)
        RECT rc3;
        GetClientRect(m_hWnd, out rc3);
        Console.WriteLine("[host] GetClientRect OK");

        // 8) PlugIn_Reserved(0)
        Reserved(0);
        Console.WriteLine("[host] PlugIn_Reserved OK");

        // 9) SetFocus(m_hWnd)
        SetFocus(m_hWnd);
        Console.WriteLine("[host] SetFocus OK");

        // 10) PreTranslateMessage 虚调用：call [*(pPlugin+0x0)+0x114] (vtable[69])
        IntPtr vptr = Marshal.ReadIntPtr(pPlugin, 0x00);
        IntPtr fn69 = Marshal.ReadIntPtr(vptr, 0x114);
        Console.WriteLine("[host] PreTranslateMessage: vptr=0x{0:X}, vtable[69]=0x{1:X}",
            vptr.ToInt64(), fn69.ToInt64());
        IntPtr pMsg = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MSG)));
        MSG msg = new MSG { message = 0x0100 }; // WM_KEYDOWN
        Marshal.StructureToPtr(msg, pMsg, false);
        VCall69_t vcall = (VCall69_t)Marshal.GetDelegateForFunctionPointer(fn69, typeof(VCall69_t));
        bool vret = vcall(pPlugin, pMsg);
        Marshal.FreeHGlobal(pMsg);
        Console.WriteLine("[host] PreTranslateMessage virtual call returned " + vret + " (NO CRASH)");

        // ---- 清理：PlugIn_Destroy 后宿主立即 AfxFreeLibrary，不再解引用 pPlugin ----
        Destroy(pPlugin);
        Console.WriteLine("[host] PlugIn_Destroy OK");

        Marshal.FreeHGlobal(pHost);
        DestroyWindow(hDockBar);
        FreeLibrary(hMod);
        Console.WriteLine("RESULT: PASS (host sequence completed, no crash)");
        return 0;
    }
}
