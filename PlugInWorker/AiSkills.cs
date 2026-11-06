using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- Command Lookup (index for search, HTML file per entry for details) ----

        private class SkillIndexEntry
        {
            public string Name;
            public string Type;
            public string Desc;
            public string Lib;   // library name (triobasic/iec/plcopen); Dir holds {lib}/{lang}
            public string File;  // HTML file name within Dir
            public string Dir;
            public string Sig;   // signature from Syntax section (for arg-count validation)
        }

        // Markdown skill: skills/general/<name>/SKILL.md (cc-haha style, name + description + when_to_use frontmatter)
        private class MdSkillEntry
        {
            public string Name;
            public string Description;
            public string WhenToUse;
            public string Dir;
            public string Body;
        }

        private static List<SkillIndexEntry> _index;
        private static DateTime _indexLoadTime;
        private static readonly Dictionary<string, Dictionary<string, object>> _skillDetailCache = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        private static List<MdSkillEntry> _mdSkills;
        private static DateTime _mdSkillsLoadTime;
        private static readonly object _mdSkillLock = new object();

        private static List<SkillIndexEntry> LoadIndex()
        {
            if (_index != null && (DateTime.Now - _indexLoadTime).TotalMinutes < 10)
                return _index;

            _index = new List<SkillIndexEntry>();
            try
            {
                if (!Directory.Exists(SkillsDir)) return _index;
                // 文档语言：默认英文，开关开启用中文。每个库按 {lib}/{lang}/ 组织。
                var lang = _useChineseDocs ? "zh" : "en";
                // 加载参考库子目录（rtbasic），每条 entry 的 Dir 标识来源。
                foreach (var lib in new[] { "rtbasic" })
                {
                    var libDir = Path.Combine(SkillsDir, lib, lang);
                    var idxFile = Path.Combine(libDir, "index.json");
                    // 语言回退：RTBasic 官方参考只有中文版，所选语言没有索引时回退到另一语言，
                    // 否则 useChineseDocs=false（默认）会让整个 catalog 变空。
                    if (!File.Exists(idxFile))
                    {
                        var alt = lang == "zh" ? "en" : "zh";
                        var altDir = Path.Combine(SkillsDir, lib, alt);
                        var altIdx = Path.Combine(altDir, "index.json");
                        if (!File.Exists(altIdx)) continue;
                        libDir = altDir;
                        idxFile = altIdx;
                    }
                    var text = File.ReadAllText(idxFile);
                    var items = _json.Deserialize<List<Dictionary<string, object>>>(text);
                    if (items == null) continue;
                    foreach (var item in items)
                        _index.Add(new SkillIndexEntry
                        {
                            Name = GetStr(item, "name") ?? "",
                            Type = GetStr(item, "type") ?? "",
                            Desc = GetStr(item, "desc") ?? "",
                            Lib = lib,
                            File = GetStr(item, "file"),
                            Dir = libDir,
                            Sig = GetStr(item, "sig")
                        });
                }
                _indexLoadTime = DateTime.Now;
            }
            catch { }
            return _index;
        }

        // ---- Markdown Skills (skills/general/<name>/SKILL.md) ----

        private static List<MdSkillEntry> LoadMdSkills()
        {
            lock (_mdSkillLock)
            {
                if (_mdSkills != null && (DateTime.Now - _mdSkillsLoadTime).TotalMinutes < 10)
                    return _mdSkills;
                var list = new List<MdSkillEntry>();
                try
                {
                    var generalDir = Path.Combine(SkillsDir, "general");
                    if (Directory.Exists(generalDir))
                    {
                        foreach (var sub in Directory.GetDirectories(generalDir))
                        {
                            var skillFile = Path.Combine(sub, "SKILL.md");
                            if (!File.Exists(skillFile)) continue;
                            var entry = ParseSkillMd(skillFile, sub);
                            if (entry != null) list.Add(entry);
                        }
                    }
                    _mdSkills = list;
                    _mdSkillsLoadTime = DateTime.Now;
                }
                catch { }
                return _mdSkills;
            }
        }

        // Minimal frontmatter parser: only reads `name` and `description` from a leading
        // --- block. Trims surrounding quotes. Returns null if name is missing or no FM.
        private static MdSkillEntry ParseSkillMd(string filePath, string dir)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
                if (!text.StartsWith("---", StringComparison.Ordinal)) return null;
                int start = 3;
                if (start < text.Length && (text[start] == '\r' || text[start] == '\n')) start++;
                int end = text.IndexOf("\n---", start, StringComparison.Ordinal);
                if (end < 0) return null;
                var frontmatter = text.Substring(start, end - start);
                int bodyStart = end + 4;
                if (bodyStart < text.Length && (text[bodyStart] == '\r')) bodyStart++;
                if (bodyStart < text.Length && (text[bodyStart] == '\n')) bodyStart++;
                var body = text.Substring(bodyStart).TrimEnd();

                string name = null, desc = null, whenToUse = null;
                foreach (var rawLine in frontmatter.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim().ToLowerInvariant();
                    var val = line.Substring(idx + 1).Trim();
                    if (val.Length >= 2 && ((val[0] == '"' && val[val.Length - 1] == '"') ||
                                            (val[0] == '\'' && val[val.Length - 1] == '\'')))
                        val = val.Substring(1, val.Length - 2);
                    if (key == "name") name = val;
                    else if (key == "description") desc = val;
                    else if (key == "when_to_use" || key == "whentouse" || key == "when-to-use") whenToUse = val;
                }
                if (string.IsNullOrEmpty(name)) return null;
                return new MdSkillEntry
                {
                    Name = name,
                    Description = desc ?? "",
                    WhenToUse = whenToUse ?? "",
                    Dir = dir,
                    Body = body,
                };
            }
            catch { }
            return null;
        }

        internal static object ReadSkill(string name)
        {
            if (string.IsNullOrEmpty(name))
                return new { error = "name is required" };
            var skills = LoadMdSkills();
            if (skills.Count == 0)
                return new { error = "No markdown skills installed. Place <name>/SKILL.md under skills/general/." };
            var q = name.ToUpperInvariant();
            var match = skills.Find(s => s.Name.ToUpperInvariant() == q);
            if (match == null)
                return new { error = $"Skill '{name}' not found. Available: " + string.Join(", ", skills.ConvertAll(s => s.Name)) };
            return new
            {
                name = match.Name,
                description = match.Description,
                markdown = match.Body,
            };
        }

        /// <summary>
        /// 列出所有可用的 markdown skill（按 category 过滤），让 AI 在工具选择阶段就能感知可用技能。
        /// 比 read_skill(name) 试错更高效 — AI 看完列表后直接选对 skill 加载。
        /// </summary>
        internal static object DiscoverSkills(string category)
        {
            var skills = LoadMdSkills();
            if (skills.Count == 0)
                return new
                {
                    count = 0,
                    skills = new object[0],
                    hint = "No markdown skills installed. Place <name>/SKILL.md under skills/general/."
                };

            IEnumerable<MdSkillEntry> filtered = skills;
            if (!string.IsNullOrEmpty(category))
            {
                var catUpper = category.ToUpperInvariant();
                filtered = skills.Where(s =>
                {
                    var dirName = Path.GetFileName(s.Dir ?? "");
                    return string.Equals(dirName, category, StringComparison.OrdinalIgnoreCase);
                });
            }

            var result = filtered.Select(s => new
            {
                name = s.Name,
                description = s.Description,
                when_to_use = s.WhenToUse,
                category = Path.GetFileName(s.Dir ?? "")
            }).ToList();

            return new
            {
                count = result.Count,
                skills = result,
                hint = "Call read_skill(name) to load the full content of any skill."
            };
        }

        private static string BuildSkillsCatalog()
        {
            var sb = new StringBuilder();
            // HTML 参考库（lookup_command 索引）— 让 AI 知道这些库存在且应主动查
            var index = LoadIndex();
            if (index.Count > 0)
            {
                var byLib = new Dictionary<string, List<SkillIndexEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in index)
                {
                    var lib = e.Lib ?? "";
                    if (!byLib.ContainsKey(lib)) byLib[lib] = new List<SkillIndexEntry>();
                    byLib[lib].Add(e);
                }
                sb.AppendLine("## Reference Libraries (lookup_command)");
                sb.AppendLine("Use the **lookup_command** tool to verify ANY command/function/function-block before writing code. Coverage:");
                foreach (var kv in byLib)
                    sb.AppendFormat("- **{0}**: {1} entries — categories: {2}\n",
                        kv.Key, kv.Value.Count, SummarizeTypes(kv.Value));
                sb.AppendLine();
                sb.AppendLine("MANDATORY: Before writing RTBasic code, call lookup_command for any identifier you are not 100% sure exists in the official reference. Do NOT trust your training-data memory of this dialect — it is a proprietary ZMotion BASIC and easily drifts into VB/QBasic habits.");
                sb.AppendLine();
            }
            // Markdown skill（read_skill 索引）
            var skills = LoadMdSkills();
            if (skills.Count > 0)
            {
                sb.AppendLine("## Available Skills");
                sb.AppendLine("These markdown skills are installed. Use the read_skill tool to load full content.");
                sb.AppendLine();
                sb.Append(FormatSkillListing(skills));

                // safe-coding 是规范性 skill（安全约束、禁用命令清单），每轮嵌入 system prompt。
                // 不能依赖 AI 主动 read_skill — read_skill 工具描述里的 BLOCKING REQUIREMENT 触发语
                // + BuildTrimmedMessages 把 read_skill 纳入 keepRecent 白名单后，跨压缩稳定可用。
                // 全文 ~200 token，成本可忽略。
                var safeCoding = skills.Find(s =>
                    string.Equals(s.Name, "safe-coding", StringComparison.OrdinalIgnoreCase));
                if (safeCoding != null && !string.IsNullOrEmpty(safeCoding.Body))
                {
                    sb.AppendLine();
                    sb.AppendLine("## Safe Coding Rules (MANDATORY)");
                    sb.AppendLine("Follow these rules whenever writing motion control code. Violations are unacceptable:");
                    sb.AppendLine();
                    sb.AppendLine(safeCoding.Body.Trim());
                }
            }
            return sb.ToString();
        }

        // 把 index.json 里的 type 字段（"Axis Parameter (Read Only)" / "Axis Parameter." / "axis parameter" 等）
        // 归一化为干净的类别名：去末尾句号、去括号注释（Read Only / MC_CONFIG / FLASH）、压空白、保留主词。
        // 这样 AI 看到的类别清单是有限可枚举的，不会被大小写/修饰词淹没。
        private static string NormalizeType(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            t = t.Trim();
            t = t.TrimEnd('.', ' ', '\t');
            int paren = t.IndexOf('(');
            if (paren >= 0) t = t.Substring(0, paren);
            t = t.Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t;
        }

        // 每个库取 top 8 类别（按条目数降序），格式 "Axis Parameter (221), System Command (86), ..."。
        // top 8 通常覆盖 80%+ 条目，足够给 AI 当"猜命令"的方向线索；再多的边际效益递减且占 token。
        private const int MaxTypeCategoriesPerLib = 8;

        private static string SummarizeTypes(List<SkillIndexEntry> entries)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                var n = NormalizeType(e.Type);
                if (string.IsNullOrEmpty(n)) continue;
                int cur;
                counts[n] = counts.TryGetValue(n, out cur) ? cur + 1 : 1;
            }
            if (counts.Count == 0) return "(uncategorized)";
            var sorted = new List<KeyValuePair<string, int>>(counts);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count && i < MaxTypeCategoriesPerLib; i++)
                parts.Add(string.Format("{0} ({1})", sorted[i].Key, sorted[i].Value));
            return string.Join(", ", parts);
        }

        // Skill 目录的 token 预算：列表只放 name + description + when_to_use（正文调用了才注入）。
        // 整个列表上限 SkillListingBudget 字符（约 1% 上下文），单条上限 MaxSkillEntryChars。
        // 超预算时按均分截断；预算极小时退化到 names-only，保证目录至少能让 AI 知道有哪些 skill。
        // 参考 claudecodefx src/tools/SkillTool/prompt.ts 的 formatCommandsWithinBudget。
        private const int SkillListingBudget = 8000;
        private const int MaxSkillEntryChars = 250;
        private const int MinSkillEntryChars = 20;

        private static string FormatSkillEntry(MdSkillEntry s, int maxChars)
        {
            string full;
            if (!string.IsNullOrEmpty(s.WhenToUse))
                full = string.Format("- **{0}**: {1} — Use when: {2}", s.Name, s.Description ?? "", s.WhenToUse);
            else
                full = string.Format("- **{0}**: {1}", s.Name, s.Description ?? "");
            if (full.Length <= maxChars) return full;
            if (maxChars <= "- **".Length + s.Name.Length + "**".Length + 2)
                return string.Format("- **{0}**", s.Name);
            return full.Substring(0, maxChars - 1) + "…";
        }

        private static string FormatSkillListing(List<MdSkillEntry> skills)
        {
            if (skills.Count == 0) return "";

            var entries = skills.ConvertAll(s => FormatSkillEntry(s, MaxSkillEntryChars));
            int total = 0;
            foreach (var e in entries) total += e.Length + 1;  // +1 for newline
            if (total <= SkillListingBudget)
                return string.Join("\n", entries) + "\n";

            // 超预算：均分剩余给每条，但每条不超 MaxSkillEntryChars
            int perEntry = SkillListingBudget / skills.Count;
            if (perEntry < MinSkillEntryChars)
            {
                // 极端情况：只放名字
                var names = new List<string>();
                foreach (var s in skills) names.Add(string.Format("- **{0}**", s.Name));
                return string.Join("\n", names) + "\n";
            }
            int capped = Math.Min(perEntry, MaxSkillEntryChars);
            var truncated = skills.ConvertAll(s => FormatSkillEntry(s, capped));
            return string.Join("\n", truncated) + "\n";
        }

        // Strip noise tags (script/style/head/comments/img) from a help HTML page so
        // the LLM gets a compact body. img is dropped unless IncludeSkillImages is set.
        private static readonly Regex _reHead =
            new Regex(@"<head\b.*?</head>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reScript =
            new Regex(@"<script\b.*?</script>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reStyle =
            new Regex(@"<style\b.*?</style>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reComment =
            new Regex(@"<!--.*?-->",
                RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reImg =
            new Regex(@"<img\b[^>]*/?>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string CleanHelpHtml(string html, bool includeImages)
        {
            if (string.IsNullOrEmpty(html)) return html;
            html = _reComment.Replace(html, "");
            html = _reHead.Replace(html, "");
            html = _reScript.Replace(html, "");
            html = _reStyle.Replace(html, "");
            if (!includeImages)
                html = _reImg.Replace(html, "");
            // Collapse runs of blank lines left behind by the stripping above.
            return Regex.Replace(html, @"\n{3,}", "\n\n");
        }

        private static Dictionary<string, object> LoadFullEntry(SkillIndexEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.File)) return null;
            // Cache key includes Dir so two skills with same-named commands don't collide.
            string cacheKey = (entry.Dir ?? "") + "|" + entry.Name;
            Dictionary<string, object> cached;
            if (_skillDetailCache.TryGetValue(cacheKey, out cached))
                return cached;

            var htmlPath = Path.Combine(entry.Dir, entry.File);
            if (!File.Exists(htmlPath)) return null;
            try
            {
                var raw = File.ReadAllText(htmlPath);
                var body = CleanHelpHtml(raw, _includeSkillImages);
                var dict = new Dictionary<string, object>
                {
                    { "name", entry.Name },
                    { "type", entry.Type ?? "" },
                    { "description", entry.Desc ?? "" },
                    { "html", body },
                };
                _skillDetailCache[cacheKey] = dict;
                return dict;
            }
            catch { }
            return null;
        }

        private static SkillIndexEntry FindSkillEntry(string commandName)
        {
            var index = LoadIndex();
            var q = commandName.ToUpperInvariant();
            foreach (var e in index)
            {
                if (e.Name.ToUpperInvariant() == q)
                    return e;
            }
            return null;
        }

        internal static object LookupCommand(string query, string fullFlag, string library = null)
        {
            if (string.IsNullOrEmpty(query))
                return new { error = "query is required" };

            // 确保签名表已构建：summary 档（full=false）需要 _signatures 填真实签名，
            // 否则返回空 signature（日志中 7 个命令 signature 全空的根因）。
            EnsureValidationIndex();

            var index = LoadIndex();

            // 按库过滤
            IEnumerable<SkillIndexEntry> searchSet = index;
            if (!string.IsNullOrEmpty(library))
            {
                var libLower = library.ToLowerInvariant().Trim();
                searchSet = index.Where(e =>
                    (e.Lib ?? "").Equals(libLower, StringComparison.OrdinalIgnoreCase));
            }
            if (!searchSet.Any())
                return new { error = string.IsNullOrEmpty(library)
                    ? "No skill data found."
                    : $"No skill data found for library '{library}'." };

            var q = query.ToUpperInvariant().Trim();

            // 搜索：精确 → 名称包含 → 描述包含
            var matched = new List<SkillIndexEntry>();

            foreach (var e in searchSet)
            {
                if (e.Name.ToUpperInvariant() == q) { matched.Add(e); break; }
            }
            if (matched.Count == 0)
            {
                foreach (var e in searchSet)
                {
                    if (e.Name.ToUpperInvariant().Contains(q)) matched.Add(e);
                    if (matched.Count >= 5) break;
                }
            }
            if (matched.Count == 0)
            {
                foreach (var e in searchSet)
                {
                    if (e.Desc != null && e.Desc.ToUpperInvariant().Contains(q)) matched.Add(e);
                    if (matched.Count >= 3) break;
                }
            }

            if (matched.Count == 0)
                return new { error = $"No matching command found for '{query}'" };

            // Layer 2（默认）：名称 + 签名 + 描述，不加载 HTML
            if (!string.Equals(fullFlag, "true", StringComparison.OrdinalIgnoreCase))
            {
                var summaries = new List<object>();
                foreach (var m in matched)
                {
                    var sigText = "";
                    if (_signatures != null && _signatures.TryGetValue(m.Name, out var sig))
                        sigText = sig.RawSignature;
                    summaries.Add(new
                    {
                        name = m.Name,
                        signature = sigText,
                        description = m.Desc ?? "",
                        library = m.Lib ?? ""
                    });
                }
                return new
                {
                    results = summaries,
                    hint = "Pass full=true for complete HTML documentation with examples."
                };
            }

            // Layer 3：完整 HTML（含 192KB 预算）
            var results = new List<object>();
            foreach (var m in matched)
            {
                var full = LoadFullEntry(m);
                if (full != null) results.Add(full);
            }

            if (results.Count == 0)
                return new { error = $"Index matched but full data not found for '{query}'" };

            const int MaxTotalHtml = 192 * 1024;
            long totalHtmlLen = 0;
            foreach (Dictionary<string, object> d in results)
                if (d["html"] is string h) totalHtmlLen += h.Length;

            if (totalHtmlLen > MaxTotalHtml)
            {
                var scale = (double)MaxTotalHtml / totalHtmlLen;
                for (int i = 0; i < results.Count; i++)
                {
                    var d = (Dictionary<string, object>)results[i];
                    var html = (string)d["html"];
                    var maxLen = Math.Max(1024, (int)(html.Length * scale));
                    if (html.Length > maxLen)
                    {
                        results[i] = new Dictionary<string, object>(d)
                        {
                            ["html"] = html.Substring(0, maxLen)
                                + "\n\n[... truncated — total lookup results capped at 192KB ...]"
                        };
                    }
                }
            }

            return new { results };
        }
    }
}
