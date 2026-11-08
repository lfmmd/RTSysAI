' ================================================================
' TaskHands -- 手动任务（任务号 1）
' 功能：Jog 点动 / 相对运动 / 绝对运动 / 回零 / 保存用户与机械参数
' 依赖：TaskMain 中定义的全局量（gCutLength / gMoveRange / gStartPos /
'       gFolDis / gFlagUser / gFlagMec / gAxisFol / gDriveAxis 等）
' ================================================================

PRINT "手动任务启动"

WHILE 1
    ' ---------------- Jog 点动 ----------------
    IF MODBUS_BIT(10) = ON THEN
        BASE(gAxisFol)
        VMOVE(1)                        ' 正向连续运动
        gDriveAxis = ON
    ELSEIF MODBUS_BIT(20) = ON THEN
        BASE(gAxisFol)
        VMOVE(-1)                       ' 反向连续运动
        gDriveAxis = ON
    ELSEIF gDriveAxis = ON THEN
        ' 松手：减速停止点动（CANCEL 后需 WAIT IDLE 才能执行绝对运动）
        BASE(gAxisFol)
        CANCEL(2)
        WAIT IDLE
        gDriveAxis = OFF
    ENDIF

    ' ---------------- 相对运动 ----------------
    IF MODBUS_BIT(30) = ON THEN
        BASE(gAxisFol)
        MOVE(MODBUS_IEEE(212))          ' 相对正向走设定距离
        WAIT IDLE
        MODBUS_BIT(30) = OFF
    ENDIF

    IF MODBUS_BIT(40) = ON THEN
        BASE(gAxisFol)
        MOVE(-1 * MODBUS_IEEE(212))     ' 相对反向走设定距离
        WAIT IDLE
        MODBUS_BIT(40) = OFF
    ENDIF

    ' ---------------- 绝对运动 ----------------
    IF MODBUS_BIT(50) = ON THEN
        BASE(gAxisFol)
        MOVEABS(MODBUS_IEEE(212))       ' 绝对运动到设定位置
        WAIT IDLE
        MODBUS_BIT(50) = OFF
    ENDIF

    ' ---------------- 回零 ----------------
    IF MODBUS_BIT(60) = ON THEN
        IF MODBUS_BIT(0) = ON THEN
            ' 仿真模式：无原点信号，直接回到坐标 0
            BASE(gAxisFol)
            MOVEABS(0)
            WAIT IDLE
            MODBUS_BIT(60) = OFF
        ELSE
            ' 实机：DATUM 模式 3 找原点
            BASE(gAxisFol)
            AXIS_STOPREASON = 0
            DATUM(3)
            WAIT IDLE
            IF AXIS_STOPREASON <> 0 THEN
                PRINT "回零失败"
            ENDIF
            MODBUS_BIT(60) = OFF
        ENDIF
    ENDIF

    ' ---------------- 保存用户参数到 FLASH ----------------
    IF MODBUS_BIT(8) = ON THEN
        gCutLength = MODBUS_IEEE(190)
        gMoveRange = MODBUS_IEEE(184)
        gStartPos = MODBUS_IEEE(186)
        gFolDis = MODBUS_IEEE(188)
        usrSaveFlag = gFlagUser          ' 校验标志（常量需转成变量才能存）
        FLASH_WRITE 1, usrSaveFlag, gCutLength, gMoveRange, gStartPos, gFolDis
        PRINT "用户参数写入成功"
        MODBUS_BIT(8) = OFF
    ENDIF

    ' ---------------- 保存机械参数到 FLASH ----------------
    IF MODBUS_BIT(9) = ON THEN
        gMainPerDis = MODBUS_IEEE(80)
        gMainPerPls = MODBUS_IEEE(82)
        gFolPerDis = MODBUS_IEEE(84)
        gFolPerPls = MODBUS_IEEE(86)
        usrSaveFlag = gFlagMec
        FLASH_WRITE 0, usrSaveFlag, gMainPerDis, gMainPerPls, gFolPerDis, gFolPerPls
        PRINT "机械参数写入成功"
        MODBUS_BIT(9) = OFF
    ENDIF
WEND
END
