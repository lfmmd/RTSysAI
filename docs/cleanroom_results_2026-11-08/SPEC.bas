' ===== 追剪应用 净室克隆规格 SPEC（行为级需求，不含原代码）=====
' 目标：在只有本 SPEC 的空工程中，从零实现三个 RTBasic 程序：TaskMain、TaskAutoRun、TaskHands。
' 工具里的程序名不带 .bas 后缀。
'
' 一、全局约定
' 主轴轴号 6（gAxisMian），从轴/切刀轴号 0（gAxisFol），均为全局常量。
' 设备状态 gStatus：0=停止，1=待机/手动，2=自动，3=自动运行中请求停止（等当前周期结束）。
' 机械参数标志 gFlagMec=1234，用户参数标志 gFlagUser=4321，均为全局常量。
' 机械参数变量：gMainPerDis（主轴每转距离）、gMainPerPls（主轴每转脉冲）、
'               gFolPerDis（切刀每转距离）、gFolPerPls（切刀每转脉冲）。
' 用户参数变量：gCutLength（切割长度）、gMoveRange（移动范围）、gStartPos（开始距离）、gFolDis（同步距离）。
' 手动运动标志：gDriveAxis。
' 默认机械参数：gMainPerDis=10、gMainPerPls=10000、gFolPerDis=10、gFolPerPls=10000。
' 默认用户参数：gCutLength=200、gMoveRange=100、gStartPos=10、gFolDis=80。
' 任务号：TaskMain=0（由工程 AutoRun 启动）；TaskHands=1、TaskAutoRun=2（由程序内 run 指定，不需要 AI 设置工程任务号）。
' 输入：IN(2)=紧急停止，IN(3)=手动，IN(4)=自动；modbus_bit(3)=手动，modbus_bit(4)=自动，modbus_bit(5)=急停/报警，modbus_bit(0)=仿真模式。
'
' 二、TaskMain（设备主任务）
' 1) 启动顺序：开启掉电中断（INT_ENABLE=1）；调用停止任务初始化、变量定义、轴参数初始化、存储数据载入、开机检查。
' 2) 主循环（无限循环）：
'    - 急停：若 IN(2)=ON 或 modbus_bit(5)=ON，调用停止任务初始化，gStatus=0。
'    - 切手动：否则若 (IN(3)=ON 或 modbus_bit(3)=ON) 且 gStatus<>1：
'        若 gStatus<>2：停止任务初始化，run 手动任务（TaskHands，任务号 1），gStatus=1；
'        否则若 gStatus<>3：gStatus=3（自动运行中等当前周期结束再切手动）。
'    - 切自动：否则若 (IN(4)=ON 或 modbus_bit(4)=ON) 且 gStatus<>2：
'        停止任务初始化，run 自动任务（TaskAutoRun，任务号 2），gStatus=2。
'    - 每轮刷新设备状态（MODBUS_IEEE(204)=主轴速度 MSPEED(6)；modbus_reg(214)=gStatus）。
'    - 每轮检查从轴报警：AXISSTATUS(0) 的 2^22 位置位则 modbus_bit(1000)=ON，否则 OFF。
' 3) 变量定义：声明上述全局变量/常量；gStatus 初始化为 0。
' 4) 存储数据载入：
'    - FLASH_READ 0 读取机械参数校验标志；若不等于 gFlagMec，写入默认机械参数并
'      FLASH_WRITE 0（带 gFlagMec 和四个机械参数）；否则从 FLASH_READ 0 读回四个参数。
'      随后同步到 MODBUS_IEEE(80/82/84/86)。
'    - FLASH_READ 1 读取用户参数校验标志；若不等于 gFlagUser，打印"配方参数初始化"，
'      写入默认用户参数并 FLASH_WRITE 1（带 gFlagUser 和四个用户参数）；否则读回。
'      随后同步到 MODBUS_IEEE(190/184/186/188)。
'    - 恢复掉电前状态：dpos(0)=vr(35)；modbus_long(200)=VR_INT(30)。
'    - 按钮复位：modbus_bit 0/3/4/5/30/40/50/60 清零；调用参数计算。
' 5) 轴参数初始化：
'    - 主轴：base(6)，ATYPE=3，SPEED=100。
'    - 从轴：base(0)，ATYPE=1，UNITS=1000，ACCEL=1000，DECEL=1000，SPEED=100，
'      CREEP=10，LSPEED=0，MERGE=1，FS_LIMIT=2000000，RS_LIMIT=-2000000，
'      DATUM_IN=-1，FWD_IN=-1，REV_IN=-1。
' 6) 参数计算：base(6) 时 UNITS=gMainPerPls/gMainPerDis；base(0) 时 UNITS=gFolPerPls/gFolPerDis。
' 7) 开机检查：若 AXISSTATUS(0) 的 2^22 位置位，modbus_bit(1000)=ON；
'    否则 modbus_bit(1000)=OFF、OP(12,ON) 伺服使能、modbus_bit(3)=ON 进入手动。
' 8) 停止任务初始化：modbus_bit 10/20/30/40/50/60 清零，modbus_bit 3/4=OFF；
'    STOPTASK 1、STOPTASK 2、STOPTASK 3；RAPIDSTOP(2)。
' 9) 掉电中断（全局 SUB ONPOWEROFF）：VR_INT(30)=MODBUS_LONG(200)；vr(35)=dpos(0)。
'
' 三、TaskAutoRun（自动追剪，任务号 2）
' - 启动打印"自动任务启动"，TRIGGER 触发示波器。
' - 若 modbus_bit(0)=ON（仿真）：ATYPE(6)=0、VMOVE(1) axis(6)；否则 ATYPE(6)=3。
' - 无限循环，每轮先计算：
'     加速距离 lFolAxisAccdis=(gMoveRange-gFolDis)/2；
'     主轴加速位移 lMainAxisAccdis=2*lFolAxisAccdis；
'     返回位移 lmainAxisBackDis=gCutLength-2*lMainAxisAccdis-gFolDis。
'   依次执行主轴 6 联动从轴 0 的 MOVESLINK：
'     正向加速 (lFolAxisAccdis, lMainAxisAccdis, 0, 1, gAxisMian)；
'     正向同步 (gFolDis, gFolDis, 1, 1, gAxisMian)；
'     正向减速 (lFolAxisAccdis, lMainAxisAccdis, 1, 0, gAxisMian)；
'     返回 (-1*gMoveRange, lmainAxisBackDis, 0, 0, gAxisMian)。
'   然后 wait LOADED(0)，modbus_long(200) 产量+1。
'   若 gStatus=3：run "TaskHands.bas",1；gStatus=1；modbus_bit(3)=OFF；结束任务。
'
' 四、TaskHands（手动，任务号 1）
' - 启动打印"手动任务启动"。
' - 无限循环：
'   Jog：modbus_bit(10)=ON -> base(0)、VMOVE(1)、gDriveAxis=TRUE；
'        modbus_bit(20)=ON -> base(0)、VMOVE(-1)、gDriveAxis=TRUE；
'        否则若 gDriveAxis=TRUE -> cancel(2) axis(0)、gDriveAxis=FALSE。
'   相对运动：modbus_bit(30)=ON -> base(0)、move(MODBUS_IEEE(212))、wait IDLE、bit30=OFF；
'             modbus_bit(40)=ON -> base(0)、move(-1*MODBUS_IEEE(212))、wait IDLE、bit40=OFF。
'   绝对运动：modbus_bit(50)=ON -> base(0)、MOVEABS(MODBUS_IEEE(212))、wait IDLE(0)、bit50=OFF。
'   回零：modbus_bit(60)=ON：
'        若 modbus_bit(0)=1（仿真）-> base(0)、moveabs(0)、wait IDLE、bit60=OFF；
'        否则 base(0)、AXIS_STOPREASON=0、DATUM(3)、wait IDLE；若 AXIS_STOPREASON<>0 打印"回零失败"；bit60=OFF。
'   保存用户参数：modbus_bit(8)=ON -> 从 MODBUS_IEEE(190/184/186/188) 读入四个用户参数，
'        FLASH_WRITE 1（带 gFlagUser 和四个参数），打印"用户参数写入成功"，bit8=OFF。
'   保存机械参数：modbus_bit(9)=ON -> 从 MODBUS_IEEE(80/82/84/86) 读入四个机械参数，
'        FLASH_WRITE 0（带 gFlagMec 和四个参数），打印"机械参数写入成功"，bit9=OFF。
'
' 五、约束
' - 所有命令先用 lookup_command 核实真实语法；不要使用参考库中不存在的命令。
' - 每个程序写完后必须调用 validate_basic，直到 ok=true。
' - 只创建 TaskMain、TaskAutoRun、TaskHands 三个程序；不要修改 SPEC，不要设置工程任务号。
