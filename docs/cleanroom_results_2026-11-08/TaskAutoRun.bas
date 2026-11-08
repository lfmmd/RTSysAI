' ================================================================
' TaskAutoRun -- 自动追剪任务（任务号 2）
' 功能：主轴带动从轴（切刀轴）做"加速-同步-减速-返回"四段追剪循环
' 依赖：TaskMain 中定义的全局量（gStatus / gCutLength / gMoveRange /
'       gFolDis / gAxisMian / gAxisFol 等）
' ================================================================

PRINT "自动任务启动"
TRIGGER                                 ' 触发示波器数据采样

' ---------------- 仿真 / 实机模式判断 ----------------
IF MODBUS_BIT(0) = ON THEN
    ' 仿真模式：主轴作为虚轴连续运动，模拟生产线
    ATYPE(gAxisMian) = 0
    BASE(gAxisMian)
    VMOVE(1)                            ' 主轴连续正向运动
ELSE
    ' 实机模式：主轴为编码器跟随轴
    ATYPE(gAxisMian) = 3
ENDIF

' ---------------- 追剪主循环 ----------------
WHILE 1
    ' ---- 每轮先计算各段距离 ----
    lFolAxisAccdis = (gMoveRange - gFolDis) / 2                     ' 从轴加速距离
    lMainAxisAccdis = 2 * lFolAxisAccdis                            ' 主轴加速位移
    lmainAxisBackDis = gCutLength - 2 * lMainAxisAccdis - gFolDis   ' 返回位移

    ' ---- 四段 MOVESLINK：主轴（连接轴）联动从轴（BASE 轴）----
    BASE(gAxisFol)
    ' 1) 正向加速：从轴速度 0 -> 主轴速度
    MOVESLINK(lFolAxisAccdis, lMainAxisAccdis, 0, 1, gAxisMian)
    ' 2) 正向同步：1 倍速同步跟料，此段执行切剪
    MOVESLINK(gFolDis, gFolDis, 1, 1, gAxisMian)
    ' 3) 正向减速：从轴速度 1 -> 0
    MOVESLINK(lFolAxisAccdis, lMainAxisAccdis, 1, 0, gAxisMian)
    ' 4) 返回：从轴反向回到起始位，主轴继续走返回位移
    MOVESLINK(-1 * gMoveRange, lmainAxisBackDis, 0, 0, gAxisMian)

    WAIT LOADED(0)                      ' 等待从轴运动缓冲区执行完毕
    MODBUS_LONG(200) = MODBUS_LONG(200) + 1     ' 产量计数 +1

    ' ---- 自动运行中请求停止：等本周期结束再切手动 ----
    IF gStatus = 3 THEN
        RUN "TaskHands.bas", 1
        gStatus = 1
        MODBUS_BIT(3) = OFF
        END                             ' 结束本任务
    ENDIF
WEND
END
