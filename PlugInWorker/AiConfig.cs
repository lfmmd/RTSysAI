using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- 默认值（唯一来源，供 ChatPanel 与请求规范化共用）----
        internal const string DefaultApiUrl = "https://api.deepseek.com/anthropic";
        internal const string DefaultModel = "claude-sonnet-4-20250514";

        // ---- Config-related instance fields ----

        private string _apiKey;
        private string _model;
        private string _lightModel;   // 轻模型：子 agent 用；留空（null/空串）= 落回主模型 _model（即现状）
        private string _apiUrl;
        private bool _showToolStatus = true;
        private bool _skillsInitialized;
        private static bool _includeSkillImages = false;
        private static bool _enableControllerValidation = false;
        private static bool _enableThinking = false;
        private static int _budgetTokens = 10000;
        private static bool _showThinking = true;
        private static bool _memoryEnabled = true;
        private static bool _localizeThinking = true;
        private static bool _useChineseDocs = false;   // skill 文档语言：false=英文（默认），true=中文
        private string _dialectMode = "auto";   // 提示词方言：auto(按项目主导方言推断)/rtbasic
        private static string _basEncoding = "auto"; // .bas 输出编码：auto(保持原编码，新文件 ANSI)/ansi/utf8

        // 本类不管理的配置键（RTSysIP / projectDir / enableHttpApi 等）：
        // 加载时缓存到内存，保存时原样写回，避免设置保存把用户配置整份覆盖掉。
        private static readonly HashSet<string> ManagedConfigKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "apiKey", "model", "lightModel", "apiUrl", "showToolStatus", "skillsInitialized",
            "includeSkillImages", "enableControllerValidation", "enableThinking", "budgetTokens",
            "showThinking", "memoryEnabled", "localizeThinking", "useChineseDocs", "dialectMode", "basEncoding"
        };
        private readonly Dictionary<string, object> _extraConfig =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // ---- Config ----

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var text = File.ReadAllText(ConfigPath);
                    var cfg = _json.Deserialize<Dictionary<string, object>>(text);
                    object val;
                    if (cfg.TryGetValue("apiKey", out val)) _apiKey = val?.ToString();
                    if (cfg.TryGetValue("model", out val)) _model = val?.ToString();
                    if (cfg.TryGetValue("lightModel", out val)) _lightModel = val?.ToString();
                    if (cfg.TryGetValue("apiUrl", out val)) _apiUrl = val?.ToString();
                    if (cfg.TryGetValue("showToolStatus", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _showToolStatus = b;
                    }
                    if (cfg.TryGetValue("skillsInitialized", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _skillsInitialized = b;
                    }
                    if (cfg.TryGetValue("includeSkillImages", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _includeSkillImages = b;
                    }
                    if (cfg.TryGetValue("enableControllerValidation", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _enableControllerValidation = b;
                    }
                    if (cfg.TryGetValue("enableThinking", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _enableThinking = b;
                    }
                    if (cfg.TryGetValue("budgetTokens", out val))
                    {
                        int i;
                        if (val != null && int.TryParse(val.ToString(), out i)) _budgetTokens = i;
                    }
                    if (cfg.TryGetValue("showThinking", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _showThinking = b;
                    }
                    if (cfg.TryGetValue("memoryEnabled", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _memoryEnabled = b;
                    }
                    if (cfg.TryGetValue("localizeThinking", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _localizeThinking = b;
                    }
                    if (cfg.TryGetValue("useChineseDocs", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _useChineseDocs = b;
                    }
                    if (cfg.TryGetValue("dialectMode", out val))
                    {
                        var s = val?.ToString();
                        if (s == "auto" || s == "rtbasic") _dialectMode = s;
                        else _dialectMode = "rtbasic"; // 兼容旧配置（triobasic/iec），统一回落到 rtbasic
                    }
                    if (cfg.TryGetValue("basEncoding", out val))
                    {
                        var s = val?.ToString();
                        if (s == "ansi" || s == "utf8") _basEncoding = s;
                        else _basEncoding = "auto";
                    }

                    // 缓存本类不管理的键，保存时写回。
                    _extraConfig.Clear();
                    foreach (var kv in cfg)
                    {
                        if (!ManagedConfigKeys.Contains(kv.Key))
                            _extraConfig[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        public void SaveConfig(string apiKey, string model, string apiUrl, bool? showToolStatus = null, bool? includeSkillImages = null, bool? enableControllerValidation = null, bool? enableThinking = null, int? budgetTokens = null, bool? showThinking = null, bool? memoryEnabled = null, bool? localizeThinking = null, bool? useChineseDocs = null, string lightModel = null, string dialectMode = null, string basEncoding = null)
        {
            _apiKey = apiKey;
            if (!string.IsNullOrEmpty(model)) _model = model;
            _lightModel = lightModel;   // null/空 = 用主模型（现状）；非空 = 子 agent 走它
            if (!string.IsNullOrEmpty(apiUrl)) _apiUrl = apiUrl;
            if (showToolStatus.HasValue) _showToolStatus = showToolStatus.Value;
            if (includeSkillImages.HasValue && includeSkillImages.Value != _includeSkillImages)
            {
                _includeSkillImages = includeSkillImages.Value;
                _skillDetailCache.Clear(); // img stripping is cached per page; force re-read
            }
            if (enableControllerValidation.HasValue) _enableControllerValidation = enableControllerValidation.Value;
            if (enableThinking.HasValue) _enableThinking = enableThinking.Value;
            if (budgetTokens.HasValue) _budgetTokens = budgetTokens.Value;
            if (showThinking.HasValue) _showThinking = showThinking.Value;
            if (memoryEnabled.HasValue) _memoryEnabled = memoryEnabled.Value;
            if (localizeThinking.HasValue) _localizeThinking = localizeThinking.Value;
            if (useChineseDocs.HasValue && useChineseDocs.Value != _useChineseDocs)
            {
                _useChineseDocs = useChineseDocs.Value;
                _index = null;                  // reload the other language's index
                _skillDetailCache.Clear();      // cached HTML is language-specific
                _validationIndexBuilt = false;  // _triobasicIds/_signatures reflect the lang dir's files
            }
            if (dialectMode != null && (dialectMode == "auto" || dialectMode == "rtbasic"))
                _dialectMode = dialectMode;
            else if (dialectMode != null)
                _dialectMode = "rtbasic"; // 兼容旧值
            if (basEncoding != null)
            {
                if (basEncoding == "ansi" || basEncoding == "utf8") _basEncoding = basEncoding;
                else _basEncoding = "auto";
            }
            var json = _json.Serialize(new { apiKey = _apiKey, model = _model, lightModel = _lightModel ?? "", apiUrl = _apiUrl, showToolStatus = _showToolStatus, skillsInitialized = _skillsInitialized, includeSkillImages = _includeSkillImages, enableControllerValidation = _enableControllerValidation, enableThinking = _enableThinking, budgetTokens = _budgetTokens, showThinking = _showThinking, memoryEnabled = _memoryEnabled, localizeThinking = _localizeThinking, useChineseDocs = _useChineseDocs, dialectMode = _dialectMode, basEncoding = _basEncoding });

            // 保留"未知键"（RTSysIP / projectDir / enableHttpApi / 用户自定义项）：
            // 1) 先写回本进程加载时缓存的未知键（即使配置文件此刻不可读也不丢）；
            // 2) 再合并当前文件中的未知键（其它进程可能新增）。
            var merged = _json.Deserialize<Dictionary<string, object>>(json)
                         ?? new Dictionary<string, object>();
            foreach (var kv in _extraConfig)
            {
                if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
            }
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var existing = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath));
                    if (existing != null)
                    {
                        foreach (var kv in existing)
                        {
                            if (!merged.ContainsKey(kv.Key) && !ManagedConfigKeys.Contains(kv.Key))
                                merged[kv.Key] = kv.Value;
                        }
                    }
                }
            }
            catch { /* 当前文件解析失败时，仍保留 _extraConfig 中的未知键 */ }
            File.WriteAllText(ConfigPath, _json.Serialize(merged));
        }

        // Auto-append /v1/messages if user typed only the base (e.g. https://api.deepseek.com/anthropic).
        // Anthropic-compatible endpoints need the full path; missing it returns 404 NotFound.
        private static string NormalizeApiUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return DefaultApiUrl + "/v1/messages";
            url = url.Trim();
            if (url.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
                return url;
            if (url.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
                return url + "/v1/messages";
            if (url.EndsWith("/anthropic/", StringComparison.OrdinalIgnoreCase))
                return url + "v1/messages";

            // 裸域名（如 https://token.sensenova.cn 或 https://token.sensenova.cn/）：
            // 补全 Anthropic 消息端点路径。否则会 POST 到网站根路径并收到 404，
            // 被上层当成网络错误反复重试。
            try
            {
                var uri = new Uri(url);
                if (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
                    return url.TrimEnd('/') + "/v1/messages";
            }
            catch { /* 非法 URL 原样返回，交由 HttpClient 报错 */ }
            return url;
        }

        public string CurrentConfig => _json.Serialize(new { apiKey = _apiKey ?? "", model = _model ?? "", lightModel = _lightModel ?? "", apiUrl = _apiUrl ?? "", showToolStatus = _showToolStatus });
        public bool ShowToolStatus => _showToolStatus;
        public bool SkillsInitialized => _skillsInitialized;
        public bool IncludeSkillImages => _includeSkillImages;
        public string DialectMode => _dialectMode;
        public static string BasEncodingMode => _basEncoding;
        public static bool EnableControllerValidation => _enableControllerValidation;

        public string InitializeSkills(bool force = false)
        {
            // 已初始化过就跳过，避免每次启动清空用户对 skills 目录的自定义修改。
            if (_skillsInitialized && !force) return null;

            var dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var srcDir = Path.Combine(dllDir, "skills");
            if (!Directory.Exists(srcDir))
                return "插件目录下未找到 skills 文件夹";

            if (!Directory.Exists(SkillsDir))
                Directory.CreateDirectory(SkillsDir);

            // Copy skills directories
            foreach (var dir in Directory.GetDirectories(srcDir, "*", SearchOption.TopDirectoryOnly))
            {
                var dest = Path.Combine(SkillsDir, Path.GetFileName(dir));
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);
                CopyDirectory(dir, dest);
            }

            _skillsInitialized = true;
            _index = null; // force reload
            // 复用 SaveConfig 的合并逻辑，保留 RTSysIP / projectDir / enableHttpApi 等未知键。
            SaveConfig(_apiKey, _model, _apiUrl, _showToolStatus, _includeSkillImages,
                _enableControllerValidation, _enableThinking, _budgetTokens, _showThinking,
                _memoryEnabled, _localizeThinking, _useChineseDocs, _lightModel, _dialectMode);
            return null;
        }

        private static void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
        }

        public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);
        public string Model => _model;
        public string LightModel => _lightModel;
        public string ApiUrl => _apiUrl;
        public static bool EnableThinking => _enableThinking;
        public static int BudgetTokens => _budgetTokens;
        public static bool ShowThinking => _showThinking;
        public static bool MemoryEnabled => _memoryEnabled;
        public static bool LocalizeThinking => _localizeThinking;
        public static bool UseChineseDocs => _useChineseDocs;
    }
}
