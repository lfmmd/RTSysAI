# AI 净室输出 vs 原项目 对比与能力评估（2026-11-08）

## 对比对象

- 原项目：`追剪应用DEMO` 的 `TaskMain.bas` / `TaskAutoRun.bas` / `TaskHands.bas`（用户生产代码）
- AI 输出：`docs/cleanroom_results_2026-11-08/`（第四轮净室克隆，工程内只有 SPEC）
- 方法：逐行静态比对 + 命令集合比对 + 独立 `validate_basic`（HTTP）；**未连接控制器，未做真实编译**

## 1. 量化对比

| 指标 | 原项目 | AI 输出 |
|---|---|---|
| TaskMain 行数 / 代码行 | 202 / 155 | 213 / 148 |
| TaskAutoRun 行数 / 代码行 | 36 / 33 | 51 / 28 |
| TaskHands 行数 / 代码行 | 73 / 65 | 96 / 73 |
| 合计行数 / 代码行 | 311 / 253 | 360 / 249 |
| SUB 过程数 | 11（10+1+0） | 7（7+0+0） |
| `validate_basic` | — | 3/3 `ok:true` |
| 未知/幻觉命令 | — | 0 |
| `BLOCKED` / 绕道 | — | 0 |

代码行数基本持平（253 vs 249），AI 多出的行主要是注释与空行。命令集合：
- TaskMain：共同命令 48 个；原项目独有 `DIM`/`EXP`，AI 独有 `GOSUB`/`PRINT`（均为等价写法）。
- TaskAutoRun：共同 18 个；原项目独有 `AXIS`/`DIM`/`SUB`，AI 独有 `BASE`/`PRINT`（`MOVESLINK(...)AXIS(x)` ↔ `BASE(x)+MOVESLINK(...)`）。
- TaskHands：共同 22 个；原项目独有 `AXIS`/`TRUE`/`FALSE`，AI 独有 `PRINT`（`ON/OFF` 代替 `TRUE/FALSE`）。

## 2. 行为映射

| SPEC/原项目行为 | 原项目实现 | AI 实现 | 结论 |
|---|---|---|---|
| 上电初始化顺序 | `INT_ENABLE=1` → 5 个初始化 SUB | 同顺序，SUB 改名 | 等价 |
| 急停 | `IN(2)`/`bit5` → 停任务、`gStatus=0` | 同 | 等价 |
| 切手动（含 `gStatus=3` 等周期） | 嵌套 `IF/ELSEIF` | 同 | 等价 |
| 切自动 | 停任务 + `RUN TaskAutoRun,2` | 同 | 等价 |
| 状态刷新 | `MODBUS_IEEE(204)=MSPEED(6)`、`MODBUS_REG(214)=gStatus` | 同 | 等价 |
| 从轴报警 2^22 | `AXISSTATUS(0) AND 2^22` → `bit1000` | `AND 4194304` | 等价 |
| 变量定义 | `SubVarDef` 内 `GLOBAL` 声明 | 文件顶部 `GLOBAL` 声明 + `InitVar` 只置 `gStatus` | 等价（更规范） |
| FLASH 块 0/1 | 读标志→不符写默认→相符读回，同步 MODBUS | 同顺序；标志先赋变量再 `FLASH_WRITE` | 等价（更稳妥） |
| 掉电恢复 | `dpos(0)=vr(35)`；`modbus_long(200)=VR_INT(30)` | `BASE(0); DPOS=VR(35)`；同 | 等价 |
| 按钮复位 | bit 0/3/4/5/30/40/50/60 | 同 | 等价 |
| 轴参数 | 主轴 6：`ATYPE=3,SPEED=100`；从轴 0 全部参数 | 同 | 等价 |
| 参数计算 | `UNITS=gMainPerPls/gMainPerDis` 等 | 同 | 等价 |
| 开机检查 | 报警→bit1000；否则 `OP(12,ON)`、`bit3=ON` | 同 | 等价 |
| 停止任务 | 清 bit + `STOPTASK 1/2/3` + `RAPIDSTOP(2)` | 同（`RAPIDSTOP` 内联） | 等价 |
| 掉电中断 | `VR_INT(30)=MODBUS_LONG(200)`；`vr(35)=dpos(0)` | 同 | 等价 |
| 自动四段追剪 | 四段 `MOVESLINK(...)axis(0)` | `BASE(0)` + 四段 `MOVESLINK(...)` | 等价（官方语义：distance 作用于 BASE 轴） |
| 产量 +1 / `gStatus=3` 切手动 | 同 | 同 | 等价 |
| 手动 Jog/相对/绝对/回零/参数保存 | 完整实现 | 完整实现 | 等价 |

## 3. 差异清单（含风险评估）

| # | 差异 | 原项目 | AI | 风险 |
|---|---|---|---|---|
| 1 | Jog 停止 | `CANCEL(2)AXIS(0)`，无等待 | `BASE(0); CANCEL(2); WAIT IDLE` | 低；更稳，但会阻塞到停稳 |
| 2 | 手动命令分支 | `bit30/40/50/60`、`bit8/9` 用 `ELSEIF` 链 | 各自独立 `IF` | 低；仅多按钮同时为 ON 时行为不同（正常操作互斥） |
| 3 | 手动标志值 | `gDriveAxis=TRUE/FALSE`（-1/0） | `=ON/OFF`（1/0） | 低；该变量仅内部使用，自洽；若有外部监视则值不同 |
| 4 | 局部变量 | `DIM lFolAxisAccdis...` 显式声明 | 未 `DIM`，依赖 RTBasic 自动定义 | **中**；语法文档支持，但未经真实编译验证 |
| 5 | FLASH 校验标志 | 直接写 `gFlagUser/gFlagMec` 常量 | 先赋 `saveFlag/usrSaveFlag` 再写 | 低；写入值相同，且更符合"变量参数"要求 |
| 6 | 辅助过程 | 10 个 SUB（含 `SubStatusRfls`/`SubMecCheck`/`SubStopAxis`） | 合并/内联为 7 个 SUB | 低；行为一致，结构更紧凑 |
| 7 | 报警位常量 | `2^22` / `exp(2,22)` | `4194304` | 低；值相同，可读性略差 |
| 8 | 注释/排版 | 简洁 | 更多中文注释和空行 | 无 |

## 4. 能力评分

| 维度 | 评分 | 依据 |
|---|---|---|
| 需求/规格还原 | 5/5 | SPEC 的行为点全部落地，状态机、FLASH 顺序、MODBUS 地址无遗漏 |
| 命令准确性（防幻觉） | 5/5 | 50+ 命令先查后写，独立校验 0 未知命令 |
| 代码结构 | 4.5/5 | SUB 划分清晰；部分辅助过程被内联，局部变量未显式 DIM |
| 行为等价 | 4.5/5 | 静态比对等价；存在 #1-#3 的轻微差异 |
| 安全/健壮 | 4.5/5 | Jog 停止增加 `WAIT IDLE`；未额外增加急停退出（与原项目一致） |
| 工具使用/自校验 | 5/5 | 主动 `validate_basic`，遇插件缺陷能定位并如实记录 |
| 诚实度 | 5/5 | 主动声明未编译、未连控制器、FLASH 变量转换等不确定点 |
| **总体** | **约 94/100** | 可独立完成行为级等价克隆，具备工程可用性；编译级验证受环境限制 |

## 5. 结论

AI 在只给定行为级 SPEC、且看不到原代码的条件下，**独立产出了与原项目行为等价的三个 RTBasic 程序**：

- 结构、命令、MODBUS 地址、FLASH 读写顺序、轴参数、状态机均对齐；
- 无幻觉命令、无校验绕道；
- 差异集中在等价写法、局部变量声明和少量健壮性增强，未发现功能性错误。

主要未验证项：**真实控制器编译/下载**（控制器未连接）、隐式局部变量与 `GOSUB` 嵌套调用的编译器兼容性。

## 6. 复现

- 结果：`docs/cleanroom_results_2026-11-08/`
- 过程：`docs/cleanroom_loop_2026-11-08.md`
- 校验：`POST http://localhost:9090/api/validate_basic`（需 `X-Auth-Token`）
