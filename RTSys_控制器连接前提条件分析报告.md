# RTSys 软件栈 — 功能操作前提条件综合分析报告

**目标项目**：`<repo>`  
**分析对象**：固高 (ZMotion) RTSys.exe + zmotion.dll + 扩展 DLL (ExtRTSysScope, ExtRTSysEtherCAT, ExtMainHmi, ZMotionPlc, ZMotionCad3, ZvEpm, ZHmiEx, zvexten_h17, zvexten_h22)  
**分析工具**：IDA Pro (11 实例，全部在线)，项目完整 C# 源码，运行时日志 (plugin.log / shim_diag.log / config.json)  
**报告目的**：为 TrioAI_RTSys 插件代码修复提供“各功能操作前提条件，特别是是否需要 IP 连接控制器”的依据  
**报告日期**：2026-11-07  
**状态**：RTSys.exe / zmotion.dll / 项目源码分析已完成；扩展 DLL 部分基于已知导出表与文件名推断（标注“待确认”的项需进一步反编译）

---

## 1. 一览表：各功能域是否需要控制器连接

| 功能域 | 需 IP/控制器连接 | 核心前提条件 | 代表证据 |
|---|---|---|---|
| 插件加载（Plug_in\ 目录 DLL） | **否** | 插件 DLL 文件存在；导出 `PlugIn_Create`（必须）、`PlugIn_SetDockBar`（停靠窗口时）、可选 `PlugIn_GetHandle`/`SetAutoSize`/`Reserved` | RTSys.exe `sub_50AA70`；`AfxLoadLibrary` + `GetProcAddress("PlugIn_*")`；**全程无连接状态检查** |
| Ribbon UI / 菜单初始化 | **否** | Plug_in 目录资源文件存在 | RTSys.exe `sub_4F8400` 遍历插件构造 ribbon 按钮；无连接检查 |
| RTBasic 离线语法校验 | **否** | 源码文本；白名单与正则 | `AiService.ValidateRTBasicCode`（纯正则，不访问控制器） |
| 程序源码本地读写（离线缓存） | **否** | `%APPDATA%\RTSysAI\programs\` 目录可写 | `ProgramWorkspace.ReadLocalSource/WriteLocalSource` |
| AI 聊天 / 技能 / 查询 / 记忆 | **否** | API Key 已配置（`config.json` `apiKey`）；不涉控制器命令 | `ApiServer` 路由 `/skills`、`/lookup`、`/memory`、`/chat` |
| API 根路径 `/` 探测 | **部分** | 无 IP 时返回 `controllerConfigured:false`；有 IP 时返回 `configured`；新增返回 `currentMode`(AutoSearch/StaticIp/Simulator) 和 `cachedIp` | `ApiServer` 路由 `GET /` 及新增 `GET /api/controller` |
| 控制器以太网连接建立 | **可选**（自动搜索优先） | `ZMC_SearchEth` 自动发现 IP → `ZMC_SearchAndOpenEth` 直接连接；或 `ZMC_OpenEth(ip, &handle)` | zmotion.dll 导出 `ZMC_SearchEth`(0x10001f40)、`ZMC_SearchAndOpenEth`(0x1000ba30)、`ZMC_OpenEth`(0x1000b940) |
| 控制器串口连接建立 | **可选** | `ZMC_OpenCom(端口号, &handle)`，波特率另设 `ZMC_SetComDefaultBaud` | zmotion.dll 导出 `ZMC_OpenCom`(0x1000b880)、`ZMC_SetComDefaultBaud` |
| 控制器 PCI 连接建立 | **可选** | `ZMC_OpenPci(卡索引, &handle)`，先查 `ZMC_GetMaxPciCards` | zmotion.dll 导出 `ZMC_OpenPci`(0x1000b980)、`ZMC_GetMaxPciCards` |
| 仿真器连接 (ZvEpm/zmotionsimu) | **可选（离线）** | `ZMC_Open(type=0, "simulator", &handle)`；或加载 `zmotionsimu.dll` 并调用其导出函数 | zmotion.dll 导出 `ZMC_Open`(0x1000b610)；`zmotionsimu.dll` 17 个导出函数 |
| 程读取/写入/编译/下载/运行/停止 | **是** | 已建立控制器句柄 (`ZMotionHandle`) | `ZMC_Up3File`/`DownZar`/`MakeZar`/`RunZarFile`/`Stop` — 第 1 参数均为 handle |
| VR / TABLE 变量读写 | **是** | 已建连接句柄 | `ZMC_ArrayReadf`/`ArrayWritef` (name="VR"/"TABLE") |
| 寄存器读写 | **是** | 已建连接句柄 | `ZMC_RegisterRead`/`ZMC_RegisterWrite` |
| 数字 / 模拟 IO 读写 | **是** | 已建连接句柄 | `ZMC_GetIn`/`SetOutput`/`GetAIn`/`SetDaOut` |
| 轴状态 / 伺服使能 | **是** | 已建连接句柄 | `ZMC_GetAxises`/`GetAxisStates`/`SetAxisEnable` |
| 控制器系统时间设置 | **是** | `hModule.unused == 0 且 byte_8E8723 == 0`（已连接且未锁定） | RTSys.exe `sub_505790`；字符串 "Controller is not connected or locked to change system time." |
| 固件 / 应用复位、重连 | **是** | 已连接 | 字符串 "Controller reset to bios, Please connect again!" |
| 控制器锁定 / 解锁、ID 读写、重置控制器 | **是** | 已连接且未锁定 | "Controller is locked, can not be modified."；`ZMC_Get/SetControllerIDDate`；`ZMC_ResetController` |
| EtherCAT 扫描 / SDO 读写 | **是** | 已连接 | `ZMC_EcatSdo_Send`/`ZMC_EcatSdo_Recv` |
| Modbus 读写 | **是** | 已连接 | `ZMC_Modbus_Get0x/Set0x/Get4x/Set4x` |
| `ZMC_GetDllVersion` / `ZMC_SearchEth` / `ZMC_GetMaxPciCards` / `ZMC_GetMaxLocalRTes` | **否** | 无需句柄，可直接调用 | zmotion.dll 导出表，第 1 参数非 handle |

**核心判断**：zmotion.dll 中**以控制器句柄 (`IntPtr handle`) 作为第 1 参数的全部函数，都必须先通过 `ZMC_OpenEth`/`ZMC_OpenCom`/`ZMC_OpenPci` 建立连接才能调用**。RTSys.exe 项目侧通过 `ZMotionHandle.ConnectAsync(ip)` 统一封装该连接，连接后才可执行一切控制器操作。

---

## 2. 需控制器 IP 连接的功能（详细）

### 2.1 三种连接方式及其前提

| 方式 | zmotion.dll 函数 | 必需参数 | 前提条件 |
|---|---|---|---|
| 以太网 (IP) | `ZMC_OpenEth(string ip, out IntPtr handle)`<br/>`ZMC_SearchAndOpenEth(string ip, out IntPtr handle)` | 控制器 IP 字符串（如 `"192.168.1.100"`） | 控制器在线、网络可达、IP 正确 |
| 串口 (COM) | `ZMC_OpenCom(int port, out IntPtr handle)`<br/>`ZMC_Open(type=1, arg="COMx", &handle)` | COM 端口号；波特率（先 `ZMC_SetComDefaultBaud`） | 串口设备存在、波特率匹配 |
| PCI 板卡 | `ZMC_OpenPci(int cardIndex, out IntPtr handle)`<br/>`ZMC_Open(type=3, arg=cardIndex, &handle)` | PCI 卡索引；先 `ZMC_GetMaxPciCards(out count)` 确定范围 | 主板有固高 PCI 运动控制卡 |

### 2.2 必须已连接后才能调用的函数类别（zmotion.dll 导出）

全部以 `IntPtr handle` 作第 1 参数，调用前必须先 `ZMC_Open*` 获得有效 handle，并在不使用时 `ZMC_Close`：

- **程序管理**：`ZMC_MakeZar`/`DownZar`/`RunZarFile`/`Stop`/`Pause`/`Resume`/`CheckProgramSyntax`
- **文件系统 (3File/通用)**：`ZMC_Up3File`/`Down3File`/`Delete3File`/`FindFirst3File`/`FindNext3File` / `ZMC_UpFile`/`DownFile`/`DeleteFile`
- **变量读写**：`ZMC_ArrayReadf`/`ArrayWritef` (VR/TABLE/变量名)、`ZMC_RegisterRead`/`RegisterWrite`
- **IO**：`ZMC_GetIn`/`SetOutput`/`GetInAll`/`GetOutAll`/`SetOutAll`/`GetAIn`/`SetDaOut`
- **轴**：`ZMC_GetAxises`/`GetAxisStates`/`GetAxisFeatures`/`SetAxisEnable`
- **任务/进程**：`ZMC_GetTaskState`/`GetTaskStackState`/`GetFileState`
- **Modbus**：`ZMC_Modbus_Get0x/Set0x/Get4x/Set4x/GetLong/SetLong`
- **EtherCAT SDO**：`ZMC_EcatSdo_Send`/`ZMC_EcatSdo_Recv`
- **驱动器参数**：`ZMC_DrivePara_GetInfo`/`GetExistList`/`GetGroupInfo`
- **固件**：`ZMC_GetFirmWareInfo`/`CheckFirmWare`/`DownFirmWare`
- **控制器配置**：`ZMC_ResetController`/`ReturnDefault`/`RomZarDefault`/`SetConnectSecureID`/`SetCustomID`/`SetMac`/`GetMac`
- **状态/查询**：`ZMC_GetState`/`GetError`/`GetSysSpecification`/`GetSoftVersion`/`GetCurIpAddr`/`GetClientId`/`GetSoftId`/`GetHardId`/`GetChipId`/`GetControllerIDDate`

### 2.3 RTSys.exe 内的明确“必须先连接”代码

最直接证据：`sub_505790`（对应 `CDlgSetSysTemTime` 系统时间设置对话框）：

```
if ( !hModule.unused || byte_8E8723 )
    → 报错 "Controller is not connected or locked to change system time."
    → 直接返回，不弹出系统时间设置对话框
```

条件含义：
- `hModule.unused == 0`：控制器句柄有效（已建连接）
- `byte_8E8723 == 0`：控制器未被锁定

相关字符串（RTSys.exe 中直接引用，明确提示需先连接）：
- `0x69ed68` "Controller is not connected or locked to change system time."
- `0x68b2c8` "Controller reset to bios, Please connect again!"
- `0x68b3a8` "User cancel update, Controller reset to app, Please connect again!"
- `0x69e328` "Controller is locked, can not enter debug mode."
- `0x6add50` "Controller is locked, can not be modified."
- `0x6add80` / `0x6adf50` / `0x6adf78` "Controller not support RTHmi/ZHmi/ZPlc function."

---

## 3. 可离线使用的功能（不需控制器连接）

### 3.1 插件加载机制（RTSys.exe `sub_50AA70`）

前提（全部满足即可加载，无连接检查）：
1. 按钮 ID 在 `[37000, 37000 + 插件数量)` 范围，索引有效
2. 构造路径 `%安装目录%Plug_in\%插件名\%DLL文件名`
3. `PathFileExistsW(路径)` 为 TRUE —— **插件文件必须存在于磁盘**
4. 扩展名判断：`.dll`（或非 `.zpl`）走 `AfxLoadLibrary`；`.zpl` 用 `ShellExecuteW` 用系统关联程序打开（不作为插件加载）
5. `AfxLoadLibrary(路径)` 成功返回非 NULL HMODULE
6. `GetProcAddress(hModule, "PlugIn_Create")` 成功（**必须**导出此函数）
7. `GetProcAddress("PlugIn_SetDockBar")` 成功且返回 TRUE —— 决定是否创建停靠窗口
8. 可选：`PlugIn_GetHandle`、`PlugIn_SetAutoSize`、`PlugIn_Reserved`

**结论对项目修复**：插件加载本身不依赖控制器 IP。`PlugInShim.dll` / `RTSysPlugInAdapter.dll` 只要部署到 `<RTSys安装目录>\Plug_in\<模块名>\`，宿主启动时即会 `AfxLoadLibrary` 并调用 `PlugIn_SetDockBar`/`SetAutoSize`/`Create`；无需控制器在线。

### 3.2 RTBasic 离线语法校验

`AiService.ValidateRTBasicCode(code)` 纯本地正则 + 关键字白名单，不访问控制器，可在无 IP 情况下对源码做静态校验（DIM 必带 AS 类型、控制流闭合、禁止 VB.NET 语法、运动指令参数数量等）。

### 3.3 程序源码本地缓存读写

`ProgramWorkspace` 将 RTBasic 源码保存到 `%APPDATA%\RTSysAI\programs\<name>.bas` + `.meta.json`，**只写本地文件，不触及控制器**。离线可列程序、读源码、写源码、打补丁；标记 `isDirty=true` 表示“本地已改、尚未下载到控制器”，连接后才执行下载。

### 3.4 AI 聊天 / 技能 / 查询 / 记忆

`ApiServer` 路由 `/skills`、`/skill/xxx`、`/lookup`、`/memory`、`/tasks`、`/validate_basic`、`/search_code` 不需要控制器连接；只需 `apiKey` 已配置（`config.json` 当前已填写，`RTSysIP` 为空）。

### 3.5 API 根路径探测

`GET /` 返回 `{status, version, controllerConfigured, rtsysIp}`。无 `RTSysIP` 时 `controllerConfigured=false`，属预期行为，非错误。

---

## 4. 控制器 IP 的获取与配置链路

### 4.1 RTSys.exe（宿主）层

- **无直接 Winsock 导入**：RTSys.exe 未导入 `WS2_32.dll` / `wininet.dll`，所有以太网/串口/PCI 操作均委托给 `zmotion.dll`。
- **无 IP 配置持久化字符串**：RTSys.exe 中未搜索到 `"RTSysIP"`、`"IPAddress"`、`"Disconnect"`、`"NotConfigured"`、`"Unreachable"`；也未直接导入 `kernel32` 的 `LoadLibrary`/`GetProcAddress`（通过 mfc100 的 `AfxLoadLibrary` 间接加载插件）。
- **连接对话框**：存在类 `CDlgConnect` (`0x7098d0`)，应是 IP/连接参数的输入界面（具体实现需进一步反编译）。
- **连接建立后可查询**：`ZMC_GetCurIpAddr`、`ZMC_GetConnectType`、`ZMC_GetConnectString`（zmotion.dll 导出）可读取当前已连接控制器的 IP、连接类型、连接字符串。

### 4.2 项目侧（TrioAI_RTSys 插件）层

配置文件 `%APPDATA%\RTSysAI\config.json`，当前内容关键项：
- `RTSysIP` = `""`（空，**当前未配置控制器 IP**）
- `apiKey` = 已配置（已脱敏）

读取/写入逻辑（源码证据）：
- `RTSysHandlers.GetRTSysIpFromConfig()`：读取 `config.json` 的 `RTSysIP` 键，返回 Trim 后字符串，空则返回 null
- `RTSysHandlers.SetRTSysIp(ip)`：写入 `config.json` 的 `RTSysIP`，并废止连接状态缓存、释放旧句柄
- `ZMotionHandle.ConnectAsync(ip)` → `ZMotionAPI.ZMC_OpenEth(ip, out h)`：以太网连接入口
- `ZMotionHandle.ConnectComAsync(portName, baudRate)` → `ZMC_Open(1, port, out h)`：串口连接入口
- `ZMotionHandle.ConnectPciAsync(cardIndex)` → `ZMC_Open(3, cardIndex, out h)`：PCI 连接入口

连接状态机（`RTSysHandlers`）：
- `NotConfigured`：未配置 `RTSysIP` → 控制器相关操作返回结构化错误（中文提示：未配置 IP，请在「设置」填写 RTSys 控制器 IP）
- `Disconnected`：已配置 IP 但无法建立连接 → 提示检查网络和控制器在线
- `Connected`：句柄有效 → 可执行控制器操作

### 4.3 插件 API 层对外表现

| 路由 | 无 RTSysIP | 有 RTSysIP 且控制器在线 | 有 RTSysIP 但控制器不可达 |
|---|---|---|---|
| `GET /` | `controllerConfigured:false` | `controllerConfigured:true` | `controllerConfigured:false`（句柄无效时） |
| `GET /api/status` | `NOT_CONFIGURED` + 提示 | `connected:true` + firmware/axis | `ControllerUnreachableMessage` |
| `GET /api/programs` | 本地缓存 only（离线安全） | 合并本地 + 控制器程序 | 同上退化 |
| `PUT /api/programs/{name}/source` | 仅保存本地缓存，`downloadedToController:false` | 编译+下载到控制器 | 保存本地，下载失败，标记 `pendingUpload` |
| `POST /api/programs/{name}/compile|run|stop` | `ControllerUnavailableError` | 正常执行 | `ControllerUnavailableError` |

**关键设计**：所有需要控制器句柄的 handler 统一走 `Safe`/`SafeAsync` + `GetHandleAsync()`；未配置 IP 或连接失败时返回结构化 `{success:false, error, hint, connectionState, requiresController:true}`，**不抛异常外泄**，AI 可直接将 `hint` 转述给用户。

---

## 5. 各扩展 DLL 前提条件（已由 PE 导入/导出表与字符串验证）

> 全部 9 个扩展 DLL 的 IDA 实例均已完成分析。以下结论基于 PE 导入表、导出表与字符串引用，非推断。

### 5.1 一览表

| DLL | 核心功能 | 需控制器连接 | 独立初始化/连接函数 | 导入 zmotion 并调 `ZMC_Open*`？ | 前提条件 |
|---|---|---|---|---|---|
| `ExtRTSysScope.dll` | 示波器波形采集/显示 | **是**（核心采集） | 无（仅 `Scope_GetMainConHandle` 取外部句柄） | 导入 zmotion 6 个函数，**无 `ZMC_Open*`** | **需控制器连接** |
| `ExtRTSysEtherCAT.dll` | EtherCAT 配置/诊断/参数下发 | **是** | 无（仅 `ECAT_GetMainConHandle` 取句柄） | 导入 zmotion 12 个函数，**无 `ZMC_Open*`** | **需控制器连接** |
| `ExtMainHmi.dll` | 主 HMI 画面编辑/运行 | **部分** | 无（UI 资源初始化仅） | 导入 `ZMC_HmiFileEncode/Decode`，**无 `ZMC_Open*`** | 离线编辑可用；在线监控需控制器 |
| `ZMotionPlc.dll` | PLC 编译/编辑（IL/LAD/SFC） | **部分** | `ZPLC_DllInit`、`ZPLC_FileInit` | **不导入 zmotion**，无 `ZMC_Open*` | 离线编程/编译可用；下载/监控/在线调试需连接 |
| `ZmotionCad3.dll` | CAD 矢量/位图编辑 | **部分** | 无（`ZMotionCad3_SetController` 仅注入外部句柄） | 导入 `ZMC_GetSysSpecification`，**无 `ZMC_Open*`** | 离线绘图可用；控制器规格查询/在线通道需连接 |
| `ZvEpm.dll` | 仿真 EPM | **否**（仿真） | `ZvEpm_Init1`、`ZvEpm_Init2` | **不导入 zmotion**（导 `zmotionsimu.dll`） | **离线可用**，但依赖仿真层 `zmotionsimu.dll` |
| `ZHmiEx.dll` | HMI 扩展绘制/渲染 | **否** | `ZHmiEx_Init1/Init2/Init`、`InitCanvas` | **不导入 zmotion**（导 `zmotionsimu.dll` 279 func） | **离线可用**（仿真/纯渲染模式） |
| `zvexten_h17.dll` | Halcon 视觉（2D 码/形状匹配） | **否** | `zvext_init` | **不导入 zmotion**，无网络库 | **离线可用**，依赖 `halconcpp.dll` + `zvlib.dll` |
| `zvexten_h22.dll` | Halcon 视觉（形状匹配增强） | **否** | `zvext_init` | **不导入 zmotion**，无网络库 | **离线可用**，依赖 `halconcpp.dll` + `zvlib.dll` |

### 5.2 关键发现

**9 个扩展 DLL 中无一导入并调用 `ZMC_Open*` 来自行建立控制器连接。**

所有需要控制器的扩展 DLL 均通过以下两种方式复用宿主已建立的连接：
1. **取宿主句柄**：通过 `Scope_GetMainConHandle` / `ECAT_GetMainConHandle` / `ZMotionCad3_SetController` 获取外部句柄注入；
2. **调用 zmotion 的 handle 型函数**：复用 `ZMC_GetConnectType` / `ZMC_Execute` / `ZMC_DirectCommand` / `ZMC_RegisterRead` / `ZMC_GetCurIpAddr` 等，这些函数的首参为 handle，隐式依赖主程序已建立的连接。

**真正负责建立控制器连接的是宿主 `RTSys.exe`（端口 13347）配合 `zmotion.dll`（端口 13337）。**

### 5.3 各 DLL 详细证据

**ExtRTSysScope.dll**（示波器）
- 导出：`CExtDialogScope`、`Scope_GetMainConHandle`、`Scope_GetSpecification`、`Scope_HideWindow`、`Scope_GetMainThemeColor/DPI`
- 字符串：`SCOPE`、`TRIGGER`、`Channel A/B`、`Auto-trigger`、`Manual-trigger`、`Condition-trigger`、`Export waveform`、`Search waveform`
- **需连接证据**：字符串 `Controller not connected.`；导入 zmotion 的 `ZMC_GetAxises`、`ZMC_RegisterRead`、`ZMC_DirectCommand`、`ZMC_GetConnectType`、`ZMC_Execute`、`ZMC_ExecuteNoAck`

**ExtRTSysEtherCAT.dll**（EtherCAT）
- 导出：`ECAT_ConvertXmlToZml`、`ECAT_ExportConfig/ImportConfig`、`ECAT_LoadXmlzmlFile`、`ECAT_OpenSlotDlg`、`ECAT_UpdatePrj`、`ECAT_EmergencyStop`、`ECAT_GetDebugState`
- 字符串：`EtherCAT nodes`、`Slave identity`、`Serial no`、`Scan slaves`、`Add slave`、`EtherCAT-%d : %s`、`PCI464`
- **需连接证据**：字符串 `Controller not connected.`、`This operation will immediately download the parameters of all axes to the controller`；导入 zmotion 的 `ZMC_GetCurIpAddr`、`ZMC_GetConnectType`、`ZMC_DirectCommand`、`ZMC_GetOutput/SetOutput`、`ZMC_GetIn`、`ZMC_EnumChildCard`、`ZMC_CustomCommand`、`ZMC_GetAxises`、`ZMC_GetAxisFeatures`、`ZMC_GetSysSpecification`

**ExtMainHmi.dll**（主 HMI，424 个导出）
- 类：`ZHmi_WindowClass`、`ZHmi_ControlClass`、`ZHmi_D2DDraw`、`ZHmi_DataSampLibClass`、`ZHmi_AlmListClass`、`ZHmi_RecipeLibClass`、`ZHmi_PicLibClass`、`ZHmi_StrLibClass`
- 导出：`ZHMI_RegisterControl`、`ZHMI_RegisterFeature`、`ZHMI_GetControl`、`ZHMI_SetDefTextFormat`
- 字符串：`PlcControl`、`RecipeLib`、`Tooltip`、`Close with father`、`ZMotionCad3.dll`
- 导入 zmotion 仅 `ZMC_HmiFileEncode`/`ZMC_HmiFileDecode`（HMI 文件加解密，非 `ZMC_Open*`）；同时导入 `ZMotionCad3.dll`；无 WS2_32

**ZMotionPlc.dll**（PLC，111 个 `ZPLC_*` 导出）
- 导出：`ZPLC_CompileFile`、`ZPLC_CompileLine`、`ZPLC_ILRead/Write`、`ZPLC_ILtoBin/LAD/SFC`、`ZPLC_LADRead/Write/AddItem/DelItem`、`ZPLC_FileLoad/Save/Init`、`ZPLC_DllInit/Free/Release`
- 字符串：`Func can not connect to bus.`、`MC can not connect to bus.`、`OR can not connect to bus.`、`ZMotionPlc Dynamic Link Library`
- **不导入 zmotion.dll**；导入 `WSOCK32.dll`（`WSAStartup`/`WSACleanup`）与 `KERNEL32.dll`（`CreateFileA`/`WriteFile`/`ReadFile`/`CloseHandle`），用于下载通信

**ZmotionCad3.dll**（CAD，30 个 `ZMotionCad3_*` 导出）
- 导出：`ZMotionCad3_NewGraph/NewSeg/NewVect/NewImage/NewVectGroup/Draw/DrawWnd/ImportImage/ImportVectGraph/OptimizePath/SmoothPath/Scale/Move`
- 字符串：`$ACADVER`、`.?AVZCadReader@@`、`gdiplus.dll`、`CAD_CHANNEL`、`CAD_LOADFILE/CAD_LOADBIN/CAD_LOADTABLE/CAD_MOUSE/CAD_CLOSE/CAD_DISPLAY/CAD_PARA/CAD_TOCODE/CAD_AUTOVIEW`
- 导入 zmotion 仅 `ZMC_GetSysSpecification`；导入 `WS2_32.dll`（`WSAStartup`/`htonl`/`htons`/`ntohl`/`ntohs`）

**ZvEpm.dll**（仿真 EPM，2 个导出）
- 导出：`ZvEpm_Init1`、`ZvEpm_Init2`；导入 `zmotionsimu.dll`（17 个函数，如 `ZBasic_*`、`ZDynaCM_SetModuleVersion`）；**不导入 zmotion.dll**，无 WS2_32

**ZHmiEx.dll**（HMI 扩展，297 个导出）
- 导出：`ZHmiEx_*`/`ZHmi_Draw_*`/`ZHmiEx_Canvas_*`/`ZPinyin_*`
- 字符串：`CAD_LOADFILE`、`CAD_CLOSE`、`CAD_TAB2CODE`、`ZMotionCad3_NewImage`、`Online command warn/error`、`CAD channel %d over max`
- 导入 `zmotionsimu.dll` 279 个函数（含 `ZMotion_Buff_GetCurMark`、`ZHmi_Device_GetValue`）；**不导入 zmotion.dll**，无 WS2_32

**zvexten_h17.dll**（Halcon 视觉，44 个 `zvext_*` 导出）
- 导出：`zvext_code2d_create/find/getResult`、`zvext_shape_create/find/contours`、`zvext_init`
- 导入 `halconcpp.dll`（89 个函数）+ `zvlib.dll`（21 个函数）；**不导入 zmotion.dll**，无 ws2_32/wsock32

**zvexten_h22.dll**（Halcon 视觉，29 个 `zvext_*` 导出）
- 导出：`zvext_init`、`zvext_shape_create/find/findRe/finds`
- 导入 `halconcpp.dll`（61 个函数）+ `zvlib.dll`（21 个函数）；**不导入 zmotion.dll**，无网络库

### 5.4 对 TrioAI_RTSys 的直接含义

1. **插件无需自行建立控制器连接**：宿主 RTSys.exe 已经（或尚未）通过 zmotion.dll 建立连接；插件与扩展 DLL 都复用同一套连接。项目侧 `RTSysHandlers.GetHandleAsync()` 独立调用 `ZMC_OpenEth` 建立自己的句柄，**与宿主连接相互独立**——即插件可以有自己的连接，不必等待宿主连接。
2. **Scope / EtherCAT 功能的可用性取决于宿主连接**：项目侧 `Handlers.OpenOscilloscope()` 调用 `mw.OpenToolWindow("Oscilloscope")`，该窗口由 `ExtRTSysScope.dll` 实现，其核心采集功能需要宿主已连接控制器。**当前项目代码未检查这一点**，可能在宿主未连接时打开出空白/报错的示波器窗口。建议在 `OpenOscilloscope()` 中先确认宿主 `Controller.IsConnected`，未连接时返回明确提示。
3. **EtherCAT 相关 API**：项目 `RTSysHandlers.ScanEtherCAT` 目前是占位实现（返回空 devices + 提示信息），`EtherCATReadSDO`/`EtherCATWriteSDO` 直接调 zmotion 的 `ZMC_EcatSdo_Recv/Send`。这些函数首参为 handle，需控制器连接；且真实 EtherCAT 功能应由 `ExtRTSysEtherCAT.dll`（其 `ECAT_OpenSlotDlg`、`ECAT_GetDebugState` 等导出）承担。
4. **PLC 功能（`ZMotionPlc.dll`）不依赖 zmotion**：其离线编译能力（IL↔LAD↔SFC 互转、语法检查）无需控制器。当前 `RTSysHandlers` 对 IEC/PLC 相关接口一律返回 "not supported on RTSys"（`ReadIecVariables`/`WriteIecVariables`/`GetIecTaskDetail`），这是**主动裁剪**而非能力限制——若后续需要支持 PLC，可评估接入 `ZPLC_*` 导出。

---

## 6. 对 TrioAI_RTSys 代码修复的建议（可直接落地）

基于以上证据，给出优先级排序的修复/确认项：

### P0：控制器连接前提的统一守门（已基本实现，需保持）

所有需控制器句柄的 handler（`ReadVR`/`WriteVR`/`ReadTable`/`WriteTable`/`ReadDriveParam`/`WriteDriveParam`/`ScanEtherCAT`/`EtherCATReadSDO`/`EtherCATWriteSDO`/`ListAxes`/`GetAxisDetail`/`GetIn`/`SetOutput`/`GetInAll`/`GetOutAll`/`SetOutAll`/`RegisterRead`/`RegisterWrite`/`DirectCommand`/`ListPrograms`/`ReadSource`/`WriteSource`/`CompileProgram`/`RunProgram`/`StopProgram` 等）均已统一通过 `Safe`/`SafeAsync` → `GetHandleAsync()` 守门。

**确认项**：检查 `Handlers.cs`（旧版 MP 风格，`RequireDirectConnection`）与 `RTSysHandlers.cs`（新版 RTSys 风格，`GetHandleAsync`）两套代码是否对同一套路由都正确守门，避免个别路由绕过连接检查直接访问 `Controller.CommandQueue` 导致空引用。

### P1：`config.json` `RTSysIP` 为空时的用户引导（当前为空，需明确）

- 当前 `config.json` 中 `RTSysIP = ""`，**所有控制器相关 API 预期返回 `NOT_CONFIGURED`**，属正常行为非缺陷。
- 建议在插件设置页或首次点击控制器相关功能时，弹出提示引导用户填写控制器 IP（参考 `ControllerNotConfiguredMessage` 中文提示）。
- 若项目需“自动发现控制器 IP”，可调用 zmotion.dll 的 `ZMC_SearchEth()`（无需句柄，可离线调用）扫描局域网控制器，再将发现的 IP 写入 `config.json` 的 `RTSysIP`。

### P2：离线编辑 vs 下载到控制器的语义边界（已正确实现，需回归测试）

- `WriteSource` / `PatchSource` 始终先写本地缓存（`ProgramWorkspace.WriteLocalSource`，`isDirty=true`），再根据连接状态决定是否下载：
  - 未配置 IP / 已断开：仅保存本地，`downloadedToController=false`，明确提示“连接后再执行下载”
  - 已连接：编译 + `ZMC_MakeZar` + `ZMC_DownZar` 到控制器，成功后 `MarkSynced`
- **回归测试点**：断开控制器后写源码应成功（本地缓存）；重连后执行 `compile`/`run` 应使用最新源码。

### P3：RTBasic 语法校验完全离线（已实现）

`ValidateRTBasicCode` 不依赖控制器，可在任意时刻调用。建议在 `WriteSource`/`PatchSource` 写入前即调用，校验失败时阻止写入并返回错误列表（当前 `WriteSource` 已在连接后编译前做了 `ValidateRtBasicCode`，建议也在纯本地写入路径做一次，避免把错误源码缓存到本地）。

### P4：插件部署路径前提（对修复部署脚本的意义）

插件需部署到 `<RTSys安装目录>\Plug_in\AIPlugin\`（当前实际部署目录），包含：
- `PlugInShimNative.dll`（纯原生，6 个 `__cdecl` 导出）
- `RTSysPlugInAdapter.dll`（托管适配器，.NET 4.8）
- `RTSysPlugInWorker.exe`、`TrioSharedLibrary.dll`、`TrioBaseLib.dll`、`TrioCustomControlsLib.dll` 等依赖
- `skills/` 目录

宿主 RTSys 启动时遍历 `Plug_in\`，`AfxLoadLibrary` 加载 `PlugInShimNative.dll`，再通过 `GetProcAddress("PlugIn_*")` 调用。无需控制器在线即可完成插件加载与面板嵌入（`plugin.log` / `shim_diag.log` 已证实：`GetHandle`/`SetDockBar`/`SetAutoSize`/`Create`/`Reserved` 均成功，`Create() OK hwnd=0x430D0C`）。

### P5：已发现缺陷（优先修复）

1. **ChatPanel 历史按钮 UI 异常**（高置信度，`plugin.log` 19:28:24 / 19:49:12）：`ChatPanel.OnHistory` 调用 `MessageBox.Show(Window owner, ...)` 时 `owner` 为 null，抛 `ArgumentNullException`，UI Dispatcher 未捕获。影响：点击“历史”按钮可能使插件 UI 崩溃。建议在 `OnHistory` 中传入有效 owner 窗口，或在 `MessageBox.Show` 周围加 try/catch。
2. **首次加载未完成观察项**（`19:09:58` `Create()` 在 `AttachView() creating HwndSource...` 后无后续）：建议用当前构建（`RTSysPlugInAdapter.dll` 21:10 构建）复现，确认 `plugin.log` 出现 `Create() OK hwnd=`。

---

## 7. 证据清单

| 证据类型 | 路径 / 地址 | 说明 |
|---|---|---|
| IDA 实例列表 | `mcp__ida-pro-mcp__list_instances` | 11 实例全部在线：RTSys.exe(13347)、zmotion.dll(13337)、扩展 DLL(13338–13346) |
| zmotion.dll 导出表 | `survey_binary` @ 13337 | 397 函数，232 已命名；`ZMC_OpenEth`(0x1000b940)、`ZMC_OpenCom`(0x1000b880)、`ZMC_OpenPci`(0x1000b980)、`ZMC_Close`(0x1000b400)、`ZMC_GetCurIpAddr`(0x10004040)、`ZMC_GetConnectType`(0x10002330) 等 |
| RTSys.exe 基本信息 | `survey_binary` @ 13347 | 10132 函数，1908 已命名；入口 `.text` 0x401000–0x5c1000 |
| “必须先连接”代码 | RTSys.exe `sub_505790` (0x505790) | `if (!hModule.unused \| byte_8E8723)` → "Controller is not connected or locked to change system time." (0x69ed68) |
| 插件加载代码 | RTSys.exe `sub_50AA70` (0x50aa70) | `AfxLoadLibrary` + `GetProcAddress("PlugIn_Create"/"SetDockBar"/"Create"/"Reserved")`；无连接检查 |
| 插件 ABI 定义 | `PlugInAbi.cs` | 6 个导出，`__cdecl`，签名与 IDA 确认一致 |
| 纯原生 shim | `PlugInShimNative.cpp` / `PlugInShim.def` | 6 个 `__declspec(dllexport)` `__cdecl` 导出，CLR 惰性启动 |
| 项目连接封装 | `ZMotionHandle.cs` / `ZMotionAPI.cs` | `ConnectAsync`/`ConnectComAsync`/`ConnectPciAsync` 调用 `ZMC_OpenEth`/`ZMC_Open`/`ZMC_OpenPci` |
| 项目连接守门 | `RTSysHandlers.cs` | `GetConnectionState()`/`GetHandleAsync()`/`ControllerUnavailableError()` 三状态（NotConfigured/Disconnected/Connected） |
| 项目本地缓存 | `ProgramWorkspace.cs` | `%APPDATA%\RTSysAI\programs\`，只读/只写本地，不触控制器 |
| 离线语法校验 | `AiService.cs` (ValidateRTBasicCode) | 纯正则，不访问控制器 |
| 配置文件 | `%APPDATA%\RTSysAI\config.json` | `RTSysIP=""`（当前未配置），`apiKey` 已配置 |
| 运行时日志 | `plugin.log` / `shim_diag.log` | 证实插件可成功加载、面板嵌入、`Create() OK hwnd=0x430D0C`；历史按钮异常 |
| API 能力基线 | `tools/API_BASELINE.md` | 33 路由实测状态（当前插件未激活，全部 UNREACHABLE/SKIP_WRITE） |

---

## 8. 结论摘要

1. **并非所有功能都需要 IP 连接控制器**。插件加载、Ribbon UI、RTBasic 离线语法校验、本地程序缓存读写、AI 聊天/技能/查询/记忆、API 根路径探测，均可在无控制器 IP 或控制器离线时使用。
2. **一切涉控制器数据读写的功能都必须先建立连接**：程读取/写入/编译/运行、VR/TABLE、寄存器、IO、轴状态、EtherCAT/Modbus、系统时间设置、固件复位、控制器锁定/ID/重置。前提是通过 `ZMC_OpenEth`（以太网 IP）/`ZMC_OpenCom`（串口）/`ZMC_OpenPci`（PCI）获得有效句柄。
3. **控制器 IP 的获取与配置**：RTSys.exe 委托 zmotion.dll 处理网络（无直接 Winsock 导入，无 `"RTSysIP"` 字符串）；项目侧通过 `config.json` 的 `RTSysIP` 键（当前为空）配置，`ZMotionHandle.ConnectAsync(ip)` 调用 `ZMC_OpenEth`。连接后可用 `ZMC_GetCurIpAddr`/`ZMC_GetConnectType`/`ZMC_GetConnectString` 查询已连控制器信息。
4. **对 TrioAI_RTSys 修复的直接指导**：保持现有的 `GetHandleAsync()` 统一守门；`RTSysIP` 为空时返回结构化 `NOT_CONFIGURED` 提示引导用户填 IP；离线写入只落本地缓存并标记 `isDirty`，连接后再下载；优先修复 ChatPanel 历史按钮 `ArgumentNullException` 缺陷。

> 扩展 DLL（ExtRTSysScope / ExtRTSysEtherCAT / ExtMainHmi / ZMotionPlc / ZMotionCad3 / ZvEpm / ZHmiEx / zvexten_h17 / zvexten_h22）的确切前提条件，其 IDA 实例均在线（端口 13338–13346），可按需继续反编译补全。
