using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RTSys.PlugIn
{
    /// <summary>
    /// 写前自动备份：程序源码(.bas) + 工程文件(.zpj)。
    /// 所有写入口（AI 工具 / HTTP / UI）统一调用；备份按原文件编码保存（ANSI/UTF-8 自动检测）。
    /// 文件名格式：程序名__yyyyMMdd_HHmmss_fff__原因.bas.bak
    /// </summary>
    internal static class ProgramBackup
    {
        internal static string BackupDirectory = Path.Combine(AiService.DataDir, "backups");

        private const int MaxPerProgram = 10;   // 每个程序/文件保留最近 N 份
        private const int MaxAgeDays = 30;      // 超过 N 天自动清理
        private const int MaxTotalFiles = 300;  // 总文件数上限
        private static readonly object _lock = new object();

        internal sealed class BackupResult
        {
            public string File;
            public string ProjectFile;
            public string Error;
            public bool SourceExisted;
        }

        internal sealed class BackupInfo
        {
            public string File;
            public string Program;
            public string Kind;
            public string Reason;
            public DateTime Time;
            public long Size;
        }

        /// <summary>备份一个程序的当前源码（写/补丁/删除/重命名前调用）。</summary>
        internal static BackupResult BackupProgramSource(string programName, string reason)
        {
            var result = new BackupResult();
            try
            {
                var sourcePath = ProgramWorkspace.GetProjectProgramPath(programName);
                var sourceCode = ExtractSource(RTSysHandlers.ReadSource(programName));
                if (sourceCode == null)
                    return result; // 新程序/无源码可备份

                result.SourceExisted = true;
                var bytes = BasEncoding.Encode(sourceCode, BasEncoding.ResolveForWrite(sourcePath));
                result.File = Write(programName, "bas", reason, bytes);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                PlugInLog.Log("程序备份失败(" + programName + "): " + ex.Message);
            }
            return result;
        }

        /// <summary>ReadSource 成功时返回匿名对象（sourceCode 属性），失败时返回 Dictionary；两种都兼容。</summary>
        private static string ExtractSource(object readResult)
        {
            if (readResult == null) return null;
            var dict = readResult as Dictionary<string, object>;
            if (dict != null)
            {
                object value;
                return dict.TryGetValue("sourceCode", out value) && value != null ? value.ToString() : null;
            }
            var prop = readResult.GetType().GetProperty("sourceCode");
            var result = prop != null ? prop.GetValue(readResult, null) : null;
            return result != null ? result.ToString() : null;
        }

        /// <summary>备份当前工程文件（.zpj）。</summary>
        internal static BackupResult BackupProjectFile(string reason)
        {
            var result = new BackupResult();
            try
            {
                var zpj = ProgramWorkspace.GetProjectFilePath();
                if (string.IsNullOrEmpty(zpj) || !File.Exists(zpj)) return result;
                var name = Path.GetFileNameWithoutExtension(zpj);
                result.ProjectFile = Write(name, "zpj", reason, File.ReadAllBytes(zpj));
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                PlugInLog.Log("工程文件备份失败: " + ex.Message);
            }
            return result;
        }

        /// <summary>列出备份；programFilter 为空表示全部。</summary>
        internal static List<BackupInfo> List(string programFilter = null)
        {
            var infos = new List<BackupInfo>();
            try
            {
                if (!Directory.Exists(BackupDirectory)) return infos;
                foreach (var path in Directory.GetFiles(BackupDirectory, "*.bak"))
                {
                    var info = Parse(Path.GetFileName(path));
                    if (info == null) continue;
                    if (!string.IsNullOrEmpty(programFilter) &&
                        !string.Equals(info.Program, programFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { info.Size = new FileInfo(path).Length; } catch { }
                    infos.Add(info);
                }
            }
            catch (Exception ex)
            {
                PlugInLog.Log("读取备份列表失败: " + ex.Message);
            }
            return infos.OrderByDescending(i => i.Time).ToList();
        }

        internal static object ListBackups(string programFilter)
        {
            var list = List(programFilter)
                .Select(i => new
                {
                    file = i.File,
                    program = i.Program,
                    kind = i.Kind,
                    reason = i.Reason,
                    time = i.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                    size = i.Size
                })
                .ToList();
            return new { success = true, count = list.Count, backups = list };
        }

        /// <summary>从备份恢复程序源码；工程文件(.zpj)备份不自动恢复，避免与运行中的工程模型脱节。</summary>
        internal static object RestoreBackup(string fileName, string targetName)
        {
            try
            {
                var safeName = Path.GetFileName(fileName ?? "");
                var info = Parse(safeName);
                if (info == null) return new { success = false, error = "备份文件名无效" };
                if (info.Kind != "bas")
                    return new { success = false, error = "工程文件备份仅用于手工恢复（避免与运行中的 RTSys 工程模型脱节）" };

                var path = Path.Combine(BackupDirectory, safeName);
                if (!File.Exists(path)) return new { success = false, error = "备份不存在: " + safeName };

                var program = string.IsNullOrWhiteSpace(targetName) ? info.Program : targetName;
                var text = BasEncoding.Decode(File.ReadAllBytes(path));
                // skipBackup=true：恢复操作本身不再生成新备份，避免递归
                return RTSysHandlers.WriteSource(program,
                    new Dictionary<string, object> { { "sourceCode", text } }, true);
            }
            catch (Exception ex)
            {
                return new { success = false, error = "恢复失败: " + ex.Message };
            }
        }

        internal static string BuildFileName(string programName, string kind, string reason)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            return Sanitize(programName) + "__" + stamp + "__" + Sanitize(reason) + "." + kind + ".bak";
        }

        internal static BackupInfo Parse(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                return null;
            var stem = fileName.Substring(0, fileName.Length - 4);
            var i2 = stem.LastIndexOf("__", StringComparison.Ordinal);
            if (i2 <= 0) return null;
            var i1 = stem.LastIndexOf("__", i2 - 1, StringComparison.Ordinal);
            if (i1 <= 0) return null;

            var program = stem.Substring(0, i1);
            var stamp = stem.Substring(i1 + 2, i2 - i1 - 2);
            var tail = stem.Substring(i2 + 2);
            var dot = tail.LastIndexOf('.');
            if (dot <= 0) return null;

            DateTime time;
            if (!DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out time))
                return null;

            return new BackupInfo
            {
                File = fileName,
                Program = program,
                Kind = tail.Substring(dot + 1).ToLowerInvariant(),
                Reason = tail.Substring(0, dot),
                Time = time
            };
        }

        internal static string Sanitize(string value)
        {
            value = string.IsNullOrEmpty(value) ? "unnamed" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }

        private static string Write(string name, string kind, string reason, byte[] bytes)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(BackupDirectory);
                var fileName = BuildFileName(name, kind, reason);
                File.WriteAllBytes(Path.Combine(BackupDirectory, fileName), bytes ?? new byte[0]);
                Prune();
                return fileName;
            }
        }

        /// <summary>保留策略：每程序最近 N 份、超过 N 天、总数上限；只处理本类生成的文件名。</summary>
        internal static void Prune()
        {
            try
            {
                if (!Directory.Exists(BackupDirectory)) return;
                var now = DateTime.Now;

                foreach (var info in List().Where(i => (now - i.Time).TotalDays > MaxAgeDays))
                    TryDelete(info.File);

                foreach (var group in List().GroupBy(i => i.Program + "\u0000" + i.Kind, StringComparer.OrdinalIgnoreCase))
                    foreach (var old in group.OrderByDescending(i => i.Time).Skip(MaxPerProgram))
                        TryDelete(old.File);

                var all = List();
                if (all.Count > MaxTotalFiles)
                    foreach (var old in all.OrderByDescending(i => i.Time).Skip(MaxTotalFiles))
                        TryDelete(old.File);
            }
            catch (Exception ex)
            {
                PlugInLog.Log("备份清理失败: " + ex.Message);
            }
        }

        private static void TryDelete(string fileName)
        {
            try { File.Delete(Path.Combine(BackupDirectory, fileName)); } catch { }
        }
    }
}
