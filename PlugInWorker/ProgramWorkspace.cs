using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    /// <summary>
    /// 本地程序工作区（离线缓存）。
    ///
    /// 将 RTBasic 程序源码以文件形式保存在 %APPDATA%\RTSysAI\programs\（可通过 WorkspaceDir 覆盖），
    /// 使“列程序/读源码/写源码/补丁/搜索”等程序编辑操作可在未连接控制器时离线进行。
    ///
    /// 设计原则（与任务要求对齐）：
    ///   - 只负责本地文件的读写与同步状态记录，绝不替用户将内容下载/上传到控制器；
    ///   - 写入本地缓存永远不会删除或覆盖控制器上的内容；
    ///   - 下载到控制器需要显式意图（控制器已连接 + 执行下载动作），绝不会作为无关工具的副作用静默发生；
    ///   - 每个程序维护同步状态（isDirty / origin / lastSyncedUtc），供 AI 判断“本地已改、尚未下载”的待上传列表。
    /// </summary>
    public static class ProgramWorkspace
    {
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private static readonly object _lock = new object();

        // ---- .zpj / 程序文件格式常量（集中定义）----
        internal const int ProjectCodePage = 936;      // RTSys 项目 .bas 的 GBK 代码页
        internal const string FileListSection = "FileList";

        // 默认工作区目录：%APPDATA%\RTSysAI\programs\；可通过 WorkspaceDir 覆盖（主要用于测试隔离）。
        private static string _workspaceDir;

        // 当前 RTSys 打开的项目目录（只读来源）。
        // 插件与 RTSys 同进程，可直接从宿主主窗口标题解析出项目目录：
        //   "TestPro.zpj - RTSys V1.3.02 - C:\...\项目\ - TaskMain.bas"
        // 也可通过 config.json 的 projectDir 显式指定。
        private static string _projectDir;
        private static DateTime _projectDirProbeUtc = DateTime.MinValue;
        private static readonly object _projectLock = new object();

        public static string ProjectDir
        {
            get
            {
                lock (_projectLock)
                {
                    if (_projectDirProbeUtc != DateTime.MinValue &&
                        (DateTime.UtcNow - _projectDirProbeUtc).TotalSeconds < 5)
                        return _projectDir;
                    _projectDir = DetectProjectDir();
                    _projectDirProbeUtc = DateTime.UtcNow;
                    return _projectDir;
                }
            }
        }

        public static void ResetProjectDir()
        {
            lock (_projectLock) { _projectDir = null; _projectDirProbeUtc = DateTime.MinValue; }
        }

        private static string DetectProjectDir()
        {
            // 1) 显式配置优先：config.json 的 projectDir
            try
            {
                var cfgPath = Path.Combine(AiService.DataDir, "config.json");
                if (File.Exists(cfgPath))
                {
                    var cfg = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath));
                    object val;
                    if (cfg != null && cfg.TryGetValue("projectDir", out val))
                    {
                        var dir = val?.ToString();
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                }
            }
            catch { }

            // 2) 从宿主主窗口标题解析（同进程可直接读取）
            try
            {
                var title = System.Diagnostics.Process.GetCurrentProcess().MainWindowTitle;
                if (!string.IsNullOrEmpty(title))
                {
                    foreach (var seg in title.Split(new[] { " - " }, StringSplitOptions.None))
                    {
                        var t = seg.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (t.Length >= 3 && t[1] == ':' && Directory.Exists(t))
                            return t;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>读取文本文件：优先 UTF-8（含 BOM），失败回退 GBK（RTSys 项目 .bas 常用编码）。</summary>
        private static string ReadTextAuto(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { }
            try { return Encoding.GetEncoding(ProjectCodePage).GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }

        /// <summary>从窗口标题取当前项目文件名（如 TestPro.zpj），取不到返回 null。</summary>
        private static string DetectProjectFileNameFromTitle()
        {
            try
            {
                var title = System.Diagnostics.Process.GetCurrentProcess().MainWindowTitle;
                if (!string.IsNullOrEmpty(title))
                {
                    foreach (var seg in title.Split(new[] { " - " }, StringSplitOptions.None))
                    {
                        var s = seg.Trim();
                        if (s.EndsWith(".zpj", StringComparison.OrdinalIgnoreCase))
                            return s;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 解析 .zpj 的 [FileList] 段，返回 (相对文件名, FileType)。
        /// 这是 RTSys 项目的权威文件清单；没有 .zpj 时返回空列表。
        /// </summary>
        private static List<Tuple<string, int>> ReadZpjFileList(string projectDir)
        {
            var result = new List<Tuple<string, int>>();
            try
            {
                string zpjPath = null;
                var zpjName = DetectProjectFileNameFromTitle();
                if (!string.IsNullOrEmpty(zpjName))
                {
                    var p = ResolveInside(projectDir, zpjName);
                    if (p != null && File.Exists(p)) zpjPath = p;
                }
                if (zpjPath == null)
                {
                    var files = Directory.GetFiles(projectDir, "*.zpj");
                    if (files.Length == 0) return result;
                    zpjPath = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
                }

                var lines = ReadTextAuto(zpjPath).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                string section = null;
                var names = new Dictionary<int, string>();
                var types = new Dictionary<int, int>();
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("'")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2);
                        continue;
                    }
                    if (!string.Equals(section, FileListSection, StringComparison.OrdinalIgnoreCase)) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();
                    if (key.StartsWith("FileType", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx, t;
                        if (int.TryParse(key.Substring(8), out idx) && int.TryParse(val, out t)) types[idx] = t;
                    }
                    else if (key.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx;
                        if (int.TryParse(key.Substring(4), out idx)) names[idx] = val;
                    }
                }
                foreach (var kv in names.OrderBy(k => k.Key))
                {
                    if (ResolveInside(projectDir, kv.Value) == null) continue;
                    int t;
                    types.TryGetValue(kv.Key, out t);
                    result.Add(Tuple.Create(kv.Value, t));
                }
            }
            catch { }
            return result;
        }

        /// <summary>列出当前项目 .zpj 中声明的 .bas 程序（只读）；无 .zpj 时回退扫描目录。</summary>
        public static List<ProgramInfo> ListProjectPrograms()
        {
            var result = new List<ProgramInfo>();
            var dir = ProjectDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;
            lock (_lock)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in ReadZpjFileList(dir))
                {
                    var rel = entry.Item1;
                    var type = entry.Item2;
                    if (type != 0 && type != 1) continue; // 1=basic 程序；5=HMI 等跳过
                    if (!rel.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)) continue;
                    var stem = Path.GetFileNameWithoutExtension(rel);
                    if (string.IsNullOrEmpty(stem) || !seen.Add(stem)) continue;
                    var resolved = ResolveInside(dir, rel);
                    if (resolved == null || !File.Exists(resolved)) continue;
                    result.Add(new ProgramInfo { name = stem, origin = "project", isDirty = false, lastSyncedUtc = null });
                }

                // 兜底：没有 .zpj 或未列出任何程序时，扫描目录下所有 .bas
                if (result.Count == 0)
                {
                    foreach (var f in Directory.GetFiles(dir, "*.bas"))
                    {
                        result.Add(new ProgramInfo
                        {
                            name = Path.GetFileNameWithoutExtension(f),
                            origin = "project",
                            isDirty = false,
                            lastSyncedUtc = null
                        });
                    }
                }
            }
            return result.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>列出当前项目 .zpj 中声明的 .hmi 画面（FileType=5，只读）；无声明时回退扫描目录。</summary>
        public static List<string> ListProjectHmiFiles()
        {
            var result = new List<string>();
            var dir = ProjectDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;
            lock (_lock)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in ReadZpjFileList(dir))
                {
                    if (entry.Item2 != 5) continue;
                    if (!entry.Item1.EndsWith(".hmi", StringComparison.OrdinalIgnoreCase)) continue;
                    var stem = Path.GetFileNameWithoutExtension(entry.Item1);
                    if (string.IsNullOrEmpty(stem) || !seen.Add(stem)) continue;
                    var resolved = ResolveInside(dir, entry.Item1);
                    if (resolved == null || !File.Exists(resolved)) continue;
                    result.Add(stem);
                }

                // 兜底：没有 .zpj 或未列出任何 HMI 时，扫描目录下所有 .hmi
                if (result.Count == 0)
                {
                    foreach (var f in Directory.GetFiles(dir, "*.hmi"))
                    {
                        var stem = Path.GetFileNameWithoutExtension(f);
                        if (!string.IsNullOrEmpty(stem) && seen.Add(stem))
                            result.Add(stem);
                    }
                }
            }
            return result.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>读取项目中的 .bas 源码；优先按 .zpj 清单定位，找不到再按根目录扫描。按 GBK 兜底解码。</summary>
        public static string ReadProjectSource(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var dir = ProjectDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            lock (_lock)
            {
                foreach (var entry in ReadZpjFileList(dir))
                {
                    if (!entry.Item1.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(Path.GetFileNameWithoutExtension(entry.Item1), name, StringComparison.OrdinalIgnoreCase))
                    {
                        var p = ResolveInside(dir, entry.Item1);
                        if (p != null && File.Exists(p)) return ReadTextAuto(p);
                    }
                }

                var exact = ResolveInside(dir, SanitizeName(name) + ".bas");
                if (exact != null && File.Exists(exact)) return ReadTextAuto(exact);
                foreach (var f in Directory.GetFiles(dir, "*.bas"))
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                        return ReadTextAuto(f);
                }
            }
            return null;
        }

        /// <summary>当前是否有打开的 RTSys 项目（ProjectDir 存在且目录内有 .zpj）。</summary>
        public static bool IsProjectAvailable
        {
            get
            {
                lock (_lock)
                {
                    try
                    {
                        var dir = ProjectDir;
                        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                               Directory.GetFiles(dir, "*.zpj").Length > 0;
                    }
                    catch { return false; }
                }
            }
        }

        /// <summary>判断项目中是否存在指定的 .bas 程序。</summary>
        public static bool ProjectProgramExists(string name)
        {
            if (ValidateProgramName(name) != null) return false;
            var dir = ProjectDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            try
            {
                if (Directory.GetFiles(dir, "*.zpj").Length == 0) return false;
            }
            catch { return false; }
            lock (_lock)
            {
                return FindProjectProgramPath(dir, name) != null;
            }
        }

        /// <summary>在项目目录新建 .bas（GBK）并加入 .zpj 的 [FileList]；成功返回 null。</summary>
        public static string CreateProjectProgram(string name, string source)
        {
            var error = ValidateProgramName(name);
            if (error != null) return error;
            var dir = ProjectDir;
            if (!IsProjectAvailable) return NoProjectError;

            lock (_lock)
            {
                try
                {
                    string loadError;
                    var model = LoadZpjModel(dir, out loadError);
                    if (model == null) return loadError;

                    if (FindProjectProgramPath(dir, name) != null || FindEntry(model.Entries, name) != null)
                        return "Program already exists: " + name;

                    var path = ResolveInside(dir, name + ".bas");
                    if (path == null) return "Invalid program path: " + name;
                    WriteGbkFile(path, source);
                    try
                    {
                        model.Entries.Add(new ZpjEntry { FileName = name + ".bas", FileType = 1 });
                        model.Save();
                    }
                    catch (Exception ex)
                    {
                        try { File.Delete(path); } catch { }
                        return "Failed to update project file: " + ex.Message;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    return "Failed to create program: " + ex.Message;
                }
            }
        }

        /// <summary>覆盖项目中已存在的 .bas（GBK）；不存在则返回错误，不隐式创建。</summary>
        public static string WriteProjectProgram(string name, string source)
        {
            var error = ValidateProgramName(name);
            if (error != null) return error;
            var dir = ProjectDir;
            if (!IsProjectAvailable) return NoProjectError;

            lock (_lock)
            {
                try
                {
                    var path = FindProjectProgramPath(dir, name);
                    if (path == null) return "Program not found: " + name;
                    WriteGbkFile(path, source);
                    return null;
                }
                catch (Exception ex)
                {
                    return "Failed to write program: " + ex.Message;
                }
            }
        }

        /// <summary>重命名项目 .bas 并同步 [FileList] 中的文件名；成功返回 null。</summary>
        public static string RenameProjectProgram(string oldName, string newName)
        {
            var error = ValidateProgramName(oldName) ?? ValidateProgramName(newName);
            if (error != null) return error;
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                return "New name is the same as the old name";
            var dir = ProjectDir;
            if (!IsProjectAvailable) return NoProjectError;

            lock (_lock)
            {
                try
                {
                    string loadError;
                    var model = LoadZpjModel(dir, out loadError);
                    if (model == null) return loadError;

                    var oldPath = FindProjectProgramPath(dir, oldName);
                    if (oldPath == null) return "Program not found: " + oldName;
                    var entry = FindEntry(model.Entries, oldName);
                    if (FindProjectProgramPath(dir, newName) != null || FindEntry(model.Entries, newName) != null)
                        return "Program already exists: " + newName;

                    var oldRel = entry != null ? entry.FileName : Path.GetFileName(oldPath);
                    var dirPart = Path.GetDirectoryName(oldRel);
                    var newRel = string.IsNullOrEmpty(dirPart)
                        ? newName + ".bas"
                        : Path.Combine(dirPart, newName + ".bas");
                    var newPath = ResolveInside(dir, newRel);
                    if (newPath == null) return "Invalid program path: " + newName;

                    File.Move(oldPath, newPath);
                    try
                    {
                        if (entry != null) entry.FileName = newRel;
                        model.Save();
                    }
                    catch (Exception ex)
                    {
                        try { File.Move(newPath, oldPath); } catch { }
                        return "Failed to update project file: " + ex.Message;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    return "Failed to rename program: " + ex.Message;
                }
            }
        }

        /// <summary>
        /// 只更新 .zpj 中的文件名（磁盘文件已由宿主 RTSys 重命名）；成功返回 null。
        /// </summary>
        public static string RenameProjectEntry(string oldName, string newName)
        {
            var error = ValidateProgramName(oldName) ?? ValidateProgramName(newName);
            if (error != null) return error;
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                return "New name is the same as the old name";
            var dir = ProjectDir;
            if (!IsProjectAvailable) return NoProjectError;

            lock (_lock)
            {
                try
                {
                    string loadError;
                    var model = LoadZpjModel(dir, out loadError);
                    if (model == null) return loadError;

                    var entry = FindEntry(model.Entries, oldName);
                    if (entry == null) return "Program not found in project file: " + oldName;
                    if (FindEntry(model.Entries, newName) != null)
                        return "Program already exists: " + newName;

                    var dirPart = Path.GetDirectoryName(entry.FileName);
                    var ext = Path.GetExtension(entry.FileName);
                    if (string.IsNullOrEmpty(ext)) ext = ".bas";
                    var newRel = string.IsNullOrEmpty(dirPart)
                        ? newName + ext
                        : Path.Combine(dirPart, newName + ext);
                    if (ResolveInside(dir, newRel) == null) return "Invalid program path: " + newName;
                    entry.FileName = newRel;
                    model.Save();
                    return null;
                }
                catch (Exception ex)
                {
                    return "Failed to update project file: " + ex.Message;
                }
            }
        }

        /// <summary>删除项目 .bas 并从 [FileList] 移除、重排编号；成功返回 null。</summary>
        public static string DeleteProjectProgram(string name)
        {
            var error = ValidateProgramName(name);
            if (error != null) return error;
            var dir = ProjectDir;
            if (!IsProjectAvailable) return NoProjectError;

            lock (_lock)
            {
                try
                {
                    string loadError;
                    var model = LoadZpjModel(dir, out loadError);
                    if (model == null) return loadError;

                    var entry = FindEntry(model.Entries, name);
                    var path = FindProjectProgramPath(dir, name);
                    if (path == null && entry == null) return "Program not found: " + name;

                    byte[] backup = null;
                    if (path != null && File.Exists(path)) backup = File.ReadAllBytes(path);
                    if (path != null && File.Exists(path)) File.Delete(path);
                    try
                    {
                        if (entry != null)
                        {
                            model.Entries.Remove(entry);
                            model.Save();
                        }
                    }
                    catch (Exception ex)
                    {
                        if (backup != null)
                        {
                            try { File.WriteAllBytes(path, backup); } catch { }
                        }
                        return "Failed to update project file: " + ex.Message;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    return "Failed to delete program: " + ex.Message;
                }
            }
        }

        /// <summary>复制项目 .bas 并把新文件加入 [FileList]；成功返回 null。</summary>
        public static string CopyProjectProgram(string sourceName, string destName)
        {
            var error = ValidateProgramName(sourceName) ?? ValidateProgramName(destName);
            if (error != null) return error;
            if (string.Equals(sourceName, destName, StringComparison.OrdinalIgnoreCase))
                return "Source and destination names are the same";
            var dir = ProjectDir;
            if (!IsProjectAvailable) return NoProjectError;

            lock (_lock)
            {
                try
                {
                    string loadError;
                    var model = LoadZpjModel(dir, out loadError);
                    if (model == null) return loadError;

                    var sourcePath = FindProjectProgramPath(dir, sourceName);
                    if (sourcePath == null) return "Program not found: " + sourceName;
                    if (FindProjectProgramPath(dir, destName) != null || FindEntry(model.Entries, destName) != null)
                        return "Program already exists: " + destName;

                    var sourceEntry = FindEntry(model.Entries, sourceName);
                    var sourceRel = sourceEntry != null ? sourceEntry.FileName : Path.GetFileName(sourcePath);
                    var dirPart = Path.GetDirectoryName(sourceRel);
                    var destRel = string.IsNullOrEmpty(dirPart)
                        ? destName + ".bas"
                        : Path.Combine(dirPart, destName + ".bas");
                    var destPath = ResolveInside(dir, destRel);
                    if (destPath == null) return "Invalid program path: " + destName;
                    if (File.Exists(destPath)) return "Program already exists: " + destName;

                    File.Copy(sourcePath, destPath);
                    try
                    {
                        model.Entries.Add(new ZpjEntry
                        {
                            FileName = destRel,
                            FileType = sourceEntry != null ? sourceEntry.FileType : 1
                        });
                        model.Save();
                    }
                    catch (Exception ex)
                    {
                        try { File.Delete(destPath); } catch { }
                        return "Failed to update project file: " + ex.Message;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    return "Failed to copy program: " + ex.Message;
                }
            }
        }

        public static string WorkspaceDir
        {
            get
            {
                if (string.IsNullOrEmpty(_workspaceDir))
                    _workspaceDir = Path.Combine(AiService.DataDir, "programs");
                return _workspaceDir;
            }
            set { _workspaceDir = value; }
        }

        /// <summary>
        /// 恢复默认工作区目录（%APPDATA%\RTSysAI\programs\）。供测试用完后重置。
        /// </summary>
        public static void ResetToDefaultDir() => _workspaceDir = null;

        public static void EnsureDirectory()
        {
            lock (_lock)
            {
                Directory.CreateDirectory(WorkspaceDir);
            }
        }

        /// <summary>
        /// 把相对路径安全地解析到 root 目录内；任何可能逃逸的输入都返回 null。
        /// 拒绝：绝对路径/UNC、"." ".." 段、冒号（备用数据流）、空值；
        /// Path.GetFullPath 后必须仍以 root + 分隔符 开头。
        /// </summary>
        internal static string ResolveInside(string root, string relative)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relative)) return null;
            var rel = relative.Trim().Trim('"');
            if (rel.Length == 0) return null;
            if (Path.IsPathRooted(rel)) return null;
            if (rel.IndexOf('\0') >= 0 || rel.IndexOf(':') >= 0) return null;

            var parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            foreach (var p in parts)
            {
                if (p == "." || p == "..") return null;
            }

            try
            {
                var fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(fullRoot, rel));
                var prefix = fullRoot + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                return full;
            }
            catch { return null; }
        }

        /// <summary>将程序名转换为安全的文件名（去除路径分隔符等非法字符）。</summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_empty";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar || invalid.Contains(c))
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            var result = sb.ToString().Trim('.', ' ');
            if (string.IsNullOrEmpty(result)) result = "_empty";
            return result;
        }

        public static string GetProgramPath(string name) => Path.Combine(WorkspaceDir, SanitizeName(name) + ".bas");
        public static string GetMetaPath(string name) => Path.Combine(WorkspaceDir, SanitizeName(name) + ".meta.json");

        /// <summary>本地已知程序的简要信息。</summary>
        public class ProgramInfo
        {
            public string name;
            public string origin;        // "local" | "controller"
            public bool isDirty;         // 本地内容与控制器是否不同（待下载/上传到控制器）
            public string lastSyncedUtc; // 最后一次与控制器同步的时间（ISO 8601）
        }

        /// <summary>单个程序的元数据（存为 .meta.json 侧边车文件）。</summary>
        public class ProgramMeta
        {
            public string name;
            public string origin;
            public bool isDirty;
            public string lastSyncedUtc;
        }

        /// <summary>列出本地已知的全部程序（离线即可）。</summary>
        public static List<ProgramInfo> ListLocalPrograms()
        {
            EnsureDirectory();
            var result = new List<ProgramInfo>();
            lock (_lock)
            {
                foreach (var metaFile in Directory.GetFiles(WorkspaceDir, "*.meta.json"))
                {
                    try
                    {
                        var meta = ReadMetaFile(metaFile);
                        if (meta == null) continue;
                        result.Add(new ProgramInfo
                        {
                            name = meta.name,
                            origin = string.IsNullOrEmpty(meta.origin) ? "local" : meta.origin,
                            isDirty = meta.isDirty,
                            lastSyncedUtc = meta.lastSyncedUtc
                        });
                    }
                    catch { /* 忽略损坏的单个元数据文件 */ }
                }

                // 兼容：只有 .bas 没有 .meta.json 的旧文件
                foreach (var basFile in Directory.GetFiles(WorkspaceDir, "*.bas"))
                {
                    var stem = Path.GetFileNameWithoutExtension(basFile);
                    if (!result.Any(r => string.Equals(r.name, stem, StringComparison.OrdinalIgnoreCase)))
                        result.Add(new ProgramInfo { name = stem, origin = "local", isDirty = true, lastSyncedUtc = null });
                }
            }
            return result.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>读取本地缓存的源码；不存在返回 null。</summary>
        public static string ReadLocalSource(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var path = GetProgramPath(name);
            lock (_lock)
            {
                if (!File.Exists(path)) return null;
                return File.ReadAllText(path);
            }
        }

        /// <summary>
        /// 将源码写入本地缓存，并标记为待同步（isDirty=true）。
        /// 只写本地文件，不触及控制器。
        /// </summary>
        public static void WriteLocalSource(string name, string source)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("程序名不能为空", nameof(name));
            source = source ?? "";
            var path = GetProgramPath(name);
            var metaPath = GetMetaPath(name);
            lock (_lock)
            {
                EnsureDirectory();
                File.WriteAllText(path, source);
                WriteMetaFile(metaPath, new ProgramMeta
                {
                    name = name,
                    origin = "local",
                    isDirty = true,
                    lastSyncedUtc = null
                });
            }
        }

        /// <summary>删除本地缓存的程序（只删本地缓存，不碰控制器）。</summary>
        public static void DeleteLocalSource(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (_lock)
            {
                var path = GetProgramPath(name);
                var metaPath = GetMetaPath(name);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
        }

        /// <summary>获取本地程序的元数据；不存在返回 null。</summary>
        public static ProgramMeta GetLocalMeta(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                return ReadMetaFile(GetMetaPath(name));
            }
        }

        /// <summary>
        /// 标记程序已与控制器同步（清除 isDirty，origin=controller，记录 lastSyncedUtc）。
        /// 在从控制器读取并缓存后，或下载到控制器成功后调用。
        /// </summary>
        public static void MarkSynced(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (_lock)
            {
                var metaPath = GetMetaPath(name);
                var meta = ReadMetaFile(metaPath) ?? new ProgramMeta { name = name };
                meta.origin = "controller";
                meta.isDirty = false;
                meta.lastSyncedUtc = DateTime.UtcNow.ToString("o");
                WriteMetaFile(metaPath, meta);
            }
        }

        /// <summary>获取所有待同步到控制器的程序名（本地已修改、尚未下载到控制器）。</summary>
        public static List<string> GetPendingUploads()
        {
            return ListLocalPrograms().Where(p => p.isDirty).Select(p => p.name).ToList();
        }

        // ---------- 项目文件操作私有实现 ----------

        private const string NoProjectError = "No open RTSys project (.zpj) found";

        /// <summary>校验项目程序名；合法返回 null。</summary>
        internal static string ValidateProgramName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Program name is required";
            if (name == "." || name == "..") return "Invalid program name: " + name;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "Invalid program name: " + name;
            if (name.EndsWith(".bas", StringComparison.OrdinalIgnoreCase))
                return "Program name must not include the .bas extension";
            return null;
        }

        private static void WriteGbkFile(string path, string source)
        {
            File.WriteAllBytes(path, Encoding.GetEncoding(ProjectCodePage).GetBytes(source ?? ""));
        }

        /// <summary>按 .zpj 清单（优先）或根目录定位项目 .bas；找不到返回 null。</summary>
        private static string FindProjectProgramPath(string projectDir, string name)
        {
            foreach (var entry in ReadZpjFileList(projectDir))
            {
                if (!entry.Item1.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(entry.Item1), name, StringComparison.OrdinalIgnoreCase))
                {
                    var p = ResolveInside(projectDir, entry.Item1);
                    if (p != null && File.Exists(p)) return p;
                }
            }

            var exact = ResolveInside(projectDir, name + ".bas");
            if (exact != null && File.Exists(exact)) return exact;
            foreach (var f in Directory.GetFiles(projectDir, "*.bas"))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            return null;
        }

        /// <summary>在 .zpj 清单中按程序名（不含扩展名）查找条目。</summary>
        private static ZpjEntry FindEntry(List<ZpjEntry> entries, string name)
        {
            foreach (var e in entries)
            {
                if (!e.FileName.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(e.FileName), name, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        private static string FindZpjPath(string projectDir)
        {
            var zpjName = DetectProjectFileNameFromTitle();
            if (!string.IsNullOrEmpty(zpjName))
            {
                var p = ResolveInside(projectDir, zpjName);
                if (p != null && File.Exists(p)) return p;
            }
            var files = Directory.GetFiles(projectDir, "*.zpj");
            if (files.Length == 0) return null;
            return files.OrderByDescending(File.GetLastWriteTimeUtc).First();
        }

        private static bool IsAnySectionHeader(string trimmed)
        {
            return trimmed.Length > 1 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']';
        }

        private static bool IsSectionHeader(string trimmed, string name)
        {
            return IsAnySectionHeader(trimmed) &&
                   string.Equals(trimmed.Substring(1, trimmed.Length - 2), name, StringComparison.OrdinalIgnoreCase);
        }

        private static ZpjEntry GetEntry(SortedDictionary<int, ZpjEntry> dict, int index)
        {
            ZpjEntry entry;
            if (!dict.TryGetValue(index, out entry))
            {
                entry = new ZpjEntry { FileType = 1 };
                dict[index] = entry;
            }
            return entry;
        }

        /// <summary>.zpj 中 [FileList] 的一条记录；AutoRun 为 null 表示原文件未设置。</summary>
        private sealed class ZpjEntry
        {
            public string FileName;
            public int FileType;
            public string AutoRun;
        }

        /// <summary>
        /// .zpj 的可编辑模型：只重建 [FileList] 段的 FileN/FileTypeN/AutoRunN，
        /// 其余段落与 [FileList] 内未识别的行原样保留；Files 计数同步更新。
        /// </summary>
        private sealed class ZpjModel
        {
            public string Path;
            public List<string> Lines;
            public string NewLine = "\r\n";
            public bool EndsWithNewLine;
            public int HeaderIndex = -1;
            public int SectionEnd = -1;
            public List<ZpjEntry> Entries;
            public List<string> OtherLines;
            public bool HasFilesLineInFileList;

            public void Save()
            {
                var generated = new List<string>();
                for (int i = 0; i < Entries.Count; i++)
                {
                    var e = Entries[i];
                    generated.Add("File" + (i + 1) + "=" + e.FileName);
                    generated.Add("FileType" + (i + 1) + "=" + e.FileType);
                    if (e.AutoRun != null) generated.Add("AutoRun" + (i + 1) + "=" + e.AutoRun);
                }

                var result = new List<string>();
                if (HeaderIndex < 0)
                {
                    result.AddRange(Lines);
                    result.Add("[FileList]");
                    if (HasFilesLineInFileList) result.Add("Files=" + Entries.Count);
                    result.AddRange(generated);
                    result.AddRange(OtherLines);
                }
                else
                {
                    for (int i = 0; i <= HeaderIndex; i++) result.Add(Lines[i]);
                    if (HasFilesLineInFileList) result.Add("Files=" + Entries.Count);
                    result.AddRange(generated);
                    result.AddRange(OtherLines);
                    for (int i = SectionEnd; i < Lines.Count; i++) result.Add(Lines[i]);
                }

                UpdateFilesCount(result, Entries.Count);

                var text = string.Join(NewLine, result);
                if (EndsWithNewLine) text += NewLine;
                File.WriteAllText(Path, text, new UTF8Encoding(false));
            }

            /// <summary>同步 [Controller]/[FileList] 中的 Files 计数（真实 .zpj 把计数放在 [Controller]）。</summary>
            private static void UpdateFilesCount(List<string> lines, int count)
            {
                string section = null;
                for (int i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (IsAnySectionHeader(trimmed))
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }
                    if (!string.Equals(section, FileListSection, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(section, "Controller", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(trimmed.Substring(0, eq).Trim(), "Files", StringComparison.OrdinalIgnoreCase))
                        continue;
                    lines[i] = "Files=" + count;
                }
            }
        }

        private static ZpjModel LoadZpjModel(string projectDir, out string error)
        {
            error = null;
            var zpjPath = FindZpjPath(projectDir);
            if (zpjPath == null)
            {
                error = NoProjectError;
                return null;
            }
            try
            {
                var text = ReadTextAuto(zpjPath);
                var model = new ZpjModel { Path = zpjPath };
                model.NewLine = text.Contains("\r\n") ? "\r\n" : (text.Contains("\n") ? "\n" : "\r\n");
                model.EndsWithNewLine = text.EndsWith("\n") || text.EndsWith("\r");
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
                if (model.EndsWithNewLine && lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                model.Lines = lines;
                model.Entries = new List<ZpjEntry>();
                model.OtherLines = new List<string>();

                for (int i = 0; i < lines.Count; i++)
                {
                    if (IsSectionHeader(lines[i].Trim(), FileListSection)) { model.HeaderIndex = i; break; }
                }
                if (model.HeaderIndex >= 0)
                {
                    model.SectionEnd = lines.Count;
                    for (int i = model.HeaderIndex + 1; i < lines.Count; i++)
                    {
                        if (IsAnySectionHeader(lines[i].Trim())) { model.SectionEnd = i; break; }
                    }

                    var byIndex = new SortedDictionary<int, ZpjEntry>();
                    for (int i = model.HeaderIndex + 1; i < model.SectionEnd; i++)
                    {
                        var trimmed = lines[i].Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith(";") || trimmed.StartsWith("'"))
                        {
                            model.OtherLines.Add(lines[i]);
                            continue;
                        }
                        var eq = trimmed.IndexOf('=');
                        if (eq <= 0) { model.OtherLines.Add(lines[i]); continue; }
                        var key = trimmed.Substring(0, eq).Trim();
                        var val = trimmed.Substring(eq + 1).Trim();
                        int idx;
                        if (string.Equals(key, "Files", StringComparison.OrdinalIgnoreCase))
                        {
                            model.HasFilesLineInFileList = true;
                            continue;
                        }
                        if (key.StartsWith("FileType", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(key.Substring(8), out idx))
                        {
                            int fileType;
                            GetEntry(byIndex, idx).FileType = int.TryParse(val, out fileType) ? fileType : 1;
                            continue;
                        }
                        if (key.StartsWith("AutoRun", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(key.Substring(7), out idx))
                        {
                            GetEntry(byIndex, idx).AutoRun = val;
                            continue;
                        }
                        if (key.StartsWith("File", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(key.Substring(4), out idx))
                        {
                            GetEntry(byIndex, idx).FileName = val;
                            continue;
                        }
                        model.OtherLines.Add(lines[i]);
                    }

                    foreach (var kv in byIndex)
                    {
                        if (string.IsNullOrEmpty(kv.Value.FileName)) continue;
                        // .zpj 可能来自外部/不可信来源：只接受工程目录内的相对路径。
                        if (ResolveInside(projectDir, kv.Value.FileName) == null) continue;
                        model.Entries.Add(kv.Value);
                    }
                }
                return model;
            }
            catch (Exception ex)
            {
                error = "Failed to read project file: " + ex.Message;
                return null;
            }
        }

        // ---------- 私有 ----------

        private static ProgramMeta ReadMetaFile(string metaPath)
        {
            if (!File.Exists(metaPath)) return null;
            var text = File.ReadAllText(metaPath);
            return _json.Deserialize<ProgramMeta>(text);
        }

        private static void WriteMetaFile(string metaPath, ProgramMeta meta)
        {
            var dir = Path.GetDirectoryName(metaPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(metaPath, _json.Serialize(meta));
        }
    }
}
