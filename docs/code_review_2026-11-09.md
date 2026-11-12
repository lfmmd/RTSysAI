# TrioAI_RTSys 代码审核报告（2026-11-09）

- 分支：`feature/hmi-reader`（含未提交：`HmiInternal.cs`、`RegressionTestMain.cs` 563 行 HMI 用例、`AiTools.cs`/`RTSysHandlers.cs` HMI 工具、`ChatPanel.cs` 7 行）
- 规模：生产 C# 约 24,263 行（`PlugInAdapter` 4 文件 + `PlugInWorker` 27 文件 + 原生 shim）。超大文件：`RTSysHandlers.cs` 2,716、`ChatPanel.cs` 2,381、`RegressionTestMain.cs` 2,152、`PlugInHostAdapter.cs` 1,360、`ApiServer.cs` 1,282、`AiService.cs` 1,208、`ProgramWorkspace.cs` 1,188、`AiHistory.cs` 1,126、`RTSysProjectModel.cs` 1,051。
- 方法：三路独立只读审核（质量与结构 / 风格与复用 / 安全修复残留核验），与 `docs/audit_report_2026-11-08.md`（下称 A08）逐项对照。行号为审核时工作区值，修复前请按符号名定位。
- 结论基线：A08 的 P0 安全项与 Q2/Q3/Q7/Q10/Q14/Q19 **已修**；剩余风险集中在 HMI 新代码的线程/ABI/指针安全、Motion 门覆盖缺口、并发伪异步与静默空结果。

---

## 1. 架构与分层（质量维度）

| # | 证据 | 问题 | 级别 |
|---|---|---|---|
| A1 | `PlugInAdapter/PlugInAdapter.csproj:40-41`、`Program.cs:71,89` | 单进程引用方向已清晰，但 `Program.Main`（独立 Worker）与 Adapter `Create` 两套启动路径分叉，旧跨进程心智模型残留 | P2 |
| A2 | `ChatPanel.cs:384 new AiService()`；`PlugInHostAdapter.cs:604,671` | UI 层可绕过 Worker 边界直接改全局业务状态 | P1 |
| A3 | `RTSysHandlers.cs:31,42,54,58`、`ApiServer.cs:236,259`、`DispatcherHelper.cs:8`、`AiService.cs:17-26` | 静态可变单例散布；任意 `new AiService()` 触发 `LoadConfig` 重置静态字段，测试与生产互相污染 | P1 |
| A4 | `AiService` 12 个 partial，字段在 `AiService.cs:28-61` | 按职责拆文件但共享可变静态状态，内聚假象；拆文件不解决问题，需收敛静态配置归属 | P2 |

原生链路清晰：`PlugInShimNative.cpp:283 ExecuteInDefaultAppDomain` → `PlugInNativeExports.cs:174` → `PlugInHostAdapter.Create`，无 `AppDomain.CreateDomain/Remoting` 残留。

## 2. 并发与异步

- **裸 `catch{}` 111 处**（生产 101）：全部 catch 181 处，其中裸吞 61%、`catch{return false/null/""/0/Zero/-1}` 28 处。最多：`ChatPanel.cs` 21、`PlugInHostAdapter.cs` 10、`Program.cs` 9、`ProgramWorkspace.cs` 8、`ApiServer.cs` 8、`RTSysHandlers.cs` 7、`AiService.cs` 6。`HmiInternal.cs` 20 处多为 `catch{return Zero/null/-1}`，调用方无法区分“无对象”与“异常”。**P1**。
- **伪异步 `Safe()` 未修**（A08 Q6）：定义 `RTSysHandlers.cs:631,651`，`Task.Run(func).GetAwaiter().GetResult()`，**45 处调用**（`676,776,868,983…2357`）。`CancellationToken` 无法打断；停止按钮无效。其它同步阻塞：`AiHistory.cs:299,311,323`、`AiService.cs:731,755,901`、`AiTools.cs:280-281`、`ApiServer.cs:987,989`、`ChatPanel.cs:579`、`PlugInHostAdapter.cs:604,671`。**P1**。
- 确认框：`ChatPanel.cs:543-582 tcs.Task.Wait(10min)+.Result`，已加 10 分钟超时 + UI 线程守卫；非 UI 线程等用户期间 `Send` 的 CTS 取消不可达。**P1**。
- `DispatcherHelper.Invoke`：`_dispatcher==null` 时 NRE（Worker 进程从未 `Capture()`）。靠 `PureIoTools` 白名单绕过，属补丁非修复。**P1**。
- 已修：`AiSkills.cs:36-44` `_index+_indexLock+ConcurrentDictionary`、`_toolDefLock`、`ApiServer.cs:235-255 TaskService` 双检锁 + `UseService`、 `AiHistory.cs:32` 持锁（Q10）、`AiService.cs:166` `100_000` 统一（Q14）。
- 未修：`JavaScriptSerializer` 静态共享 `AiService.cs:26`、`RTSysHandlers.cs:43`、`ProgramWorkspace.cs:24`、`ChatPanel.cs:34`（S3-8，官方不保证线程安全）；`_candidates`（`RTSysProjectModel.cs:63`）锁外遍历（Q20）。**P1/P2**。

## 3. 可靠性

| # | 证据 | 问题 | 级别 |
|---|---|---|---|
| R1 | `ZMotionExtensions.cs:555-558` 空数组；调用方 `RTSysHandlers.cs:1178,1205,2282` 仍 `success=true` | 轴状态静默成功，AI 误判“无轴/无报警”（A08 Q4 部分未修；`GetAxisCount` 已接 `ZMC_GetAxises`，轴状态未接） | P1 |
| R2 | `ListDigitalIO:2300-2325`、`ListAnalogueIO:2355-2375` 规格为 0 时显式 `Error` | 已收敛，与 R1 不一致（P2） |
| R3 | AI `AiService.cs:101 120s+817` 退避；连接 `RTSysHandlers.cs:39-41,277` 2.5s/15s/10s；`AwaitWithTimeout:277-282` 只放弃等待，原生线程泄漏阻塞 | 超时/冷却策略不一致 | P1 |
| R4 | `WriteSource:978-1076`、`CreateProgram:1927-1972`、`RenameProgram:2067-2135` | 写事务无回滚，无 tmp+rename 原子写；部分失败留“项目已改/本地未改” | P1 |
| R5 | `Program.cs:24,37` Mutex 不 Dispose；`AiService.cs:101` Http 不 Dispose；`RTSysProjectModel.cs:898-915` GCHandle 超时泄漏；`PlugInHostAdapter.cs:407-408` 重复 Create 若 Detach 失败泄漏 | 资源缓慢增长（P2；A08 S4 HGlobal 已修，`ZMotionExtensions.cs:79-96,107-127` finally Free） |
| R6 | `ChatPanel.cs:91 win.Closed+=` 匿名无法退订；`ApiServer.cs:20` 静态事件 | 重复 Create 旧面板不回收（P2） |

## 4. 测试覆盖

`RegressionTestMain.cs` 20 组：签名、工具注册、无控制器执行、连接状态、响应形状、`PureIoTools` 分类、离线缓存、GBK 工程、CRUD 离线、P0 加固（`193 TestP0Hardening`：`FixedTimeEquals`/路径沙箱/`validate_basic`）、HMI 文件解析、`lookup_command` 分级、标题栏当前文件、`read_source` 行号、HMI 内部 API 反射（`1643`）、HMI 读写/UI 刷新（需 RTSys 运行）。

**未覆盖**：HMI 桥接离线不可测（需 RTSys 运行，CI 跳过）【P1】；`ApiServer` 鉴权集成仅测纯函数，无“无令牌→401/跨源拒/nonce 一次性”HTTP 级断言【P1】；假 CWnd 全槽在独立 `AbiTest`【P2】；`DispatcherHelper` 未 Capture、`Safe` 取消、并发 `lookup_command` 无断言【P1】；真实控制器分支需 `--sim`【P1】。

生产混入：`AiOptimizationTests.cs` 已移出生产编译（`PlugInWorker.csproj` 无此条目，`RegressionTest.csproj:39` 通配纳入；`/api/test` 路由已删）。残留：文件仍在 `PlugInWorker/` 生产目录，易误加回（P2，A08 Q17 编译/路由已修）。

## 5. 风格统一性

### 5.1 命名空间/类命名
全部 `RTSys.PlugIn` 一致；`AiService` 规范 `internal partial class`（13 分部）；`RTSysHandlers` 为 `internal static class` 一致。法定义务冲突：`GetInt` 同名不同返回类型（`AiJson.cs:180,191` `int` vs `RTSysHandlers.cs:1708` `int?`），检索困难（P2）。

### 5.2 方法命名：4 个 `Get*` 语义各异（最大风格断裂点，P1）
`AiJson.cs:172 GetStr`（缺键→null，bool→"true"）、`AiJson.cs:180 GetInt`（缺键→0，只解析字符串）、`AiJson.cs:191 GetInt(d,key,def)`（处理 double/long）、`AiJson.cs:252 GetBool`、`RTSysHandlers.cs:1693 GetString`/`1708 GetInt`（缺键→null）/`1720 GetBool`（处理 bool/double/int 四种）、`ApiServer.cs:1238,1245` 第三套、`AiJson.cs:120,126 GetStringValue/GetIntValue` 第四套。`HmiSetGeometry` 用的 `RTSysHandlers` 版与 `AiTools` 分发层的 `AiJson` 版缺省语义不同。

### 5.3 常量
`HmiInternal` PascalCase 自洽；`ProgramWorkspace` 混用（`ProjectCodePage:28` 同文件 PascalCase）；`HmiInternal.cs:289-290,296` 硬编码 `0x00/0x11/0x08` 未走 `OffFeature*`（P1）。

### 5.4 错误范式：五套并存
`Error(msg)→{success:false,error}`（`RTSysHandlers.cs:595`，主范式✅）；`Safe` 包装（`631-669`，HMI 全程✅）；手写 `{success:false}` 字面量（HMI 段 `2450,2490…`，P2）；`{error}` 无 `success`（`AiTools.cs:152…`、`AiSkills.cs:178…`、`HmiFile.cs:56,60…`，靠 `IsToolError:129-136` 嗅探弥合⚠️）；字符串 `"Error: "+ex.Message`（`AiTools.cs:120`）与抛异常（`RTSysHandlers.cs:198,204,264`、`ProgramWorkspace.cs:839` 抛 `ArgumentException` 全项目唯一）。

## 6. 可复用性

### 6.1 已存在、可直接复用（无需新建抽象）
| # | 目标 | 现有原语 |
|---|---|---|
| R1 | `ListOfflinePrograms` 重复合并 | `RTSysHandlers.cs:804 MergePrograms(local,project,null)` |
| R2 | `HmiControlTypeName` vs `ControlTypes` 双源漂移 | `HmiFile.cs:32 ControlTypes`（补 622-627 等后，`HmiControlTypeName` 仅保留 11） |
| R3 | 三套字典解析器合一 | `AiJson.cs:191 GetInt` + `AiJson.cs:172 GetStr` + `RTSysHandlers.cs:1720 GetBool`（补 `d?.` 空安全） |
| R4 | HMI UI 线程封送 | `RTSysProjectModel.cs:884 UiInvoke`（提升 `internal` + 返回 bool），并把三只读 HMI 工具移出 `PureIoTools`（`AiTools.cs:61`） |
| R5 | 文件名安全 | `ProgramWorkspace.cs:720 ResolveInside`（`HmiFile.cs:64` 已正确复用） |
| R6 | 配置单键写 | `AiConfig.cs:134 SaveConfig` 键合并（`RTSysHandlers.cs:501 SetRTSysIp` 独立手写，应改走键合并） |
| R7 | 只读 `HmiGetFeature` 回退 | `HmiInternal.cs:330 GetFeatureDouble`、`347 GetFeatureString`（现只读 int，丢失 double/string） |

### 6.2 需新建抽象（说明）
| # | 抽象 | 为何不能用现有 / 为何本次要建 |
|---|---|---|
| N1 | `EnsureResolved` 加锁/Lazy | HMI 四入口 + 六读写方法并发可达，非单次（**P0**） |
| N2 | `SetFeatureString` ABI 修正或删除 | 无现成 CString 构造；符号 `CString&` 与委托 `const char*` 矛盾，修正后写侧与读侧共享入口（**P0**） |
| N3 | `HmiGetFeature` 三态返回 | Handler 层需“按值类型选读”分支，写侧共享同一特征三元组描述（P1） |
| N4 | 空几何校验 | 单次，一行 `if` 即可，**不封装** |

### 6.3 明确不做的过度抽象
`HmiControlTypeName` 仅 `HmiReadWindow:2516` 一处 → 并入查表后删除，不建注册表接口。`ResolveTarget` 仅 3 处 → 修正范围后保留私有 helper。`SaveConfig` 18 参已超载 → 不再加参。`ValidateRtBasicCode`/`AiValidation.ValidateTrioBasicCode`/`AiControllerValidation.ValidateRTBasicCode` 三套严格度不同 → **禁止合并**，仅改名区分。

## 7. HMI 新代码 vs 已验 P0（风格/并发/原生）

基线：`ResolveInside:720` + `ValidateProgramName:909` + 回归 `200-209`；`IsAuthorized:127` + `Consume/IssueNonce:94,104`；`FakeCWnd stub 0x280 + x64 拒绝 + VirtualProtect`。

| # | 问题 | 证据 | 级别 |
|---|---|---|---|
| 3.3 | `EnsureResolved` 无锁，`Safe/Task.Run` 下多工具并行双绑 | `HmiInternal.cs:129-196` | **P0** |
| 3.5 | 三只读 HMI 工具误进 `PureIoTools`，与 `RTSysHandlers.cs:2439` “宿主 UI 线程” 自述矛盾；写工具未进（正确）走 `DispatcherHelper.Invoke` | `AiTools.cs:33-62` vs `108-110` | **P0** |
| 3.6 | `SetFeatureString` 符号 `CString&` 与委托 `const char*` ABI 不匹配，且无调用方 | `HmiInternal.cs:45-46,111,388-407` | **P0** |
| 3.2 | `ToInt32` 截断；`IsPlausible/GetObjectType` 无 `EnsureResolved` | `HmiInternal.cs:254,261,286,441-453` | P0/P1 |
| 3.4 | `ResolveTarget` 注释“只扫首窗口”与实际遍历全部窗口不符；跨窗口同 ID 静默命中首个；未知 `targetType` 静默 Zero | `RTSysHandlers.cs:2617-2637` | P1 |
| 3.7 | 空几何 `rc=0` → `success=true` 假成功；`goto Done` 部分写入未说明 | `2590-2611` | P1 |
| 3.8 | 只读 int，丢失 double/string（`OffFeatureDouble:72`/`OffFeatureString:74` 已确认） | `2537-2551` | P1 |
| 4.1 | `EnumerateFeatures` subId 硬编码 `0x11`，与 `OffFeature*` 两套偏移体系 | `289-296` | P1 |
| 4.3/4.4 | 特征缺失回 `?? 0` 无法区分；`GetFeatureString` 注释与 `""` 非 null 不符 | `2457,347,456-466` | P2 |

**肯定**：HMI 全程 `Safe` + 结构化错误；`Bind<T>:198-204` 与 `PlugInHostAdapter:1221` 范式一致；`HmiFile.Read` 复用 `ResolveInside`（`HmiFile.cs:64`）是本轮最规范一处；错误不抛到工具层。

## 8. 巨型文件拆分建议（纯文件搬移，partial 已支持）

| 文件 | 拆分点（按职责） | 最小可落地 |
|---|---|---|
| `ChatPanel.cs` 2,381 | ① `Palette/ConfigurePalette`→`ChatPanelTheme.cs`；② 对话框 `OnSettings/History/Memory/About`→`ChatPanelDialogs.cs`；③ 确认/审批→`ChatPanelConfirmation.cs` | ①+② |
| `RTSysHandlers.cs` 2,716 | ① 连接管理 `76-592`→`RTSysConnection.cs`；② CRUD `719-2135`→`RTSysProgramHandlers.cs`；③ 编辑器/断点 `2135-2716`→`RTSysEditorHandlers.cs` | ① |
| `ApiServer.cs` 1,282 | ① 鉴权/nonce `22-195`→`ApiAuth.cs`；② 路由 `HandleRequestCore` 按资源分；③ `HandleBody/SendJson`→`ApiHttp.cs` | ① |
| `ProgramWorkspace.cs` 1,188 | ① `.zpj` 解析→`ZpjReader.cs`；② 本地缓存→`LocalProgramCache.cs`；③ 校验/路径→`ProgramPathGuard.cs`（复用 `ResolveInside`） | ③ |
| `RTSysProjectModel.cs` 1,051 | ① WndProc 钩子→`HostWndHook.cs`；② 原生模型→`NativeProjectModel.cs`；③ 扫描→`FileViewScanner.cs` | ① |
| `AiTools.cs` 747 | ① 工具定义→`AiToolDefinitions.cs`；② Task→`AiTaskTools.cs`；③ 计划模式→`AiPlanMode.cs` | ① |
| `AiService.cs+AiHistory.cs` | 已 partial，无需拆文件；问题在静态字段归属（A3） | 不动文件 |

## 9. A08 对照总表

**已修（可关闭）**：S1-1（`ApiServer.cs:127-141`）、S1-2（`94-114,143-173`）、H8（`PlugInHostAdapter.cs:54-80`）、H1（`RTSysProjectModel.cs:892-960`）、S2-3（`RTSysHandlers.cs:2072`）、S2-4（`720 ResolveInside` +18 处）、S2-5（`1180 2MB`）、S2-6（HTTP 32KB+nonce）、Q1、Q2、Q3（`AiSkills.cs:60`/`AiValidation.cs:71,82` 全改 rtbasic）、Q5（`AiConfig.cs:236` 守卫，部分）、Q7、Q10、Q14、Q17（编译/路由）、Q19、S3-3 Mutex（`Program.cs:50`）、S3-4 端口（`Program.cs:89`）。
**仍未修**：
- **P1**：Q6 伪异步（45 处 `Safe` + 15 阻塞点）；Q4 轴状态空数组；Q8 `HandleBody` method 未校验；S3-8 共享 `JavaScriptSerializer`；Q15 双写配置（`ChatPanel.cs:1659+1670`，`1888` 硬编码回退）；Q9 `origin` 词表（`743 both` vs `825 project+controller` vs `1072 local`）。
- **P2**：Q13 prompt/工具池漂移；Q18 Lang 缺键；Q20 `_candidates` 锁外遍历；Q21 每次重解析 `.zpj`；Q22 巨型方法；Q23 构建脚本；Q24 ZMotion 无效参数；Q25 `ValidateRtBasicCode` 白名单（`1640` 仍在）；Q26 双序列化。Q16 可关闭（`--port` 已接）。

安全残留与最小修复见配套任务书 `docs/security_fix_handoff_2026-11-09.md`。
