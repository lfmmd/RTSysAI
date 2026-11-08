using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RTSys.PlugIn
{
    /// <summary>
    /// RTSys 工程视图（CFileView）内部模型桥接。
    ///
    /// 与“外部改 .zpj / 文件”不同，这里直接复用宿主自己的项目模型和处理器：
    ///   - 通过内存扫描定位 CFileView*（树 HWND 存在 +0x2578，文件数组在 +0x2540/+0x2544）
    ///   - 删除：CBCGPTreeCtrlEx::SelectItem 选中节点后发送 WM_COMMAND 0x8099，
    ///     由宿主自己的删除处理器更新模型/工程树
    ///   - 重命名/加入工程：直接调用宿主内部模型函数（0x583E40 / 0x583950，
    ///     参数为 ATL CString），随后调用 0x46B290 从模型重建工程树
    ///
    /// 所有调用都在宿主 UI 线程上执行（临时子类化主窗口），不移动鼠标、不抢焦点。
    /// 原生模型调用仅在版本签名完全匹配时启用（见 EnsureNativeModel），
    /// 否则返回 false，由调用方回退到文件级操作；独立 worker 中同样返回 false。
    /// </summary>
    internal static class RTSysProjectModel
    {
        private const int OFF_TREE_OBJECT = 0x2558;   // CBCGPTreeCtrlEx 对象
        private const int OFF_TREE_HWND = 0x2578;     // tree 对象 + CWnd::m_hWnd (0x20)
        private const int OFF_ARRAY = 0x2540;
        private const int OFF_COUNT = 0x2544;
        private const int ENTRY_SIZE = 16;
        private const int ENTRY_ITEM = 0;
        private const int ENTRY_TYPE = 4;
        private const int ENTRY_NAME = 12;

        private const int CMD_DELETE_FILE = 0x8099;
        private const int CMD_RENAME_FILE = 0x809A;
        private const int CMD_TASK_NUMBER = 0x80D2;

        private const int WM_COMMAND = 0x111;
        // 通过 RegisterWindowMessage 获得系统内唯一消息号；固定 WM_APP 偏移可能与宿主自定义
        // 消息冲突，导致 WndProc 把宿主的 wParam 当成 GCHandle，进而 CLR 崩溃（0xC0000005 in clr.dll）。
        private static uint _invokeMsg;
        private const string InvokeMsgName = "RTSysAI.Plugin.Invoke.9F1C4B7E-3A2D-4E8B-9C6F-1D2E3F4A5B6C";
        // 只接受自己分配的 GCHandle 指针，避免任何外部/冲突消息把任意 wParam 当句柄解引用。
        private static readonly HashSet<IntPtr> _pendingInvokes = new HashSet<IntPtr>();
        private static readonly object _pendingLock = new object();
        private const int GWLP_WNDPROC = -4;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int IDYES = 6;                       // BCGPMessageBox 的“是”
        private const int CacheSeconds = 3;                // CFileView 定位缓存
        private const long MaxScanRegionBytes = 16L * 1024 * 1024; // 单块内存扫描上限

        private static readonly object _lock = new object();
        private static IntPtr _mainHwnd;
        private static IntPtr _cfileView;
        private static IntPtr _treeHwnd;
        private static IntPtr _treeObject;
        private static int _array;
        private static int _count;
        private static DateTime _cacheUtc = DateTime.MinValue;
        private static readonly List<FileViewCandidate> _candidates = new List<FileViewCandidate>();

        private class FileViewCandidate
        {
            public IntPtr Obj;
            public IntPtr Tree;
            public int Array;
            public int Count;
        }

        private static IntPtr _bcgModule;
        private static SelectItemFn _selectItem;
        private static IntPtr _origWndProc;
        private static IntPtr _hookedHwnd;
        private static WndProcDelegate _wndProcDelegate;
        private static bool _hooked;

        #region ---- Win32 ----

        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int SelectItemFn(IntPtr thisPtr, IntPtr item);
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLong(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint dwErrCode);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr addr, byte[] buffer, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr addr, out MEMORY_BASIC_INFORMATION info, UIntPtr size);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_PRIVATE = 0x20000;
        private const uint PAGE_NOACCESS = 0x01;
        private const uint PAGE_GUARD = 0x100;

        #endregion

        #region ---- 公共 API ----

        public static bool IsAvailable
        {
            get
            {
                IntPtr obj, tree;
                return TryGetProjectView(out obj, out tree);
            }
        }

        /// <summary>返回当前工程视图中的文件项（name/type/item）。</summary>
        public static List<ProjectItemInfo> GetItems()
        {
            var result = new List<ProjectItemInfo>();
            IntPtr obj, tree;
            if (!TryGetProjectView(out obj, out tree)) return result;
            lock (_lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    var name = ReadEntryName(_array, i);
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new ProjectItemInfo
                    {
                        Name = name,
                        Type = ReadEntryInt(_array, i, ENTRY_TYPE),
                        Item = new IntPtr(ReadEntryInt(_array, i, ENTRY_ITEM))
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// 删除工程中的程序：选中树节点后调用宿主自己的“删除文件”命令。
        /// 宿主会更新模型/工程树/磁盘文件；这里只负责精确定位与触发。
        /// </summary>
        public static bool TryDelete(string name, out string error)
        {
            error = null;
            IntPtr obj, tree;
            if (!TryGetProjectView(out obj, out tree))
            {
                error = "project view not found (RTSys process?)";
                PlugInLog.Log("TryDelete(" + name + "): " + error);
                return false;
            }

            IntPtr item, itemObj;
            if (!TryFindItem(obj, name, out itemObj, out item))
            {
                error = "program not found in project view: " + name;
                PlugInLog.Log("TryDelete(" + name + "): " + error);
                return false;
            }

            bool selected = false;
            if (!UiInvoke(() => selected = SelectItem(itemObj, item)) || !selected)
            {
                error = "failed to select project tree item";
                PlugInLog.Log("TryDelete(" + name + "): " + error);
                return false;
            }

            // “删除文件”由工程视图（CFileView）的消息映射处理；直接发给视图窗口，
            // 不依赖主框架“当前激活视图”的转发（编辑器为活动视图时主框架会忽略该命令）。
            IntPtr view = GetParent(_treeHwnd);
            PostMessage(view != IntPtr.Zero ? view : _mainHwnd, WM_COMMAND, new IntPtr(CMD_DELETE_FILE), IntPtr.Zero);

            // 宿主对普通程序文件（type 1/2）直接删除、不弹确认框；
            // 分组项才会弹 BCGPMessageBox。轮询条目是否消失，期间出现确认框则自动应答 Yes。
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(100);
                IntPtr dialog = FindConfirmationDialog();
                if (dialog != IntPtr.Zero)
                {
                    SendMessage(dialog, WM_COMMAND, new IntPtr(IDYES), IntPtr.Zero);
                    Thread.Sleep(200);
                }
                IntPtr dummy, dummyObj;
                if (!TryFindItem(itemObj, name, out dummyObj, out dummy)) return true;
            }
            error = "delete command did not remove the item";
            PlugInLog.Log("TryDelete(" + name + "): " + error);
            return false;
        }

        /// <summary>
        /// 重命名工程中的程序：直接调用宿主工程模型的改名函数（内部同时重命名磁盘文件），
        /// 然后刷新工程视图树。不弹出任何对话框、不抢焦点。
        /// </summary>
        public static bool TryRename(string name, string newName, out string error)
        {
            error = null;
            IntPtr obj, tree;
            if (!TryGetProjectView(out obj, out tree))
            {
                error = "project view not found (RTSys process?)";
                return false;
            }

            string oldEntry;
            if (!TryResolveEntryName(obj, name, out oldEntry))
            {
                error = "program not found in project view: " + name;
                return false;
            }

            string ext = Path.GetExtension(oldEntry);
            string dirPart = Path.GetDirectoryName(oldEntry);
            string newEntry = newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? newName
                : newName + ext;
            if (!string.IsNullOrEmpty(dirPart))
                newEntry = Path.Combine(dirPart, newEntry);

            string dir = ProgramWorkspace.ProjectDir;
            if (ProgramWorkspace.ResolveInside(dir, newEntry) == null)
            {
                error = "invalid new program path: " + newEntry;
                return false;
            }

            IntPtr dummy;
            if (TryFindItemIn(obj, newEntry, out dummy))
            {
                error = "target program already exists in project: " + newEntry;
                return false;
            }
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, newEntry)))
            {
                error = "target file already exists: " + newEntry;
                return false;
            }

            if (!EnsureNativeModel(out error)) return false;

            bool ok = false;
            string nativeError = null;
            CStringHandle oldStr = default(CStringHandle), newStr = default(CStringHandle);
            try
            {
                oldStr = CreateCString(oldEntry, out nativeError);
                if (oldStr.Ptr == IntPtr.Zero) { error = nativeError; return false; }
                newStr = CreateCString(newEntry, out nativeError);
                if (newStr.Ptr == IntPtr.Zero) { error = nativeError; return false; }

                int rc = 0;
                bool invoked = UiInvoke(() =>
                {
                    rc = _renameNative(_modelGlobal, oldStr.Ptr, newStr.Ptr);
                    if (rc != 0) _rebuildView(_cfileView);
                });
                if (!invoked) { error = "failed to invoke host UI thread"; return false; }
                if (rc == 0) { error = "host rename rejected (target may already exist)"; return false; }
                ok = true;
            }
            catch (Exception ex)
            {
                error = "native rename failed: " + ex.Message;
                PlugInLog.LogException("RTSysProjectModel.TryRename", ex);
                return false;
            }
            finally
            {
                FreeCString(ref newStr);
                FreeCString(ref oldStr);
            }

            for (int i = 0; i < 20; i++)
            {
                if (TryFindItemIn(obj, newEntry, out dummy)) return ok;
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, newEntry))) return ok;
                Thread.Sleep(50);
            }
            error = "project model was not updated after rename";
            return false;
        }

        /// <summary>
        /// 把已存在的文件加入工程模型（内部调用宿主“增加到工程”的实现），随后刷新工程视图树。
        /// fileName 需带扩展名（相对项目目录）。
        /// </summary>
        public static bool TryAddFileEntry(string fileName, out string error)
        {
            error = null;
            IntPtr obj, tree;
            if (!TryGetProjectView(out obj, out tree))
            {
                error = "project view not found (RTSys process?)";
                return false;
            }

            string dir = ProgramWorkspace.ProjectDir;
            if (string.IsNullOrEmpty(dir))
            {
                error = "project directory not available";
                return false;
            }
            if (!File.Exists(Path.Combine(dir, fileName)))
            {
                error = "file not found in project directory: " + fileName;
                return false;
            }
            IntPtr dummy;
            if (TryFindItemIn(obj, fileName, out dummy))
            {
                error = "file already exists in project model: " + fileName;
                return false;
            }

            if (!EnsureNativeModel(out error)) return false;

            string nativeError = null;
            CStringHandle pathStr = default(CStringHandle);
            bool ok = false;
            try
            {
                pathStr = CreateCString(fileName, out nativeError);
                if (pathStr.Ptr == IntPtr.Zero) { error = nativeError; return false; }

                int rc = 0;
                bool invoked = UiInvoke(() =>
                {
                    rc = _addNative(_modelGlobal, pathStr.Ptr);
                    if (rc != 0) _rebuildView(_cfileView);
                });
                if (!invoked) { error = "failed to invoke host UI thread"; return false; }
                if (rc == 0) { error = "host add-to-project rejected the file"; return false; }
                ok = true;
            }
            catch (Exception ex)
            {
                error = "native add failed: " + ex.Message;
                PlugInLog.LogException("RTSysProjectModel.TryAddFileEntry", ex);
                return false;
            }
            finally
            {
                FreeCString(ref pathStr);
            }

            for (int i = 0; i < 20; i++)
            {
                if (TryFindItemIn(obj, fileName, out dummy)) return ok;
                Thread.Sleep(50);
            }
            error = "project model was not updated after add";
            return false;
        }

        /// <summary>恢复主窗口 WndProc（插件卸载前调用）。</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                try
                {
                    // 只恢复实际被挂钩的那个窗口，避免窗口重建后把旧 WndProc 写到新窗口。
                    if (_hooked && _hookedHwnd != IntPtr.Zero && IsWindow(_hookedHwnd))
                        SetWindowLong(_hookedHwnd, GWLP_WNDPROC, _origWndProc);
                }
                catch { }
                _hooked = false;
                _hookedHwnd = IntPtr.Zero;
                _origWndProc = IntPtr.Zero;
                _wndProcDelegate = null;
            }
        }

        #endregion

        #region ---- 原生模型调用（重命名 / 加入工程） ----

        // 以下地址/RVA 均针对 RTSys.exe 1.3.02（SHA-256 见 ExpectedExeHash）。
        // 版本不匹配时 EnsureNativeModel 会直接失败，调用方回退到文件级操作。
        private const uint OFF_RENAME = 0x183E40;     // sub_583E40: 重命名模型项（含磁盘文件）
        private const uint OFF_ADD = 0x183950;        // sub_583950: 把文件加入工程模型
        private const uint OFF_REBUILD = 0x06B290;    // sub_46B290: 从模型重建工程视图树
        private const uint OFF_MODEL_GLOBAL = 0x426868; // dword_826868: 工程模型单例
        private const uint OFF_CSTR_CTOR_SLOT = 0x1C4150; // mfc140u!CStringT<wchar_t>::ctor(LPCWSTR) IAT
        private const uint OFF_CSTR_DTOR_SLOT = 0x1C415C; // mfc140u!CStringT<wchar_t>::dtor IAT
        private const string ExpectedExeHash = "3849aee2c5a27a9b3d576604477307078e71738ebd968fd3c01a808aa7dbef8a";

        // 只比较前 5 字节：后续 SEH 偏移是重定位项，加载后会被改写。
        private static readonly byte[] _renamePrologue = { 0x55, 0x8B, 0xEC, 0x6A, 0xFF };
        private static readonly byte[] _addPrologue = { 0x55, 0x8B, 0xEC, 0x6A, 0xFF };

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void CStringCtorFn(IntPtr thisPtr, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void CStringDtorFn(IntPtr thisPtr);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int RenameModelFn(IntPtr thisPtr, IntPtr oldName, IntPtr newName);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int AddFileModelFn(IntPtr thisPtr, IntPtr fileName);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void RebuildViewFn(IntPtr fileView);

        private struct CStringHandle { public IntPtr Ptr; }

        private static readonly object _nativeLock = new object();
        private static bool _nativeChecked;
        private static bool _nativeOk;
        private static string _nativeError;
        private static IntPtr _moduleBase;
        private static IntPtr _modelGlobal;
        private static CStringCtorFn _cstrCtor;
        private static CStringDtorFn _cstrDtor;
        private static RenameModelFn _renameNative;
        private static AddFileModelFn _addNative;
        private static RebuildViewFn _rebuildView;

        private static bool EnsureNativeModel(out string error)
        {
            lock (_nativeLock)
            {
                error = _nativeError;
                if (_nativeChecked) return _nativeOk;

                _nativeChecked = true;
                _nativeOk = false;
                try
                {
                    var proc = Process.GetCurrentProcess();
                    string exePath = proc.MainModule.FileName;
                    string hash = ComputeFileHash(exePath);
                    if (!string.Equals(hash, ExpectedExeHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _nativeError = "RTSys.exe 版本不匹配（SHA-256），已禁用内部模型调用";
                        PlugInLog.Log("RTSysProjectModel: " + _nativeError);
                        error = _nativeError;
                        return false;
                    }
                    _moduleBase = proc.MainModule.BaseAddress;
                    if (!BytesMatch(OFF_RENAME, _renamePrologue) || !BytesMatch(OFF_ADD, _addPrologue))
                    {
                        _nativeError = "RTSys.exe 内部函数签名不匹配，已禁用内部模型调用";
                        PlugInLog.Log("RTSysProjectModel: " + _nativeError);
                        error = _nativeError;
                        return false;
                    }

                    IntPtr ctor = Marshal.ReadIntPtr(_moduleBase + (int)OFF_CSTR_CTOR_SLOT);
                    IntPtr dtor = Marshal.ReadIntPtr(_moduleBase + (int)OFF_CSTR_DTOR_SLOT);
                    if (!IsExecutable(ctor) || !IsExecutable(dtor))
                    {
                        _nativeError = "CString IAT 入口无效，已禁用内部模型调用";
                        PlugInLog.Log("RTSysProjectModel: " + _nativeError);
                        error = _nativeError;
                        return false;
                    }

                    _cstrCtor = Marshal.GetDelegateForFunctionPointer<CStringCtorFn>(ctor);
                    _cstrDtor = Marshal.GetDelegateForFunctionPointer<CStringDtorFn>(dtor);
                    _modelGlobal = _moduleBase + (int)OFF_MODEL_GLOBAL;
                    _renameNative = Marshal.GetDelegateForFunctionPointer<RenameModelFn>(_moduleBase + (int)OFF_RENAME);
                    _addNative = Marshal.GetDelegateForFunctionPointer<AddFileModelFn>(_moduleBase + (int)OFF_ADD);
                    _rebuildView = Marshal.GetDelegateForFunctionPointer<RebuildViewFn>(_moduleBase + (int)OFF_REBUILD);

                    _nativeOk = true;
                    _nativeError = null;
                    PlugInLog.Log("RTSysProjectModel: 原生模型调用已启用 (base=0x" + _moduleBase.ToString("X") + ")");
                }
                catch (Exception ex)
                {
                    _nativeError = "原生模型调用初始化失败: " + ex.Message;
                    PlugInLog.LogException("RTSysProjectModel.EnsureNativeModel", ex);
                }
                error = _nativeError;
                return _nativeOk;
            }
        }

        private static CStringHandle CreateCString(string value, out string error)
        {
            error = null;
            IntPtr holder = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(holder, IntPtr.Zero);
            IntPtr source = Marshal.StringToHGlobalUni(value);
            try
            {
                _cstrCtor(holder, source);
                if (Marshal.ReadIntPtr(holder) == IntPtr.Zero)
                {
                    error = "CString 构造失败";
                    Marshal.FreeHGlobal(holder);
                    return default(CStringHandle);
                }
                return new CStringHandle { Ptr = holder };
            }
            catch (Exception ex)
            {
                error = "CString 构造异常: " + ex.Message;
                Marshal.FreeHGlobal(holder);
                return default(CStringHandle);
            }
            finally
            {
                Marshal.FreeHGlobal(source);
            }
        }

        private static void FreeCString(ref CStringHandle handle)
        {
            if (handle.Ptr == IntPtr.Zero) return;
            try { _cstrDtor(handle.Ptr); }
            catch { }
            finally { Marshal.FreeHGlobal(handle.Ptr); handle.Ptr = IntPtr.Zero; }
        }

        private static string ComputeFileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool BytesMatch(uint rva, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (Marshal.ReadByte(_moduleBase + (int)rva + i) != expected[i]) return false;
            }
            return true;
        }

        private static bool IsExecutable(IntPtr address)
        {
            if (address == IntPtr.Zero) return false;
            MEMORY_BASIC_INFORMATION info;
            if (VirtualQuery(address, out info, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == UIntPtr.Zero)
                return false;
            const uint PAGE_EXECUTE = 0x10, PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_WRITECOPY = 0x80;
            return info.State == MEM_COMMIT
                   && (info.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
        }

        /// <summary>在模型数组中按名称查找实际条目名（含扩展名）。</summary>
        private static bool TryResolveEntryName(IntPtr obj, string name, out string entryName)
        {
            entryName = null;
            int arr = ReadInt32(obj + OFF_ARRAY);
            int cnt = ReadInt32(obj + OFF_COUNT);
            if (arr == 0 || cnt <= 0) return false;

            string target = (name ?? "").Trim();
            bool hasExt = target.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)
                          || target.EndsWith(".hmi", StringComparison.OrdinalIgnoreCase)
                          || target.EndsWith(".plc", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < cnt; i++)
            {
                string n = ReadEntryName(arr, i);
                if (string.IsNullOrEmpty(n)) continue;
                if (string.Equals(n, target, StringComparison.OrdinalIgnoreCase)
                    || (!hasExt && string.Equals(n, target + ".bas", StringComparison.OrdinalIgnoreCase)))
                {
                    entryName = n;
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region ---- 模型定位 ----

        private static bool TryGetProjectView(out IntPtr obj, out IntPtr tree)
        {
            obj = IntPtr.Zero; tree = IntPtr.Zero;
            lock (_lock)
            {
                // 缓存 3 秒，并验证对象仍有效
                if (_cfileView != IntPtr.Zero && (DateTime.UtcNow - _cacheUtc).TotalSeconds < CacheSeconds)
                {
                    if (ReadInt32(_cfileView + OFF_TREE_HWND) == _treeHwnd.ToInt32())
                    {
                        obj = _cfileView; tree = _treeHwnd; return true;
                    }
                }

                IntPtr main = Process.GetCurrentProcess().MainWindowHandle;
                if (main == IntPtr.Zero) return false;
                _mainHwnd = main;

                var trees = new List<IntPtr>();
                EnumChildWindows(main, (h, l) =>
                {
                    var cls = new StringBuilder(64);
                    GetClassName(h, cls, cls.Capacity);
                    if (cls.ToString() == "SysTreeView32" && IsWindowVisible(h)) trees.Add(h);
                    return true;
                }, IntPtr.Zero);

                _candidates.Clear();
                foreach (var th in trees)
                {
                    foreach (var c in ScanForFileViews(th))
                    {
                        c.Tree = th;
                        _candidates.Add(c);
                    }
                }
                if (_candidates.Count == 0) return false;

                // 旧实例可能残留在内存中；选择条目最多的那个（当前活动的工程模型）。
                FileViewCandidate best = _candidates[0];
                foreach (var c in _candidates)
                    if (c.Count > best.Count) best = c;

                _cfileView = best.Obj;
                _treeHwnd = best.Tree;
                _treeObject = new IntPtr(best.Obj.ToInt32() + OFF_TREE_OBJECT);
                _array = best.Array;
                _count = best.Count;
                _cacheUtc = DateTime.UtcNow;
                obj = best.Obj; tree = best.Tree;
                PlugInLog.Log("TryGetProjectView: candidates=" + _candidates.Count
                              + " bestCount=" + best.Count
                              + " obj=0x" + best.Obj.ToString("X")
                              + " tree=0x" + best.Tree.ToString("X"));
                return true;
            }
        }

        private static List<FileViewCandidate> ScanForFileViews(IntPtr treeHwnd)
        {
            var result = new List<FileViewCandidate>();
            byte[] pattern = BitConverter.GetBytes(treeHwnd.ToInt32());
            long address = 0;
            var mbi = new MEMORY_BASIC_INFORMATION();
            IntPtr process = GetCurrentProcess();
            int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

            while (address < 0x7FFFFFFF)
            {
                if (VirtualQuery(new IntPtr(address), out mbi, (UIntPtr)mbiSize) == UIntPtr.Zero) break;
                long baseAddr = mbi.BaseAddress.ToInt64();
                uint size = mbi.RegionSize;
                if (size == 0) break;

                bool readable = mbi.State == MEM_COMMIT
                                && (mbi.Protect & PAGE_NOACCESS) == 0
                                && (mbi.Protect & PAGE_GUARD) == 0
                                && mbi.Protect != 0;
                if (readable && mbi.Type == MEM_PRIVATE && size <= MaxScanRegionBytes)
                {
                    var buffer = new byte[size];
                    IntPtr read;
                    if (ReadProcessMemory(process, mbi.BaseAddress, buffer, new IntPtr(size), out read)
                        && read.ToInt64() == size)
                    {
                        int pos = 0;
                        while (true)
                        {
                            int hit = IndexOf(buffer, pattern, pos);
                            if (hit < 0) break;
                            long candidate = baseAddr + hit - OFF_TREE_HWND;
                            if (candidate > 0x10000)
                            {
                                int arr = ReadInt32(new IntPtr(candidate + OFF_ARRAY));
                                int cnt = ReadInt32(new IntPtr(candidate + OFF_COUNT));
                                if (arr != 0 && cnt > 0 && cnt <= 100 && LooksLikeFileList(arr, cnt))
                                {
                                    result.Add(new FileViewCandidate
                                    {
                                        Obj = new IntPtr(candidate),
                                        Tree = treeHwnd,
                                        Array = arr,
                                        Count = cnt
                                    });
                                }
                            }
                            pos = hit + 1;
                        }
                    }
                }
                long next = baseAddr + size;
                if (next <= address) break;
                address = next;
            }
            return result;
        }

        private static bool LooksLikeFileList(int arr, int count)
        {
            int fileLike = 0;
            for (int i = 0; i < count; i++)
            {
                string n = ReadEntryName(arr, i);
                if (string.IsNullOrEmpty(n)) continue;
                if (n.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".hmi", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".plc", StringComparison.OrdinalIgnoreCase))
                    fileLike++;
            }
            return fileLike > 0;
        }

        private static bool TryFindItem(IntPtr preferred, string name, out IntPtr obj, out IntPtr item)
        {
            item = IntPtr.Zero;
            obj = preferred;
            if (preferred != IntPtr.Zero && TryFindItemIn(preferred, name, out item))
                return true;

            // 内存中可能残留旧的 CFileView 实例；逐个候选对象查找目标文件。
            // 快照后再遍历：_candidates 由 TryGetProjectView 在 _lock 内重建。
            List<FileViewCandidate> candidates;
            lock (_lock) { candidates = new List<FileViewCandidate>(_candidates); }
            foreach (var c in candidates)
            {
                if (c.Obj == preferred) continue;
                if (TryFindItemIn(c.Obj, name, out item))
                {
                    obj = c.Obj;
                    return true;
                }
            }
            obj = IntPtr.Zero;
            return false;
        }

        private static bool TryFindItemIn(IntPtr obj, string name, out IntPtr item)
        {
            item = IntPtr.Zero;
            int arr = ReadInt32(obj + OFF_ARRAY);
            int cnt = ReadInt32(obj + OFF_COUNT);
            if (arr == 0 || cnt <= 0) return false;

            string target = (name ?? "").Trim();
            bool hasExt = target.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)
                          || target.EndsWith(".hmi", StringComparison.OrdinalIgnoreCase)
                          || target.EndsWith(".plc", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < cnt; i++)
            {
                string n = ReadEntryName(arr, i);
                if (string.IsNullOrEmpty(n)) continue;
                if (string.Equals(n, target, StringComparison.OrdinalIgnoreCase)
                    || (!hasExt && string.Equals(n, target + ".bas", StringComparison.OrdinalIgnoreCase)))
                {
                    int handle = ReadEntryInt(arr, i, ENTRY_ITEM);
                    if (handle != 0) { item = new IntPtr(handle); _array = arr; _count = cnt; return true; }
                }
            }
            return false;
        }

        private static int ReadEntryInt(int arr, int index, int offset)
        {
            return ReadInt32(new IntPtr(arr + index * ENTRY_SIZE + offset));
        }

        private static string ReadEntryName(int arr, int index)
        {
            int namePtr = ReadEntryInt(arr, index, ENTRY_NAME);
            if (namePtr == 0) return null;
            return ReadWide(new IntPtr(namePtr), 260);
        }

        private static int ReadInt32(long address)
        {
            return ReadInt32(new IntPtr(address));
        }

        private static int ReadInt32(IntPtr address)
        {
            var buf = new byte[4];
            IntPtr read;
            if (ReadProcessMemory(GetCurrentProcess(), address, buf, new IntPtr(4), out read)
                && read.ToInt32() == 4)
                return BitConverter.ToInt32(buf, 0);
            return 0;
        }

        private static string ReadWide(IntPtr address, int maxChars)
        {
            var buf = new byte[maxChars * 2];
            IntPtr read;
            if (!ReadProcessMemory(GetCurrentProcess(), address, buf, new IntPtr(buf.Length), out read))
                return null;
            int chars = read.ToInt32() / 2;
            var sb = new StringBuilder(chars);
            for (int i = 0; i < chars; i++)
            {
                char c = (char)(buf[i * 2] | (buf[i * 2 + 1] << 8));
                if (c == 0) break;
                sb.Append(c);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            if (needle.Length == 0) return -1;
            int end = haystack.Length - needle.Length;
            for (int i = start; i <= end; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        #endregion

        #region ---- BCG 树方法 / UI 线程 ----

        private static bool SelectItem(IntPtr obj, IntPtr item)
        {
            try
            {
                if (_selectItem == null)
                {
                    if (_bcgModule == IntPtr.Zero)
                        _bcgModule = GetModuleHandle("BCGCBPRO3410u143.dll");
                    if (_bcgModule == IntPtr.Zero)
                    {
                        PlugInLog.Log("SelectItem: BCG module not loaded");
                        return false;
                    }
                    IntPtr fn = GetProcAddress(_bcgModule,
                        "?SelectItem@CBCGPTreeCtrlEx@@QAEHPAU_TREEITEM@@@Z");
                    if (fn == IntPtr.Zero)
                    {
                        PlugInLog.Log("SelectItem: export not found");
                        return false;
                    }
                    _selectItem = Marshal.GetDelegateForFunctionPointer<SelectItemFn>(fn);
                }
                return _selectItem(new IntPtr(obj.ToInt32() + OFF_TREE_OBJECT), item) != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 在宿主 UI 线程执行 action。返回 false 表示无法安全封送（不降级到后台线程直接改 MFC 模型）。
        /// </summary>
        private static bool UiInvoke(Action action)
        {
            try
            {
                if (_mainHwnd == IntPtr.Zero)
                    _mainHwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (_mainHwnd == IntPtr.Zero) return false;

                uint pid;
                uint uiThread = GetWindowThreadProcessId(_mainHwnd, out pid);
                if (uiThread == GetCurrentThreadId()) { action(); return true; }

                if (!EnsureHook()) return false;

                var handle = GCHandle.Alloc(action);
                IntPtr hp = GCHandle.ToIntPtr(handle);
                lock (_pendingLock) { _pendingInvokes.Add(hp); }
                IntPtr result;
                // 返回值为 0 表示超时/失败；消息处理完成后由 WndProc 释放 GCHandle。
                IntPtr ok = SendMessageTimeout(_hookedHwnd, _invokeMsg, hp,
                    IntPtr.Zero, SMTO_ABORTIFHUNG, 5000, out result);
                if (ok != IntPtr.Zero)
                {
                    // WndProc 已释放 GCHandle；这里不再重复 Free，避免 GC 句柄表损坏。
                    return true;
                }

                // 超时：消息可能仍在队列中，交由 WndProc 释放；若窗口已销毁则句柄泄漏（可接受，避免悬空）。
                PlugInLog.Log("RTSysProjectModel.UiInvoke: SendMessageTimeout failed (error=" + Marshal.GetLastWin32Error() + ")");
                return false;
            }
            catch { return false; }
        }

        private static bool EnsureHook()
        {
            lock (_lock)
            {
                if (_mainHwnd == IntPtr.Zero) return false;
                if (_invokeMsg == 0)
                {
                    _invokeMsg = RegisterWindowMessage(InvokeMsgName);
                    if (_invokeMsg == 0)
                    {
                        PlugInLog.Log("RTSysProjectModel.EnsureHook: RegisterWindowMessage failed");
                        return false;
                    }
                }
                if (_hooked && _hookedHwnd == _mainHwnd) return true;

                // 窗口重建：先在旧窗口上恢复原 WndProc（若仍有效），再挂到新窗口。
                if (_hooked && _hookedHwnd != IntPtr.Zero && IsWindow(_hookedHwnd))
                {
                    try { SetWindowLong(_hookedHwnd, GWLP_WNDPROC, _origWndProc); } catch { }
                }
                _hooked = false;
                _hookedHwnd = IntPtr.Zero;

                _wndProcDelegate = WndProc;
                IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
                SetLastError(0);
                IntPtr prev = SetWindowLong(_mainHwnd, GWLP_WNDPROC, ptr);
                int err = Marshal.GetLastWin32Error();
                if (prev == IntPtr.Zero && err != 0)
                {
                    PlugInLog.Log("RTSysProjectModel.EnsureHook: SetWindowLong failed, error=" + err);
                    _wndProcDelegate = null;
                    return false;
                }
                _origWndProc = prev;
                _hooked = true;
                _hookedHwnd = _mainHwnd;
                return true;
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_invokeMsg != 0 && msg == _invokeMsg)
            {
                if (wParam == IntPtr.Zero) return IntPtr.Zero;
                lock (_pendingLock)
                {
                    if (!_pendingInvokes.Contains(wParam))
                        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
                    _pendingInvokes.Remove(wParam);
                }
                GCHandle handle = default(GCHandle);
                try
                {
                    handle = GCHandle.FromIntPtr(wParam);
                    var action = handle.Target as Action;
                    action?.Invoke();
                }
                catch { }
                finally
                {
                    if (handle.IsAllocated) handle.Free();
                }
                return IntPtr.Zero;
            }
            return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
        }

        private static IntPtr FindConfirmationDialog()
        {
            uint pid = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p != pid || !IsWindowVisible(h)) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "#32770") return true;
                var text = new StringBuilder(512);
                GetWindowText(h, text, text.Capacity);
                string t = text.ToString();
                if (t.IndexOf("sure", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("删除", StringComparison.Ordinal) >= 0
                    || t.IndexOf("确定", StringComparison.Ordinal) >= 0
                    || t.IndexOf("确认", StringComparison.Ordinal) >= 0)
                {
                    found = h;
                    return false;
                }

                // BCGPMessageBox 的提示文字通常在子 Static 控件里，标题只是应用名。
                bool childMatch = false;
                EnumChildWindows(h, (c, l2) =>
                {
                    if (childMatch) return false;
                    var cls2 = new StringBuilder(64);
                    GetClassName(c, cls2, cls2.Capacity);
                    if (cls2.ToString() != "Static") return true;
                    var txt = new StringBuilder(512);
                    GetWindowText(c, txt, txt.Capacity);
                    string s = txt.ToString();
                    if (s.IndexOf("sure", StringComparison.OrdinalIgnoreCase) >= 0
                        || s.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0
                        || s.IndexOf("删除", StringComparison.Ordinal) >= 0
                        || s.IndexOf("确定", StringComparison.Ordinal) >= 0
                        || s.IndexOf("确认", StringComparison.Ordinal) >= 0)
                        childMatch = true;
                    return true;
                }, IntPtr.Zero);
                if (childMatch)
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        #endregion

        internal class ProjectItemInfo
        {
            public string Name;
            public int Type;
            public IntPtr Item;
        }
    }
}
