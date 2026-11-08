# 自动备份与编码自动跟随（2026-11-08）

## 1. 需求

1. 修改项目代码时自动备份，覆盖所有写入口（AI 工具 / HTTP / UI）。
2. RTSys 左下角“输出默认编码”可在 ANSI / UTF-8 间切换，插件不应要求用户再设置一次；应自动跟随。

## 2. 编码调查结论

- “输出默认编码: ANSI ∨”是 RTSys 的**自绘状态栏控件**（下拉项只有 `ANSI` 与 `UTF-8`）。
- 该控件没有 UIA 元素、没有子窗口文本、注册表/配置文件中也没有对应键，`RTSys.exe` 中无 `ANSI`/`Encoding` 字符串。
- 结论：**无法从插件直接读取或设置这个控件**。

### 采用的自动跟随策略（不需要用户输入）

`PlugInWorker/BasEncoding.cs`：

| 场景 | 规则 |
|---|---|
| 已有 `.bas` 文件 | 按字节检测：UTF-8 BOM / UTF-8 / ANSI(GBK)，写回时保持原编码 |
| 新 `.bas` 文件 | 统计当前工程目录内 `.bas` 的实际编码并跟随多数；无文件时用 RTSys 默认 ANSI |
| 纯 ASCII 文件 | 视为 ANSI（两种编码字节相同，避免把新建模板误判为 UTF-8） |
| 高级覆盖 | 配置键 `basEncoding`：`auto`（默认）/ `ansi` / `utf8`，不需要在界面上填 |

读取侧同步改为按上述规则解码（`ProgramWorkspace.ReadTextAuto`）。

> 通过 RTSys 编辑器保存的文件由宿主按其编码设置落盘；插件写入后再次检测，自动跟随。

## 3. 自动备份实现

`PlugInWorker/ProgramBackup.cs`：

- 目录：`%APPDATA%\RTSysAI\backups`
- 命名：`程序名__yyyyMMdd_HHmmss_fff__原因.bas.bak`；工程文件为 `项目名__...__原因.zpj.bak`
- 编码：按原文件编码保存（与第 2 节同一套检测），恢复时自动解码
- 保留：每个程序/文件最近 **10** 份；超过 **30** 天清理；总数上限 **300**
- 失败策略：
  - `delete` / `rename`：源码或 `.zpj` 备份失败 → **取消操作**
  - `write` / `patch`：源码备份失败 → **取消写入**；`.zpj` 备份失败 → 取消新建
- 覆盖写入路径（全部下沉到 `RTSysHandlers`，AI/HTTP/UI 共用）：

| 操作 | 源码备份 | `.zpj` 备份 |
|---|---|---|
| `write_source`（已有程序） | ✅ | — |
| `write_source`（新建到工程） | ✅（若已有） | ✅ |
| `patch_source` | ✅ | — |
| `create_program` | — | ✅ |
| `copy_program` | — | ✅ |
| `rename_program` | ✅ | ✅ |
| `delete_program` | ✅ | ✅ |

响应中新增 `backup` / `projectBackup` / `backupError` 字段；旧版本中 `ReadSource` 返回匿名对象、`as Dictionary` 取不到源码，导致**源码备份实际从未生效**，本次已修正。

## 4. 查看与恢复

- HTTP：
  - `GET /api/backups[?name=程序名]`
  - `POST /api/backups/{备份文件名}/restore`，body `{"name":"目标程序"}`（可省略）
- AI 工具：
  - `list_backups(name?)`
  - `restore_backup(file, name?)` —— 写操作，需确认
- `.zpj` 备份只提供列表与手工恢复（自动写回会与运行中的工程模型脱节）。

## 5. 验证

- 回归测试：**344 passed / 0 failed**（新增编码检测、备份命名/保留策略用例）。
- 独立 worker + 临时工程 HTTP 端到端：
  - `write_source` → `Orig__...__write.bas.bak`（GBK，解码后与原文件一致）
  - `patch_source` → `...__patch.bas.bak`
  - `create_program` → `TempProj__...__create.zpj.bak`
  - `rename_program` → 源码 + `.zpj` 备份
  - `delete_program` → 源码 + `.zpj` 备份
  - `list_backups` 正确解析 25 条
  - `restore_backup` 恢复后 `read_source` 与原文件逐字一致
- 真实 TestPro 工程四个文件 SHA-256 与测试前完全一致；测试配置已恢复。

## 6. 修改文件

- 新增：`PlugInWorker/BasEncoding.cs`、`PlugInWorker/ProgramBackup.cs`
- 修改：`AiConfig.cs`（`basEncoding`）、`ProgramWorkspace.cs`（路径暴露/编码）、`RTSysHandlers.cs`（备份与响应字段）、`AiTools.cs`（工具/清单）、`ApiServer.cs`（备份路由）、`AiService.cs`（移除旧备份实现）、两个 `.csproj`
- 测试：`RegressionTestMain.cs` 新增编码与备份用例

## 7. 限制

- `.zpj` 自动恢复未开放（避免与运行中的 RTSys 工程模型冲突）。
- 通过编辑器写入时，最终落盘编码仍由 RTSys 决定；插件按文件实际编码检测并跟随，不强行修改宿主设置。
