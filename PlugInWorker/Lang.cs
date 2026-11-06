using System;
using System.Collections.Generic;

namespace RTSys.PlugIn
{
    internal static class Lang
    {
        private static readonly Dictionary<string, Dictionary<string, string>> _strings = new Dictionary<string, Dictionary<string, string>>
        {
            ["zh"] = new Dictionary<string, string>
            {
                { "Title", "AI助手" },
                { "New", "新对话" },
                { "Clear", "清空" },
                { "History", "历史" },
                { "Settings", "设置" },
                { "Send", "发送" },
                { "Stop", "停止" },
                { "Allow", "允许" },
                { "Reject", "拒绝" },
                { "ConfirmMsg", "允许 AI 执行此操作？" },
                { "Rejected", "用户拒绝了此操作。" },
                { "NoApiKey", "未配置 API Key。点击[设置]配置。" },
                { "SettingsSaved", "设置已保存。" },
                { "SettingsTitle", "AI 助手设置" },
                { "ApiKey", "API Key:" },
                { "ApiUrl", "API URL:" },
                { "Model", "模型:" },
                { "LightModel", "轻模型(子agent,留空同主模型):" },
                { "Save", "保存" },
                { "Cancel", "取消" },
                { "Error", "错误" },
                { "HistoryTitle", "对话历史" },
                { "NoHistory", "暂无历史记录。" },
                { "Open", "打开" },
                { "Delete", "删除" },
                { "Close", "关闭" },
                { "ConfirmDelete", "确认删除此对话？" },
                { "Confirm", "确认" },
                { "SelectConversation", "请选择一个对话。" },
                { "EmptyKey", "API Key 或 URL 不能为空。" },
                { "AIRequests", "AI 请求执行" },
                { "Copy", "复制" },
                { "CopyAll", "全部复制" },
                { "ShowToolStatus", "显示工具执行状态" },
                { "ShowToolStatusDesc", "在聊天中显示黄色工具调用状态信息" },
                { "IncludeSkillImages", "Skill 查询包含图片" },
                { "IncludeSkillImagesDesc", "查询 TrioBASIC/IEC/PLCOpen 指令时在返回内容中保留 <img> 标签（默认关闭以节省 Token）" },
                { "ControllerValidation", "控制器语法校验（仅模拟器）" },
                { "ControllerValidationDesc", "连接模拟器时，将代码逐行发送到控制器进行语法校验（ValidationService）。需要模拟器连接。" },
                { "EnableThinking", "启用扩展思考" },
                { "EnableThinkingDesc", "启用后 AI 会显示推理过程（Extended Thinking），消耗更多 Token" },
                { "BudgetTokens", "思考 Token 预算:" },
                { "ThinkingLabel", "思考过程" },
                { "ShowThinking", "显示思考过程" },
                { "ShowThinkingDesc", "在聊天中自动展开显示 AI 的推理过程（关闭后仍可手动点击展开）" },
                { "Memory", "记忆" },
                { "MemoryTitle", "AI 持久化记忆" },
                { "MemoryDesc", "此内容在所有对话和重启后保留。仅当您明确要求（如「记住…」）时 AI 才更新此记忆；其余情况请手动编辑。" },
                { "ClearMemory", "清空记忆" },
                { "MemorySaved", "记忆已保存。" },
                { "MemoryEnabled", "启用持久化记忆" },
                { "MemoryEnabledDesc", "启用后 AI 可以跨会话记住用户偏好和项目知识" },
                { "LocalizeThinking", "思考过程本地化" },
                { "LocalizeThinkingDesc", "启用后 AI 的扩展思考（推理过程）会跟随 MotionPerfect 系统语言，而非默认英文" },
                { "UseChineseDocs", "使用中文帮助文档" },
                { "UseChineseDocsDesc", "启用后命令查询返回中文文档（默认关闭=英文）" },
                { "DialectMode", "提示词方言" },
                { "DialectModeDesc", "选择 AI 系统提示词的方言版本。Auto=按项目程序自动推断（纯 IEC 项目用 IEC，其余用 TrioBASIC）；也可手动锁定。" },
                { "DialectAuto", "自动（按项目推断）" },
            },
            ["en"] = new Dictionary<string, string>
            {
                { "Title", "AI助手" },
                { "New", "New" },
                { "Clear", "Clear" },
                { "History", "History" },
                { "Settings", "Settings" },
                { "Send", "Send" },
                { "Stop", "Stop" },
                { "Allow", "Allow" },
                { "Reject", "Reject" },
                { "ConfirmMsg", "Allow AI to execute this operation?" },
                { "Rejected", "Operation rejected by user." },
                { "NoApiKey", "API key not configured. Click 'Settings' to set your API key." },
                { "SettingsSaved", "Settings saved." },
                { "SettingsTitle", "AI Assistant Settings" },
                { "ApiKey", "API Key:" },
                { "ApiUrl", "API URL:" },
                { "Model", "Model:" },
                { "Save", "Save" },
                { "Cancel", "Cancel" },
                { "Error", "Error" },
                { "HistoryTitle", "Conversation History" },
                { "NoHistory", "No history yet." },
                { "Open", "Open" },
                { "Delete", "Delete" },
                { "Close", "Close" },
                { "ConfirmDelete", "Delete this conversation?" },
                { "Confirm", "Confirm" },
                { "SelectConversation", "Please select a conversation." },
                { "EmptyKey", "API Key or URL cannot be empty." },
                { "AIRequests", "AI requests" },
                { "Copy", "Copy" },
                { "CopyAll", "Copy All" },
                { "ShowToolStatus", "Show Tool Status" },
                { "ShowToolStatusDesc", "Display yellow tool execution status messages in chat" },
                { "IncludeSkillImages", "Include images in skill lookups" },
                { "IncludeSkillImagesDesc", "Keep <img> tags when returning TrioBASIC/IEC/PLCOpen command help (off by default to save tokens)" },
                { "ControllerValidation", "Controller syntax validation (simulator only)" },
                { "ControllerValidationDesc", "When connected to a simulator, validate code line-by-line via EXECUTE parse. Requires simulator connection." },
                { "EnableThinking", "Enable Extended Thinking" },
                { "EnableThinkingDesc", "When enabled, AI shows its reasoning process (Extended Thinking), consuming more tokens" },
                { "BudgetTokens", "Thinking Token Budget:" },
                { "ThinkingLabel", "Thinking Process" },
                { "ShowThinking", "Show Thinking Process" },
                { "ShowThinkingDesc", "Auto-expand AI reasoning process in chat (can still click to expand when off)" },
                { "Memory", "Memory" },
                { "MemoryTitle", "AI Persistent Memory" },
                { "MemoryDesc", "This content persists across all conversations and restarts. AI updates this memory ONLY when you explicitly ask (e.g. \"remember this\"); otherwise edit manually." },
                { "ClearMemory", "Clear Memory" },
                { "MemorySaved", "Memory saved." },
                { "MemoryEnabled", "Enable Persistent Memory" },
                { "MemoryEnabledDesc", "When enabled, AI can remember user preferences and project knowledge across sessions" },
                { "LocalizeThinking", "Localize Thinking" },
                { "LocalizeThinkingDesc", "When enabled, AI's extended thinking (reasoning) follows MotionPerfect's system language instead of defaulting to English" },
                { "UseChineseDocs", "Use Chinese Documentation" },
                { "UseChineseDocsDesc", "When enabled, command lookups return Chinese docs (off by default = English)" },
                { "DialectMode", "Prompt Dialect" },
                { "DialectModeDesc", "Which dialect variant of the AI system prompt to use. Auto = infer from project programs (IEC-only projects use IEC, others use TrioBASIC); or lock manually." },
                { "DialectAuto", "Auto (by project)" },
            },
            ["de"] = new Dictionary<string, string>
            {
                { "Title", "AI助手" },
                { "New", "Neu" },
                { "Clear", "Leeren" },
                { "History", "Verlauf" },
                { "Settings", "Einstellungen" },
                { "Send", "Senden" },
                { "Stop", "Stopp" },
                { "Allow", "Zulassen" },
                { "Reject", "Ablehnen" },
                { "ConfirmMsg", "KI die Ausführung erlauben?" },
                { "Rejected", "Vom Benutzer abgelehnt." },
                { "NoApiKey", "API-Key nicht konfiguriert. Klicken Sie auf 'Einstellungen'." },
                { "SettingsSaved", "Einstellungen gespeichert." },
                { "SettingsTitle", "KI-Assistent Einstellungen" },
                { "ApiKey", "API Key:" },
                { "ApiUrl", "API URL:" },
                { "Model", "Modell:" },
                { "Save", "Speichern" },
                { "Cancel", "Abbrechen" },
                { "Error", "Fehler" },
                { "HistoryTitle", "Gesprächsverlauf" },
                { "NoHistory", "Kein Verlauf vorhanden." },
                { "Open", "Öffnen" },
                { "Delete", "Löschen" },
                { "Close", "Schließen" },
                { "ConfirmDelete", "Gespräch löschen?" },
                { "Confirm", "Bestätigen" },
                { "SelectConversation", "Bitte ein Gespräch auswählen." },
                { "EmptyKey", "API Key oder URL darf nicht leer sein." },
                { "AIRequests", "KI-Anfrage" },
                { "Copy", "Kopieren" },
                { "CopyAll", "Alles kopieren" },
                { "ShowToolStatus", "Tool-Status anzeigen" },
                { "ShowToolStatusDesc", "Gelbe Tool-Ausfuehrungsstatusmeldungen im Chat anzeigen" },
                { "IncludeSkillImages", "Bilder in Skill-Abfragen einschließen" },
                { "IncludeSkillImagesDesc", "<img>-Tags bei TrioBASIC/IEC/PLCOpen-Hilfeausgaben behalten (standardmäßig aus, um Tokens zu sparen)" },
                { "ControllerValidation", "Controller-Syntaxprüfung (nur Simulator)" },
                { "ControllerValidationDesc", "Bei Verbindung mit einem Simulator Code zeilenweise über EXECUTE parsen validieren." },
                { "EnableThinking", "Erweitertes Denken aktivieren" },
                { "EnableThinkingDesc", "Wenn aktiviert, zeigt die KI ihren Denkprozess, was mehr Token verbraucht" },
                { "BudgetTokens", "Denk-Token-Budget:" },
                { "ThinkingLabel", "Denkprozess" },
                { "ShowThinking", "Denkprozess anzeigen" },
                { "ShowThinkingDesc", "Denkprozess der KI automatisch anzeigen" },
                { "Memory", "Gedaechtnis" },
                { "MemoryTitle", "KI persistentes Gedaechtnis" },
                { "MemoryDesc", "Dieser Inhalt bleibt ueber alle Gespraeche und Neustarts hinweg erhalten." },
                { "ClearMemory", "Gedaechtnis loeschen" },
                { "MemorySaved", "Gedaechtnis gespeichert." },
                { "MemoryEnabled", "Persistentes Gedaechtnis aktivieren" },
                { "MemoryEnabledDesc", "KI kann Benutzereinstellungen ueber Sitzungen hinweg merken" },
                { "LocalizeThinking", "Denken lokalisieren" },
                { "LocalizeThinkingDesc", "Wenn aktiviert, folgt das erweiterte Denken der Sprache von MotionPerfect statt standardmaessig Englisch" },
                { "UseChineseDocs", "Chinesische Dokumentation verwenden" },
                { "UseChineseDocsDesc", "Wenn aktiviert, liefern Befehlsabfragen chinesische Doku (standardmaessig aus = Englisch)" },
                { "DialectMode", "Prompt-Dialekt" },
                { "DialectModeDesc", "Welche Dialektvariante des KI-System-Prompts verwendet wird. Auto = aus Projekt ableiten (nur-IEC-Projekte verwenden IEC, sonst TrioBASIC); oder manuell festlegen." },
                { "DialectAuto", "Auto (nach Projekt)" },
            },
            ["fr"] = new Dictionary<string, string>
            {
                { "Title", "AI助手" },
                { "New", "Nouveau" },
                { "Clear", "Effacer" },
                { "History", "Historique" },
                { "Settings", "Paramètres" },
                { "Send", "Envoyer" },
                { "Stop", "Arrêter" },
                { "Allow", "Autoriser" },
                { "Reject", "Refuser" },
                { "ConfirmMsg", "Autoriser l'IA à exécuter cette opération ?" },
                { "Rejected", "Opération refusée par l'utilisateur." },
                { "NoApiKey", "Clé API non configurée. Cliquez sur 'Paramètres'." },
                { "SettingsSaved", "Paramètres enregistrés." },
                { "SettingsTitle", "Paramètres de l'assistant IA" },
                { "ApiKey", "Clé API :" },
                { "ApiUrl", "URL API :" },
                { "Model", "Modèle :" },
                { "Save", "Enregistrer" },
                { "Cancel", "Annuler" },
                { "Error", "Erreur" },
                { "HistoryTitle", "Historique des conversations" },
                { "NoHistory", "Aucun historique." },
                { "Open", "Ouvrir" },
                { "Delete", "Supprimer" },
                { "Close", "Fermer" },
                { "ConfirmDelete", "Supprimer cette conversation ?" },
                { "Confirm", "Confirmer" },
                { "SelectConversation", "Veuillez sélectionner une conversation." },
                { "EmptyKey", "La clé API ou l'URL ne peut pas être vide." },
                { "AIRequests", "Requête IA" },
                { "Copy", "Copier" },
                { "CopyAll", "Tout copier" },
                { "ShowToolStatus", "Afficher statut outil" },
                { "ShowToolStatusDesc", "Afficher les messages d'etat d'execution des outils en jaune" },
                { "IncludeSkillImages", "Inclure les images dans les recherches de skill" },
                { "IncludeSkillImagesDesc", "Conserver les balises <img> lors du retour de l'aide TrioBASIC/IEC/PLCOpen (désactivé par défaut pour économiser des tokens)" },
                { "ControllerValidation", "Validation syntaxique du contrôleur (simulateur uniquement)" },
                { "ControllerValidationDesc", "Lorsque connecté au simulateur, valider le code ligne par ligne via EXECUTE parse." },
                { "EnableThinking", "Activer la réflexion étendue" },
                { "EnableThinkingDesc", "Si activée, l'IA affiche son raisonnement, consommant plus de tokens" },
                { "BudgetTokens", "Budget tokens de réflexion :" },
                { "ThinkingLabel", "Processus de réflexion" },
                { "ShowThinking", "Afficher le processus de réflexion" },
                { "ShowThinkingDesc", "Afficher automatiquement le raisonnement de l'IA" },
                { "Memory", "Mémoire" },
                { "MemoryTitle", "Mémoire persistante de l'IA" },
                { "MemoryDesc", "Ce contenu persiste à travers toutes les conversations et redémarrages." },
                { "ClearMemory", "Effacer la mémoire" },
                { "MemorySaved", "Mémoire enregistrée." },
                { "MemoryEnabled", "Activer la mémoire persistante" },
                { "MemoryEnabledDesc", "L'IA peut mémoriser les préférences utilisateur entre les sessions" },
                { "LocalizeThinking", "Localiser la réflexion" },
                { "LocalizeThinkingDesc", "Si activé, la réflexion étendue de l'IA suit la langue système de MotionPerfect au lieu de l'anglais par défaut" },
                { "UseChineseDocs", "Utiliser la documentation chinoise" },
                { "UseChineseDocsDesc", "Si activé, les recherches de commandes renvoient la documentation chinoise (désactivé par défaut = anglais)" },
                { "DialectMode", "Dialecte de prompt" },
                { "DialectModeDesc", "Quelle variante dialectale du prompt système IA utiliser. Auto = déduire du projet (projets IEC uniquement utilisent IEC, sinon TrioBASIC) ; ou verrouiller manuellement." },
                { "DialectAuto", "Auto (selon projet)" },
            },
        };

        private static string _langCode;

        public static string LangCode
        {
            get
            {
                if (_langCode == null)
                {
                    var culture = System.Threading.Thread.CurrentThread.CurrentUICulture.Name;
                    if (culture.StartsWith("zh")) _langCode = "zh";
                    else if (culture.StartsWith("de")) _langCode = "de";
                    else if (culture.StartsWith("fr")) _langCode = "fr";
                    else _langCode = "en";
                }
                return _langCode;
            }
        }

        public static string S(string key)
        {
            Dictionary<string, string> dict;
            if (_strings.TryGetValue(LangCode, out dict))
            {
                string val;
                if (dict.TryGetValue(key, out val)) return val;
            }
            // Fallback to English
            if (_strings["en"].TryGetValue(key, out var fallback)) return fallback;
            return key;
        }

        /// <summary>
        /// Pick a localized string for one-off system messages.
        /// Only zh/en are translated; other UI languages fall back to en.
        /// </summary>
        public static string L(string zh, string en)
        {
            return LangCode == "zh" ? zh : en;
        }
    }

}
