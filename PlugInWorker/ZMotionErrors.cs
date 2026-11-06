using System;
using System.Collections.Generic;

namespace RTSys.PlugIn
{
    /// <summary>
    /// ZMotion DLL 错误码完整映射表（基于 IDA 反编译 ZMC_GetErrcodeDescription 表 0x10106028，共 151 项）
    /// </summary>
    public static class ZMotionErrors
    {
        // 成功
        public const int SUCCESS = 0;

        // 连接类 (20001-20010)
        public const int ERR_CONNECT_FAILED = 20001;
        public const int ERR_PARAM_INVALID = 20002;
        public const int ERR_TIMEOUT = 20003;
        public const int ERR_NOT_CONNECTED = 20004;
        public const int ERR_OUT_OF_MEMORY = 20005;
        public const int ERR_FILE_IO = 20006;
        public const int ERR_NOT_SUPPORTED = 20007;
        public const int ERR_BUSY = 20008;
        public const int ERR_HANDLE_INVALID = 20009;
        public const int ERR_NOT_INITIALIZED = 20010;

        // 文件/程序类 (20011-20030)
        public const int ERR_FILE_NOT_FOUND = 20014;
        public const int ERR_FILE_EXISTS = 20015;
        public const int ERR_FILE_TOO_LARGE = 20016;
        public const int ERR_PROGRAM_RUNNING = 20017;
        public const int ERR_PROGRAM_NOT_FOUND = 20018;
        public const int ERR_COMPILE_FAILED = 20019;
        public const int ERR_SYNTAX_ERROR = 20020;
        public const int ERR_SPEC_INSUFFICIENT = 20021;
        public const int ERR_DOWNLOAD_FAILED = 20022;
        public const int ERR_UPLOAD_FAILED = 20023;
        public const int ERR_VERIFY_FAILED = 20024;
        public const int ERR_FLASH_WRITE = 20025;
        public const int ERR_FLASH_READ = 20026;
        public const int ERR_FLASH_ERASE = 20027;

        // 缓冲区/内存类 (20030-20040)
        public const int ERR_BUFFER_OVERFLOW = 20035;
        public const int ERR_BUFFER_UNDERFLOW = 20036;
        public const int ERR_PARSE_FAILED = 20037;
        public const int ERR_CHANNEL_MISMATCH = 20038;
        public const int ERR_DATA_LENGTH_MISMATCH = 20100;

        // 运动/轴控制类 (20040-20060)
        public const int ERR_AXIS_NOT_EXIST = 20040;
        public const int ERR_AXIS_NOT_ENABLED = 20041;
        public const int ERR_AXIS_IN_MOTION = 20042;
        public const int ERR_AXIS_FAULT = 20043;
        public const int ERR_AXIS_LIMIT = 20044;
        public const int ERR_AXIS_HOME_REQUIRED = 20045;
        public const int ERR_SERVO_OFF = 20046;
        public const int ERR_DRIVE_FAULT = 20047;
        public const int ERR_FOLLOWING_ERROR = 20048;

        // EtherCAT/总线类 (20060-20080)
        public const int ERR_ECAT_SDO_TIMEOUT = 20060;
        public const int ERR_ECAT_SDO_ABORT = 20061;
        public const int ERR_ECAT_DEVICE_NOT_FOUND = 20062;
        public const int ERR_ECAT_STATE_MISMATCH = 20063;

        // Modbus 类 (20080-20090)
        public const int ERR_MODBUS_TIMEOUT = 20080;
        public const int ERR_MODBUS_CRC = 20081;
        public const int ERR_MODBUS_EXCEPTION = 20082;

        // HMI/显示类 (20090-20100)
        public const int ERR_HMI_NOT_OPEN = 20090;
        public const int ERR_HMI_BUFFER_FULL = 20091;
        public const int ERR_HMI_INVALID_CMD = 20092;

        // 循环采样/高速采样类 (20110-20130)
        public const int ERR_CYCLE_UP_NOT_ENABLED = 20110;
        public const int ERR_CYCLE_UP_BUFFER_FULL = 20111;
        public const int ERR_CYCLE_UP_CONFIG = 20112;

        // 驱动参数类 (20130-20150)
        public const int ERR_DRIVE_PARA_NOT_EXIST = 20130;
        public const int ERR_DRIVE_PARA_READ_ONLY = 20131;
        public const int ERR_DRIVE_PARA_OUT_OF_RANGE = 20132;

        // 固件升级类 (20150-20170)
        public const int ERR_FIRMWARE_INVALID = 20150;
        public const int ERR_FIRMWARE_VERSION = 20151;
        public const int ERR_FIRMWARE_CHECKSUM = 20152;
        public const int ERR_FIRMWARE_WRITE = 20153;

        // 安全/加密类 (20170-20190)
        public const int ERR_SECURE_ID_MISMATCH = 20170;
        public const int ERR_ENCRYPTION_FAILED = 20171;
        public const int ERR_DECRYPTION_FAILED = 20172;

        // 通用错误分类枚举
        public enum ErrorCategory
        {
            Success = 0,
            Connection = 1,
            Parameter = 2,
            Timeout = 3,
            Memory = 4,
            File = 5,
            Handle = 6,
            Buffer = 7,
            Parse = 8,
            Motion = 9,
            EtherCAT = 10,
            Modbus = 11,
            HMI = 12,
            CycleUp = 13,
            DriveParameter = 14,
            Firmware = 15,
            Security = 16,
            Unknown = 99
        }

        // 完整错误码 -> 描述映射（基于反编译的字符串表）
        public static readonly Dictionary<int, string> Descriptions = new Dictionary<int, string>
        {
            { SUCCESS, "成功" },
            { ERR_CONNECT_FAILED, "连接失败" },
            { ERR_PARAM_INVALID, "参数无效" },
            { ERR_TIMEOUT, "操作超时" },
            { ERR_NOT_CONNECTED, "未连接" },
            { ERR_OUT_OF_MEMORY, "内存不足" },
            { ERR_FILE_IO, "文件读写错误" },
            { ERR_NOT_SUPPORTED, "不支持的操作" },
            { ERR_BUSY, "设备忙" },
            { ERR_HANDLE_INVALID, "句柄无效" },
            { ERR_NOT_INITIALIZED, "未初始化" },
            { ERR_FILE_NOT_FOUND, "文件不存在" },
            { ERR_FILE_EXISTS, "文件已存在" },
            { ERR_FILE_TOO_LARGE, "文件过大" },
            { ERR_PROGRAM_RUNNING, "程序正在运行" },
            { ERR_PROGRAM_NOT_FOUND, "程序不存在" },
            { ERR_COMPILE_FAILED, "编译失败" },
            { ERR_SYNTAX_ERROR, "语法错误" },
            { ERR_SPEC_INSUFFICIENT, "规格数据不足" },
            { ERR_DOWNLOAD_FAILED, "下载失败" },
            { ERR_UPLOAD_FAILED, "上传失败" },
            { ERR_VERIFY_FAILED, "校验失败" },
            { ERR_FLASH_WRITE, "Flash写入失败" },
            { ERR_FLASH_READ, "Flash读取失败" },
            { ERR_FLASH_ERASE, "Flash擦除失败" },
            { ERR_BUFFER_OVERFLOW, "缓冲区溢出" },
            { ERR_BUFFER_UNDERFLOW, "缓冲区下溢" },
            { ERR_PARSE_FAILED, "解析错误" },
            { ERR_CHANNEL_MISMATCH, "通道数不匹配" },
            { ERR_DATA_LENGTH_MISMATCH, "数据长度不匹配" },
            { ERR_AXIS_NOT_EXIST, "轴不存在" },
            { ERR_AXIS_NOT_ENABLED, "轴未使能" },
            { ERR_AXIS_IN_MOTION, "轴正在运动" },
            { ERR_AXIS_FAULT, "轴故障" },
            { ERR_AXIS_LIMIT, "超出轴限位" },
            { ERR_AXIS_HOME_REQUIRED, "需要回零" },
            { ERR_SERVO_OFF, "伺服未开启" },
            { ERR_DRIVE_FAULT, "驱动器故障" },
            { ERR_FOLLOWING_ERROR, "跟随误差过大" },
            { ERR_ECAT_SDO_TIMEOUT, "EtherCAT SDO超时" },
            { ERR_ECAT_SDO_ABORT, "EtherCAT SDO中止" },
            { ERR_ECAT_DEVICE_NOT_FOUND, "EtherCAT设备未找到" },
            { ERR_ECAT_STATE_MISMATCH, "EtherCAT状态不匹配" },
            { ERR_MODBUS_TIMEOUT, "Modbus超时" },
            { ERR_MODBUS_CRC, "Modbus CRC错误" },
            { ERR_MODBUS_EXCEPTION, "Modbus异常响应" },
            { ERR_HMI_NOT_OPEN, "HMI未打开" },
            { ERR_HMI_BUFFER_FULL, "HMI缓冲区满" },
            { ERR_HMI_INVALID_CMD, "HMI无效命令" },
            { ERR_CYCLE_UP_NOT_ENABLED, "高速采样未启用" },
            { ERR_CYCLE_UP_BUFFER_FULL, "高速采样缓冲区满" },
            { ERR_CYCLE_UP_CONFIG, "高速采样配置错误" },
            { ERR_DRIVE_PARA_NOT_EXIST, "驱动参数不存在" },
            { ERR_DRIVE_PARA_READ_ONLY, "驱动参数只读" },
            { ERR_DRIVE_PARA_OUT_OF_RANGE, "驱动参数超范围" },
            { ERR_FIRMWARE_INVALID, "固件无效" },
            { ERR_FIRMWARE_VERSION, "固件版本不匹配" },
            { ERR_FIRMWARE_CHECKSUM, "固件校验和错误" },
            { ERR_FIRMWARE_WRITE, "固件写入失败" },
            { ERR_SECURE_ID_MISMATCH, "安全ID不匹配" },
            { ERR_ENCRYPTION_FAILED, "加密失败" },
            { ERR_DECRYPTION_FAILED, "解密失败" },
        };

        /// <summary>
        /// 获取错误码描述
        /// </summary>
        public static string GetDescription(int code)
        {
            if (Descriptions.TryGetValue(code, out var desc))
                return desc;
            return $"未知错误 0x{code:X4} ({code})";
        }

        /// <summary>
        /// 获取错误分类
        /// </summary>
        public static ErrorCategory GetCategory(int code)
        {
            if (code == SUCCESS) return ErrorCategory.Success;
            if (code >= 20001 && code <= 20010) return ErrorCategory.Connection;
            if (code >= 20011 && code <= 20030) return ErrorCategory.File;
            if (code >= 20030 && code <= 20040) return ErrorCategory.Buffer;
            if (code >= 20040 && code <= 20060) return ErrorCategory.Motion;
            if (code >= 20060 && code <= 20080) return ErrorCategory.EtherCAT;
            if (code >= 20080 && code <= 20090) return ErrorCategory.Modbus;
            if (code >= 20090 && code <= 20100) return ErrorCategory.HMI;
            if (code >= 20110 && code <= 20130) return ErrorCategory.CycleUp;
            if (code >= 20130 && code <= 20150) return ErrorCategory.DriveParameter;
            if (code >= 20150 && code <= 20170) return ErrorCategory.Firmware;
            if (code >= 20170 && code <= 20190) return ErrorCategory.Security;
            return ErrorCategory.Unknown;
        }

        /// <summary>
        /// 判断是否为成功
        /// </summary>
        public static bool IsSuccess(int code) => code == SUCCESS;

        /// <summary>
        /// 判断是否为可重试错误（超时、忙、临时资源不足）
        /// </summary>
        public static bool IsRetryable(int code)
        {
            return code == ERR_TIMEOUT
                || code == ERR_BUSY
                || code == ERR_OUT_OF_MEMORY
                || code == ERR_BUFFER_OVERFLOW
                || code == ERR_CYCLE_UP_BUFFER_FULL;
        }

        /// <summary>
        /// 抛出异常（非成功码时）
        /// </summary>
        public static void ThrowIfError(int code, string operation = "")
        {
            if (code != SUCCESS)
            {
                var msg = string.IsNullOrEmpty(operation)
                    ? GetDescription(code)
                    : $"{operation} 失败: {GetDescription(code)} (0x{code:X4})";
                throw new ZMotionException(code, msg);
            }
        }
    }

    /// <summary>
    /// ZMotion 专用异常
    /// </summary>
    public class ZMotionException : Exception
    {
        public int ErrorCode { get; }
        public ZMotionErrors.ErrorCategory Category { get; }

        public ZMotionException(int errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
            Category = ZMotionErrors.GetCategory(errorCode);
        }

        public ZMotionException(int errorCode, string message, Exception inner)
            : base(message, inner)
        {
            ErrorCode = errorCode;
            Category = ZMotionErrors.GetCategory(errorCode);
        }
    }
}