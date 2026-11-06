// ============================================================================
// PlugInShimNative.cpp —— 纯原生 C++ 原生导出桩（无 /clr，无 C++/CLI）
//
// 职责：
//   1. __declspec(dllexport) 导出 PlugIn_GetHandle / SetDockBar / SetAutoSize /
//      Create / Reserved / Destroy（全部 __cdecl，与 RTSys.exe ABI 精确匹配）。
//   2. 惰性启动 CLR（CLRCreateInstance -> ICLRMetaHost -> ICLRRuntimeInfo ->
//      ICLRRuntimeHost），通过 ExecuteInDefaultAppDomain 调用托管
//      RTSys.PlugIn.PlugInNativeBridge 静态方法。
//   3. 托管异常在托管层自行 try/catch，绝不穿透到 native。
//
// 构建（纯原生 cl.exe，不依赖 C++/CLI 工作负载 / v180 工具集）：
//   cl.exe /c /nologo /utf-8 /W3 /EHsc /LD PlugInShimNative.cpp ^
//     /link /DLL /OUT:PlugInShimNative.dll /DEF:PlugInShimNative.def mscoree.lib ole32.lib
//
// 注意：路径含空格时需引号；建议先 cd 到无空格目录（如 C:\build_shim）再编译。
//       必须加 /utf-8，否则源码中的中文注释按 GBK 解码会破坏后续代码。
// ============================================================================
#define UNICODE
#define _UNICODE
#define _USRDLL
#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <mscoree.h>
#include <objbase.h>
#include <tchar.h>
#include <shlwapi.h>
#include <stdio.h>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shlwapi.lib")

// CLRCreateInstance is exported from mscoree.dll (v4+) but not declared in
// older SDK headers; declare it manually.
typedef HRESULT (WINAPI *PFN_CLRCreateInstance)(REFCLSID rclsid, REFIID riid, LPVOID* ppInterface);

// v4 CLR hosting GUIDs (values per NETFXSDK 4.8 metahost.h / mscoree.h)
static const CLSID MY_CLSID_CLRMetaHost    = {0x9280188D, 0x0E8E, 0x4867, {0xB3, 0x0C, 0x7F, 0xA8, 0x38, 0x84, 0xE8, 0xDE}};
static const IID   MY_IID_ICLRMetaHost     = {0xD332DB9E, 0xB9B3, 0x4125, {0x82, 0x07, 0xA1, 0x48, 0x84, 0xF5, 0x32, 0x16}};
static const IID   MY_IID_ICLRRuntimeInfo  = {0xBD39D1D2, 0xBA2F, 0x486A, {0x89, 0xB0, 0xB4, 0xB0, 0xCB, 0x46, 0x68, 0x91}};
static const IID   MY_IID_ICLRRuntimeHost  = {0x90F1A06C, 0x7712, 0x4762, {0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02}};
static const CLSID MY_CLSID_CLRRuntimeHost = {0x90F1A06E, 0x7712, 0x4762, {0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02}};

// v4 hosting interfaces (hand-written vtables to avoid mscoree.h v2 clashes).
// 成员顺序严格对齐 NETFXSDK 4.8 metahost.h / mscoree.h。
struct MyICLRMetaHostVtbl {
    HRESULT(WINAPI *QueryInterface)(void*, REFIID, void**);
    ULONG(WINAPI *AddRef)(void*);
    ULONG(WINAPI *Release)(void*);
    HRESULT(WINAPI *GetRuntime)(void*, LPCWSTR pwzVersion, REFIID riid, void** ppRuntime);
    HRESULT(WINAPI *GetVersionFromFile)(void*, LPCWSTR pwzFilePath, LPWSTR pwzBuffer, DWORD* pcchBuffer);
    HRESULT(WINAPI *EnumerateInstalledRuntimes)(void*, void** ppEnumerator);
    HRESULT(WINAPI *EnumerateLoadedRuntimes)(void*, HANDLE hndProcess, void** ppEnumerator);
    HRESULT(WINAPI *RequestRuntimeLoadedNotification)(void*, HANDLE hnd, DWORD dwFlags, void* pCallback);
    HRESULT(WINAPI *ExitProcess)(void*, int exitCode);
};

struct MyICLRRuntimeInfoVtbl {
    HRESULT(WINAPI *QueryInterface)(void*, REFIID, void**);
    ULONG(WINAPI *AddRef)(void*);
    ULONG(WINAPI *Release)(void*);
    HRESULT(WINAPI *GetVersionString)(void*, LPWSTR, DWORD*);
    HRESULT(WINAPI *GetRuntimeDirectory)(void*, LPWSTR, DWORD*);
    HRESULT(WINAPI *IsLoaded)(void*, LPCWSTR, BOOL*);
    HRESULT(WINAPI *LoadErrorString)(void*, UINT, LPWSTR, DWORD*);
    HRESULT(WINAPI *LoadLibrary)(void*, LPCWSTR, HMODULE*);
    HRESULT(WINAPI *GetProcAddress)(void*, LPCWSTR, FARPROC*);
    HRESULT(WINAPI *GetInterface)(void*, REFCLSID, REFIID, void**);
    HRESULT(WINAPI *IsLoadable)(void*, REFIID, BOOL*);
    HRESULT(WINAPI *SetDefaultStartupFlags)(void*, DWORD, DWORD);
    HRESULT(WINAPI *GetDefaultStartupFlags)(void*, DWORD*, DWORD*);
    HRESULT(WINAPI *BindAsLegacyV2Runtime)(void*);
    HRESULT(WINAPI *IsStarted)(void*, LPVOID*, DWORD*);
};

struct MyICLRRuntimeHostVtbl {
    HRESULT(WINAPI *QueryInterface)(void*, REFIID, void**);
    ULONG(WINAPI *AddRef)(void*);
    ULONG(WINAPI *Release)(void*);
    HRESULT(WINAPI *Start)(void*);
    HRESULT(WINAPI *Stop)(void*);
    HRESULT(WINAPI *SetHostControl)(void*, void*);
    HRESULT(WINAPI *GetCLRControl)(void*, void**);
    HRESULT(WINAPI *UnloadAppDomain)(void*, DWORD, BOOL);
    HRESULT(WINAPI *ExecuteInAppDomain)(void*, DWORD, void*, void*);
    HRESULT(WINAPI *GetCurrentAppDomainId)(void*, DWORD*);
    HRESULT(WINAPI *ExecuteApplication)(void*, LPCWSTR, DWORD, LPCWSTR*, DWORD, LPCWSTR*, int*);
    HRESULT(WINAPI *ExecuteInDefaultAppDomain)(void*, LPCWSTR pwzAssemblyPath, LPCWSTR pwzTypeName,
                                               LPCWSTR pwzMethodName, LPCWSTR pwzArgument, int* pReturnValue);
};

typedef struct MyICLRMetaHost    { const struct MyICLRMetaHostVtbl*    lpVtbl; } MyICLRMetaHost;
typedef struct MyICLRRuntimeInfo { const struct MyICLRRuntimeInfoVtbl* lpVtbl; } MyICLRRuntimeInfo;
typedef struct MyICLRRuntimeHost { const struct MyICLRRuntimeHostVtbl* lpVtbl; } MyICLRRuntimeHost;

// --------------------------------------------------------------------------
// 运行时路径解析：全部相对于 shim DLL 自身所在目录
// --------------------------------------------------------------------------
static HMODULE   g_hSelf      = NULL;
static WCHAR     g_asmPath[MAX_PATH]  = {0};   // <dll目录>\RTSysPlugInAdapter.dll
static WCHAR     g_typeName[] = L"RTSys.PlugIn.PlugInNativeBridge";

// 取 DLL 自身所在目录（不含末尾反斜杠）
static BOOL BuildDllDirPath(LPWSTR out, size_t cch, LPCWSTR leaf)
{
    WCHAR selfPath[MAX_PATH] = {0};
    DWORD n = GetModuleFileNameW(g_hSelf, selfPath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return FALSE;

    WCHAR* lastSlash = wcsrchr(selfPath, L'\\');
    if (lastSlash == NULL) return FALSE;
    *lastSlash = L'\0';

    if (leaf == NULL || leaf[0] == L'\0')
    {
        wcsncpy_s(out, cch, selfPath, _TRUNCATE);
    }
    else
    {
        if (PathCombineW(out, selfPath, leaf) == NULL) return FALSE;
    }
    return TRUE;
}

// 初始化运行时路径：DLL 目录 + 托管适配器 DLL
static BOOL InitRuntimePaths(void)
{
    if (g_asmPath[0] != L'\0') return TRUE;

    WCHAR dir[MAX_PATH] = {0};
    if (!BuildDllDirPath(dir, _countof(dir), NULL))
    {
        return FALSE;
    }
    if (PathCombineW(g_asmPath, dir, L"RTSysPlugInAdapter.dll") == NULL)
    {
        return FALSE;
    }
    return TRUE;
}

// 托管桥接方法名（public static int Method(string arg)）
static const WCHAR* m_GetHandle   = L"GetHandle";
static const WCHAR* m_SetDockBar  = L"SetDockBar";
static const WCHAR* m_SetAutoSize = L"SetAutoSize";
static const WCHAR* m_Create      = L"Create";
static const WCHAR* m_Reserved    = L"Reserved";
static const WCHAR* m_Destroy     = L"Destroy";

// --------------------------------------------------------------------------
// CLR 宿主状态（全局缓存，首次调用时惰性初始化）
// --------------------------------------------------------------------------
static MyICLRRuntimeHost* g_pRuntimeHost = NULL;
static HMODULE            g_hMscoree     = NULL;
static CRITICAL_SECTION   g_csClr;
static BOOL               g_clrStarted   = FALSE;

static inline void LogLine(const WCHAR* msg)
{
    WCHAR logPath[MAX_PATH] = {0};
    BuildDllDirPath(logPath, _countof(logPath), L"shim_diag.log");

    FILE* f = NULL;
    if (_wfopen_s(&f, logPath, L"a") == 0)
    {
        fwprintf(f, L"%ls\n", msg);
        fclose(f);
    }
}

static inline void LogHr(const WCHAR* ctx, HRESULT hr)
{
    WCHAR buf[128];
    swprintf_s(buf, L"[Shim] %ls = 0x%08X", ctx, hr);
    LogLine(buf);
}

// --------------------------------------------------------------------------
// 启动 CLR 并取得 ICLRRuntimeHost
// --------------------------------------------------------------------------
static HRESULT EnsureClr(MyICLRRuntimeHost** ppHost)
{
    if (ppHost == NULL) return E_POINTER;
    *ppHost = NULL;

    if (!InitRuntimePaths()) return E_FAIL;

    EnterCriticalSection(&g_csClr);
    if (g_clrStarted && g_pRuntimeHost != NULL)
    {
        g_pRuntimeHost->lpVtbl->AddRef(g_pRuntimeHost);
        *ppHost = g_pRuntimeHost;
        LeaveCriticalSection(&g_csClr);
        return S_OK;
    }

    HRESULT hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
    {
        LogHr(L"CoInitializeEx", hr);
        LeaveCriticalSection(&g_csClr);
        return hr;
    }
    LogLine(L"CoInitializeEx ok");

    g_hMscoree = LoadLibraryW(L"mscoree.dll");
    if (g_hMscoree == NULL)
    {
        LogLine(L"LoadLibrary mscoree.dll failed");
        LeaveCriticalSection(&g_csClr);
        return HRESULT_FROM_WIN32(GetLastError());
    }

    PFN_CLRCreateInstance pfn = (PFN_CLRCreateInstance)GetProcAddress(g_hMscoree, "CLRCreateInstance");
    if (pfn == NULL)
    {
        LogLine(L"CLRCreateInstance not found");
        LeaveCriticalSection(&g_csClr);
        return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    }
    LogLine(L"CLRCreateInstance found");

    MyICLRMetaHost* pMeta = NULL;
    hr = pfn(MY_CLSID_CLRMetaHost, MY_IID_ICLRMetaHost, (void**)&pMeta);
    if (FAILED(hr))
    {
        LogHr(L"CLRCreateInstance(CLSID_CLRMetaHost/IID_ICLRMetaHost)", hr);
        LeaveCriticalSection(&g_csClr);
        return hr;
    }
    LogLine(L"CLRCreateInstance ok");

    MyICLRRuntimeInfo* pInfo = NULL;
    hr = pMeta->lpVtbl->GetRuntime(pMeta, L"v4.0.30319", MY_IID_ICLRRuntimeInfo, (void**)&pInfo);
    pMeta->lpVtbl->Release(pMeta);
    if (FAILED(hr))
    {
        LogHr(L"ICLRMetaHost::GetRuntime(v4)", hr);
        LeaveCriticalSection(&g_csClr);
        return hr;
    }
    LogLine(L"ICLRMetaHost::GetRuntime ok");

    MyICLRRuntimeHost* pHost = NULL;
    hr = pInfo->lpVtbl->GetInterface(pInfo, MY_CLSID_CLRRuntimeHost, MY_IID_ICLRRuntimeHost, (void**)&pHost);
    pInfo->lpVtbl->Release(pInfo);
    if (FAILED(hr))
    {
        LogHr(L"ICLRRuntimeInfo::GetInterface(ICLRRuntimeHost)", hr);
        LeaveCriticalSection(&g_csClr);
        return hr;
    }
    LogLine(L"ICLRRuntimeInfo::GetInterface ok");

    hr = pHost->lpVtbl->Start(pHost);
    if (FAILED(hr))
    {
        LogHr(L"ICLRRuntimeHost::Start", hr);
        pHost->lpVtbl->Release(pHost);
        LeaveCriticalSection(&g_csClr);
        return hr;
    }
    LogLine(L"ICLRRuntimeHost::Start ok");

    g_clrStarted = TRUE;
    g_pRuntimeHost = pHost;
    g_pRuntimeHost->lpVtbl->AddRef(g_pRuntimeHost); // shim 保留自己的引用，避免被调用方 Release 销毁
    *ppHost = pHost;
    LeaveCriticalSection(&g_csClr);
    LogLine(L"EnsureClr done");
    return S_OK;
}

// --------------------------------------------------------------------------
// 调用托管静态方法（public static int Method(string arg)）
// --------------------------------------------------------------------------
static HRESULT CallManaged(MyICLRRuntimeHost* pHost, const WCHAR* method,
                           const WCHAR* arg, int* pRetVal)
{
    if (pHost == NULL || method == NULL) return E_POINTER;
    int retVal = 0;
    HRESULT hr = pHost->lpVtbl->ExecuteInDefaultAppDomain(pHost, g_asmPath, g_typeName,
                                                          method, arg, &retVal);
    if (FAILED(hr))
    {
        WCHAR buf[256];
        swprintf_s(buf, L"[Shim] ExecuteInDefaultAppDomain(%ls) failed: 0x%08X", method, hr);
        LogLine(buf);
        return hr;
    }
    if (pRetVal) *pRetVal = retVal;
    WCHAR buf[128];
    swprintf_s(buf, L"ExecuteInDefaultAppDomain(%ls) -> ret=%d", method, retVal);
    LogLine(buf);
    return S_OK;
}

// ============================================================================
// 6 个原生导出（全部 extern "C" __cdecl __declspec(dllexport)）
// ============================================================================

extern "C" __declspec(dllexport) void __cdecl PlugIn_GetHandle(void** pHostContext)
{
    LogLine(L"-> PlugIn_GetHandle");
    MyICLRRuntimeHost* pHost = NULL;
    if (!SUCCEEDED(EnsureClr(&pHost))) { LogLine(L"<- GetHandle EnsureClr failed"); return; }
    WCHAR arg[32];
    swprintf_s(arg, L"0x%p", pHostContext);
    int retVal = 0;
    CallManaged(pHost, m_GetHandle, arg, &retVal);
    pHost->lpVtbl->Release(pHost);
    LogLine(L"<- PlugIn_GetHandle");
}

extern "C" __declspec(dllexport) bool __cdecl PlugIn_SetDockBar(void)
{
    LogLine(L"-> PlugIn_SetDockBar");
    MyICLRRuntimeHost* pHost = NULL;
    if (!SUCCEEDED(EnsureClr(&pHost))) { LogLine(L"<- SetDockBar EnsureClr failed"); return false; }
    int retVal = 0;
    CallManaged(pHost, m_SetDockBar, L"", &retVal);
    pHost->lpVtbl->Release(pHost);
    LogLine(L"<- PlugIn_SetDockBar");
    return (retVal != 0);
}

extern "C" __declspec(dllexport) bool __cdecl PlugIn_SetAutoSize(void)
{
    LogLine(L"-> PlugIn_SetAutoSize");
    MyICLRRuntimeHost* pHost = NULL;
    if (!SUCCEEDED(EnsureClr(&pHost))) { LogLine(L"<- SetAutoSize EnsureClr failed"); return false; }
    int retVal = 0;
    CallManaged(pHost, m_SetAutoSize, L"", &retVal);
    pHost->lpVtbl->Release(pHost);
    LogLine(L"<- PlugIn_SetAutoSize");
    return (retVal != 0);
}

extern "C" __declspec(dllexport) void* __cdecl PlugIn_Create(void* pHost)
{
    LogLine(L"-> PlugIn_Create");
    MyICLRRuntimeHost* pClr = NULL;
    if (!SUCCEEDED(EnsureClr(&pClr))) { LogLine(L"<- Create EnsureClr failed"); return NULL; }
    WCHAR arg[32];
    swprintf_s(arg, L"0x%p", pHost);
    int retVal = 0;
    CallManaged(pClr, m_Create, arg, &retVal);
    pClr->lpVtbl->Release(pClr);
    LogLine(L"<- PlugIn_Create");
    return (void*)(size_t)retVal;
}

extern "C" __declspec(dllexport) void __cdecl PlugIn_Reserved(int reserved)
{
    LogLine(L"-> PlugIn_Reserved");
    MyICLRRuntimeHost* pHost = NULL;
    if (!SUCCEEDED(EnsureClr(&pHost))) { LogLine(L"<- Reserved EnsureClr failed"); return; }
    WCHAR arg[32];
    swprintf_s(arg, L"%d", reserved);
    int retVal = 0;
    CallManaged(pHost, m_Reserved, arg, &retVal);
    pHost->lpVtbl->Release(pHost);
    LogLine(L"<- PlugIn_Reserved");
}

extern "C" __declspec(dllexport) void __cdecl PlugIn_Destroy(void* pViewOrWindow)
{
    LogLine(L"-> PlugIn_Destroy");
    MyICLRRuntimeHost* pHost = NULL;
    if (!SUCCEEDED(EnsureClr(&pHost))) { LogLine(L"<- Destroy EnsureClr failed"); return; }
    WCHAR arg[32];
    swprintf_s(arg, L"0x%p", pViewOrWindow);
    int retVal = 0;
    CallManaged(pHost, m_Destroy, arg, &retVal);
    pHost->lpVtbl->Release(pHost);
    LogLine(L"<- PlugIn_Destroy");
}

// --------------------------------------------------------------------------
// DllMain
// --------------------------------------------------------------------------
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        g_hSelf = hModule;
        InitializeCriticalSection(&g_csClr);
        break;
    case DLL_PROCESS_DETACH:
        if (g_pRuntimeHost)
        {
            g_pRuntimeHost->lpVtbl->Stop(g_pRuntimeHost);
            g_pRuntimeHost->lpVtbl->Release(g_pRuntimeHost);
            g_pRuntimeHost = NULL;
        }
        if (g_hMscoree)
        {
            FreeLibrary(g_hMscoree);
            g_hMscoree = NULL;
        }
        g_clrStarted = FALSE;
        DeleteCriticalSection(&g_csClr);
        break;
    }
    return TRUE;
}
