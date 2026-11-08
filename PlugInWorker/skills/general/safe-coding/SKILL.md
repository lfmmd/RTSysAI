---
name: safe-coding
description: 安全运动编程规范（RTBasic/RTSys）—— 避免死锁、安全停止、轴错误处理。
---

# 安全运动编程规范

## 强制安全规则

- 绝不写没有退出条件、不检查轴状态的无限循环。
- 控制循环里必须检查 `MOTION_ERROR`（运动错误）与 `AXISSTATUS`（轴状态位）。
- 检测到致命错误后，使用 `RAPIDSTOP`（或 `CANCEL`）急停；`WDOG` 是系统看门狗参数，不应作为常规“关闭安全”的手段。
- 轴正在运动时，禁止 `SERVO = OFF`（关使能）或释放抱闸；必须先停止且 `MSPEED(轴号) == 0`。

## 标准模式：带超时的移动

```basic
' 轴组 0 移动并等待，带超时保护
BASE(0)
ATYPE = 1
UNITS = 4000
SPEED = 200
ACCEL = 1000
DECEL = 1000
DPOS = 0
MOVE(100)

t = TICKS                  ' 毫秒计时
WHILE AXISEMG_IN = 0       ' 等待到位
    IF TICKS - t > 5000 THEN
        RAPIDSTOP             ' 超时急停
        PRINT "Move timeout on axis 0"
        RETURN
    ENDIF
    ' 不要使用 PAUSE：官方明确 PAUSE 仅限 PC 软件调用，BASIC 程序中使用会导致整个程序停止。
WEND

IF MOTION_ERROR <> 0 THEN
    RAPIDSTOP
    PRINT "Motion error detected"
ENDIF
```

## 回零指令（重要）

- RTBasic **没有 `HOME` 指令**；回零使用 `DATUM(mode)`（mode+10 = 碰到限位后反找）。
- 回零前务必确认 `DATUM_IN` 原点开关已配置、方向正确，首跑低速并准备急停。

## 运行前检查清单

1. 所有轴已回零（`DATUM` 完成）？
2. 急停输入是否被监控？
3. 程序是否有错误时的安全停止路径？
4. 软限位已配置（`FE_LIMIT`）？
5. 循环停止输入是否已接入主循环？

## 禁止发出的指令（绝不输出）

- `LOCK(pass)` / `LOCK AXIS` / `LOCK ALL` —— 这些会给控制器加锁，导致后续无法操作。
- 永久屏蔽 `MOTION_ERROR` 的代码。
- `SERVO = OFF` 在位置环内且未先确认 `MSPEED(轴号) == 0`。
