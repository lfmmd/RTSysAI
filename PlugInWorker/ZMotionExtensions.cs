// ============================================================================
// ZMotionExtensions.cs —— ZMotion 高级异步封装：提供类型安全、自动编码转换、异步包装的 API
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RTSys.PlugIn
{
    /// <summary>
    /// ZMotion 高级异步封装：提供类型安全、自动编码转换、异步包装的 API
    /// </summary>
    public static class ZMotionExtensions
    {
        // ========== 结构体定义 ==========

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct SysSpecification
        {
            // 该 DLL 只保证返回一个 dword；其余字段在该版本上未经二进制验证，保持为 0。
            public int RawValue;
            public byte axisCount;           // 轴数
            public byte ioInCount;           // 输入点数
            public byte ioOutCount;          // 输出点数
            public byte aiCount;             // 模拟输入通道数
            public byte aoCount;             // 模拟输出通道数
            public ushort vrSize;            // VR 变量总数
            public ushort tableSize;         // TABLE 总数
            public uint programMemory;       // 程序存储空间 (bytes)
            public uint dataMemory;          // 数据存储空间 (bytes)
            public ushort maxTasks;          // 最大任务数
            public ushort maxPrograms;       // 最大程序数
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct AxisState
        {
            public byte axis;                // 轴号
            public byte status;              // 状态位图
            public int position;             // 当前位置 (pulse)
            public int targetPos;            // 目标位置
            public int velocity;             // 当前速度
            public int error;                // 跟随误差
            public ushort torque;            // 扭矩
            public ushort temperature;       // 温度
            public uint alarmCode;           // 报警码
            public byte servoStatus;         // 伺服状态
            public byte homeStatus;          // 回零状态
            public byte limitStatus;         // 限位状态
            public byte reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct FirmwareInfo
        {
            public uint version;             // 版本号
            public uint buildDate;           // 编译日期
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string model;             // 型号
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string serial;            // 序列号
        }

        // ========== 连接扩展 ==========

        /// <summary>
        /// 获取控制器完整规格信息
        /// </summary>
        public static async Task<SysSpecification> GetSysSpecificationAsync(this ZMotionHandle handle)
        {
            // DLL 内部会按 struct_SysMaxSpecification 复制最多 0x44 字节。这里按该大小分配并清零，
            // 只读取第一个 dword；其余字段在该版本上不可信，不做结构体解析。
            const int specBufferSize = 0x44;
            IntPtr ptr = Marshal.AllocHGlobal(specBufferSize);
            try
            {
                for (int i = 0; i < specBufferSize; i += 4)
                    Marshal.WriteInt32(ptr, i, 0);

                int result = await Task.Run(() => ZMotionAPI.ZMC_GetSysSpecification(handle.DangerousGetHandle(), ptr));
                ZMotionErrors.ThrowIfError(result, "GetSysSpecification");
                return new SysSpecification
                {
                    RawValue = Marshal.ReadInt32(ptr),
                    reserved = new byte[16]
                };
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// 获取固件版本信息
        /// </summary>
        public static async Task<(float version, string build, int buildNo)> GetSoftVersionAsync(this ZMotionHandle handle)
        {
            // 该导出没有长度参数：用显式大小的原生缓冲区（256 宽字符）并清零，
            // 避免 StringBuilder 自动容量与原生写入不一致导致的越界风险。
            const int buildChars = 256;
            IntPtr buffer = Marshal.AllocHGlobal(buildChars * 2);
            try
            {
                for (int i = 0; i < buildChars * 2; i += 2) Marshal.WriteInt16(buffer, i, 0);
                float version = 0;
                int buildNo = 0;
                int result = await Task.Run(() =>
                {
                    float v;
                    int n;
                    int r = ZMotionAPI.ZMC_GetSoftVersion(handle.DangerousGetHandle(), out v, buffer, out n);
                    version = v; buildNo = n;
                    return r;
                });
                ZMotionErrors.ThrowIfError(result, "GetSoftVersion");
                return (version, Marshal.PtrToStringUni(buffer) ?? "", buildNo);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 获取控制器状态
        /// </summary>
        public static async Task<int> GetStateAsync(this ZMotionHandle handle)
        {
            byte state = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetState(handle.DangerousGetHandle(), out state));
            ZMotionErrors.ThrowIfError(result, "GetState");
            return state;
        }

        /// <summary>
        /// 获取最后错误码
        /// </summary>
        public static async Task<int> GetLastErrorAsync(this ZMotionHandle handle)
        {
            int error = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetError(handle.DangerousGetHandle(), out error));
            ZMotionErrors.ThrowIfError(result, "GetError");
            return error;
        }

        // ========== 程序编译下载 ==========

        /// <summary>
        /// 编译源码并下载到控制器 (一体化操作)
        /// </summary>
        public static async Task<(bool success, string error)> CompileAndDownloadAsync(
            this ZMotionHandle handle, string sourceCode, string programName, bool runAfterDownload = false)
        {
            // 1. 保存源码到临时文件
            string tempDir = System.IO.Path.GetTempPath();
            string srcFile = System.IO.Path.Combine(tempDir, $"{programName}_{Guid.NewGuid():N}.src");
            string zarFile = System.IO.Path.ChangeExtension(srcFile, ".zar");

            try
            {
                // 写入源码 (UTF-8 -> GB2312)
                byte[] gbBytes = ZMotionAPI.Gb2312.GetBytes(sourceCode);
                await Task.Run(() => System.IO.File.WriteAllBytes(srcFile, gbBytes));

                // 2. 编译生成 ZAR（MakeZar 不接收 handle：source, output, pass, uid）
                int result = await Task.Run(() => ZMotionAPI.ZMC_MakeZar(srcFile, zarFile, "", 0));
                if (result != ZMotionErrors.SUCCESS)
                {
                    string errDesc = ZMotionErrors.GetDescription(result);
                    return (false, $"编译失败 (0x{result:X4}): {errDesc}");
                }

                // 3. 下载 ZAR 到控制器
                result = await Task.Run(() => ZMotionAPI.ZMC_DownZar(handle.DangerousGetHandle(), zarFile, 0));
                if (result != ZMotionErrors.SUCCESS)
                {
                    string errDesc = ZMotionErrors.GetDescription(result);
                    return (false, $"下载失败 (0x{result:X4}): {errDesc}");
                }

                // 4. 可选：运行程序
                if (runAfterDownload)
                {
                    result = await Task.Run(() => ZMotionAPI.ZMC_RunZarFile(handle.DangerousGetHandle(), programName));
                    if (result != ZMotionErrors.SUCCESS)
                    {
                        string errDesc = ZMotionErrors.GetDescription(result);
                        return (false, $"运行失败 (0x{result:X4}): {errDesc}");
                    }
                }

                return (true, "");
            }
            finally
            {
                // 清理临时文件
                try
                {
                    if (System.IO.File.Exists(srcFile)) System.IO.File.Delete(srcFile);
                    if (System.IO.File.Exists(zarFile)) System.IO.File.Delete(zarFile);
                }
                catch { }
            }
        }

        /// <summary>
        /// 直接从内存编译下载 (无临时文件)
        /// </summary>
        public static async Task<(bool success, string error)> CompileAndDownloadMemAsync(
            this ZMotionHandle handle, string sourceCode, string programName)
        {
            // 使用 ZMC_DownMemZar - 需要先 MakeZar 生成内存缓冲区
            string tempDir = System.IO.Path.GetTempPath();
            string srcFile = System.IO.Path.Combine(tempDir, $"{programName}_{Guid.NewGuid():N}.src");
            string zarFile = System.IO.Path.ChangeExtension(srcFile, ".zar");

            try
            {
                byte[] gbBytes = ZMotionAPI.Gb2312.GetBytes(sourceCode);
                await Task.Run(() => System.IO.File.WriteAllBytes(srcFile, gbBytes));

                int result = await Task.Run(() => ZMotionAPI.ZMC_MakeZar(srcFile, zarFile, "", 0));
                if (result != ZMotionErrors.SUCCESS)
                {
                    return (false, $"编译失败: {ZMotionErrors.GetDescription(result)}");
                }

                // 读取生成的 ZAR 到内存
                byte[] zarData = await Task.Run(() => System.IO.File.ReadAllBytes(zarFile));
                GCHandle zarHandle = GCHandle.Alloc(zarData, GCHandleType.Pinned);
                try
                {
                    result = await Task.Run(() => ZMotionAPI.ZMC_DownMemZar(handle.DangerousGetHandle(), zarHandle.AddrOfPinnedObject(), zarData.Length, 0));
                    if (result != ZMotionErrors.SUCCESS)
                    {
                        return (false, $"下载失败: {ZMotionErrors.GetDescription(result)}");
                    }
                }
                finally
                {
                    zarHandle.Free();
                }

                return (true, "");
            }
            finally
            {
                try { if (System.IO.File.Exists(srcFile)) System.IO.File.Delete(srcFile); } catch { }
                try { if (System.IO.File.Exists(zarFile)) System.IO.File.Delete(zarFile); } catch { }
            }
        }

        /// <summary>
        /// 运行程序
        /// </summary>
        public static async Task RunProgramAsync(this ZMotionHandle handle, string programName)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_RunZarFile(handle.DangerousGetHandle(), programName));
            ZMotionErrors.ThrowIfError(result, $"RunProgram {programName}");
        }

        /// <summary>
        /// 停止程序
        /// </summary>
        public static async Task StopProgramAsync(this ZMotionHandle handle)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_Stop(handle.DangerousGetHandle()));
            ZMotionErrors.ThrowIfError(result, "StopProgram");
        }

        /// <summary>
        /// 暂停/恢复程序
        /// </summary>
        public static async Task PauseProgramAsync(this ZMotionHandle handle)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_Pause(handle.DangerousGetHandle()));
            ZMotionErrors.ThrowIfError(result, "PauseProgram");
        }

        public static async Task ResumeProgramAsync(this ZMotionHandle handle)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_Resume(handle.DangerousGetHandle()));
            ZMotionErrors.ThrowIfError(result, "ResumeProgram");
        }

        // ========== VR / TABLE 读写 ==========

        /// <summary>
        /// 读取 VR 变量 (int32 数组)
        /// </summary>
        public static async Task<int[]> ReadVRAsync(this ZMotionHandle handle, int address, int count)
        {
            int[] buffer = new int[count];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_ArrayReadf(
                    handle.DangerousGetHandle(), "VR", address, count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"ReadVR[{address}:{count}]");
                return buffer;
            }
            finally
            {
                handleGC.Free();
            }
        }

        /// <summary>
        /// 写入 VR 变量
        /// </summary>
        public static async Task WriteVRAsync(this ZMotionHandle handle, int address, int[] values)
        {
            GCHandle handleGC = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_ArrayWritef(
                    handle.DangerousGetHandle(), "VR", address, values.Length, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"WriteVR[{address}:{values.Length}]");
            }
            finally
            {
                handleGC.Free();
            }
        }

        /// <summary>
        /// 读取 TABLE 变量
        /// </summary>
        public static async Task<int[]> ReadTableAsync(this ZMotionHandle handle, int address, int count)
        {
            int[] buffer = new int[count];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_ArrayReadf(
                    handle.DangerousGetHandle(), "TABLE", address, count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"ReadTABLE[{address}:{count}]");
                return buffer;
            }
            finally
            {
                handleGC.Free();
            }
        }

        /// <summary>
        /// 写入 TABLE 变量
        /// </summary>
        public static async Task WriteTableAsync(this ZMotionHandle handle, int address, int[] values)
        {
            GCHandle handleGC = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_ArrayWritef(
                    handle.DangerousGetHandle(), "TABLE", address, values.Length, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"WriteTABLE[{address}:{values.Length}]");
            }
            finally
            {
                handleGC.Free();
            }
        }

        /// <summary>
        /// 读取通用变量 (VAR/GLOBAL/LOCAL)
        /// </summary>
        public static async Task<float[]> ReadVarAsync(this ZMotionHandle handle, string name, int address, int count)
        {
            float[] buffer = new float[count];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_ArrayReadf(
                    handle.DangerousGetHandle(), name, address, count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"ReadVar {name}[{address}:{count}]");
                return buffer;
            }
            finally
            {
                handleGC.Free();
            }
        }

        // ========== 寄存器读写 ==========

        /// <summary>
        /// 读取寄存器 (原始字节)
        /// </summary>
        // 原生 ZMC_RegisterRead/Write 的第二参是寄存器名字符串（如 "VR"/"TABLE"），
        // 而现有调用方传入的是 int regType，映射关系未经确认。按 fail-safe 原则拒绝调用。
        public static Task<byte[]> RegisterReadAsync(this ZMotionHandle handle, int regType, int address, int count)
        {
            return Task.FromException<byte[]>(new ZMotionException(
                ZMotionErrors.ERR_NOT_SUPPORTED,
                "register access is not available: native register name mapping is unverified"));
        }

        /// <summary>
        /// 写入寄存器
        /// </summary>
        public static Task RegisterWriteAsync(this ZMotionHandle handle, int regType, int address, byte[] data)
        {
            return Task.FromException(new ZMotionException(
                ZMotionErrors.ERR_NOT_SUPPORTED,
                "register access is not available: native register name mapping is unverified"));
        }

        // ========== IO 读写 ==========

        /// <summary>
        /// 读取单个数字输入
        /// </summary>
        public static async Task<bool> GetInAsync(this ZMotionHandle handle, short bit)
        {
            byte resultByte = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetIn(handle.DangerousGetHandle(), bit, out resultByte));
            ZMotionErrors.ThrowIfError(result, $"GetIn {bit}");
            return resultByte != 0;
        }

        /// <summary>
        /// 写入单个数字输出
        /// </summary>
        public static async Task SetOutputAsync(this ZMotionHandle handle, short bit, bool value)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_SetOutput(handle.DangerousGetHandle(), bit, value ? (byte)1 : (byte)0));
            ZMotionErrors.ThrowIfError(result, $"SetOutput {bit}={value}");
        }

        /// <summary>
        /// 读取单个数字输出
        /// </summary>
        public static async Task<bool> GetOutputAsync(this ZMotionHandle handle, short bit)
        {
            byte resultByte = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetOutput(handle.DangerousGetHandle(), bit, out resultByte));
            ZMotionErrors.ThrowIfError(result, $"GetOutput {bit}");
            return resultByte != 0;
        }

        /// <summary>
        /// 批量读取所有数字输入 (返回位数组，每字节8个点)
        /// </summary>
        public static async Task<byte[]> GetInAllAsync(this ZMotionHandle handle)
        {
            // 原生 API 一次返回 0..31 共 32 个点的位图（单个 uint32），不是 256 字节缓冲区。
            uint state = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetInAll(
                handle.DangerousGetHandle(), 0, 31, out state));
            ZMotionErrors.ThrowIfError(result, "GetInAll");
            return BitConverter.GetBytes(state);
        }

        /// <summary>
        /// 批量读取所有数字输出
        /// </summary>
        public static async Task<byte[]> GetOutAllAsync(this ZMotionHandle handle)
        {
            uint state = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetOutAll(
                handle.DangerousGetHandle(), 0, 31, out state));
            ZMotionErrors.ThrowIfError(result, "GetOutAll");
            return BitConverter.GetBytes(state);
        }

        /// <summary>
        /// 批量写入所有数字输出
        /// </summary>
        public static async Task SetOutAllAsync(this ZMotionHandle handle, byte[] data)
        {
            // 原生 API 只能写入一个 uint32（0..31 点）；超出部分无法表达，明确拒绝而不是越界写入。
            if (data == null || data.Length == 0 || data.Length > 4)
                throw new ZMotionException(
                    ZMotionErrors.ERR_PARAM_INVALID,
                    "SetOutAll supports at most 32 outputs (4 bytes) on this zmotion.dll API");

            uint state = 0;
            for (int i = 0; i < data.Length; i++)
                state |= (uint)data[i] << (i * 8);

            int result = await Task.Run(() => ZMotionAPI.ZMC_SetOutAll(
                handle.DangerousGetHandle(), 0, 31, state));
            ZMotionErrors.ThrowIfError(result, "SetOutAll");
        }

        /// <summary>
        /// 读取模拟输入
        /// </summary>
        public static async Task<float> GetAInAsync(this ZMotionHandle handle, short channel)
        {
            float value = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetAIn(handle.DangerousGetHandle(), channel, out value));
            ZMotionErrors.ThrowIfError(result, $"GetAIn {channel}");
            return value;
        }

        /// <summary>
        /// 读取模拟输出
        /// </summary>
        public static async Task<float> GetDaOutAsync(this ZMotionHandle handle, short channel)
        {
            float value = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetDaOut(handle.DangerousGetHandle(), channel, out value));
            ZMotionErrors.ThrowIfError(result, $"GetDaOut {channel}");
            return value;
        }

        /// <summary>
        /// 写入模拟输出
        /// </summary>
        public static async Task SetDaOutAsync(this ZMotionHandle handle, short channel, float value)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_SetDaOut(handle.DangerousGetHandle(), channel, value));
            ZMotionErrors.ThrowIfError(result, $"SetDaOut {channel}={value}");
        }

        // ========== 轴状态 ==========

        /// <summary>
        /// 获取轴数量。ZMC_GetAxises 的真实签名已由反汇编确认（handle → uint8，ret 4），
        /// 直接读取宿主控制器句柄内的轴数（模拟器/控制器未初始化时可能返回 0）。
        /// </summary>
        public static byte GetAxisCount(this ZMotionHandle handle)
        {
            try
            {
                return ZMotionAPI.ZMC_GetAxises(handle.DangerousGetHandle());
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 获取单轴完整状态。
        /// ZMC_GetAxisStates 需要 (handle, axis, 输出缓冲区) 三个参数；在无法确认
        /// 控制器/模拟器支持该导出之前，直接返回受控异常，绝不触发 AccessViolation。
        /// </summary>
        public static Task<AxisState> GetAxisStateAsync(this ZMotionHandle handle, byte axis)
        {
            return Task.FromException<AxisState>(new ZMotionException(
                -1, "axis state not available on this connection"));
        }

        /// <summary>
        /// 获取所有轴状态
        /// </summary>
        public static Task<AxisState[]> GetAllAxisStatesAsync(this ZMotionHandle handle)
        {
            return Task.FromResult(Array.Empty<AxisState>());
        }

        /// <summary>
        /// 使能/失能轴伺服
        /// </summary>
        public static async Task SetAxisEnableAsync(this ZMotionHandle handle, byte axis, bool enable)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_SetAxisEnable(handle.DangerousGetHandle(), axis, enable ? (byte)1 : (byte)0));
            ZMotionErrors.ThrowIfError(result, $"SetAxisEnable {axis}={enable}");
        }

        // ========== 直接命令执行 ==========

        /// <summary>
        /// 执行直接命令字符串 (如 "MOVEABS(0,1000)")
        /// </summary>
        public static async Task<string> DirectCommandAsync(this ZMotionHandle handle, string command, int timeoutMs = 5000, int responseBufferSize = 1024)
        {
            byte[] response = new byte[responseBufferSize];
            int result = await Task.Run(() => ZMotionAPI.ZMC_Execute(
                handle.DangerousGetHandle(), command, timeoutMs, response, (uint)responseBufferSize));

            if (result != ZMotionErrors.SUCCESS)
            {
                string errDesc = ZMotionErrors.GetDescription(result);
                throw new ZMotionException(result, $"DirectCommand '{command}' 失败: {errDesc}");
            }

            // 查找字符串结束符
            int len = Array.IndexOf(response, (byte)0);
            if (len < 0) len = response.Length;
            return ZMotionAPI.Gb2312.GetString(response, 0, len);
        }

        /// <summary>
        /// 执行简单命令 (无响应)
        /// </summary>
        public static async Task ExecuteNoAckAsync(this ZMotionHandle handle, string command, int timeoutMs = 5000)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_ExecuteNoAck(handle.DangerousGetHandle(), command, timeoutMs));
            ZMotionErrors.ThrowIfError(result, $"ExecuteNoAck '{command}'");
        }

        // ========== Modbus ==========

        // 该 zmotion.dll 的 Modbus 导出签名是 (handle, start, count, data*)，
        // 没有 slave 参数。slave=0 时按控制器本地地址访问；其它值无法表达，明确拒绝。
        private static void EnsureLocalModbusSlave(int slave)
        {
            if (slave != 0)
                throw new ZMotionException(
                    ZMotionErrors.ERR_NOT_SUPPORTED,
                    "Modbus slave addressing is not supported by this zmotion.dll API (slave must be 0)");
        }

        public static async Task<byte[]> ModbusReadCoilsAsync(this ZMotionHandle handle, int slave, int address, int count)
        {
            EnsureLocalModbusSlave(slave);
            byte[] buffer = new byte[(count + 7) / 8];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                uint result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Get0x(
                    handle.DangerousGetHandle(), (ushort)address, (ushort)count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError((int)result, $"ModbusReadCoils {slave}:{address}:{count}");
                return buffer;
            }
            finally { handleGC.Free(); }
        }

        public static async Task ModbusWriteCoilsAsync(this ZMotionHandle handle, int slave, int address, byte[] data)
        {
            EnsureLocalModbusSlave(slave);
            GCHandle handleGC = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                uint result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Set0x(
                    handle.DangerousGetHandle(), (ushort)address, (ushort)(data.Length * 8), handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError((int)result, $"ModbusWriteCoils {slave}:{address}");
            }
            finally { handleGC.Free(); }
        }

        public static async Task<ushort[]> ModbusReadRegistersAsync(this ZMotionHandle handle, int slave, int address, int count)
        {
            EnsureLocalModbusSlave(slave);
            ushort[] buffer = new ushort[count];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                uint result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Get4x(
                    handle.DangerousGetHandle(), (ushort)address, (ushort)count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError((int)result, $"ModbusReadRegisters {slave}:{address}:{count}");
                return buffer;
            }
            finally { handleGC.Free(); }
        }

        public static async Task ModbusWriteRegistersAsync(this ZMotionHandle handle, int slave, int address, ushort[] data)
        {
            EnsureLocalModbusSlave(slave);
            GCHandle handleGC = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                uint result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Set4x(
                    handle.DangerousGetHandle(), (ushort)address, (ushort)data.Length, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError((int)result, $"ModbusWriteRegisters {slave}:{address}");
            }
            finally { handleGC.Free(); }
        }

        // ========== EtherCAT SDO ==========

        // 反汇编显示 ZMC_EcatSdo_Send/Recv 的真实参数个数分别为 5/6，且参数语义
        // （数据长度 + 数据指针）与 index/subIndex/dataType 不符。与高层 SDO 读写
        // 的映射无法确认，按 fail-safe 原则拒绝调用，避免错误 ABI 造成堆损坏。
        public static Task<int> EcatSdoReadAsync(this ZMotionHandle handle, short slot, short position, int index, int subIndex, int dataType)
        {
            return Task.FromException<int>(new ZMotionException(
                ZMotionErrors.ERR_NOT_SUPPORTED,
                "EtherCAT SDO read is not available: native signature/semantics unverified"));
        }

        public static Task EcatSdoWriteAsync(this ZMotionHandle handle, short slot, short position, int index, int subIndex, int dataType, int value)
        {
            return Task.FromException(new ZMotionException(
                ZMotionErrors.ERR_NOT_SUPPORTED,
                "EtherCAT SDO write is not available: native signature/semantics unverified"));
        }

        // ========== 固件/系统 ==========

        public static async Task ResetControllerAsync(this ZMotionHandle handle)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_ResetController(handle.DangerousGetHandle()));
            ZMotionErrors.ThrowIfError(result, "ResetController");
        }

        public static async Task SetTimeoutAsync(this ZMotionHandle handle, int ms)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_SetTimeOut(handle.DangerousGetHandle(), ms));
            ZMotionErrors.ThrowIfError(result, $"SetTimeout {ms}ms");
        }

        // ========== 程序文件系统 ==========

        public static async Task<List<string>> ListProgramsAsync(this ZMotionHandle handle)
        {
            var programs = new List<string>();
            var name = new StringBuilder(1024);
            int size = 0;

            int result = await Task.Run(() => ZMotionAPI.ZMC_FindFirst3File(handle.DangerousGetHandle(), name, out size));
            if (result != ZMotionErrors.SUCCESS)
            {
                // 明确区分“读取失败”和“没有程序”——不再静默返回空列表。
                throw new ZMotionException(result, $"ListPrograms(FindFirst): {ZMotionErrors.GetDescription(result)}");
            }

            // 成功但文件名为空 = 控制器上没有程序（模拟器空机的正常情况）。
            if (string.IsNullOrEmpty(name.ToString())) return programs;
            programs.Add(name.ToString());

            while (true)
            {
                var next = new StringBuilder(1024);
                int nr = await Task.Run(() => ZMotionAPI.ZMC_FindNext3File(handle.DangerousGetHandle(), next, out size));
                if (nr != ZMotionErrors.SUCCESS) break; // 枚举结束
                var nm = next.ToString();
                if (string.IsNullOrEmpty(nm)) break;
                programs.Add(nm);
            }
            return programs;
        }

        // ZMC_Up3File 的真实参数个数为 3，且没有缓冲区长度参数，无法安全确定写入边界。
        // 在确认其语义之前拒绝调用，避免原生代码越界写入托管缓冲区。
        public static Task<string> ReadProgramAsync(this ZMotionHandle handle, string programName)
        {
            return Task.FromException<string>(new ZMotionException(
                ZMotionErrors.ERR_NOT_SUPPORTED,
                "program upload is not available: ZMC_Up3File ABI/semantics unverified"));
        }

        public static async Task DeleteProgramAsync(this ZMotionHandle handle, string programName)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_Delete3File(handle.DangerousGetHandle(), programName));
            ZMotionErrors.ThrowIfError(result, $"DeleteProgram {programName}");
        }

        // ========== 综合状态获取 (用于 AI GetStatus) ==========

        public static async Task<dynamic> GetFullStatusAsync(this ZMotionHandle handle)
        {
            var spec = await handle.GetSysSpecificationAsync();
            var (version, build, buildNo) = await handle.GetSoftVersionAsync();
            int state = await handle.GetStateAsync();
            byte axisCount = handle.GetAxisCount();
            var axisStates = await handle.GetAllAxisStatesAsync();

            return new
            {
                Connected = true,
                State = state,
                FirmwareVersion = version,
                Build = build,
                BuildNumber = buildNo,
                AxisCount = axisCount,
                AxisStates = axisStates,
                Specification = new
                {
                    RawValue = spec.RawValue,
                    AxisCount = spec.axisCount,
                    IOInCount = spec.ioInCount,
                    IOOutCount = spec.ioOutCount,
                    AICount = spec.aiCount,
                    AOCount = spec.aoCount,
                    VRSize = spec.vrSize,
                    TableSize = spec.tableSize,
                    ProgramMemory = spec.programMemory,
                    DataMemory = spec.dataMemory,
                    MaxTasks = spec.maxTasks,
                    MaxPrograms = spec.maxPrograms
                },
                Programs = await handle.ListProgramsAsync()
            };
        }

        /// <summary>
        /// 设置默认超时（5秒），用于新建立的连接句柄
        /// </summary>
        public static async Task SetDefaultTimeoutAsync(this ZMotionHandle handle)
        {
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_SetTimeOut(handle.DangerousGetHandle(), 5000));
                if (result != ZMotionErrors.SUCCESS)
                {
                    PlugInLog.Log("设置默认超时失败: " + ZMotionErrors.GetDescription(result));
                }
            }
            catch (Exception ex)
            {
                PlugInLog.LogException("SetDefaultTimeoutAsync", ex);
            }
        }
    }
}
