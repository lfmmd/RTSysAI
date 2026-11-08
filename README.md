# RTSysAI — RTSys AI 助手插件

> 镜像：[GitHub](https://github.com/lfmmd/RTSysAI) | [Gitee](https://gitee.com/lfmmd/RTSysAI)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![RTSys](https://img.shields.io/badge/RTSys-ZMotion-orange.svg)](https://www.zmotion.com.cn/)
[![API: Anthropic Messages](https://img.shields.io/badge/API-Anthropic%20Messages-ff6b6b.svg)](https://docs.anthropic.com/en/api/messages)
[![GitHub stars](https://img.shields.io/github/stars/lfmmd/RTSysAI?style=social)](https://github.com/lfmmd/RTSysAI/stargazers)

为 ZMotion **RTSys** 运动控制 IDE 提供 AI 编程助手能力的插件。AI 通过工具调用（tool use）直接读写 RTBasic 程序、操作 `.zpj` 工程、查询 RTBasic 命令参考、读写控制器数据，让自然语言驱动的运动控制开发成为可能。

本项目是 [TrioAI](https://github.com/lfmmd/TrioAI)（MotionPerfect 版）向 RTSys 的移植版本。

## 特性

- **嵌入式 AI 助手面板**：在 RTSys 内嵌对话式 AI 助手，原生 WPF UI，无外部运行时依赖
- **53 个 AI 工具**：覆盖程序/工程读写、控制器交互、VR/TABLE/IO/轴/驱动/EtherCAT、RTBasic 命令查询、HMI 画面解析、备份恢复等
- **RTSys 宿主深度集成**：
  - 通过 Scintilla 桥接读取/写入宿主中已打开的程序（含未保存内容）
  - 通过宿主工程模型（CFileView）完成新建/删除/重命名/加入工程，不弹对话框、不抢焦点
  - 解析 `.zpj` 工程文件与 `.hmi` 画面文件（HMI 只读）
- **离线可用**：未连接控制器时，程序读写落到本地缓存，并标记 `isDirty` / `pendingUpload`，连接后再下载到控制器
- **RTBasic 严格语法约束**：内置命令/签名索引，`validate_basic` 在写入前校验，降低幻觉命令
- **子 Agent**：`research` / `review` / `debug` / `explore` / `verify` 在独立上下文中完成调研、评审、诊断、项目摸底和独立验证
- **二次确认机制**：写程序、写控制器、运行/停止、删除/重命名等破坏性操作需用户在 UI 中确认
- **自动备份**：写程序/工程前自动备份原文件（保留原编码），可列出并恢复
- **Plan Mode**：先只读调查、提交计划，用户批准后才开放写操作
- **HTTP API（可选）**：本地 HTTP 接口，默认关闭；启用后供外部脚本/自动化/其他 AI 客户端调用
- **会话持久化 + 持久记忆**：对话历史、跨会话记忆
- **多语言 UI**：中文、英文、德文、法文

## 架构

RTSys 是 x86 的 MFC 桌面程序，插件通过原生 DLL 导出 `PlugIn_*` 生命周期函数：

```
RTSys.exe
  └─ RTSysPlugInNative.dll          原生 shim（导出 6 个 PlugIn_*，托管 CLR Hosting）
       └─ RTSysPlugInAdapter.dll    托管适配器 + WPF 对话面板
            └─ RTSysPlugInWorker    AI 服务 / 工具 / HTTP API / 宿主桥接（程序集）
```

当前为**单进程模式**：Adapter 引用 Worker 程序集，`ChatPanel` 直接调用进程内的 `AiService.Chat()`，不经过 HTTP。`RTSysPlugInWorker.exe` 同样会构建，可作为独立进程入口用于调试/回归测试。

### Plug_in ABI（已由 IDA 确认）

| 导出名 | 签名 | 调用约定 | 说明 |
|---|---|---|---|
| `PlugIn_GetHandle` | `void(void**)` | `__cdecl` | 输出宿主上下文 |
| `PlugIn_SetDockBar` | `bool(void)` | `__cdecl` | 能力查询：是否支持 DockBar |
| `PlugIn_SetAutoSize` | `bool(void)` | `__cdecl` | 能力查询：是否自动尺寸 |
| `PlugIn_Create` | `CWnd*(void*)` | `__cdecl` | 创建插件窗口，返回 `CWnd*` |
| `PlugIn_Reserved` | `void(int)` | `__cdecl` | 保留，参数恒为 0 |
| `PlugIn_Destroy` | `void(void*)` | `__cdecl` | 销毁窗口（`AfxFreeLibrary` 前调用） |

## 构建与安装

### 前提

- Visual Studio 2019/2022（含 C++ 桌面工作负载；`build_deploy.bat` 也会探测 VS 18 安装路径）
- .NET Framework 4.8 开发包
- 已安装 RTSys。构建脚本默认从 `%USERPROFILE%\Desktop\RTSys` 读取 RTSys 目录，可用环境变量覆盖：

```bat
set RTSYS_DIR=D:\path\to\RTSys
```

### 一键构建

```bat
build_deploy.bat            :: 仅编译
build_deploy.bat deploy     :: 编译并部署到 %RTSYS_DIR%\Plug_in\AIPlugin\
```

脚本依次编译：

1. `PlugInAdapter\PlugInAdapter.csproj` → `RTSysPlugInAdapter.dll`
2. `PlugInNativeShim\PlugInShimNative.cpp`（`cl.exe`）→ `RTSysPlugInNative.dll`
3. `PlugInWorker\PlugInWorker.csproj` → `RTSysPlugInWorker.exe`
4. 可选部署：复制上述产物、依赖 DLL 和 `skills\` 到 `RTSys\Plug_in\AIPlugin\`

### 手动部署

将以下文件复制到 `RTSys\Plug_in\AIPlugin\`：

```
RTSysPlugInNative.dll
RTSysPlugInAdapter.dll
RTSysPlugInWorker.exe
RTSysPlugInWorker.exe.config
TrioSharedLibrary.dll
TrioCommunicationsLibrary.dll
TrioBaseLib.dll
TrioCustomControlsLib.dll
Newtonsoft.Json.dll
skills\
```

其中 `Trio*`、`Newtonsoft.Json.dll` 等外部依赖由 RTSys 安装目录提供。

### 注册插件菜单

部署后运行：

```bat
python register_plugin.py --no-deploy
```

脚本会写入 `HKCU\Software\RTSys Application 1.0\RTSys\Plugin`（`Count` / `PluginN` / `Menu nameN` / `Menu promptN` / `DirN`），重启 RTSys 后生效。

### 生成 RTBasic 帮助库（首次使用建议执行）

RTBasic 命令参考库从本机 RTSys 自带帮助文档生成，生成物不纳入版本控制：

```bat
python tools\build_rtbasic_skills.py --src "%RTSYS_DIR%\help\RTBasic"
```

输出到 `PlugInWorker\skills\rtbasic\zh\`（已在 `.gitignore` 中忽略）。重新执行 `build_deploy.bat deploy` 后，帮助库会随 `skills\` 一起部署。

首次打开面板后，进入 **设置 → 初始化 Skill 数据**。未初始化时插件仍可运行，但 `lookup_command` / `validate_basic` 只能使用内置提示词，代码质量会下降。

## 配置

配置文件：`%APPDATA%\RTSysAI\config.json`

在面板顶部点击 **设置**，可配置：

| 字段 | 说明 |
|------|------|
| **API Key** | 服务商控制台申请的 Key |
| **API URL** | Anthropic Messages 兼容的 Base URL |
| **模型** | 主模型名 |
| **轻模型** | 子 Agent 使用的模型，留空则与主模型一致 |
| **控制器 IP** | RTSys / ZMotion 控制器地址；留空时程序仍可离线编辑 |
| **HTTP API 端口** | 1024–65535，默认 `9090` |
| **扩展思考 / 思考预算** | 控制 Extended Thinking 与 `budget_tokens` |
| **方言 / 编码** | RTBasic 提示词方言、`.bas` 输出编码 |
| **Skill 初始化** | 将内置 `skills\` 复制到 `%APPDATA%\RTSysAI\skills\` |

### 兼容的 API 服务

接口遵循 [Anthropic Messages API](https://docs.anthropic.com/en/api/messages)（`/v1/messages`），**不兼容 OpenAI Chat Completions 格式**。

| 平台 | API URL（base_url）示例 |
|------|--------------------------|
| **DeepSeek** | `https://api.deepseek.com/anthropic` |
| **智谱 GLM** | `https://open.bigmodel.cn/api/anthropic` |
| **Anthropic 官方** | `https://api.anthropic.com` |
| 任意 Anthropic 兼容代理 | 你的代理 base URL |

模型名以服务商控制台为准。默认值为 `https://api.deepseek.com/anthropic` + `claude-sonnet-4-20250514`，可在设置中修改。

> 如果只有 OpenAI 兼容 API，可用 [LiteLLM](https://github.com/BerriAI/litellm) 等代理转换为 Anthropic Messages 格式。

## 使用

通过 RTSys 的插件菜单打开 **AI 助手** 面板，输入问题即可。

**示例对话：**

- *"列出当前工程里所有程序，并说明每个程序的作用"*
- *"读一下 MAIN1，检查有没有 RTBasic 语法错误"*
- *"新建一个 TEST 程序：循环检测 VR(512)，值为 1 时把 OP(0,0) 置 1"*
- *"把 MAIN1 里所有 MOVEABS 的第二个参数改成 100"*
- *"连接控制器，读 VR(0) 起 10 个值"*
- *"解析当前工程的 HMI 画面，列出所有按钮控件"*
- *"为什么轴 0 不动？查一下报警和驱动状态"*

**输入框快捷键：**

- **Enter**：发送
- **Shift + Enter**：换行

**破坏性操作确认：** 底部弹出确认面板，点 **允许** 执行、**拒绝** 取消。

## AI 工具列表

> 共 53 个工具。

**控制器状态与连接**

- `get_status` — 连接状态、产品名、固件版本、待上传程序
- `controller_status` — 当前连接状态/模式
- `controller_autoconnect` — 自动搜索并连接控制器
- `controller_simulator` — 连接仿真器

**程序与工程**

- `list_programs` / `list_project_items` — 程序/工程条目列表
- `read_source` / `write_source` / `patch_source` — 读、整体写、文本级增量编辑
- `create_program` / `delete_program` / `rename_program` / `copy_program`
- `open_program` / `search_code`
- `save_project` / `open_project`

**备份**

- `list_backups` / `restore_backup` — 列出/恢复写前备份

**控制器数据**

- `read_vr` / `write_vr`
- `read_table` / `write_table`
- `list_axes` / `get_axis_detail`
- `list_digital_io` / `read_digital_io`
- `list_analogue_io` / `read_analogue_io`
- `list_alarms`
- `read_drive_param` / `write_drive_param`
- `read_ethercat_sdo` / `write_ethercat_sdo`
- `run_program` / `stop_program`

**知识库与校验**

- `lookup_command` — 查询 RTBasic/命令参考
- `validate_basic` — 按命令/签名索引校验源码
- `read_skill` / `discover_skills` — 加载/发现 Markdown 技能包

**RTSys 宿主**

- `read_hmi` — 只读解析 `.hmi` 画面文件
- `list_plugins` — 列出 `Plug_in` 下已安装插件

**记忆、任务与计划**

- `update_memory`
- `task_create` / `task_update` / `task_list`
- `enter_plan_mode` / `exit_plan_mode`

**子 Agent（只读、独立上下文）**

- `research` — 查文档/命令细节并返回摘要
- `review` — 代码评审（按严重级别排序）
- `debug` — 运行时故障根因诊断
- `explore` — 项目摸底，建立程序索引
- `verify` — 写入后的独立验证（PASS/FAIL/PARTIAL）

## HTTP API（可选）

HTTP API **默认关闭**，避免普通使用时占用端口/对外暴露。启用方式（二选一）：

- 在 `%APPDATA%\RTSysAI\config.json` 中设置 `"enableHttpApi": true`
- 或设置环境变量 `RTSYS_AI_HTTP=1`

启用后监听 `http://localhost:9090`（端口可在设置中修改，失败时依次回退 9090→9091→9092→9093）。

### 鉴权

所有请求都需要 `X-Auth-Token` 请求头。令牌文件：

```
%APPDATA%\RTSysAI\api_token.txt
```

也可在 **设置 → 复制 API 令牌** 获取。写记忆（`/api/memory`）另需 `X-Confirm-Nonce`；运动控制请求另需 `X-Motion-Nonce`。一次性授权码 5 分钟内有效。

### 快速示例

```powershell
$token = Get-Content "$env:APPDATA\RTSysAI\api_token.txt" -Raw

# 身份/版本
curl -H "X-Auth-Token: $token" http://localhost:9090/api/identity

# 控制器状态
curl -H "X-Auth-Token: $token" http://localhost:9090/api/status

# 列出程序
curl -H "X-Auth-Token: $token" http://localhost:9090/api/programs

# 读 MAIN1 源码
curl -H "X-Auth-Token: $token" http://localhost:9090/api/programs/MAIN1/source

# 读 VR(0) 起 10 个值
curl -H "X-Auth-Token: $token" "http://localhost:9090/api/vr/0?count=10"

# 校验 RTBasic 源码
curl -X POST -H "X-Auth-Token: $token" -H "Content-Type: application/json" `
     -d '{"code":"BASE(0,1)\nMOVE(100)"}' `
     http://localhost:9090/api/validate_basic
```

### 运动控制端点

`run` / `stop`、VR/TABLE/IO/驱动写入、EtherCAT 写入等端点默认**双重保护**：

1. 需要显式开启运动控制 API（面板设置）
2. 每次请求还需携带 UI 生成的一次性 `X-Motion-Nonce`

普通只读查询不需要 nonce。

### 主要路由

```
/api/identity              /api/status               /api/project
/api/project/items         /api/programs             /api/backups
/api/vr                    /api/table                /api/axes
/api/io                    /api/alarms               /api/drive
/api/ethercat              /api/sysvar               /api/descriptors
/api/plugins               /api/robots               /api/recipes
/api/remote-devices        /api/msbus/scan           /api/controller
/api/validate_basic        /api/search_code          /api/events
/api/processes             /api/skills               /api/skill
/api/lookup                /api/tasks                /api/memory
```

## 安全机制

### 二次确认

以下操作必须在 UI 中由用户点 **允许** 才能执行：

- `write_source` / `patch_source` — 写代码
- `create_program` / `delete_program` / `rename_program` / `copy_program`
- `write_vr` / `write_table` / `write_drive_param` / `write_ethercat_sdo`
- `run_program` / `stop_program`
- `restore_backup`

### 自动备份

写程序/工程前自动备份到 `%APPDATA%\RTSysAI\backups\`，保留原文件编码（ANSI/UTF-8 自动检测）。可通过 `list_backups` 查看、`restore_backup` 恢复。

### Plan Mode

`enter_plan_mode` 会阻断所有写/运行类工具，只允许只读调查；用户批准计划后由 `exit_plan_mode` 退出。

### 密钥与隐私

- API Key 只保存在本机 `%APPDATA%\RTSysAI\config.json`，不会写入仓库
- HTTP API 默认关闭；开启后仍需令牌
- 仓库不包含任何真实 API Key、控制器 IP 或本机用户路径

## 项目结构

```
TrioAI_RTSys/
├─ PlugInAdapter/                托管适配器 + WPF UI（宿主进程内）
│   ├─ PlugInAbi.cs              Plug_in ABI 定义
│   ├─ PlugInHostAdapter.cs      6 个导出的托管实现、生命周期
│   ├─ PlugInNativeExports.cs    原生导出入口
│   └─ ChatPanel.cs              对话/设置/历史/记忆/关于 UI
├─ PlugInNativeShim/             原生 shim（cl.exe 编译）
│   ├─ PlugInShimNative.cpp      CLR Hosting + PlugIn_* 导出
│   └─ PlugInShimNative.def
├─ PlugInWorker/                 AI 服务与业务逻辑
│   ├─ Program.cs                独立进程入口 / 回归测试入口
│   ├─ AiService.cs              AI 调用循环、SSE 流式解析
│   ├─ AiConfig.cs               配置、Skill 初始化
│   ├─ AiHistory.cs              历史裁剪/压缩
│   ├─ AiPrompt.cs               系统提示词
│   ├─ AiTools.cs                53 个工具定义与分派
│   ├─ AiSubagent.cs             子 Agent
│   ├─ AiSkills.cs               Skill 索引/加载
│   ├─ AiValidation.cs           RTBasic 语法校验
│   ├─ ApiServer.cs              HTTP API（可选）
│   ├─ RTSysHandlers.cs          工具具体实现
│   ├─ RTSysProjectModel.cs      CFileView 工程模型桥接
│   ├─ RTSysEditor.cs            Scintilla 编辑器桥接
│   ├─ ProgramBackup.cs          写前自动备份
│   ├─ BasEncoding.cs            .bas 编码检测/转换
│   ├─ ZMotionAPI.cs             zmotion.dll P/Invoke
│   ├─ ZMotionExtensions.cs      VR/TABLE/IO/轴/驱动等封装
│   ├─ ZMotionHandle.cs          控制器句柄
│   └─ skills/                   general / prompts / rtbasic（生成）
├─ AbiTest/                       ABI 测试
├─ HostSim/                      宿主模拟器（无 RTSys 时调试）
├─ tools/                        构建、探测、部署辅助脚本
├─ docs/                         分析与审核报告
├─ build_deploy.bat              一键编译/部署
├─ register_plugin.py            写入插件注册表
└─ verify_plugin.ps1             端到端验证
```

## 兼容性

- **RTSys**（ZMotion，x86 进程）
- **.NET Framework 4.8**
- **Windows 10 / 11**

原生工程模型桥接（删除/重命名/加入工程）依赖 RTSys 内部函数签名；版本不匹配时会自动回退到文件级操作（直接改 `.bas` / `.zpj`）。

## 已知限制

- **帮助库不随仓库分发**：需用 `tools/build_rtbasic_skills.py` 从本机 RTSys 帮助生成
- **HTTP API 默认关闭**：需要显式开启；运动控制端点还需要一次性 nonce
- **`.hmi` 只读**：`read_hmi` 只解析，不修改画面
- **部分工具需要控制器连接**：轴、IO、报警、驱动、EtherCAT 等
- **单进程模式**：Adapter 与 Worker 同进程；某个工具崩溃会影响宿主，建议通过 Plan Mode/只读工具降低风险

## 许可证

[MIT License](LICENSE)

本项目由第三方开发，与 ZMotion / RTSys 官方无关。插件按“原样”提供，不提供任何明示或暗示的保证，使用风险由用户自行承担。
