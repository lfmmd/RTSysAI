# RTSysAI Tool → 软件实际函数 对应关系报告

**目标项目**：`<repo>`  
**分析对象**：AI 工具 (AiTools.cs) → HTTP 路由 (ApiServer.cs) → 处理器 (RTSysHandlers.cs / AiService.cs) → 底层实际函数 (zmotion.dll `ZMC_*` / 宿主 MP API / 纯本地)  
**报告日期**：2026-11-07  
**证据来源**：项目完整 C# 源码 + IDA Pro 反编译 (zmotion.dll / RTSys.exe) + 运行时日志

---

## 1. 总览：调用链路

```
AI 工具 (AiTools.cs DispatchTool)
    │
    ├─► RTSysHandlers.<方法>()  (RTSysHandlers.cs)
    │       │
    │       ├─► ZMotionHandle 封装层 (ZMotionExtensions.cs / ZMotionAPI.cs)
    │       │       └─► zmotion.dll ZMC_*  (首参为 handle 的函数，需先 ZMC_Open*)
    │       │
    │       ├─► 宿主 MP 控制器对象 (Controller.CommandQueue / Axes / SysVars / ...)
    │       │       └─► Trio.CommunicationsLibrary / Trio.SharedLibrary (VR/TABLE/IO/轴/驱动/EtherCAT)
    │       │
    │       └─► 纯本地 (ProgramWorkspace 本地缓存 / 正则语法校验 / 技能文档)
    │
    └─► AiService.<方法>()  (纯内存/文件，不访问控制器)
            └─► 本地文件 / 内存任务 / Plan Mode 状态
```

每条 AI tool 的调用都经过 `ExecuteTool` → `DispatchTool`。写操作（`WriteTools` 集合）还会触发确认/Plan Mode 拦截；读操作可绕过 UI 线程（`PureIoTools`）。

---

## 2. 完整对应表

| # | AI 工具名 | HTTP 路由 (ApiServer.cs) | Handler 方法 | 底层实际函数 / API | 需控制器连接 | 前提条件 |
|---|-----------|--------------------------|--------------|-------------------|-------------|----------|
| 1 | `get_status` | `GET /api/status` | `RTSysHandlers.GetStatus()` | 若未连：本地状态；若已连：`ZMotionHandle.GetFullStatusAsync()` → `ZMC_GetSysSpecification` / `ZMC_GetSoftVersion` / `ZMC_GetState` / `ZMC_GetAxises` / `ZMC_FindFirst3File`… | 否（不连时返回本地状态+提示） | 未配置 IP 时返回 `NotConfigured` + 提示；已配置但未连时返回 `Disconnected` |
| 2 | `list_programs` | `GET /api/programs` | `RTSysHandlers.ListPrograms()` | 本地缓存 `ProgramWorkspace.ListLocalPrograms()`；若已连：句柄 `ZMC_FindFirst3File`/`ZMC_FindNext3File` | 部分 | 不连时仅返回本地缓存；已连时合并本地+控制器 |
| 3 | `read_source` | `GET /api/programs/{name}/source` | `RTSysHandlers.ReadSource(name)` | 本地缓存优先；未命中且已连：句柄 `ZMC_Up3File(handle, name, buffer)` | 部分 | 不连时仅读本地缓存；不连且未缓存则返回连接错误 |
| 4 | `write_source` | `PUT /api/programs/{name}/source` | `RTSysHandlers.WriteSource(name, body)` | 本地缓存 `ProgramWorkspace.WriteLocalSource`；若已连：`ZMC_MakeZar` + `ZMC_DownZar` 编译下载 | 部分 | 始终写本地；不连时只标记 `pendingUpload`，不下载到控制器 |
| 5 | `patch_source` | `PATCH /api/programs/{name}/source` | `RTSysHandlers.PatchSource(name, body)` | 同上（源码文本替换后再写本地/下载） | 部分 | 同上 |
| 6 | `read_vr` | `GET /api/vr/{addr}?count=N` | `RTSysHandlers.ReadVR(addr, count)` | `ZMotionExtensions.ReadVRAsync(handle, addr, count)` → `ZMC_ArrayReadf(handle, "VR", addr, count, buf)` | **是** | 句柄有效（已 `ZMC_Open*`） |
| 7 | `write_vr` | `PUT /api/vr/{addr}` | `RTSysHandlers.WriteVR(addr, body)` | `ZMotionExtensions.WriteVRAsync(handle, addr, values)` → `ZMC_ArrayWritef(handle, "VR", addr, count, buf)` | **是** | 句柄有效；写操作需用户确认 |
| 8 | `read_table` | `GET /api/table/{addr}?count=N` | `RTSysHandlers.ReadTable(addr, count)` | `ZMotionExtensions.ReadTableAsync(handle, addr, count)` → `ZMC_ArrayReadf(handle, "TABLE", addr, count, buf)` | **是** | 句柄有效 |
| 9 | `write_table` | `PUT /api/table/{addr}` | `RTSysHandlers.WriteTable(addr, body)` | `ZMotionExtensions.WriteTableAsync(handle, addr, values)` → `ZMC_ArrayWritef(handle, "TABLE", addr, count, buf)` | **是** | 句柄有效；写操作需确认 |
| 10 | `list_axes` | `GET /api/axes` | `RTSysHandlers.ListAxes()` | `ZMotionHandle.GetAxisCount()` → `ZMC_GetAxises(handle)`；或 `ctrl.Axes` | **是** | 句柄有效 |
| 11 | `get_axis_detail` | `GET /api/axes/{index}` | `RTSysHandlers.GetAxisDetail(index)` | `ZMotionHandle.GetAxisStateAsync(axis)` → `ZMC_GetAxisStates(handle, axis, buf)` | **是** | 句柄有效 |
| 12 | `compile_program` | `POST /api/programs/{name}/compile` | `RTSysHandlers.CompileProgram(name)` | 读取源码（本地或 `ZMC_Up3File`），`ZMC_MakeZar(handle, srcFile, 0)` 编译；错误来自编译器返回码 | **是** | 句柄有效；RTBasic 语法先做本地正则校验 |
| 13 | `run_program` | `POST /api/programs/{name}/run` | `RTSysHandlers.RunProgram(name, body)` | `ZMotionExtensions.RunProgramAsync(handle, name)` → `ZMC_RunZarFile(handle, name)` | **是** | 句柄有效；需确认（影响实时控制器状态） |
| 14 | `stop_program` | `POST /api/programs/{name}/stop` | `RTSysHandlers.StopProgram(name, body)` | `ZMotionExtensions.StopProgramAsync(handle)` → `ZMC_Stop(handle)` | **是** | 句柄有效；需确认 |
| 15 | `lookup_command` | `GET /api/lookup?query=…` | `AiService.LookupCommand(query, full, library)` | 纯本地：技能文档 / 命令参考 HTML（triobasic/iec/plcopen） | **否** | 不访问控制器；会话级去重缓存 |
| 16 | `read_skill` | `GET /api/skill/{name}` | `AiService.ReadSkill(name)` | 纯本地：`skills/` 目录下的 markdown 文件 | **否** | 不访问控制器 |
| 17 | `discover_skills` | `GET /api/skills?category=…` | `AiService.DiscoverSkills(category)` | 纯本地：扫描 `skills/` 目录 | **否** | 不访问控制器 |
| 18 | `search_code` | `GET /api/search_code?query=…` | `RTSysHandlers.SearchCode(query)` | 纯本地：扫描 `ProgramWorkspace` 本地缓存 `.bas` 文件 | **否** | 不访问控制器；离线可用 |
| 19 | `read_drive_param` | `GET /api/drive/{axis}/{addr}?nd=N` | `RTSysHandlers.ReadDriveParam(axis, addr, nd)` | `ZMotionExtensions.EcatSdoReadAsync(handle, 0, axis, addr, 0, 7)` → `ZMC_EcatSdo_Recv(handle, slot, position, index, subIndex, dataType, value)` | **是** | 句柄有效；通过 EtherCAT SDO 读驱动器参数 |
| 20 | `write_drive_param` | `PUT /api/drive/{axis}/{addr}` | `RTSysHandlers.WriteDriveParam(axis, addr, body)` | `ZMotionExtensions.EcatSdoWriteAsync(handle, 0, axis, addr, 0, 7, value)` → `ZMC_EcatSdo_Send(handle, slot, position, index, subIndex, dataType)` | **是** | 句柄有效；需确认 |
| 21 | `scan_ethercat` | `GET /api/ethercat/devices?slot=N` | `RTSysHandlers.ScanEtherCAT(slot)` | `ETHERCAT.ExecuteInitProtocol(queue, slot, …)` → 底层 `ZMC_EcatSdo_*` / `ZMC_EnumChildCard` | **是** | 句柄有效；当前 RTSysHandlers 为占位实现（返回空 devices + 提示信息） |
| 22 | `read_ethercat_sdo` | `GET /api/ethercat/sdo?slot=&position=&index=&subindex=&type=` | `RTSysHandlers.EtherCATReadSDO(slot, pos, idx, sub, type)` | `CO.ExecuteCANOpenRead(queue, slot, position, index, subindex, dt, …)` → `ZMC_EcatSdo_Recv(handle, slot, position, index, subIndex, dataType, value)` | **是** | 句柄有效 |
| 23 | `write_ethercat_sdo` | `PUT /api/ethercat/sdo` (body) | `RTSysHandlers.EtherCATWriteSDOFromDict(body)` | `CO.ExecuteCANOpenWrite(queue, slot, position, index, subindex, dt, value)` → `ZMC_EcatSdo_Send(handle, slot, position, index, subIndex, dataType)` | **是** | 句柄有效；需确认 |
| 24 | `list_plugins` | `GET /api/plugins` | `RTSysHandlers.ListAttachedPlugins()` | `Controller.GetAttachedObject("IRobotService")` / `GetAttachedObject("RemoteDeviceManager")` | 否（返回空或对象） | 返回附加服务可用性，不直接访问控制器IO |
| 25 | `list_project_items` | `GET /api/project/items` | `RtsysHandlers.ListProjectItems()` | `ProgramWorkspace.ListLocalPrograms()`；若已连：合并控制器程序列表 | 部分 | 项目项列表，离线时仅本地 |
| 26 | `create_program` | `POST /api/programs` | `RTSysHandlers.CreateProgram(body)` | 返回 `Error("CreateProgram not supported on RTSys (no project files).")` | **否**（始终失败） | RTSys 不支持项目文件，是 stub |
| 27 | `delete_program` | `DELETE /api/programs/{name}` | `RTSysHandlers.DeleteProgram(name)` | 返回 `Error("DeleteProgram not supported on RTSys (no project files).")` | **否**（始终失败） | stub |
| 28 | `rename_program` | `PUT /api/programs/{name}/rename` | `RTSysHandlers.RenameProgram(name, body)` | 返回 `Error("RenameProgram not supported on RTSys (no project files).")` | **否**（始终失败） | stub |
| 29 | `copy_program` | `POST /api/programs/{name}/copy` | `RTSysHandlers.CopyProgram(name, body)` | 返回 `Error("CopyProgram not supported on RTSys (no project files).")` | **否**（始终失败） | stub |
| 30 | `upload` | `POST /api/programs/{name}/upload` | `RTSysHandlers.Upload(name)` | 返回 `Error("Upload not supported on RTSys (no project files).")` | **否**（始终失败） | stub |
| 31 | `download` | `POST /api/programs/{name}/download` | `RTSysHandlers.Download(name)` | 返回 `Error("Download not supported on RTSys (no project files).")` | **否**（始终失败） | stub |
| 32 | `open_program` | `POST /api/programs/{name}/open` | `RtsysHandlers.OpenProgram(name)` | 返回 `Error("OpenProgram not supported on RTSys (no editor integration).")` | **否**（始终失败） | stub |
| 33 | `get_program_process` | `GET /api/programs/{name}/process` | `RtsysHandlers.GetProgramProcess(name)` | 返回 `Error("GetProgramProcess not supported on RTSys.")` | **否**（始终失败） | stub |
| 34 | `set_program_process` | `PUT /api/programs/{name}/process` | `RtsysHandlers.SetProgramProcess(name, body)` | 返回 `Error("SetProgramProcess not supported on RTSys.")` | **否**（始终失败） | stub |
| 35 | `list_breakpoints` | `GET /api/programs/{name}/breakpoints` | `RtsysHandlers.ListBreakpoints(name)` | 返回 `Error("Breakpoints not supported on RTSys.")` | **否**（始终失败） | stub |
| 36 | `set_breakpoint` | `POST/PUT /api/programs/{name}/breakpoint` | `RtsysHandlers.SetBreakpoint(name, body)` | 返回 `Error("Breakpoints not supported on RTSys.")` | **否**（始终失败） | stub |
| 37 | `clear_all_breakpoints` | `DELETE /api/programs/{name}/breakpoints` | `RtsysHandlers.ClearAllBreakpoints(name)` | 返回 `Error("Breakpoints not supported on RTSys.")` | **否**（始终失败） | stub |
| 38 | `list_processes` | `GET /api/processes` | `RtsysHandlers.ListProcesses()` | 返回 `Error("Process listing not supported on RTSys.")` | **否**（始终失败） | stub |
| 39 | `get_process_variable` | `GET /api/processes/{pid}/variables?name=&program=` | `RtsysHandlers.GetProcessVariable(pid, program, variable)` | 返回 `Error("Process variables not supported on RTSys.")` | **否**（始终失败） | stub |
| 40 | `get_events` | `GET /api/events?since=…` | `RtsysHandlers.GetEvents(sinceTicks)` | 返回 `Error("Event log not supported on RTSys.")` | **否**（始终失败） | stub |
| 41 | `list_descriptors` | `GET /api/descriptors` | `RtsysHandlers.ListDescriptors()` | 返回 `Error("Program type descriptors not supported on RTSys.")` | **否**（始终失败） | stub |
| 42 | `read_iec_variables` | — (无 HTTP 路由，仅工具) | `RtsysHandlers.ReadIecVariables(name, scope)` | 返回 `Error("IEC variables not supported on RTSys")` | **否**（始终失败） | stub，RTSys 不支持 IEC |
| 43 | `write_iec_variables` | — | `RtsysHandlers.WriteIecVariables(name, scope, text)` | 返回 `Error("IEC variables not supported on RTSys")` | **否**（始终失败） | stub |
| 44 | `get_iec_task_detail` | — | `RtsysHandlers.GetIecTaskDetail(name)` | 返回 `Error("IEC tasks not supported on RTSys")` | **否**（始终失败） | stub |
| 45 | `read_sysvar` | `GET /api/sysvar/{name}` | `RtsysHandlers.ReadSysVar(name)` | 返回 `Error("System variables not directly accessible on RTSys. Use VR/TABLE.")` | **否**（始终失败） | 引导改用 VR/TABLE |
| 46 | `write_sysvar` | `PUT /api/sysvar/{name}` | `RtsysHandlers.WriteSysVar(name, body)` | 返回 `Error("System variables not directly writable on RTSys.")` | **否**（始终失败） | 引导改用 VR/TABLE |
| 47 | `list_digital_io` | `GET /api/io/digital/line` | `RtsysHandlers.ListDigitalIO()` | 返回 `Error("Digital IO not supported on RTSys (no direct hardware access).")` | **否**（始终失败） | stub |
| 48 | `read_digital_io` | `GET /api/io/digital/line/{idx}` | `RtsysHandlers.ReadDigitalIO(idx)` | 返回 `Error("Digital IO not supported on RTSys.")` | **否**（始终失败） | stub |
| 49 | `write_digital_io` | `PUT /api/io/digital/line/{idx}` | `RtsysHandlers.WriteDigitalIO(idx, body)` | 返回 `Error("Digital IO not supported on RTSys.")` | **否**（始终失败） | stub |
| 50 | `list_analogue_io` | `GET /api/io/analogue/line` | `RtsysHandlers.ListAnalogueIO()` | 返回 `Error("Analogue IO not supported on RTSys.")` | **否**（始终失败） | stub |
| 51 | `read_analogue_io` | `GET /api/io/analogue/line/{idx}` | `RtsysHandlers.ReadAnalogueIO(idx)` | 返回 `Error("Analogue IO not supported on RTSys.")` | **否**（始终失败） | stub |
| 52 | `write_analogue_io` | `PUT /api/io/analogue/line/{idx}` | `RtsysHandlers.WriteAnalogueIO(idx, body)` | 返回 `Error("Analogue IO not supported on RTSys.")` | **否**（始终失败） | stub |
| 53 | `scan_msbus` | `GET /api/msbus/scan?slot=N` | `RtsysHandlers.ScanMsBus(slot)` | 返回 `Error("MSBus scan not supported on RTSys.")` | **否**（始终失败） | stub |
| 54 | `list_remote_devices` | `GET /api/remote-devices` | `RtsysHandlers.ListRemoteDevices()` | `Controller.GetAttachedObject("RemoteDeviceManager")` → 遍历 Gateways/Devices | 部分 | 返回远程设备网关列表，不直接访问控制器IO |
| 55 | `list_robots` | `GET /api/robots` | `RtsysHandlers.ListRobots()` | `Controller.GetAttachedObject("IRobotService")` → `GetRobots()` | 部分 | 返回机器人定义列表 |
| 56 | `list_recipes` | `GET /api/recipes` | `RtsysHandlers.ListRecipes()` | 返回 `Error("Recipes not supported on RTSys.")` | **否**（始终失败） | stub |
| 57 | `list_alarms` | `GET /api/alarms` | `RtsysHandlers.ListAlarms()` | 返回 `Error("Alarms not supported on RTSys.")` | **否**（始终失败） | stub |
| 58 | `update_memory` | `POST /api/memory` | `AiService.SaveMemory(content)` | 纯本地：写入 `%APPDATA%\RTSysAI\` 记忆文件 | **否** | 不访问控制器；仅在用户显式要求时调用 |
| 59 | `task_create/update/list` | — (无 HTTP 路由) | `AiService.TaskCreate/Update/List()` | 纯内存：`AiSession._tasks` 列表 | **否** | 不访问控制器；多步任务跟踪 |
| 60 | `enter_plan_mode` / `exit_plan_mode` | — | `AiService.EnterPlanMode()` / `ExitPlanMode(plan)` | 纯内存：切换 `_planMode` 标志 + 用户审批回调 | **否** | 不访问控制器；写操作拦截开关 |
| 61 | `research` / `review` / `debug` / `explore` / `verify` | — | `AiService.RunSubagent(query, agentType, maxTurns)` | 子 agent 独立上下文，只读工具子集（`SubagentReadTools`） | 否（子 agent 内只读） | 不修改任何东西；仅返回结论/诊断/审核报告 |
| 62 | `controller_autoconnect` **(新增)** | `POST /api/controller/autoconnect` | `RTSysHandlers.TriggerAutoConnectAsync()` | ① `ZMC_SearchAndOpenEth("", &h)` → ② `ZMC_SearchEth(buf, len)` 发现 IP → `ZMC_OpenEth(ip, &h)` → ③ `ZMC_OpenEth(staticIP, &h)` → ④ `ZMC_Open(0, "simulator", &h)` | 否（这本身就是连接动作） | 依次尝试四种方式，任一成功即返回；全失败返回结构化错误 |
| 63 | `controller_simulator` **(新增)** | `POST /api/controller/simulator` | `RTSysHandlers.TriggerSimulatorConnectAsync()` | `ZMC_Open(0, "simulator", &h)`；检测 `zmotionsimu.dll` | 否（这本身就是连接动作） | 仿真器需已启动（ZvEpm/zmotionsimu） |
| 64 | `controller_status` **(新增)** | `GET /api/controller` | `RTSysHandlers.GetConnectionState()` + `GetCurrentConnectMode()` + `GetCachedIp()` | 纯本地状态查询 + `ZMC_SearchEth` 探测 | 否 | 无需控制器即可查询 |

---

## 3. 需控制器连接的工具清单（核心）

以下工具**必须**先建立控制器连接（句柄有效，即先通过 `ZMC_OpenEth`/`ZMC_OpenCom`/`ZMC_OpenPci` 获得 `ZMotionHandle`），否则由 `RTSysHandlers.GetHandleAsync()` 抛 `ControllerUnavailableException`，转成结构化错误 `{success:false, error, hint, connectionState, requiresController:true}`：

| 工具 | 底层 zmotion.dll 函数（首参为 handle） |
|------|--------------------------------------|
| `read_vr` / `write_vr` | `ZMC_ArrayReadf(handle, "VR", …)` / `ZMC_ArrayWritef(handle, "VR", …)` |
| `read_table` / `write_table` | `ZMC_ArrayReadf(handle, "TABLE", …)` / `ZMC_ArrayWritef(handle, "TABLE", …)` |
| `list_axes` | `ZMC_GetAxises(handle)` |
| `get_axis_detail` | `ZMC_GetAxisStates(handle, axis, buf)` |
| `compile_program` | `ZMC_Up3File(handle, name, buf)`（读源码）+ `ZMC_MakeZar(handle, srcFile, 0)`（编译） |
| `run_program` | `ZMC_RunZarFile(handle, name)` |
| `stop_program` | `ZMC_Stop(handle)` |
| `read_drive_param` / `write_drive_param` | `ZMC_EcatSdo_Recv` / `ZMC_EcatSdo_Send` (通过 EtherCAT SDO) |
| `scan_ethercat` / `read_ethercat_sdo` / `write_ethercat_sdo` | `ZMC_EcatSdo_*` / `ZMC_EnumChildCard` |
| `get_status`（已连时） | `ZMC_GetSysSpecification` / `ZMC_GetSoftVersion` / `ZMC_GetState` / `ZMC_GetAxises` / `ZMC_FindFirst3File`… |

---

## 4. 可离线使用的工具清单（不需控制器连接）

| 工具 | 实际实现（纯本地） |
|------|-------------------|
| `get_status`（未连时） | 返回本地 `connectionState` + 提示，不访问控制器 |
| `list_programs`（未连时） | 仅返回 `ProgramWorkspace` 本地缓存 |
| `read_source`（未连且已缓存） | 仅读本地缓存 `.bas` 文件 |
| `write_source` / `patch_source`（未连时） | 仅写本地缓存，标记 `isDirty=true`，不下载到控制器 |
| `lookup_command` | 本地命令参考 HTML（技能文档） |
| `read_skill` / `discover_skills` | 本地 `skills/` 目录 markdown |
| `search_code` | 扫描本地缓存程序文件 |
| `update_memory` / `task_*` / `enter_plan_mode` / `exit_plan_mode` / `research` / `review` / `debug` / `explore` / `verify` | 纯内存 / 本地文件，不访问控制器 |

---

## 5. 始终返回 "not supported" 的 stub 工具（RTSys 不支持）

以下工具在 RTSys 下**始终失败**，返回 `{success:false, error: "… not supported on RTSys."}`，不是因为缺少控制器连接，而是 RTSys 产品线本身不提供该功能（属于 MP / RTPlc 产品线能力）：

`create_program`、`delete_program`、`rename_program`、`copy_program`、`upload`、`download`、`open_program`、`get_program_process`、`set_program_process`、`list_breakpoints`、`set_breakpoint`、`clear_all_breakpoints`、`list_processes`、`get_process_variable`、`get_events`、`list_descriptors`、`read_iec_variables`、`write_iec_variables`、`get_iec_task_detail`、`read_sysvar`、`write_sysvar`、`list_digital_io`、`read_digital_io`、`write_digital_io`、`list_analogue_io`、`read_analogue_io`、`write_analogue_io`、`scan_msbus`、`list_recipes`、`list_alarms`

**修复建议**：若后续需要支持 IEC/PLC，可接入 `ZMotionPlc.dll` 的 `ZPLC_*` 导出（离线编译无需控制器；下载/监控需串口或网络连接）；若需要系统变量/IO，可改用 VR/TABLE 替代（`read_sysvar`/`write_sysvar` 已明确引导使用 VR/TABLE）。

---

## 6. 工具 → 实际函数 的关键前提条件总结

1. **所有需句柄的工具（第 3 栏）共享同一守门点**：`RTSysHandlers.GetHandleAsync()` → `ZMotionHandle.ConnectAsync(ip)` → `ZMC_OpenEth(ip, &handle)`。若 `config.json` 中 `RTSysIP` 为空，直接返回 `NotConfigured` 错误，不尝试连接。
2. **连接三种方式**：以太网 `ZMC_OpenEth`（IP 必填）、串口 `ZMC_OpenCom`（端口号+波特率）、PCI `ZMC_OpenPci`（卡索引）。项目侧当前仅走以太网路径（`ConnectAsync`）。
3. **写操作确认机制**：`WriteTools` 集合中的工具在 `ExecuteTool` 中会触发 `OnConfirmWrite` 用户确认；`AutoAllowWriteTools`（编辑/编译类）首次确认后会话内放行；运行/停止/上下传/变量写入类每次都需确认。
4. **Plan Mode 拦截**：`_planMode` 为 true 时，全部 `WriteTools` 被阻断，返回 `BLOCKED: Plan Mode is active…`，必须先 `exit_plan_mode` 获得用户批准。
5. **RTSys 与宿主连接独立**：项目侧 `ZMotionHandle` 建立自己的 zmotion 句柄，与宿主 RTSys.exe 已有的控制器连接相互独立。插件可自行连接控制器，不必等待宿主连接。

---

## 7. 文件引用

| 文件 | 含义 |
|------|------|
| `PlugInWorker/AiTools.cs` | AI 工具定义、分发 `DispatchTool`、确认/Plan Mode 逻辑、工具 schema |
| `PlugInWorker/RTSysHandlers.cs` | RTSys 风格的 handler 实现（新版，通过 `ZMotionHandle` 访问控制器） |
| `PlugInWorker/Handlers.cs` | MP 风格的 handler 实现（旧版，通过 `Controller.CommandQueue` 访问，部分为 stub） |
| `PlugInWorker/ZMotionAPI.cs` | zmotion.dll P/Invoke 签名（100+ `ZMC_*` 函数） |
| `PlugInWorker/ZMotionExtensions.cs` | ZMotionHandle 封装层（ReadVR/WriteVR/ReadTable/WriteTable/RunProgram/StopProgram 等） |
| `PlugInAdapter/ApiServer.cs` | HTTP 路由分发（`GET /api/vr/{addr}` → `ReadVR` 等） |
| `PlugInWorker/ConfigPath` | `%APPDATA%\RTSysAI\config.json`，`RTSysIP` 键当前为空 |
