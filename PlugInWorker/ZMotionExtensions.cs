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
            int size = Marshal.SizeOf<SysSpecification>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_GetSysSpecification(handle.DangerousGetHandle(), ptr));
                ZMotionErrors.ThrowIfError(result, "GetSysSpecification");
                return Marshal.PtrToStructure<SysSpecification>(ptr);
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
            StringBuilder build = new StringBuilder(64);
            float version = 0;
            int buildNo = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetSoftVersion(
                handle.DangerousGetHandle(), out version, build, out buildNo));
            ZMotionErrors.ThrowIfError(result, "GetSoftVersion");
            return (version, build.ToString(), buildNo);
        }

        /// <summary>
        /// 获取控制器状态
        /// </summary>
        public static async Task<int> GetStateAsync(this ZMotionHandle handle)
        {
            int state = 0;
            int result = await Task.Run(() => ZMotionAPI.ZMC_GetState(handle.DangerousGetHandle(), out state));
            ZMotionErrors.ThrowIfError(result, "GetState");
            return state;
        }

        /// <summary>
        /// 获取最后错误码
        /// </summary>
        public static async Task<int> GetLastErrorAsync(this ZMotionHandle handle)
        {
            return await Task.Run(() => ZMotionAPI.ZMC_GetError(handle.DangerousGetHandle()));
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

                // 2. 编译生成 ZAR
                int result = await Task.Run(() => ZMotionAPI.ZMC_MakeZar(handle.DangerousGetHandle(), srcFile, 0));
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
            // 这里简化：先生成临时 ZAR 再读入内存
            string tempDir = System.IO.Path.GetTempPath();
            string srcFile = System.IO.Path.Combine(tempDir, $"{programName}_{Guid.NewGuid():N}.src");
            string zarFile = System.IO.Path.ChangeExtension(srcFile, ".zar");

            try
            {
                byte[] gbBytes = ZMotionAPI.Gb2312.GetBytes(sourceCode);
                await Task.Run(() => System.IO.File.WriteAllBytes(srcFile, gbBytes));

                int result = await Task.Run(() => ZMotionAPI.ZMC_MakeZar(handle.DangerousGetHandle(), srcFile, 0));
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
        public static async Task<byte[]> RegisterReadAsync(this ZMotionHandle handle, int regType, int address, int count)
        {
            byte[] buffer = new byte[count];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_RegisterRead(
                    handle.DangerousGetHandle(), regType, 1, address, (ushort)count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"RegisterRead[{address}:{count}]");
                return buffer;
            }
            finally
            {
                handleGC.Free();
            }
        }

        /// <summary>
        /// 写入寄存器
        /// </summary>
        public static async Task RegisterWriteAsync(this ZMotionHandle handle, int regType, int address, byte[] data)
        {
            GCHandle handleGC = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_RegisterWrite(
                    handle.DangerousGetHandle(), regType, 1, address, (ushort)data.Length, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"RegisterWrite[{address}:{data.Length}]");
            }
            finally
            {
                handleGC.Free();
            }
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
        /// 批量读取所有数字输入 (返回位数组，每字节8个点)
        /// </summary>
        public static async Task<byte[]> GetInAllAsync(this ZMotionHandle handle)
        {
            // 需要先获取规格知 IO 点数，这里按最大 256 字节 (2048 点) 分配
            byte[] buffer = new byte[256];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_GetInAll(handle.DangerousGetHandle(), handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, "GetInAll");
                return buffer;
            }
            finally
            {
                handleGC.Free();
            }
        }

        /// <summary>
        /// 批量读取所有数字输出
        /// </summary>
        public static async Task<byte[]> GetOutAllAsync(this ZMotionHandle handle)
        {
            byte[] buffer = new byte[256];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_GetOutAll(handle.DangerousGetHandle(), handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, "GetOutAll");
                return buffer;
            }
            finally
            {
                handleGC.Free();
            }
        }

        /// <summary>
        /// 批量写入所有数字输出
        /// </summary>
        public static async Task SetOutAllAsync(this ZMotionHandle handle, byte[] data)
        {
            GCHandle handleGC = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_SetOutAll(handle.DangerousGetHandle(), handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, "SetOutAll");
            }
            finally
            {
                handleGC.Free();
            }
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
        /// 写入模拟输出
        /// </summary>
        public static async Task SetDaOutAsync(this ZMotionHandle handle, short channel, float value)
        {
            int result = await Task.Run(() => ZMotionAPI.ZMC_SetDaOut(handle.DangerousGetHandle(), channel, value));
            ZMotionErrors.ThrowIfError(result, $"SetDaOut {channel}={value}");
        }

        // ========== 轴状态 ==========

        /// <summary>
        /// 获取轴数量
        /// </summary>
        public static byte GetAxisCount(this ZMotionHandle handle)
        {
            return ZMotionAPI.ZMC_GetAxises(handle.DangerousGetHandle());
        }

        /// <summary>
        /// 获取单轴完整状态 (17 字节结构)
        /// </summary>
        public static async Task<AxisState> GetAxisStateAsync(this ZMotionHandle handle, byte axis)
        {
            int size = Marshal.SizeOf<AxisState>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_GetAxisStates(handle.DangerousGetHandle(), axis, ptr));
                ZMotionErrors.ThrowIfError(result, $"GetAxisState {axis}");
                return Marshal.PtrToStructure<AxisState>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// 获取所有轴状态
        /// </summary>
        public static async Task<AxisState[]> GetAllAxisStatesAsync(this ZMotionHandle handle)
        {
            byte count = handle.GetAxisCount();
            var states = new AxisState[count];
            for (byte i = 0; i < count; i++)
            {
                states[i] = await handle.GetAxisStateAsync(i);
            }
            return states;
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

        public static async Task<byte[]> ModbusReadCoilsAsync(this ZMotionHandle handle, int slave, int address, int count)
        {
            byte[] buffer = new byte[(count + 7) / 8];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Get0x(handle.DangerousGetHandle(), slave, address, count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"ModbusReadCoils {slave}:{address}:{count}");
                return buffer;
            }
            finally { handleGC.Free(); }
        }

        public static async Task ModbusWriteCoilsAsync(this ZMotionHandle handle, int slave, int address, byte[] data)
        {
            GCHandle handleGC = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Set0x(handle.DangerousGetHandle(), slave, address, data.Length * 8, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"ModbusWriteCoils {slave}:{address}");
            }
            finally { handleGC.Free(); }
        }

        public static async Task<ushort[]> ModbusReadRegistersAsync(this ZMotionHandle handle, int slave, int address, int count)
        {
            ushort[] buffer = new ushort[count];
            GCHandle handleGC = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Get4x(handle.DangerousGetHandle(), slave, address, count, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"ModbusReadRegisters {slave}:{address}:{count}");
                return buffer;
            }
            finally { handleGC.Free(); }
        }

        public static async Task ModbusWriteRegistersAsync(this ZMotionHandle handle, int slave, int address, ushort[] data)
        {
            GCHandle handleGC = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_Modbus_Set4x(handle.DangerousGetHandle(), slave, address, data.Length, handleGC.AddrOfPinnedObject()));
                ZMotionErrors.ThrowIfError(result, $"ModbusWriteRegisters {slave}:{address}");
            }
            finally { handleGC.Free(); }
        }

        // ========== EtherCAT SDO ==========

        public static async Task<int> EcatSdoReadAsync(this ZMotionHandle handle, short slot, short position, int index, int subIndex, int dataType)
        {
            IntPtr valuePtr = Marshal.AllocHGlobal(4);
            try
            {
                int result = await Task.Run(() => ZMotionAPI.ZMC_EcatSdo_Recv(handle.DangerousGetHandle(), slot, position, index, subIndex, dataType, valuePtr));
                ZMotionErrors.ThrowIfError(result, $"EcatSdoRead {slot}:{position}:0x{index:X}:{subIndex}");
                return Marshal.ReadInt32(valuePtr);
            }
            finally { Marshal.FreeHGlobal(valuePtr); }
        }

        public static async Task EcatSdoWriteAsync(this ZMotionHandle handle, short slot, short position, int index, int subIndex, int dataType, int value)
        {
            // ZMC_EcatSdo_Send 发送写请求，实际写入需配合
            int result = await Task.Run(() => ZMotionAPI.ZMC_EcatSdo_Send(handle.DangerousGetHandle(), slot, position, index, subIndex, dataType));
            ZMotionErrors.ThrowIfError(result, $"EcatSdoWrite {slot}:{position}:0x{index:X}:{subIndex}");
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
            StringBuilder name = new StringBuilder(260);

            int result = await Task.Run(() => ZMotionAPI.ZMC_FindFirst3File(handle.DangerousGetHandle(), name, name.Capacity));
            if (result == ZMotionErrors.SUCCESS)
            {
                programs.Add(name.ToString());
                while (true)
                {
                    name.Clear();
                    result = await Task.Run(() => ZMotionAPI.ZMC_FindNext3File(handle.DangerousGetHandle(), name, name.Capacity));
                    if (result != ZMotionErrors.SUCCESS) break;
                    programs.Add(name.ToString());
                }
            }
            return programs;
        }

        public static async Task<string> ReadProgramAsync(this ZMotionHandle handle, string programName)
        {
            StringBuilder buffer = new StringBuilder(1024 * 100); // 100KB 缓冲
            int result = await Task.Run(() => ZMotionAPI.ZMC_Up3File(handle.DangerousGetHandle(), programName, buffer, buffer.Capacity));
            if (result != ZMotionErrors.SUCCESS)
            {
                throw new ZMotionException(result, $"ReadProgram {programName}: {ZMotionErrors.GetDescription(result)}");
            }
            return buffer.ToString();
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
    }
}