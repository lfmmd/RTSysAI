' ================================================================
' TaskMain -- 设备主任务（任务号 0，由工程 AutoRun 启动）
' 功能：全局量定义 / 掉电中断 / 主循环状态机 /
'       轴参数初始化 / FLASH 存储数据载入 / 开机检查
' ================================================================

' ---------------- 全局常量 ----------------
GLOBAL CONST gAxisMian = 6          ' 主轴轴号
GLOBAL CONST gAxisFol = 0           ' 从轴（切刀轴）轴号
GLOBAL CONST gFlagMec = 1234        ' 机械参数校验标志
GLOBAL CONST gFlagUser = 4321       ' 用户参数校验标志

' ---------------- 全局变量 ----------------
GLOBAL gStatus                      ' 设备状态：0停止 1手动 2自动 3自动运行中请求停止
GLOBAL gMainPerDis                  ' 主轴每转距离
GLOBAL gMainPerPls                  ' 主轴每转脉冲
GLOBAL gFolPerDis                   ' 切刀每转距离
GLOBAL gFolPerPls                   ' 切刀每转脉冲
GLOBAL gCutLength                   ' 切割长度
GLOBAL gMoveRange                   ' 移动范围
GLOBAL gStartPos                    ' 开始距离
GLOBAL gFolDis                      ' 同步距离
GLOBAL gDriveAxis                   ' 手动连续运动标志

' ================================================================
' 主程序
' ================================================================
INT_ENABLE = 1                      ' 开启中断总开关（掉电中断生效）
GOSUB InitStop                      ' 停止任务初始化
GOSUB InitVar                       ' 变量定义
GOSUB InitAxis                      ' 轴参数初始化
GOSUB LoadStore                     ' 存储数据载入
GOSUB PowerOnCheck                  ' 开机检查

WHILE 1
    ' ---------- 急停处理 ----------
    IF IN(2) = ON OR MODBUS_BIT(5) = ON THEN
        GOSUB InitStop
        gStatus = 0
    ' ---------- 切换到手动 ----------
    ELSEIF (IN(3) = ON OR MODBUS_BIT(3) = ON) AND gStatus <> 1 THEN
        IF gStatus <> 2 THEN
            ' 当前不在自动运行：直接切手动
            GOSUB InitStop
            RUN "TaskHands.bas", 1
            gStatus = 1
        ELSEIF gStatus <> 3 THEN
            ' 正在自动运行：请求停止，等当前周期结束后由自动任务切手动
            gStatus = 3
        ENDIF
    ' ---------- 切换到自动 ----------
    ELSEIF (IN(4) = ON OR MODBUS_BIT(4) = ON) AND gStatus <> 2 THEN
        GOSUB InitStop
        RUN "TaskAutoRun.bas", 2
        gStatus = 2
    ENDIF

    ' ---------- 每轮刷新设备状态 ----------
    MODBUS_IEEE(204) = MSPEED(gAxisMian)    ' 主轴速度
    MODBUS_REG(214) = gStatus               ' 设备状态

    ' ---------- 每轮检查从轴报警（AXISSTATUS 的 2^22 位）----------
    IF AXISSTATUS(gAxisFol) AND 4194304 THEN
        MODBUS_BIT(1000) = ON
    ELSE
        MODBUS_BIT(1000) = OFF
    ENDIF
WEND
END

' ================================================================
' 变量定义（运行时初始化部分）
' ================================================================
SUB InitVar()
    gStatus = 0                     ' 设备状态初始化为停止
END SUB

' ================================================================
' 停止任务初始化
' ================================================================
SUB InitStop()
    MODBUS_BIT(10) = OFF            ' 清手动按钮
    MODBUS_BIT(20) = OFF
    MODBUS_BIT(30) = OFF
    MODBUS_BIT(40) = OFF
    MODBUS_BIT(50) = OFF
    MODBUS_BIT(60) = OFF
    MODBUS_BIT(3) = OFF             ' 清手动/自动选择按钮
    MODBUS_BIT(4) = OFF
    STOPTASK 1                      ' 停止手动任务
    STOPTASK 2                      ' 停止自动任务
    STOPTASK 3                      ' 停止备用任务
    RAPIDSTOP(2)                    ' 所有轴按最大减速度立即停止
END SUB

' ================================================================
' 轴参数初始化
' ================================================================
SUB InitAxis()
    ' ---- 主轴 ----
    BASE(gAxisMian)
    ATYPE = 3                       ' 主轴轴类型（虚轴/编码器跟随）
    SPEED = 100

    ' ---- 从轴（切刀轴）----
    BASE(gAxisFol)
    ATYPE = 1                       ' 脉冲轴
    UNITS = 1000                    ' 脉冲当量
    ACCEL = 1000
    DECEL = 1000
    SPEED = 100
    CREEP = 10                      ' 回零爬行速度
    LSPEED = 0
    MERGE = ON                      ' 连续插补不减速
    FS_LIMIT = 2000000              ' 正向软限位
    RS_LIMIT = -2000000             ' 负向软限位
    DATUM_IN = -1                   ' 原点信号无效
    FWD_IN = -1                     ' 正向限位无效
    REV_IN = -1                     ' 负向限位无效
END SUB

' ================================================================
' 参数计算：按每转距离/每转脉冲换算脉冲当量
' ================================================================
SUB CalcParam()
    BASE(gAxisMian)
    UNITS = gMainPerPls / gMainPerDis
    BASE(gAxisFol)
    UNITS = gFolPerPls / gFolPerDis
END SUB

' ================================================================
' 存储数据载入（FLASH 掉电保存参数）
' ================================================================
SUB LoadStore()
    LOCAL chkFlag                   ' 读回的校验标志
    LOCAL saveFlag                  ' 写入 FLASH 的校验标志（常量不可直接写入）

    ' ---------- 机械参数（FLASH 块 0）----------
    FLASH_READ 0, chkFlag
    IF chkFlag <> gFlagMec THEN
        ' 校验失败：写入默认机械参数
        gMainPerDis = 10
        gMainPerPls = 10000
        gFolPerDis = 10
        gFolPerPls = 10000
        saveFlag = gFlagMec
        FLASH_WRITE 0, saveFlag, gMainPerDis, gMainPerPls, gFolPerDis, gFolPerPls
    ELSE
        ' 校验通过：读回机械参数
        FLASH_READ 0, chkFlag, gMainPerDis, gMainPerPls, gFolPerDis, gFolPerPls
    ENDIF
    MODBUS_IEEE(80) = gMainPerDis
    MODBUS_IEEE(82) = gMainPerPls
    MODBUS_IEEE(84) = gFolPerDis
    MODBUS_IEEE(86) = gFolPerPls

    ' ---------- 用户参数（FLASH 块 1）----------
    FLASH_READ 1, chkFlag
    IF chkFlag <> gFlagUser THEN
        PRINT "配方参数初始化"
        gCutLength = 200
        gMoveRange = 100
        gStartPos = 10
        gFolDis = 80
        saveFlag = gFlagUser
        FLASH_WRITE 1, saveFlag, gCutLength, gMoveRange, gStartPos, gFolDis
    ELSE
        FLASH_READ 1, chkFlag, gCutLength, gMoveRange, gStartPos, gFolDis
    ENDIF
    MODBUS_IEEE(190) = gCutLength
    MODBUS_IEEE(184) = gMoveRange
    MODBUS_IEEE(186) = gStartPos
    MODBUS_IEEE(188) = gFolDis

    ' ---------- 恢复掉电前状态 ----------
    BASE(gAxisFol)
    DPOS = VR(35)                   ' 恢复从轴掉电前位置
    MODBUS_LONG(200) = VR_INT(30)   ' 恢复产量计数

    ' ---------- 按钮复位 + 参数计算 ----------
    MODBUS_BIT(0) = OFF             ' 仿真模式
    MODBUS_BIT(3) = OFF             ' 手动
    MODBUS_BIT(4) = OFF             ' 自动
    MODBUS_BIT(5) = OFF             ' 急停/报警
    MODBUS_BIT(30) = OFF
    MODBUS_BIT(40) = OFF
    MODBUS_BIT(50) = OFF
    MODBUS_BIT(60) = OFF
    GOSUB CalcParam
END SUB

' ================================================================
' 开机检查
' ================================================================
SUB PowerOnCheck()
    IF AXISSTATUS(gAxisFol) AND 4194304 THEN
        ' 从轴有报警：置报警位，不使能
        MODBUS_BIT(1000) = ON
    ELSE
        MODBUS_BIT(1000) = OFF
        OP(12, ON)                  ' 伺服使能输出
        MODBUS_BIT(3) = ON          ' 进入手动
    ENDIF
END SUB

' ================================================================
' 掉电中断（必须是全局 SUB 过程），只做少量掉电保存动作
' ================================================================
GLOBAL SUB ONPOWEROFF()
    VR_INT(30) = MODBUS_LONG(200)   ' 保存产量计数
    VR(35) = DPOS(gAxisFol)         ' 保存从轴当前位置
END SUB
