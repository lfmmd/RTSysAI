using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RTSys.PlugIn
{
    /// <summary>
    /// ZMotion DLL (zmotion.dll) P/Invoke 签名完整定义
    /// 基于 IDA 反编译导出函数表 (100+ ZMC_* 函数)
    /// 调用约定: __stdcall, 字符集: Ansi (GB2312)
    /// </summary>
    internal static class ZMotionAPI
    {
        private const string DllName = "zmotion.dll";
        private const CallingConvention Conv = CallingConvention.StdCall;
        private const CharSet Charset = CharSet.Ansi;

        // ========== 编码转换辅助 ==========
        internal static readonly Encoding Gb2312 = Encoding.GetEncoding("gb2312");

        internal static string ToGb2312(string utf8)
        {
            if (string.IsNullOrEmpty(utf8)) return "";
            byte[] gbBytes = Encoding.Convert(Encoding.UTF8, Gb2312, Encoding.UTF8.GetBytes(utf8));
            return Gb2312.GetString(gbBytes);
        }

        internal static string FromGb2312(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "";
            return Marshal.PtrToStringAnsi(ptr) ?? "";
        }

        internal static string FromGb2312(byte[] bytes, int length)
        {
            if (bytes == null || length <= 0) return "";
            return Gb2312.GetString(bytes, 0, length);
        }

        // ========== 连接管理 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Open")]
        public static extern int ZMC_Open(int type, string arg, out IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_OpenEth")]
        public static extern int ZMC_OpenEth(string ip, out IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_OpenCom")]
        public static extern int ZMC_OpenCom(int port, out IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_OpenPci")]
        public static extern int ZMC_OpenPci(int cardIndex, out IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Close")]
        public static extern int ZMC_Close(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_FastOpen")]
        public static extern int ZMC_FastOpen(int type, string arg, out IntPtr handle);

        // 真实签名（反汇编 ret 0xc）：(ipaddr, timeoutMs, handle)。原声明缺少 timeoutMs，
        // stdcall 下会少压 4 字节并被调用方回收，导致栈失衡。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SearchAndOpenEth")]
        private static extern int ZMC_SearchAndOpenEthNative(string ip, uint timeoutMs, out IntPtr handle);

        public static int ZMC_SearchAndOpenEth(string ip, out IntPtr handle)
        {
            return ZMC_SearchAndOpenEthNative(ip, 0, out handle);
        }

        // 真实签名（反汇编 ret 0x14）：(minComId, maxComId, comId*, timeoutMs, handle)。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SearchAndOpenCom")]
        public static extern int ZMC_SearchAndOpenCom(uint minComId, uint maxComId, out uint comId, uint timeoutMs, out IntPtr handle);

        // ========== 基础信息 ==========
        // 真实签名（反汇编 ret 4）：只有 1 个输出缓冲区参数。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetDllVersion")]
        public static extern int ZMC_GetDllVersion(StringBuilder version);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetSoftVersion")]
        public static extern int ZMC_GetSoftVersion(IntPtr handle, out float version, IntPtr build, out int buildNo);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetSysSpecification")]
        public static extern int ZMC_GetSysSpecification(IntPtr handle, IntPtr specStruct);

        // ZMC_GetState 的真实输出是 uint8（头文件 uint8* pstate）。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetState")]
        public static extern int ZMC_GetState(IntPtr handle, out byte state);

        // 真实签名（反汇编 ret 8）：(handle, int* error)。原声明只有 handle，
        // 每次调用残留 4 字节栈，是堆/栈损坏来源之一。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetError")]
        public static extern int ZMC_GetError(IntPtr handle, out int error);

        // 真实签名（反汇编 ret）：无参数，直接返回错误描述字符串指针。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetErrcodeDescription")]
        public static extern IntPtr ZMC_GetErrcodeDescription();

        // ========== 超时设置 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetTimeOut")]
        public static extern int ZMC_SetTimeOut(IntPtr handle, int ms);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetTimeOut")]
        public static extern int ZMC_GetTimeOut(IntPtr handle, out int ms);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetTimeOutAutoClose")]
        public static extern int ZMC_SetTimeOutAutoClose(IntPtr handle, int enable);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetTimeOutAutoClose")]
        public static extern int ZMC_GetTimeOutAutoClose(IntPtr handle, out int enable);

        // ========== 程序管理 (ZAR/ZPJ/3File) ==========
        // 真实签名（反汇编 ret 0x10 + 头文件）：MakeZar 不接收 handle。
        // (pZpjFile, pZarFile, pPass, uid)
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_MakeZar")]
        public static extern int ZMC_MakeZar(string zpjFile, string zarFile, string pass, uint uid);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_MakeZar2")]
        public static extern int ZMC_MakeZar2(IntPtr pzpj, string basDir, string zarFile, string pass, uint uid);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DownZar")]
        public static extern int ZMC_DownZar(IntPtr handle, string fileName, int mode);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DownMemZar")]
        public static extern int ZMC_DownMemZar(IntPtr handle, IntPtr buffer, int size, int mode);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_RunZarFile")]
        public static extern int ZMC_RunZarFile(IntPtr handle, string programName);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Stop")]
        public static extern int ZMC_Stop(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Pause")]
        public static extern int ZMC_Pause(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Resume")]
        public static extern int ZMC_Resume(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CheckProgramSyntax")]
        public static extern int ZMC_CheckProgramSyntax(IntPtr handle, string sourceCode);

        // 3File 系列 (控制器文件系统)
        // 真实签名（IDA 反汇编确认）：第三参是 int* size（输出），不是缓冲区长度。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_FindFirst3File")]
        public static extern int ZMC_FindFirst3File(IntPtr handle, StringBuilder fileName, out int size);

        // 真实签名（反汇编 ret 0xc + 头文件）：(handle, fileName, int* fileSize)。
        // 原声明的 4 个参数会导致 stdcall 栈失衡。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_FindNext3File")]
        public static extern int ZMC_FindNext3File(IntPtr handle, StringBuilder fileName, out int fileSize);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetCur3File")]
        public static extern int ZMC_GetCur3File(IntPtr handle, StringBuilder fileName, out int fileSize);

        // 反汇编 ret 0xc：真实参数个数为 3，但参数语义（无缓冲区长度）无法从二进制确认。
        // 包装层 ReadProgramAsync 已改为 fail-safe，不再按猜测 ABI 调用本函数。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Up3File")]
        public static extern int ZMC_Up3File(IntPtr handle, string fileName, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Up3FileToMem")]
        public static extern int ZMC_Up3FileToMem(IntPtr handle, string fileName, out IntPtr buffer, out int size);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Down3File")]
        public static extern int ZMC_Down3File(IntPtr handle, string fileName, IntPtr buffer, int size);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Delete3File")]
        public static extern int ZMC_Delete3File(IntPtr handle, string fileName);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_RemoveAll3Files")]
        public static extern int ZMC_RemoveAll3Files(IntPtr handle);

        // 真实签名（反汇编 ret 0x10 + 头文件）：(handle, fileName, byte* ifExist, int* fileSize)。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Check3File")]
        public static extern int ZMC_Check3File(IntPtr handle, string fileName, out byte ifExist, out int fileSize);

        // ========== 直接命令执行 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DirectCommand")]
        public static extern int ZMC_DirectCommand(IntPtr handle, string command, int timeout, uint bufferSize);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Execute")]
        public static extern int ZMC_Execute(IntPtr handle, string command, int timeout, byte[] response, uint responseSize);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ExecuteNoAck")]
        public static extern int ZMC_ExecuteNoAck(IntPtr handle, string command, int timeout);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ExecuteWaitDown")]
        public static extern int ZMC_ExecuteWaitDown(IntPtr handle, int timeout);

        // 真实签名（反汇编 ret 0x14 + 头文件）：(handle, buffer, max, read*, ifDown*)。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ExecuteGetReceive")]
        public static extern int ZMC_ExecuteGetReceive(IntPtr handle, byte[] buffer, uint maxLen, out uint readLen, out byte ifDown);

        // 真实签名（反汇编 ret + 头文件）：只有 handle，剩余空间通过返回值给出。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ExecuteGetRemainBuffSpace")]
        public static extern int ZMC_ExecuteGetRemainBuffSpace(IntPtr handle);

        // ========== VR / TABLE / 变量数组读写 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ArrayReadf")]
        public static extern int ZMC_ArrayReadf(IntPtr handle, string name, int address, int count, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ArrayWritef")]
        public static extern int ZMC_ArrayWritef(IntPtr handle, string name, int address, int count, IntPtr buffer);

        // ========== 寄存器读写 (通用) ==========
        // 真实签名（反汇编 ret 0x18 + 头文件）：第二参是寄存器名字符串，不是 int 类型。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_RegisterRead")]
        public static extern int ZMC_RegisterRead(IntPtr handle, string regName, byte size, int address, ushort count, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_RegisterWrite")]
        public static extern int ZMC_RegisterWrite(IntPtr handle, string regName, byte size, int address, ushort count, IntPtr buffer);

        // ========== IO 读写 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetIn")]
        public static extern int ZMC_GetIn(IntPtr handle, short bit, out byte value);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetOutput")]
        public static extern int ZMC_SetOutput(IntPtr handle, short bit, byte value);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetOutput")]
        public static extern int ZMC_GetOutput(IntPtr handle, short bit, out byte value);

        // 真实签名（反汇编 ret 0x10 + 头文件）：
        //   GetInAll/GetOutAll: (handle, first, end, uint32* state)
        //   SetOutAll:          (handle, first, end, uint32 state)
        // DLL 只写/读一个 dword，原 256 字节缓冲区声明与实际 ABI 完全不符。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetInAll")]
        public static extern int ZMC_GetInAll(IntPtr handle, ushort first, ushort end, out uint state);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetOutAll")]
        public static extern int ZMC_GetOutAll(IntPtr handle, ushort first, ushort end, out uint state);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetOutAll")]
        public static extern int ZMC_SetOutAll(IntPtr handle, ushort first, ushort end, uint state);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetAIn")]
        public static extern int ZMC_GetAIn(IntPtr handle, short channel, out float value);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetDaOut")]
        public static extern int ZMC_SetDaOut(IntPtr handle, short channel, float value);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetDaOut")]
        public static extern int ZMC_GetDaOut(IntPtr handle, short channel, out float value);

        // ========== 轴状态 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetAxises")]
        public static extern byte ZMC_GetAxises(IntPtr handle);

        // 真实签名（反汇编 ret 0xc + 头文件）：(handle, uint8 axis, struct_AxisStates* out)。
        // 调用方必须提供至少 17 字节的输出缓冲区；缺少第三参时 DLL 会把栈上的垃圾当作
        // 输出指针写入，这正是 2 参数调用仍然 AccessViolation 的原因。
        // 该导出当前被包装层禁用（见 ZMotionExtensions.GetAxisStateAsync）。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetAxisStates")]
        public static extern int ZMC_GetAxisStates(IntPtr handle, byte axis, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetAxisFeatures")]
        public static extern int ZMC_GetAxisFeatures(IntPtr handle, byte axis, out int features);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetAxisEnable")]
        public static extern int ZMC_SetAxisEnable(IntPtr handle, byte axis, byte enable);

        // ========== 任务/进程状态 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetTaskState")]
        public static extern int ZMC_GetTaskState(IntPtr handle, int taskId, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetTaskStackState")]
        public static extern int ZMC_GetTaskStackState(IntPtr handle, int taskId, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetFileState")]
        public static extern int ZMC_GetFileState(IntPtr handle, string fileName, IntPtr buffer);

        // ========== Modbus ==========
        // 真实签名（反汇编 ret 0x10 + 头文件）：(handle, uint16 start, uint16 count, data*)。
        // 该 DLL 的 Modbus 导出没有 slave 参数；原 5 参数声明会破坏栈。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Modbus_Get0x")]
        public static extern uint ZMC_Modbus_Get0x(IntPtr handle, ushort start, ushort count, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Modbus_Set0x")]
        public static extern uint ZMC_Modbus_Set0x(IntPtr handle, ushort start, ushort count, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Modbus_Get4x")]
        public static extern uint ZMC_Modbus_Get4x(IntPtr handle, ushort start, ushort count, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Modbus_Set4x")]
        public static extern uint ZMC_Modbus_Set4x(IntPtr handle, ushort start, ushort count, IntPtr buffer);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Modbus_GetLong")]
        public static extern int ZMC_Modbus_GetLong(IntPtr handle, int slave, int address, out int value);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Modbus_SetLong")]
        public static extern int ZMC_Modbus_SetLong(IntPtr handle, int slave, int address, int value);

        // ========== EtherCAT SDO ==========
        // 反汇编 ret 0x14：发送侧真实为 5 个参数，第 4 参是 1..2048 的数据长度，第 5 参是数据指针；
        // 不包含 index/subIndex/dataType。高层 SDO 语义无法从该导出确认，包装层已 fail-safe。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_EcatSdo_Send")]
        public static extern int ZMC_EcatSdo_Send(IntPtr handle, ushort slot, ushort position, uint dataLength, IntPtr data);

        // 反汇编 ret 0x18：真实为 6 个参数，参数语义未确认；包装层已 fail-safe，不直接调用。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_EcatSdo_Recv")]
        public static extern int ZMC_EcatSdo_Recv(IntPtr handle, ushort slot, IntPtr outInfo, uint bufferLength, IntPtr buffer, IntPtr outLength);

        // ========== 驱动器参数 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DrivePara_GetInfo")]
        public static extern int ZMC_DrivePara_GetInfo(IntPtr handle, int axis, int address, IntPtr info);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DrivePara_GetExistList")]
        public static extern int ZMC_DrivePara_GetExistList(IntPtr handle, int axis, IntPtr list, int maxCount);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DrivePara_GetGroupInfo")]
        public static extern int ZMC_DrivePara_GetGroupInfo(IntPtr handle, int axis, int group, IntPtr info);

        // ========== 断点调试 ==========
        // 真实签名（反汇编 ret 0x10 + 头文件）：(handle, fileName, line, uint8 pauseAll)。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_BreakAdd")]
        public static extern int ZMC_BreakAdd(IntPtr handle, string fileName, uint line, byte pauseAll);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_BreakDel")]
        public static extern int ZMC_BreakDel(IntPtr handle, string fileName, uint line);

        // 真实签名（反汇编 ret 4 + 头文件）：(handle)。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_BreakClear")]
        public static extern int ZMC_BreakClear(IntPtr handle);

        // ========== 高速采样 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CycleUpEnable")]
        public static extern int ZMC_CycleUpEnable(IntPtr handle, int axis, float interval, string config);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CycleUpDisable")]
        public static extern int ZMC_CycleUpDisable(IntPtr handle, int axis);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CycleUpForceOnce")]
        public static extern int ZMC_CycleUpForceOnce(IntPtr handle, int axis);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CycleUpReadBuff")]
        public static extern int ZMC_CycleUpReadBuff(IntPtr handle, int axis, IntPtr buffer, int maxCount, out int actualCount);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CycleUpReadBuffInt")]
        public static extern int ZMC_CycleUpReadBuffInt(IntPtr handle, int axis, IntPtr buffer, int maxCount, out int actualCount);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CycleUpGetRecvTimes")]
        public static extern int ZMC_CycleUpGetRecvTimes(IntPtr handle, int cycleIndex);

        // ========== 固件升级 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetFirmWareInfo")]
        public static extern int ZMC_GetFirmWareInfo(IntPtr handle, IntPtr info);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CheckFirmWare")]
        public static extern int ZMC_CheckFirmWare(IntPtr handle, string fileName);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DownFirmWare")]
        public static extern int ZMC_DownFirmWare(IntPtr handle, string fileName);

        // ========== 控制器配置 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ResetController")]
        public static extern int ZMC_ResetController(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ReturnDefault")]
        public static extern int ZMC_ReturnDefault(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_RomZarDefault")]
        public static extern int ZMC_RomZarDefault(IntPtr handle);

        // 真实签名（反汇编 ret 0x10 + 头文件）：(baudRate, byteSize, parity, stopBits)。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetComDefaultBaud")]
        public static extern int ZMC_SetComDefaultBaud(uint baudRate, uint byteSize, uint parity, uint stopBits);

        // 真实签名（反汇编 ret 4）：(uint8 id)。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetComDefaultID")]
        public static extern int ZMC_SetComDefaultID(byte id);

        // 真实签名（反汇编 ret 4）：(int32 secureId)，不是 handle。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetConnectSecureID")]
        public static extern int ZMC_SetConnectSecureID(int secureId);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetComCurID")]
        public static extern int ZMC_SetComCurID(IntPtr handle, int id);

        // 真实签名（反汇编 ret）：无参数，数量由返回值给出。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetMaxPciCards")]
        public static extern int ZMC_GetMaxPciCards();

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetMaxLocalRTes")]
        public static extern int ZMC_GetMaxLocalRTes();

        // ========== 网络/通讯 ==========
        // 真实签名（反汇编 ret 8 + 头文件）：(handle, char* ip)，无长度参数。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetCurIpAddr")]
        public static extern int ZMC_GetCurIpAddr(IntPtr handle, StringBuilder ip);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetClientId")]
        public static extern int ZMC_GetClientId(IntPtr handle, out int id);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetSoftId")]
        public static extern int ZMC_GetSoftId(IntPtr handle, out int id);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetHardId")]
        public static extern int ZMC_GetHardId(IntPtr handle, out int id);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetChipId")]
        public static extern int ZMC_GetChipId(IntPtr handle, out int id);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetControllerIDDate")]
        public static extern int ZMC_GetControllerIDDate(IntPtr handle, StringBuilder id, StringBuilder date, int maxLen);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetControllerIDDate")]
        public static extern int ZMC_SetControllerIDDate(IntPtr handle, string id, string date);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetCustomID")]
        public static extern int ZMC_SetCustomID(IntPtr handle, string id);

        // ========== 文件系统 (通用) ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_FindFirstFile")]
        public static extern int ZMC_FindFirstFile(IntPtr handle, StringBuilder fileName, int maxLen);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_FindNextFile")]
        public static extern int ZMC_FindNextFile(IntPtr handle, StringBuilder fileName, int maxLen);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_UpFile")]
        public static extern int ZMC_UpFile(IntPtr handle, string fileName, StringBuilder buffer, int maxLen);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_UpFileToMem")]
        public static extern int ZMC_UpFileToMem(IntPtr handle, string fileName, out IntPtr buffer, out int size);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DownFile")]
        public static extern int ZMC_DownFile(IntPtr handle, string fileName, IntPtr buffer, int size);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_DeleteFile")]
        public static extern int ZMC_DeleteFile(IntPtr handle, string fileName);

        // ========== 日志 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_UpLog")]
        public static extern int ZMC_UpLog(IntPtr handle, StringBuilder buffer, int maxLen);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_UpLogToMem")]
        public static extern int ZMC_UpLogToMem(IntPtr handle, out IntPtr buffer, out int size);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ClearLog")]
        public static extern int ZMC_ClearLog(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_WriteLog")]
        public static extern int ZMC_WriteLog(string format, params object[] args);

        // ========== MAC 地址 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetMac")]
        public static extern int ZMC_SetMac(IntPtr handle, byte[] mac);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetMac")]
        public static extern int ZMC_GetMac(IntPtr handle, byte[] mac);

        // ========== 回调/异步 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SetAutoUpCallBack")]
        public static extern int ZMC_SetAutoUpCallBack(IntPtr handle, IntPtr callback);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CallBack_Setup")]
        public static extern int ZMC_CallBack_Setup(IntPtr handle, IntPtr callback);

        // ========== HMI / 显示 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ELcdOpen")]
        public static extern int ZMC_ELcdOpen(IntPtr handle, int type, string param);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ELcdClose")]
        public static extern int ZMC_ELcdClose(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ELcdDataGet")]
        public static extern int ZMC_ELcdDataGet(IntPtr handle, IntPtr buffer, int size);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ELcdDataCheck")]
        public static extern int ZMC_ELcdDataCheck(IntPtr handle, IntPtr buffer, int size);

        // ========== 其他工具函数 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CalStringHash")]
        public static extern int ZMC_CalStringHash(string str);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetStringType")]
        public static extern int ZMC_GetStringType(IntPtr handle, string str, out int type);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_StepRun")]
        public static extern int ZMC_StepRun(IntPtr handle, string fileName, int line);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_CancelOnline")]
        public static extern int ZMC_CancelOnline(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_ReadMessage")]
        public static extern int ZMC_ReadMessage(IntPtr handle, IntPtr buffer, int size);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_MultiLineN")]
        public static extern int ZMC_MultiLineN(IntPtr handle, string command, int timeout);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GlobalVarGet")]
        public static extern int ZMC_GlobalVarGet(IntPtr handle, string name, out float value);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GlobalArrayGet")]
        public static extern int ZMC_GlobalArrayGet(IntPtr handle, string name, int index, out float value);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_LowFormatNand")]
        public static extern int ZMC_LowFormatNand(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_TempModBaud")]
        public static extern int ZMC_TempModBaud(IntPtr handle, int baud);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_JumpApp")]
        public static extern int ZMC_JumpApp(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_JumpZbios")]
        public static extern int ZMC_JumpZbios(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_TestSoftware")]
        public static extern int ZMC_TestSoftware(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_StartHardwareTest")]
        public static extern int ZMC_StartHardwareTest(IntPtr handle);

        // ========== 信号/事件 ==========
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Signal_BasicP")]
        public static extern int ZMC_Signal_BasicP(IntPtr handle);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_Signal_BasicV")]
        public static extern int ZMC_Signal_BasicV(IntPtr handle);

        // ========== 设备枚举 ==========
        // 真实签名（反汇编 ret 0xc）：(ipList, maxLen, timeoutMs)。原声明只有 2 个参数，
        // 调用后栈残留 4 字节；这里保留原来的 2 参数调用入口，内部补 timeoutMs=0。
        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_SearchEth")]
        private static extern int ZMC_SearchEthNative(StringBuilder ipList, uint maxLen, uint timeoutMs);

        public static int ZMC_SearchEth(StringBuilder ipList, int maxLen)
        {
            return ZMC_SearchEthNative(ipList, (uint)maxLen, 0);
        }

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_EnumChildCard")]
        public static extern int ZMC_EnumChildCard(IntPtr handle, IntPtr buffer, int maxCount);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_EnumLocal")]
        public static extern int ZMC_EnumLocal(IntPtr handle, IntPtr buffer, int maxCount);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_LocalRTManage")]
        public static extern int ZMC_LocalRTManage(IntPtr handle, int cmd, IntPtr param);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetLocalRTState")]
        public static extern int ZMC_GetLocalRTState(IntPtr handle, out int state);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetConnectType")]
        public static extern int ZMC_GetConnectType(IntPtr handle, out int type);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetConnectString")]
        public static extern int ZMC_GetConnectString(IntPtr handle, StringBuilder str, int maxLen);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetProgress")]
        public static extern int ZMC_GetProgress(IntPtr handle, out int progress);

        [DllImport(DllName, CallingConvention = Conv, CharSet = Charset, EntryPoint = "ZMC_GetStopPauseTaskid")]
        public static extern int ZMC_GetStopPauseTaskid(IntPtr handle, out int taskId);
    }
}