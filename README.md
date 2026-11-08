# RTSys 插件 — Plug_in\ 接口移植脚手架

## 项目结构

```
RTSys 插件/
├─ PlugInAdapter/                          C# 托管类库 (.NET Framework 4.8)
│   ├─ PlugInAdapter.csproj
│   ├─ PlugInAbi.cs           Plug_in\ ABI 定义（PlugIn_GetHandle/SetDockBar/SetAutoSize/Create/Reserved/Destroy）
│   ├─ PlugInHostAdapter.cs   托管适配器单例，对应全部 6 个 native 导出
│   ├─ PluginRuntime.cs       启动 HTTP API 服务 + WPF 视图（委托注入）
│   └─ ZMotionAPI.cs          P/Invoke zmotion.dll（从上游项目迁移，业务逻辑不变）
├─ PlugInNativeShim/            C++/CLI 原生 shim（RTSys.exe 实际加载的 DLL）
│   ├─ PlugInNativeShim.vcxproj (/clr，Win32)
│   ├─ PlugInShim.cpp           __declspec(dllexport) 导出 PlugIn_* 转发托管
│   ├─ PlugInShim.def           模块定义（6 个导出）
│   ├─ pch.h / pch.cpp
└─ README.md
```

## ABI 对照表（已由 IDA 反汇编精确确认）

| 导出名 | 签名 | 调用约定 | 参数说明 | 返回值 |
|---|---|---|---|---|
| PlugIn_GetHandle | `void(__cdecl*)(void**)` | __cdecl | pHostContext: 输出 `void**`，插件把宿主上下文写入 | void |
| PlugIn_SetDockBar | `bool(__cdecl*)(void)` | __cdecl | 零参数，能力查询 | true=支持 DockBar |
| PlugIn_SetAutoSize | `bool(__cdecl*)(void)` | __cdecl | 零参数，能力查询 | true=自动尺寸 |
| PlugIn_Create | `CWnd*(__cdecl*)(void*)` | __cdecl | pHost: CPluginView* 或 主框架指针 | 插件窗口 CWnd* |
| PlugIn_Reserved | `void(__cdecl*)(int)` | __cdecl | reserved 恒为 0 | void（忽略） |
| PlugIn_Destroy | `void(__cdecl*)(void*)` | __cdecl | pViewOrWindow: 窗口指针，需在 AfxFreeLibrary 前调用 | void |

## 构建步骤

### 前提
- Visual Studio 2019/2022，C++/CLI 工作负载
- .NET Framework 4.8 开发者包
- Trio.SharedLibrary.dll 可用（外部依赖 DLL，若不可用，见下方“备选”）

### 1. 编译 C# 托管库 (PlugInAdapter)
```
msbuild <插件目录>\PlugInAdapter\PlugInAdapter.csproj /p:Configuration=Release /p:Platform="Any CPU"
```
输出：`PlugInAdapter\bin\Release\RTSysPlugInAdapter.dll`

### 2. 编译 C++/CLI 原生 shim (PlugInNativeShim)
在 Visual Studio 中打开 `PlugInNativeShim.vcxproj`，配置：
- 配置：Release | Win32
- CLR 支持：/clr (`<CLRSupport>true</CLRSupport>`)
- 引用 RTSysPlugInAdapter.dll（`..\PlugInAdapter\bin\Release\RTSysPlugInAdapter.dll`）
- 模块定义文件：`PlugInShim.def`

输出：`PlugInNativeShim\Release\PlugInShim.dll`

### 3. 部署到 RTSys
将 `PlugInShim.dll` 及所有依赖（RTSysPlugInAdapter.dll、Trio.SharedLibrary.dll（外部依赖）、Newtonsoft.Json.dll 等）复制到：
```
<以 RTSys 安装目录>\Plug_in\<模块名>\PlugInShim.dll
```
RTSys 启动时会遍历 `Plug_in\` 目录，`AfxLoadLibrary` 加载并按 `GetProcAddress("PlugIn_*")` 调用。

### 4. 生成 RTBasic 帮助库（可选，建议）

RTBasic 命令参考库由本机 RTSys 自带的帮助文档生成，生成物不纳入版本控制：

```
python tools/build_rtbasic_skills.py --src "<RTSys安装目录>\help\RTBasic"
```

输出到 `PlugInWorker/skills/rtbasic/zh/`（已加入 `.gitignore`）。未生成时插件仍可运行，只是命令查询只能使用内置提示词。

## 关键设计决策

### 为什么用 C++/CLI shim 而不是 DllExport？
.NET Framework 4.8 **不支持** `UnmanagedCallersOnly`，托管 DLL 无法直接导出原生函数。方案对比：
- **C++/CLI shim**（本方案）：最可靠，VS 原生支持，native↔managed 转发清晰，调试方便。
- **DllExport (Robert Giesecke)**：net48 兼容性差，VS 版本依赖强，不推荐。

### 为什么直接 P/Invoke zmotion.dll 而不走宿主 hostCtx？
上游项目的 `ZMotionAPI.cs` 已直接 P/Invoke `zmotion.dll` 的 100+ `ZMC_*` 函数（`ZMC_Execute`/`ZMC_RegisterRead/Write` 等），完全绕过宿主 `*_GetMainConHandle` 虚表模式。移植时**无需修改**这套逻辑，直接复用即可。

## 备选：若 Trio.SharedLibrary 不可用

`PlugInHostAdapter.cs` 和 `PluginRuntime.cs` 目前不直接继承 `Trio.SharedLibrary.PlugInBase`（避免编译依赖），而是自行实现生命周期。若需要与 MP 原有 `PlugInBase` 语义完全对齐，需补充：
- `PlugInBase(ResourceDictionary[], IFactoryBase[])` 构造函数
- `protected virtual void OnInitialize()` / `OnDispose()`
- `IFactoryBase` / `IPlugInInfo` / `PlugInInfoBase`

这些可在拿到 `Trio.SharedLibrary.dll` 后按 ABI agent 已提取的 API 清单补全。

## 调试检查清单

- [ ] `PlugInShim.dll` 导出表包含 6 个函数（`dumpbin /exports PlugInShim.dll`）
- [ ] RTSys 启动后 `Plug_in\` 目录下插件被 `AfxLoadLibrary` 加载
- [ ] `PlugIn_GetHandle` 被调用，宿主把 `hModule` 全局作为 `void*` 传回
- [ ] `PlugIn_Create` 返回的窗口句柄被宿主 `ShowWindow(5)` / `MoveWindow`
- [ ] HTTP API 服务器端口可访问（默认由 `ApiServer.Start()` 决定）
- [ ] `ZMC_Execute` 等 P/Invoke 调用 `zmotion.dll` 成功（需先 `ZMC_Open` 连接控制器）
