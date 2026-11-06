using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;

namespace RTSys.PlugIn
{
    /// <summary>
    /// ZMotion 连接句柄安全包装器（自动释放非托管资源）
    /// </summary>
    public sealed class ZMotionHandle : SafeHandle
    {
        private static readonly IntPtr InvalidHandleValue = IntPtr.Zero;

        public ZMotionHandle() : base(InvalidHandleValue, ownsHandle: true) { }

        public ZMotionHandle(IntPtr preExistingHandle, bool ownsHandle = true)
            : base(InvalidHandleValue, ownsHandle)
        {
            SetHandle(preExistingHandle);
        }

        public override bool IsInvalid => handle == InvalidHandleValue;

        /// <summary>
        /// 创建并连接（工厂方法）
        /// </summary>
        public static async Task<ZMotionHandle> ConnectAsync(string ip, int type = 2, int timeoutMs = 5000)
        {
            var handle = new ZMotionHandle();
            IntPtr h = IntPtr.Zero;
            int result = await Task.Run(() => ZMotionAPI.ZMC_OpenEth(ip, out h));
            ZMotionErrors.ThrowIfError(result, $"Connect to {ip}");
            handle.SetHandle(h);

            // 设置超时
            if (timeoutMs > 0)
            {
                result = await Task.Run(() => ZMotionAPI.ZMC_SetTimeOut(h, timeoutMs));
                ZMotionErrors.ThrowIfError(result, "SetTimeout");
            }
            return handle;
        }

        /// <summary>
        /// COM 口连接
        /// </summary>
        public static async Task<ZMotionHandle> ConnectComAsync(string portName, int baudRate = 115200, int timeoutMs = 5000)
        {
            // ZMC_OpenCom 需要端口号，格式如 "COM3" -> 3
            int portNum = 0;
            if (portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                int.TryParse(portName.Substring(3), out portNum);

            var handle = new ZMotionHandle();
            // 先设置默认波特率
            int result = await Task.Run(() => ZMotionAPI.ZMC_SetComDefaultBaud(portNum, baudRate));
            if (result != ZMotionErrors.SUCCESS && result != ZMotionErrors.ERR_NOT_SUPPORTED)
                ZMotionErrors.ThrowIfError(result, $"SetComDefaultBaud {portName}");

            IntPtr h = IntPtr.Zero;
            result = await Task.Run(() => ZMotionAPI.ZMC_Open(1, portNum.ToString(), out h));
            ZMotionErrors.ThrowIfError(result, $"Connect COM{portNum}");
            handle.SetHandle(h);

            if (timeoutMs > 0)
            {
                result = await Task.Run(() => ZMotionAPI.ZMC_SetTimeOut(h, timeoutMs));
                ZMotionErrors.ThrowIfError(result, "SetTimeout");
            }
            return handle;
        }

        /// <summary>
        /// PCI 板卡连接
        /// </summary>
        public static async Task<ZMotionHandle> ConnectPciAsync(int cardIndex = 0, int timeoutMs = 5000)
        {
            var handle = new ZMotionHandle();
            IntPtr h = IntPtr.Zero;
            int result = await Task.Run(() => ZMotionAPI.ZMC_Open(3, cardIndex.ToString(), out h));
            ZMotionErrors.ThrowIfError(result, $"Connect PCI card {cardIndex}");
            handle.SetHandle(h);

            if (timeoutMs > 0)
            {
                result = await Task.Run(() => ZMotionAPI.ZMC_SetTimeOut(h, timeoutMs));
                ZMotionErrors.ThrowIfError(result, "SetTimeout");
            }
            return handle;
        }

        protected override bool ReleaseHandle()
        {
            if (!IsInvalid)
            {
                try
                {
                    ZMotionAPI.ZMC_Close(handle);
                }
                catch { }
                handle = InvalidHandleValue;
            }
            return true;
        }

        public void DisposeSafe()
        {
            Dispose();
            GC.SuppressFinalize(this);
        }
    }
}