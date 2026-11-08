# TrioAI_RTSys 插件当前状态分析报告

- 日期：2026-11-08
- 范围：当前工作区（worker + adapter + 原生 shim + 测试工程）
- 目的：在独立新建的 RTSys 工程上，通过 HTTP 接口验证插件全部主要功能
- 说明：本报告基于源码静态分析 + 本会话已执行的构建/回归/HTTP 实测

---

## 1. 结论摘要

| 维度 | 结论 |
|---|---|
| 架构 | 插件以原生 shim 加载进 RTSys 进程；业务逻辑在 `RTSysPlugInAdapter.dll`（内含 worker 程序集）中运行；同一套 HTTP API 也可由独立 `RTSysPlugInWorker.exe` 提供 |
| 工程读写 | 支持程序清单/源码读取、覆盖写入、局部补丁、新建/复制/重命名/删除；工程树由宿主内部模型同步（`internalProjectModel=true`） |
| 控制器能力 | 通过 `zmotion.dll`；自动搜索 → 静态 IP → 仿真器；轴/IO 在信息不可用时明确返回失败，不再伪报 0 |
| 安全 | 本机 HTTP API 需 `X-Auth-Token`；拒绝带 `Origin` 的跨源请求；无 CORS 头；运动控制端点默认关闭且需一次性 nonce；请求体上限 2MB |
| 测试 | 回归 255 passed；AI 工具矩阵离线/模拟器均 43 passed；HTTP 端到端 19/19（真实工程） |
| 主要缺口 | 巨型路由 switch 已完成主要拆分；UI 大重构、全量 i18n、`.zpj` 缓存等未做（非功能缺陷） |

---

## 2. 组成与运行形态

```
RTSys.exe
 ├─ Plug_in\AIPlugin\RTSysPlugInNative.dll     C++ 原生 shim，导出 PlugIn_*（__cdecl）
 ├─ Plug_in\AIPlugin\RTSysPlugInAdapter.dll    托管适配器（WPF 面板 + ApiServer + 业务逻辑）
 │    └─ 引用 RTSysPlugInWorker.exe 程序集（业务代码）
 └─ 可选：RTSysPlugInWorker.exe 独立进程（同一套 ApiServer/AiService）

HTTP: http://localhost:<httpPort>/  默认 9090（可回退 9091-9093，设置内可改）
鉴权: X-Auth-Token（%APPDATA%\RTSysAI\api_token.txt）
```

- 端口配置键：`httpPort`（1024–65535，默认 9090，设置面板可改，立即生效）
- 控制器 IP 配置键：`RTSysIP`（可选；不填时自动搜索 + 仿真器兜底）
- 工程目录：优先读配置 `projectDir`，否则从宿主标题解析

---

## 3. HTTP API 面（42 个路由）

| 类别 | 路由 |
|---|---|
| 基础 | `/`、`/api/identity`、`/api/status`、`/api/controller`（GET/autoconnect/simulator） |
| 工程/程序 | `/api/project`、`/api/project/items`、`/api/programs`、`…/{name}`、`source`、`open`、`rename`、`copy`、`compile`、`run`、`stop`、`process`、`upload`、`download` |
| 控制器只读 | `/api/axes`、`/api/vr`、`/api/table`、`/api/sysvars`、`/api/io/digital|analogue`、`/api/ethercat/devices|sdo`、`/api/drive` |
| 控制器写（默认关闭） | `PUT /api/vr`、`/api/table`、`/api/io/*`、`/api/drive`、`/api/ethercat/sdo`、`POST …/run|stop` |
| AI/知识 | `/api/lookup`、`/api/skills`、`/api/skill`、`/api/validate_basic`、`/api/search_code`、`/api/memory`、`/api/tasks` |
| 其他 | `/api/events`、`/api/processes`、`/api/breakpoints`、`/api/plugins`、`/api/alarms`、`/api/recipes`、`/api/robots`、`/api/remote-devices`、`/api/msbus`、`/api/oscilloscope`、`/api/descriptors`、`/api/chat` |

鉴权与限制：
- 所有请求（除鉴权失败本身）要求 `X-Auth-Token`；`Origin` 非空直接拒绝。
- 运动控制路由：配置 `enableMotionControlApi=true` + `X-Motion-Nonce`（UI 一次性授权，5 分钟有效）。
- 记忆写入：`X-Confirm-Nonce` + 32KB 上限。
- 请求体：≤2MB，`Content-Type: application/json`。
- 未匹配路由统一 404；未写响应的分支由兜底逻辑补 404。

---

## 4. AI 工具面（50 个）

- 工程/程序：`create_program`、`delete_program`、`rename_program`、`copy_program`、`open_program`、`save_project`、`open_project`、`read_source`、`write_source`、`patch_source`、`search_code`、`list_programs`、`list_project_items`、`compile_program`、`run_program`、`stop_program`、`set_program_process`
- 控制器只读：`get_status`、`list_axes`、`get_axis_detail`、`read_vr`、`read_table`、`list_digital_io`、`read_digital_io`、`list_analogue_io`、`read_analogue_io`、`read_drive_param`、`read_ethercat_sdo`、`list_plugins`、`list_alarms`
- 控制器写：`write_vr`、`write_table`、`write_digital_io`、`write_analogue_io`、`write_drive_param`、`write_ethercat_sdo`
- 知识与技能：`lookup_command`、`read_skill`、`discover_skills`、`validate_basic`（对应 HTTP `/api/validate_basic`）
- 会话/计划：`enter_plan_mode`、`exit_plan_mode`、`update_memory`、`task_create/update/list`
- 子 agent：`research`、`review`、`debug`、`explore`、`verify`（各自只读工具池）
- 连接控制：`controller_autoconnect`、`controller_simulator`、`controller_status`

写入确认：`WriteTools` 逐次确认；`AutoAllowWriteTools`（编辑/编译/保存/打开）会话内首次确认后放行；`update_memory` 已纳入逐次确认。

---

## 5. 工程树与磁盘一致性

- 删除/重命名/新建/复制：优先调用宿主内部模型（`0x583E40` / `0x583950` / `0x46B290`）与 `WM_COMMAND 0x8099`，成功后 `internalProjectModel=true`。
- `.zpj` 由插件同步（删除/重命名条目），磁盘文件同步删除/改名。
- 版本闸门：RTSys.exe SHA-256 + 函数序言校验，不匹配自动回退文件级操作。
- 路径沙箱：`ResolveInside` 拒绝绝对路径/UNC/`..`/ADS，`.zpj` 条目与用户输入统一校验。

---

## 6. 安全与健壮性现状

- 鉴权：令牌文件仅当前用户 ACL；常量时间比较；令牌持久（无过期）。
- 敏感操作 nonce：一次性、5 分钟有效；设置面板“生成一次性授权码”。
- 运动控制：默认关闭，需设置中显式开启。
- 请求体限制、Content-Type 校验、错误信息不回显内部异常（详情只写日志）。
- 插件 ABI：假 CWnd stub 与 vtable 不重叠（stub +0x280），x64 拒绝，写入后只读保护。
- 主窗口钩子：线程判断修正、记录实际挂钩窗口、失败返回而不是后台线程直调 MFC、`SendMessageTimeout` 5s。
- Worker：Mutex 单实例生效、父进程退出自动退出、`worker.ready` 写入 pid|实际端口。
- 已知边界：`ZMC_SearchEth` 原生调用无超时（3s 托管超时 + 挂起守卫）；`GetSoftVersion` 已改显式原生缓冲区。

---

## 7. 测试与验证基线

| 项目 | 结果 |
|---|---|
| `RegressionTest.csproj` 回归 | 255 passed / 0 failed |
| AI 工具矩阵（离线） | 43 passed / 0 failed / 7 skipped |
| AI 工具矩阵（模拟器） | 43 passed / 0 failed / 7 skipped |
| HTTP 端到端（真实工程） | 19 passed / 0 failed（读/写/补丁/复制/重命名/删除/搜索/校验/安全拒绝） |
| 端口设置 | 9090→9095→9090 实测通过（监听迁移、鉴权正常） |
| 鉴权/安全 | 无令牌 401；带 Origin 401；motion 403；memory 无 nonce 403；无 CORS 头 |
| 工程树同步 | create/copy/rename/delete 均 `internalProjectModel=true` |

---

## 8. 已知限制（非阻塞）

1. `get_status` 在未配置控制器 IP 时不做自动发现，直接报 NotConfigured（AI 会被提示询问 IP）。
2. 写入前校验是结构/语法校验，不拦截未知命令（`validate_basic` 才做幻觉检查；最终由控制器编译器兜底）。
3. `Q22` UI 大重构（BuildUI/OnSettings 拆分）未做。
4. 全量 i18n 文案与 `.zpj` 短缓存未做（后者有读-改-写竞态风险）。
5. 部分无长度参数的 `StringBuilder` P/Invoke 未改造（第三方 DLL 约定）。
6. 独立 worker 的控制器连接与面板共享同一份 `config.json`，因此 `RTSysIP/httpPort` 同时影响两者。

---

## 9. 新建工程上的 HTTP 验收方案

目标：在**独立的临时 RTSys 工程**上验证，不触碰用户真实工程。

步骤：
1. 创建临时工程目录（如 `%TEMP%\RTSysAI_Scratch_<ts>`），写入 `Scratch.zpj` + `Main.bas`/`Task2.bas`（GBK）。
2. 备份 `%APPDATA%\RTSysAI\config.json`，将 `projectDir` 指向临时工程；`httpPort` 保持 9090。
3. 重启 RTSys 打开 `Scratch.zpj`，打开 AI 插件面板使进程内 ApiServer 启动。
4. 读取 `api_token.txt`，执行：
   - 鉴权：无令牌 401、带令牌 200、Origin 401。
   - 清单：`/api/programs`、`/api/project/items` 应包含临时工程文件。
   - 读：`read_source`、`search_code`、`lookup_command`、`validate_basic`。
   - 写：create → write → patch → copy → rename → delete，断言 `internalProjectModel=true` 与 `.zpj` 计数变化。
   - 安全：`newName=..\evil`、重复创建被拒绝；`/api/test` 404。
5. 清理：删除临时工程与测试程序，恢复 `projectDir`，重启 RTSys 打开原工程。

---

## 10. 净室开发实测：能力缺口清单（2026-11-08）

测试方式：新建独立工程（仅 SPEC.bas），由插件 AI 读取规格、查文档、实现程序、补丁、复制/改名/删除，全程使用插件自身工具。

### 10.1 结论
工程读写链路（读规格 → 生成代码 → 写入 → 回读 → 补丁 → 复制 → 改名 → 删除）全部成功；最终工程只剩 SPEC + CleanRoom_Main，SPEC 未被修改。

### 10.2 未通过/降级的能力

| # | 能力 | 日志/证据 | 根因 | 影响 | 修复方向 |
|---|---|---|---|---|---|
| 1 | `compile_program` / 上传 | `{"success":false,"error":"program upload is not available: ZMC_Up3File ABI/semantics unverified"}` | ZMC_Up3File/ZMC_UpZarToMem 的 ABI/语义未确认，上传链路被禁用 | 无法编译验证、无法下载到控制器；净室开发只能产出文件 | 逆向确认上传导出签名后启用；否则从 schema 移除 `compile_program`，避免无效调用 |
| 2 | `research` / `verify` 子 agent | 两次 `research` 最终结论为“我会完整查阅这些命令的文档。”；主 agent 判定为空结论 | 子 agent 把首个纯文本回复当作最终结论；模型倾向写“我会…”承诺 | 子 agent 不可用，主 agent 需自行重做；浪费轮次 | 子 agent prompt 禁止承诺式输出；检测占位/过短结论并重试一次；记录子 agent 失败原因 |
| 3 | `lookup_command full=true` | DATUM 完整 HTML 被截断（约 74K），mode 3/4 无法确认 | 详情 HTML 有长度上限 | 安全关键知识缺失（回零 mode），AI 退回不可靠记忆 | full=true 提高上限/按章节返回；或在索引里补全 DATUM mode 表 |
| 4 | 程序名归一化 | `read_source("Main.bas")` 报 `program upload is not available…`；`read_source("Main")` 成功 | 未去除 `.bas` 后缀，未命中后走控制器上传兜底 | 误导性错误、AI 误判能力缺失 | handler 入口统一去除 `.bas`；未命中返回“程序不存在” |
| 5 | 自动发现 | 反复 `ZMC_SearchAndOpenEth 失败: 设备忙`，静态 IP 127.0.0.1 成功 | RTSys 进程内与宿主/仿真器争用搜索通道 | 自动发现实际不可用，依赖静态 IP；留空时体验差 | 进程内且已配置静态 IP 时优先直连；设备忙时不重复搜索/降噪 |
| 6 | `validate_basic` 未暴露给 AI | AI 工具 schema 中无 `validate_basic` | 仅 HTTP 路由存在 | AI 无法在写入前做幻觉命令校验 | 增加只读 AI 工具 `validate_basic` |
| 7 | 提示词知识冲突 | 系统提示安全示例含 `PAUSE 10`；官方文档禁止在 BASIC 程序中使用 PAUSE；SPEC 的 HOME 不存在 | 提示词未与 RTBasic 参考同步 | 可能生成被官方明确禁止的代码 | 修正安全示例（改 WAIT IDLE/IDLE 轮询），补充 HOME→DATUM 说明 |
| 8 | 网络瞬断 | 3 次重试后 `AI 请求执行失败`（随后手动重试成功） | 外部网络/网关瞬断 | 单轮对话失败 | 可接受；可增加更长退避与错误分类提示 |

### 10.3 已验证正常的能力
- 工程：list/read/write/patch/copy/rename/delete、工程树同步、`.zpj` 更新、SPEC 只读保护。
- 知识：lookup_command 基本查询（BASE/SPEED/ACCEL/DECEL/MOVEABS/MOVE/DATUM/IDLE 等）。
- 控制器：静态 IP 连接、status、模拟器矩阵；轴/IO 不可用时明确失败。
- 安全：令牌鉴权、写入确认、路径逃逸拒绝。
