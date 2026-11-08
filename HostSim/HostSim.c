// HostSim.c —— 忠实复刻 RTSys.exe 宿主对插件的调用序列（基于 IDA sub_50AA70）
// 编译（x86，以便加载 x86 原生 shim DLL）：
//   cl.exe /nologo /EHsc /Ox HostSim.c /link user32.lib kernel32.lib gdi32.lib /OUT:HostSim.exe
//
// 复现序列（IDA 0x50aea1–0x50b0fa）：
//   1. PlugIn_SetDockBar()        → true
//   2. PlugIn_SetAutoSize()       → true
//   3. PlugIn_Create(pHost)       → CWnd*（fake CWnd 块，+0x20=m_hWnd）
//   4. ShowWindow(hwnd, 5)
//   5. GetClientRect(m_hWnd, &Rect)
//   6. MoveWindow(hwnd, left, top, w, h, TRUE)
//   7. GetWindowRect(m_hWnd, &rect_)
//   8. GetClientRect(m_hWnd, &rect__1)
//   9. PlugIn_Reserved(0)
//  10. ShowControlBar-ish vcall on CPluginView（跳过，不涉及插件指针）
//  11. SetFocus(hwnd)
//  12. PreTranslateMessage 虚调用：call [*(ptr+0)+0x114]  —— 触发 vtable[69]
//  13. PlugIn_Destroy(ptr)
//
// 矩阵测试：传入不同 pHost，观察是否崩溃。

#include <windows.h>
#include <stdio.h>

typedef void(__cdecl *P_GetHandle)(void**);
typedef BOOL(__cdecl *P_SetDockBar)(void);
typedef BOOL(__cdecl *P_SetAutoSize)(void);
typedef void*(__cdecl *P_Create)(void*);
typedef void(__cdecl *P_Reserved)(int);
typedef void(__cdecl *P_Destroy)(void*);

// 模拟宿主 CPluginView：对象偏移 +0x20 存放 DockBar 的 HWND
typedef struct {
    BYTE pad[0x20];
    HWND hWndDockBar;
} FakeCPluginView;

// 宿主对 CWnd* 的直接调用（不通过 vtable，直接 API）：
static HWND g_pluginHwnd = 0;

static void ShowWindow_plugin(void* pPlugin)
{
    HWND h = *(HWND*)((BYTE*)pPlugin + 0x20);
    ShowWindow(h, SW_SHOW);
    g_pluginHwnd = h;
    printf("  [host] ShowWindow(m_hWnd=0x%p) OK\n", h);
}

static void GetClientRect_plugin(void* pPlugin)
{
    HWND h = *(HWND*)((BYTE*)pPlugin + 0x20);
    RECT rc;
    BOOL ok = GetClientRect(h, &rc);
    printf("  [host] GetClientRect(m_hWnd=0x%p) -> %d  rect=%d,%d %dx%d\n",
           h, ok, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
}

static void GetWindowRect_plugin(void* pPlugin)
{
    HWND h = *(HWND*)((BYTE*)pPlugin + 0x20);
    RECT rc;
    BOOL ok = GetWindowRect(h, &rc);
    printf("  [host] GetWindowRect(m_hWnd=0x%p) -> %d  rect=%d,%d %dx%d\n",
           h, ok, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
}

static void MoveWindow_plugin(void* pPlugin)
{
    HWND h = *(HWND*)((BYTE*)pPlugin + 0x20);
    RECT rc; GetClientRect(h, &rc);
    BOOL ok = MoveWindow(h, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, TRUE);
    printf("  [host] MoveWindow(m_hWnd=0x%p, %dx%d) -> %d\n", h,
           rc.right - rc.left, rc.bottom - rc.top, ok);
}

static void SetFocus_plugin(void* pPlugin)
{
    HWND h = *(HWND*)((BYTE*)pPlugin + 0x20);
    HWND r = SetFocus(h);
    printf("  [host] SetFocus(m_hWnd=0x%p) -> 0x%p\n", h, r);
}

// PreTranslateMessage 虚调用：call [*(pPlugin+0x0)+0x114](pPlugin, MSG*)
static void PreTranslateMessage_plugin(void* pPlugin)
{
    void* vptr = *(void**)pPlugin;          // +0x00 vptr
    void* fn = *(void**)((BYTE*)vptr + 0x114); // vtable[69]
    MSG msg = {0};
    msg.message = WM_KEYDOWN;
    printf("  [host] PreTranslateMessage: vptr=0x%p, vtable[69]=0x%p -> calling...\n", vptr, fn);
    // 这是 __thiscall：ecx=pPlugin，参数 &msg，返回 BOOL
    __asm {
        mov ecx, pPlugin
        lea edx, msg
        call fn
    }
    printf("  [host] PreTranslateMessage returned OK\n");
}

int main(int argc, char** argv)
{
    const char* dllPath = "RTSysPlugInNative.dll";
    if (argc > 1) dllPath = argv[1];

    HMODULE hMod = LoadLibraryA(dllPath);
    if (!hMod) { printf("FAIL: LoadLibrary %s err=%lu\n", dllPath, GetLastError()); return 1; }
    printf("  loaded shim: %s\n", dllPath);

    P_GetHandle   GetHandle   = (P_GetHandle)   GetProcAddress(hMod, "PlugIn_GetHandle");
    P_SetDockBar  SetDockBar  = (P_SetDockBar)  GetProcAddress(hMod, "PlugIn_SetDockBar");
    P_SetAutoSize SetAutoSize = (P_SetAutoSize) GetProcAddress(hMod, "PlugIn_SetAutoSize");
    P_Create      Create      = (P_Create)      GetProcAddress(hMod, "PlugIn_Create");
    P_Reserved    Reserved    = (P_Reserved)    GetProcAddress(hMod, "PlugIn_Reserved");
    P_Destroy     Destroy     = (P_Destroy)     GetProcAddress(hMod, "PlugIn_Destroy");

    if (!GetHandle || !SetDockBar || !SetAutoSize || !Create || !Reserved || !Destroy) {
        printf("FAIL: missing exports\n"); return 1;
    }

    // 创建模拟的 DockBar 窗口（子窗口，供 HwndSource 嵌入使用）
    HWND hDockBar = CreateWindowExA(0, "STATIC", "DockBar",
        WS_CHILD | WS_VISIBLE, 0, 0, 800, 600,
        GetDesktopWindow(), NULL, GetModuleHandle(NULL), NULL);
    if (!hDockBar) { printf("FAIL: CreateWindow DockBar\n"); return 1; }

    // 构造模拟 CPluginView：+0x20 = DockBar HWND
    FakeCPluginView view = {0};
    view.hWndDockBar = hDockBar;

    void* hostCtx = &view;
    GetHandle(&hostCtx);
    printf("  hostCtx written -> 0x%p\n", *(void**)hostCtx);

    BOOL dock = SetDockBar();  printf("  SetDockBar -> %d\n", dock);
    BOOL autosize = SetAutoSize(); printf("  SetAutoSize -> %d\n", autosize);

    // ---- 核心：PlugIn_Create，返回 CWnd*（fake CWnd 块）----
    void* pPlugin = Create(&view);
    printf("  PlugIn_Create -> 0x%p\n", pPlugin);
    if (!pPlugin) { printf("FAIL: Create returned NULL\n"); DestroyLibrary: FreeLibrary(hMod); return 1; }

    // 宿主后续序列
    ShowWindow_plugin(pPlugin);
    GetClientRect_plugin(pPlugin);
    MoveWindow_plugin(pPlugin);
    GetWindowRect_plugin(pPlugin);
    GetClientRect_plugin(pPlugin);

    Reserved(0);
    SetFocus_plugin(pPlugin);

    // 触发 vtable[69] 虚调用（PreTranslateMessage）—— 键盘消息时宿主会这么做
    PreTranslateMessage_plugin(pPlugin);

    // ---- 清理：PlugIn_Destroy 后宿主立即 AfxFreeLibrary，不再解引用 pPlugin ----
    Destroy(pPlugin);
    printf("  PlugIn_Destroy called OK\n");

    FreeLibrary(hMod);
    DestroyWindow(hDockBar);
    printf("RESULT: PASS (no crash)\n");
    return 0;

DestroyLibrary:
    FreeLibrary(hMod);
    return 1;
}
