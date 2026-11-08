# TestPro 净室克隆循环记录（2026-11-08）

## 目标与验收标准

在 TestPro 工程中只保留 SPEC（从真实 TaskMain/TaskAutoRun/TaskHands 提炼的行为需求），由插件 AI 从零实现三个程序。验收：

1. 三个程序由本会话 AI 从 SPEC 创建，SPEC 不被修改；
2. 每个程序 `validate_basic` = `ok:true`；
3. SPEC 的关键行为逐条覆盖；
4. 全程无校验器误报、无 `BLOCKED`、无内联/改名等绕道；
5. 工程树与 `.zpj` 同步。

## 迭代结果

| 轮次 | 结果 | 暴露的问题 |
|---|---|---|
| 1 | 三程序完成，`ok:true`，但初始化子流程被**内联** | `validate_basic` 把自定义 `SUB` 定义名当未知命令 |
| 2 | 三程序完成，改用 `GLOBAL SUB` 规避 | 多 SUB 时只认第一个：`_reSubDef` 的 `\s+` 跨行把 `END SUB\nSUB X` 的第二个 `SUB` 当名字吞掉 |
| 3 | 未产出 | 模型自行进入 Plan Mode + research 子 agent，卡在确认框 |
| 4 | **通过**：三程序 `ok:true`，普通 `SUB`，无绕道 | — |

最终结果存档：`docs/cleanroom_results_2026-11-08/`（SPEC + 三个程序）。

## 本轮修复的插件缺陷

| 缺陷 | 修复 |
|---|---|
| `write_source` 用 VB.NET 规则校验 RTBasic（拒绝 `SUB`/`GLOBAL`/`DIM`、要求 `AS`） | `AiTools.cs` 改用与 `validate_basic` 同源的 `ValidateTrioBasicCodeErrors` |
| 自定义 SUB 定义/调用被报 `Identifiers not in TrioBASIC reference` | `AiValidation.cs` 收集行首 `SUB`/`GLOBAL SUB` 定义名并放行 |
| 多 SUB 只认第一个（`\s+` 跨行吞掉第二个 `SUB`） | `_reSubDef` 改为 `^[ \t]*(?:GLOBAL[ \t]+)?SUB[ \t]+(...)`，按行匹配 |
| `application/json` 无 charset 时按 GBK 解码，中文源码 `validate_basic` 返回 500 | `ApiServer.HandleBody` 默认 UTF-8 |
| 同名本地缓存阻塞 `create_program` | 有工程时只拒绝工程内同名程序 |
| 子 agent 失败也扣派发配额 | 仅成功派发才计数 |
| 新建程序模板含非法 RTBasic（`SPEED(100)` 是读、`HOME` 不存在） | 改为 `BASE(0)/SPEED = 100/ACCEL = 500/DECEL = 500/END` |
| `[Cache Monitor]` 刷聊天区 | 只写日志 |

回归测试：`PlugInWorker/bin/Test/RTSysPlugInWorkerTests.exe` → **336 passed / 0 failed**。

## 恢复状态

- 真实 `TaskMain.bas` / `TaskAutoRun.bas` / `TaskHands.bas` / `TestPro.zpj` 已从备份恢复，SHA-256 与基线一致；
- 真实工程目录中的 `SPEC.bas` 已移除（存档于 `docs/cleanroom_results_2026-11-08/SPEC.bas`）；
- 程序缓存已恢复为测试前版本；
- 备份目录：`%TEMP%\TestPro_clone_backup_20261108_211206`。

## 遗留

- 控制器未连接，只做了离线 `validate_basic`，未执行真实编译/下载；
- `compile_program`/上传链路仍因 `ZMC_Up3File` 语义未确认而不可用；
- 运行净室测试时不要让模型自行进入 Plan Mode（提示词已显式禁止）。
