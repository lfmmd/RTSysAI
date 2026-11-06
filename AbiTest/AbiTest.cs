// AbiTest.cs —— 在 ABI 层面验证宿主对 PlugIn_Create 返回值的期望。
//
// 忠实复刻 RTSys.exe 宿主（IDA sub_50AA70 / 0x4ADCA0）对返回值 pPlugin 的每一次解引用：
//   A) ShowWindow(pPlugin)        → MFC 内部读 [pPlugin+0x20] 作为 m_hWnd
//   B) GetClientRect(pPlugin)     → 宿主显式 push dword ptr [pPlugin+0x20]
//   C) MoveWindow(pPlugin, ...)   → MFC 内部读 [pPlugin+0x20]
//   D) GetWindowRect(pPlugin)     → push dword ptr [pPlugin+0x20]
//   E) SetFocus(pPlugin)          → MFC 内部读 [pPlugin+0x20]
//   F) PreTranslateMessage        → [pPlugin+0x00]=vptr; call [vptr+0x114](pPlugin,&MSG)
//
// 矩阵：
//   组合 A/C/E（返回裸 HWND）  → 期望在 B 处崩溃（宿主读 [hwnd+0x20] → 访问违例）
//   组合 D（返回假 CWnd 块）    → 期望全部通过
//
// 用法: AbiTest.exe
using System;
using System.Runtime.InteropServices;

internal static class AbiTest
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, uint dwSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr CreateWindowExA(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

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
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int x; public int y;
    }

    // x86 __thiscall：ecx=this(CWnd*)，栈参=MSG*，返回 BOOL
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool VCall69_t(IntPtr pThis, IntPtr pMsg);

    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_EXECUTE_READWRITE = 0x40, MEM_RELEASE = 0x8000;

    // stub: xor eax,eax; ret 4  → 返回 FALSE 的 __thiscall 成员函数
    private static readonly byte[] s_nullStub = { 0x33, 0xC0, 0xC2, 0x04, 0x00 };

    /// <summary>与插件 FakeCWnd.Allocate 完全一致的分配逻辑。</summary>
    private static IntPtr AllocateFakeCWnd(IntPtr hwnd)
    {
        const int blockSize = 0x300;
        IntPtr block = VirtualAlloc(IntPtr.Zero, (uint)blockSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (block == IntPtr.Zero) return IntPtr.Zero;
        IntPtr vtable = new IntPtr(block.ToInt64() + 0x100);
        IntPtr stub = new IntPtr(block.ToInt64() + 0x200);
        Marshal.Copy(s_nullStub, 0, stub, s_nullStub.Length);
        FlushInstructionCache(GetCurrentProcess(), stub, (uint)s_nullStub.Length);
        for (int i = 0; i < 70; i++) Marshal.WriteIntPtr(vtable, i * IntPtr.Size, stub);
        Marshal.WriteIntPtr(block, 0x00, vtable);
        Marshal.WriteIntPtr(block, 0x20, hwnd);
        return block;
    }

    private static int Main()
    {
        // 真实插件窗口（模拟 HwndSource 产生的子窗口）
        IntPtr parent = GetDesktopWindow();
        IntPtr hwnd = CreateWindowExA(0, "STATIC", "PluginWin",
            0x40000000 | 0x10000000, 0, 0, 400, 300, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine("real plugin HWND = 0x" + hwnd.ToString("X"));
        Console.WriteLine();

        // ---- 组合 A/E：宿主期望裸 HWND 的情形（当前插件的旧行为）----
        RunCase("组合 A/E: 返回裸 HWND (0x" + hwnd.ToString("X") + ")", hwnd);

        // ---- 组合 D：返回“假 CWnd”块（+0x20 = HWND, +0 = vtable）----
        IntPtr block = AllocateFakeCWnd(hwnd);
        RunCase("组合 D: 返回假 CWnd 块 (0x" + block.ToString("X") + ")", block);

        VirtualFree(block, 0, MEM_RELEASE);
        return 0;
    }

    private static void RunCase(string label, IntPtr pPlugin)
    {
        Console.WriteLine("=== " + label + " ===");
        try
        {
            // A) ShowWindow：MFC 读 [p+0x20]
            IntPtr m_hWnd = Marshal.ReadIntPtr(pPlugin, 0x20);
            Console.WriteLine("  [m_hWnd = p+0x20] = 0x" + m_hWnd.ToString("X"));
            ShowWindow(m_hWnd, 5);
            Console.WriteLine("  A) ShowWindow OK");

            // B) GetClientRect：宿主显式 push dword ptr [p+0x20]
            RECT rc; GetClientRect(m_hWnd, out rc);
            Console.WriteLine("  B) GetClientRect OK ({0}x{1})", rc.Right - rc.Left, rc.Bottom - rc.Top);

            // C) MoveWindow
            MoveWindow(m_hWnd, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, true);
            Console.WriteLine("  C) MoveWindow OK");

            // D) GetWindowRect
            RECT rc2; GetWindowRect(m_hWnd, out rc2);
            Console.WriteLine("  D) GetWindowRect OK");

            // E) SetFocus
            SetFocus(m_hWnd);
            Console.WriteLine("  E) SetFocus OK");

            // F) PreTranslateMessage: [p+0x0]=vptr; call [vptr+0x114](p,&MSG)
            IntPtr vptr = Marshal.ReadIntPtr(pPlugin, 0x00);
            IntPtr fn69 = Marshal.ReadIntPtr(vptr, 0x114);
            Console.WriteLine("  [vptr = p+0x00] = 0x" + vptr.ToString("X") + ", vtable[69]=0x" + fn69.ToString("X"));
            IntPtr pMsg = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MSG)));
            MSG msg = new MSG { message = 0x0100 };
            Marshal.StructureToPtr(msg, pMsg, false);
            VCall69_t vcall = (VCall69_t)Marshal.GetDelegateForFunctionPointer(fn69, typeof(VCall69_t));
            bool vret = vcall(pPlugin, pMsg);
            Marshal.FreeHGlobal(pMsg);
            Console.WriteLine("  F) PreTranslateMessage vcall OK -> " + vret);

            Console.WriteLine("  => PASS (no crash)\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  => EXCEPTION: " + ex.GetType().Name + ": " + ex.Message + "\n");
        }
    }
}
