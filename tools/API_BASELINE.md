# RTSys AI Plugin API 能力基线（现阶段）

- 实测时间：2026-11-06 21:17（本地）
- 探测工具：`tools/api_probe.py`（只读；写入路由一律 SKIP_WRITE）
- 部署目录：`%USERPROFILE%\Desktop\RTSys\Plug_in\AIPlugin\`
- 当前部署构建时间：`RTSysPlugInAdapter.dll` / `RTSysPlugInWorker.exe` = 2026-11-06 21:09:41；`RTSysPlugInNative.dll` = 2026-11-06 11:36:31
- 配置：`%APPDATA%\RTSysAI\config.json` 中不存在 `RTSysIP` 键（已确认，仅检查键名，未读取/输出值）

## 1. 当前激活状态：未激活

判定依据：

1. `tasklist` 无 `RTSys.exe` 进程；`netstat` 无 9090/9091/9092/9093 监听。
2. `python tools/api_probe.py` 返回 `reachable = false`，27 个只读路由全部 `UNREACHABLE`，6 个写入路由 `SKIP_WRITE`。
3. `plugin.log` 最后一条为 `[2026-11-06 19:49:27.207] Create() OK hwnd=0x31B12`，文件 mtime 为 19:49:27；当前部署 DLL 构建于 21:09:41，晚于该日志。即当前构建从未被加载过。
4. `shim_diag.log` mtime 为 20:40:36，说明 20:40 左右宿主 RTSys 启动并加载了 native shim（`PlugIn_Reserved` / `SetDockBar` / `SetAutoSize` 被调用），但同一时段 `plugin.log` 无任何新记录。这与“懒加载、用户未点击 AI 插件菜单”一致，即宿主加载了 shim，但插件 `Create()` 未被触发。

结论：插件当前未激活，本次无法验证任何 API 路由的运行时语义。

## 2. 能力矩阵（实测）

说明：
- 当前判定为本次实测结果；由于插件未激活，所有只读路由均为 `UNREACHABLE`。
- “激活后预期”依据现有源码行为推断，且假设配置仍无 `RTSysIP`。
- 写入路由未发送任何请求，避免改动控制器状态。

| # | 方法 | 路由 | 类型 | 当前 HTTP | 当前判定 | 激活后预期 |
|---|------|------|------|-----------|----------|------------|
| 1 | GET | `/` | read | - | UNREACHABLE | OK（`controllerConfigured:false`） |
| 2 | GET | `/api/status` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 3 | GET | `/api/programs` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 4 | GET | `/api/programs/0/source` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 5 | GET | `/api/vr/0` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 6 | GET | `/api/table/0` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 7 | GET | `/api/axes` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 8 | GET | `/api/axes/0` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 9 | GET | `/api/project/items` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 10 | GET | `/api/ethercat/sdo?...` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 11 | GET | `/api/drive/0/0x6040` | read | - | UNREACHABLE | NOT_CONFIGURED |
| 12 | GET | `/api/plugins` | read | - | UNREACHABLE | OK |
| 13 | GET | `/api/skills` | read | - | UNREACHABLE | OK |
| 14 | GET | `/api/skill/safe-coding` | read | - | UNREACHABLE | OK（skill 存在） |
| 15 | GET | `/api/lookup?query=MOVE` | read | - | UNREACHABLE | OK |
| 16 | GET | `/api/tasks` | read | - | UNREACHABLE | OK |
| 17 | GET | `/api/memory` | read | - | UNREACHABLE | OK |
| 18 | GET | `/api/sysvars` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 19 | POST | `/api/oscilloscope/open` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 20 | POST | `/api/chat` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 21 | GET | `/api/robots` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 22 | GET | `/api/recipes` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 23 | GET | `/api/alarms` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 24 | GET | `/api/remote-devices` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 25 | GET | `/api/msbus/scan` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 26 | GET | `/api/processes` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 27 | GET | `/api/events` | mp | - | UNREACHABLE | NOT_SUPPORTED |
| 28 | PUT | `/api/vr/0` | write | - | SKIP_WRITE | 不执行 |
| 29 | PUT | `/api/table/0` | write | - | SKIP_WRITE | 不执行 |
| 30 | POST | `/api/programs/0/compile` | write | - | SKIP_WRITE | 不执行 |
| 31 | POST | `/api/programs/0/run` | write | - | SKIP_WRITE | 不执行 |
| 32 | POST | `/api/programs/0/stop` | write | - | SKIP_WRITE | 不执行 |
| 33 | PUT | `/api/ethercat/sdo` | write | - | SKIP_WRITE | 不执行 |

统计：`OK 0 / NOT_CONFIGURED 0 / NOT_SUPPORTED 0 / ERROR 0 / SKIP_WRITE 6 / UNREACHABLE 27`，共 33 项。

## 3. 历史加载情况（plugin.log）

插件曾多次成功加载并启动 ApiServer（均在端口 9090）：

- `19:09:58` `Create() enter` → `ApiServer.Start() OK on port 9090`，但日志停在 `AttachView() creating HwndSource...`，没有 `Create() OK`；下一次记录已是 `19:25:56` 的新一轮 `Create()`。首次加载是否异常中止未能确认（可能是用户关闭/宿主重启），列为观察项。
- `19:25:56` `Create() enter` → `ApiServer.Start() OK` → `19:25:57.575 Create() OK hwnd=0x151344`。
- `19:49:03` `Create() enter` → `ApiServer.Start() OK` → `19:49:04.916 Create() OK hwnd=0x1192C`。
- `19:49:26` `Create() enter` → `ApiServer.Start() OK` → `19:49:27.207 Create() OK hwnd=0x31B12`。

因此：插件历史上可以成功激活并监听 9090，但本轮（21:09 构建）尚未被激活，API 路由能力未知。

## 4. 已发现的真实缺陷（现有部署/历史日志）

### 4.1 ChatPanel 历史按钮触发未处理 UI 异常（高置信度）

`plugin.log` 记录两次（`19:28:24`、`19:49:12`）同一异常：

```
EXCEPTION in EnsureUiThread() UI thread start: 值不能为 null。参数名: window
   at System.Windows.Interop.WindowInteropHelper..ctor(Window window)
   at System.Windows.MessageBox.Show(Window owner, ...)
   at RTSys.PlugIn.ChatPanel.OnHistory(Object sender, RoutedEventArgs e)
```

即 `ChatPanel.OnHistory` 调用 `MessageBox.Show` 时 owner window 为 null，导致 `ArgumentNullException`，在 UI Dispatcher 上未被捕获。该缺陷出现在 21:09 之前的构建中；当前构建是否已修复需激活后点击“历史”按钮复现确认。

影响：点击历史按钮可能使插件 UI 崩溃/重启，进而影响 ApiServer 可用性。这是本次发现的最明确缺陷。

### 4.2 首次加载未完成（观察项，待确认）

`19:09:58` 首次 `Create()` 在 `AttachView() creating HwndSource...` 后无后续，也没有异常记录，随后出现新的 `Create()`。可能是用户主动关闭、宿主重启，或加载阶段静默失败。建议用当前构建复现并观察 `plugin.log`。

### 4.3 非缺陷但会影响判读

- `config.json` 无 `RTSysIP`：所有需要控制器句柄的路由预期返回 `RTSysIP not configured`，这是任务说明中的预期行为，不是缺陷。
- 当前无法验证 `ERROR`（500/NRE）类 API 缺陷，因为端口不可达。`api_probe.py` 已用本地 mock 验证过 `ERROR` 判定逻辑（500、`NullReference`、`未将对象引用`）。

## 5. 复测方法

1. 双击 `%USERPROFILE%\Desktop\RTSys\RTSys.exe` 启动宿主。
2. 在 RTSys 界面点击「AI 插件」菜单，等待插件面板出现（懒加载，脚本无法代替点击）。
3. 确认 `%APPDATA%\RTSysAI\plugin.log` 出现 `Create() OK hwnd=`。
4. 运行：`python "<repo>\tools\api_probe.py"`。
5. 若需要机器可读结果：追加 `--json`。
6. 也可使用 `tools\verify_e2e.ps1` 自动停止/启动 RTSys、提示点击菜单、轮询激活并调用探测脚本，日志写入 `tools\verify_e2e.log`。

激活后判读规则：

- `OK`：200 且响应为合法 JSON。
- `NOT_CONFIGURED`：响应含 `RTSysIP not configured`，属预期。
- `NOT_SUPPORTED`：响应含 `not supported on RTSys`，属预期。
- `ERROR`：500 或响应含 `未将对象引用` / `NullReference`，属缺陷，需要修复。
- `UNREACHABLE`：端口不可达，说明插件未激活或 ApiServer 启动失败。
