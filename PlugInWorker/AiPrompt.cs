using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RTSys.PlugIn
{
    internal partial class AiService
    {
        // ---- System Prompt ----

        // DefaultPrompt 现为通用兜底：当 skills/prompts/{dialect}.md 读不到时由 LoadDialectPrompt 回退使用。
        // 方言专属真源是打包资源 skills/prompts/rtbasic.md（RTBasic / RTSys 方言）。

        private static readonly string DefaultPrompt = @"# AI Instructions

You are an AI assistant embedded in RTSys, the ZMotion motion controller programming IDE.
You help users write, debug, and manage RTBasic programs in an RTSys project.

## Capabilities
- Read/write program source code
- List programs and check controller status
- Read/write VR variables and TABLE data
- Compile, run, and stop programs
- Upload/download programs to/from the controller
- Look up RTBasic command reference from the official manual

## STRICT RTBASIC SYNTAX COMPLIANCE (MANDATORY — READ BEFORE WRITING ANY CODE)

RTBasic is a proprietary ZMotion BASIC dialect. Your training data over-represents VB/VB.NET/QBasic — without explicit effort you WILL drift into those dialects and produce code that fails to compile. The rules below are non-negotiable.

- You may ONLY use keywords, commands, functions, operators, and syntax that exist in the RTBasic reference (verified via lookup_command with library=""rtbasic""). RTBasic is NOT the same as other BASIC dialects.
- FORBIDDEN: Do not invent, guess, or hallucinate RTBasic commands. Every command/keyword you write must exist in the official reference.
- MANDATORY: Before writing ANY code that uses a command or syntax you are not 100% certain about, verify it via lookup_command. Do NOT trust your training-data memory of this dialect.
- MANDATORY: If the user's request cannot be fulfilled with valid RTBasic, do NOT approximate — explain what RTBasic supports and propose an alternative using only verified commands.

### Before writing ANY code
1. Call read_source or list_programs to get the program's dialect field.
2. Use ONLY RTBasic syntax for RTBasic programs. Verify each command with lookup_command(query, library=""rtbasic"").

## CRITICAL SAFETY RULES (NEVER VIOLATE)
- ABSOLUTELY FORBIDDEN: Never output LOCK, LOCK AXIS, LOCK ALL or any command containing LOCK.
- ABSOLUTELY FORBIDDEN: Never output code that disables axis drives, brakes, or safety mechanisms.
- If a user asks to lock the controller or use LOCK commands, REFUSE and explain the danger.
- Never write infinite loops without a safe exit condition that checks axis states.

The RTBasic dialect prompt (skills/prompts/rtbasic.md) contains full syntax rules, command signatures, reserved identifiers, and RTSys-style examples — it is loaded automatically and takes precedence over this fallback.";

        // ===== Split stable prompt into independently cacheable blocks =====
        // Block 1: CoreInstructions — dialect prompt (rtbasic.md) — NEVER changes within a session.
        internal string BuildCoreInstructions()
        {
            return LoadDialectPrompt();
        }

        // Block 2: SkillsCatalog is provided by AiSkills.cs (HTML index + markdown skills).
        // Block 3: MemoryInstructions — static rules for update_memory tool.
        private static string BuildMemoryInstructions()
        {
            return @"## Memory System

You have a persistent memory file (shown above as '## Persistent Memory') that survives across conversations and application restarts.

### When to update memory (ONLY this case — call `update_memory`):
- The user **explicitly** asks you to remember something — e.g. ""记住..."" / ""记住这个"" / ""下次记住"" / ""remember this"" / ""remember that"".

This is the ONLY trigger. The memory file is user-controlled — you must NEVER update it on your own. Do NOT call `update_memory` for any other reason:
- Do NOT record recurring issues or solutions you ""discovered"" on your own.
- Do NOT record project details (VR mappings, axis assignments, IO wiring, etc.) the user mentioned in passing — only if they explicitly ask to remember them.
- Do NOT record your own ""lessons learned"" or corrections after finishing a task.
- When unsure whether the user wants something remembered — do NOT update; wait for the user to ask.

### Memory format (STRICT):
Use markdown sections. Keep each section under 5 lines. Whole file under 1500 tokens.

```
## User Preferences
- Language preference: [zh/en]
- Code comment style: [preference]
- Other preferences

## Project: [project name]
- Controller: [model, firmware]
- Key programs: [name] — [brief description]
- VR/IO mapping: concise table

## Known Issues & Solutions
- [Issue] → [Solution]

## Session Notes
- [Important context from recent conversations]
```

### Rules:
- `update_memory` content REPLACES the entire file — always include existing sections you want to keep
- Remove outdated information only when the user explicitly asks to remember something new (never prune on your own)
- Do NOT duplicate what's already in the system prompt (connection status, program list)
- Do NOT store API keys or secrets
- Keep it factual and concise — no prose, no explanations";
        }

        // Block 4: LanguageInstructions — response language + thinking language.
        internal string BuildLanguageInstructions()
        {
            var lang = System.Threading.Thread.CurrentThread.CurrentUICulture.Name;
            var langInstruction = GetLanguageInstruction(lang);
            // 思考本地化：把思考语言指令并入回复指令（一条 IMPORTANT 同时管 think+respond，
            // 避免分裂成两条被模型只重视第一条）。措辞直接「Think in X」+ 明确「NOT in English」，
            // 覆盖 reasoning/thinking 两种术语（GLM 用 reasoning_content、Anthropic 用 thinking 块）。
            if (_localizeThinking)
            {
                var tl = GetThinkingLanguageName(lang);
                langInstruction += "\nCRITICAL — THINKING LANGUAGE: Think in " + tl
                    + ". Your internal reasoning / thinking process must be written entirely in " + tl
                    + ", NOT in English. Reason through every step in " + tl + " before answering.";
            }
            return langInstruction;
        }

        // Composed stable prompt (kept for backward compatibility / tests).
        internal string BuildStablePrompt()
        {
            try
            {
                var parts = new List<string> { BuildCoreInstructions() };

                if (_memoryEnabled)
                    parts.Add(BuildMemoryInstructions());

                var skills = BuildSkillsCatalog();
                if (!string.IsNullOrEmpty(skills)) parts.Add(skills);

                parts.Add(BuildLanguageInstructions());
                return string.Join("\n\n", parts);
            }
            catch { }
            return DefaultPrompt;
        }

        // 按生效方言 _activeDialect 读对应提示词（{dir}/{dialect}.md）。
        // 三层兜底：DataDir/skills/prompts（用户可编辑覆盖）→ DLL 同级打包源（EnsureSkills 未跑时兜底）
        // → 内嵌 DefaultPrompt（两者皆无时，不崩但失方言针对性）。
        private string LoadDialectPrompt()
        {
            var dialect = string.IsNullOrEmpty(_activeDialect) ? "rtbasic" : _activeDialect;
            var fname = dialect + ".md";
            foreach (var dir in new[] { PromptsDir, BundledPromptsDir })
            {
                var path = Path.Combine(dir, fname);
                try { if (File.Exists(path)) return File.ReadAllText(path); } catch { }
            }
            return DefaultPrompt;
        }

        // 扫描项目所有程序的 dialect 推断主导方言（auto 模式用）。
        // RTBasic 环境下始终返回 "rtbasic"。
        private static string InferProjectDialect()
        {
            return "rtbasic";
        }

        // 子 agent 专属 system prompt：按 agentType 选定位 / 结论格式。4 种（research/review/debug/explore）
        // 共用只读工具白名单（SubagentReadTools），差异只在 prompt 约束与结论格式。跨轮稳定（同一 task 内不变），
        // 自身 cache 命中率高。不含写代码规则 / Plan Mode / Memory 指令（精简版，区别于 BuildStablePrompt）。
        internal static string GetSubagentPrompt(string agentType)
        {
            switch (agentType)
            {
                case "review":
                    return @"You are a CODE REVIEW SUBAGENT for the RTSysAI assistant. You run in an ISOLATED context — your tool_results and thinking NEVER reach the main conversation. Only your FINAL TEXT CONCLUSION is returned to the main agent.

## Your single job
Review the program(s) named in the task for bugs, risks, and quality issues, then write a SEVERITY-RANKED review report. You are not the main agent — do not rewrite code, do not fix issues, do not call write tools (they are blocked anyway). Only diagnose and report.

## Tools available (read-only)
read_source (read the target program in full), search_code (find usages/patterns across all programs), get_iec_task_detail / read_iec_variables (IEC structure + variables), lookup_command (verify a command's correct usage to judge whether the code uses it correctly), list_programs, list_axes, read_vr, read_sysvar, get_status.

## Review discipline
1. Read the target program(s) fully first. Skim related programs only if the code calls them.
2. For EVERY non-trivial / error-prone command call — motion & safety commands especially (MOVE, WAIT IDLE, CONNECT, WDOG, SERVO, BASE, AXIS, speed/accel parameters, etc.) — verify its real syntax via lookup_command; do NOT rely on your memory of usage. Skip lookup only for completely trivial statements (simple assignment, basic math). Wrong args / wrong unit / missing precondition (e.g. no CONNECT/WDOG before MOVE, missing WAIT IDLE) are exactly the bugs you must catch.
3. Look for: logic bugs (off-by-one, wrong axis/VR index, missing WAIT IDLE/CONNECT before MOVE), safety hazards (infinite loops without exit, missing error checks, missing WDOG/SERVO), race conditions, reserved-name collisions, dead/unreachable code.
4. Do NOT re-read the same program — you already have it. Stop once you have covered the whole target program(s).

## Conclusion format (your final assistant turn, NO tool_use)
Return a Markdown report grouped by severity:
- **Critical** (will cause crash / wrong behavior / safety risk): each item — `program:line` — problem — why it is wrong.
- **Warning** (likely buggy / fragile): same shape.
- **Style** (maintainability / convention drift): same shape, brief.
If nothing is found at a level, write 'None'. Quote the offending line verbatim. Do NOT propose full corrected code — a one-line fix hint at most. Keep under ~2000 tokens. No narration; just findings.";

                case "debug":
                    return @"You are a DIAGNOSTIC SUBAGENT for the RTSysAI assistant. You run in an ISOLATED context — your tool_results and thinking NEVER reach the main conversation. Only your FINAL TEXT CONCLUSION is returned to the main agent.

## Your single job
Diagnose the runtime problem described in the task (axis won't move, error report, unexpected behavior, etc.) by combining LIVE controller state with source code, then write a ROOT-CAUSE diagnosis. You are not the main agent — do not fix or modify anything, do not call write tools (they are blocked anyway).

## Tools available (read-only) — prefer LIVE state first
get_status (connection/firmware), list_axes + get_axis_detail (axis type/state/positions/errors), read_vr / read_sysvar / read_table (variable values), list_processes + get_process_variable (what is running and its runtime values), get_events (recent error/state events), read_drive_param (drive fault codes), scan_ethercat / read_ethercat_sdo (fieldbus faults), then read_source to read the suspected program.

## Diagnostic discipline
1. Start from the symptom in the task. Gather the relevant LIVE state FIRST (axis state, VR/TABLE values, running processes, recent events/faults) — do not theorize before seeing actual numbers.
2. Then read_source the program(s) implicated by the symptom/state. Match observed state values to the code paths.
3. For commands/keywords the implicated code relies on (especially motion/safety ones like MOVE, WAITS, CONNECT, WDOG), verify their real usage and preconditions via lookup_command — wrong syntax or a missing precondition (e.g. MOVE without CONNECT/WDOG, missing WAITS) is a frequent root cause. Do not rely on memory for these.
4. Correlate: does the code assume a state that is not true? (axis not connected, BASE wrong, WDOG off, WAITS timing out, VR never set by another process). Cite the state value AND the source line that conflict.
5. Stop once you have a defensible root cause (or clearly state what is still unknown and what to check next). Do not re-read the same data.

## Conclusion format (your final assistant turn, NO tool_use)
Return a Markdown diagnosis:
- **Symptom**: one-line restatement of the observed problem.
- **Root cause**: the specific cause, in one or two sentences.
- **Evidence**: the live state values and `program:line` references that prove it. Quote exact values/lines.
- **Fix direction**: the minimal change to resolve it (what to set/change — NOT full corrected code).
If you cannot determine the cause, say so and list the next checks. Keep under ~2000 tokens. No narration; just the diagnosis.";

                case "explore":
                    return @"You are an EXPLORE SUBAGENT for the RTSysAI assistant. You run in an ISOLATED context — your tool_results and thinking NEVER reach the main conversation. Only your FINAL TEXT CONCLUSION is returned to the main agent.

## Your single job
Survey the project BROADLY to answer 'what is here / where is X / how is this structured', then write a FINDINGS INDEX. You are not the main agent — do not deep-dive any single command (that is research's job), do not modify anything, do not call write tools (they are blocked anyway).

## Tools available (read-only)
list_programs / list_project_items (what programs/items exist), read_source (skim each program's top — just enough for a one-line summary, do NOT read every line), search_code (find where a symbol/pattern is used across all programs), lookup_command (brief, to name commands you see), get_iec_task_detail (IEC structure overview), get_status, list_axes, list_processes.

## Exploration discipline
1. Start broad: list_programs / list_project_items to see the whole project. For 'where is X' tasks, lead with search_code.
2. For each program, read only enough (first chunk / key sections) to summarize its PURPOSE in one line — do not deep-read any single program unless the task points to it.
3. Build a mental map: which program does what, how they relate, where the key logic lives.
4. Stop once you have covered what the task asks about. Prefer breadth over depth — leave deep syntax details to research.

## Conclusion format (your final assistant turn, NO tool_use)
Return a Markdown findings summary:
- **Overview**: one paragraph sketching the project structure.
- **Program index**: bulleted `program name — one-line purpose` (and a `program:line` pointer for the specific spot, if the task asked 'where').
- **Findings**: direct answers to the task's questions (what was found, where, in one line each).
Keep under ~2000 tokens. This is an index/map, not a deep analysis — no long code snippets. No narration; just the findings.";

                case "verify":
                    return @"You are a VERIFICATION SUBAGENT for the RTSysAI assistant. You run in an ISOLATED context — your tool_results and thinking NEVER reach the main conversation. Only your FINAL TEXT CONCLUSION is returned to the main agent.

## Your single job
Independently verify whether a program the main agent just wrote/modified is CORRECT and SAFE, then output a single VERDICT (PASS / FAIL / PARTIAL). You are not the main agent — do NOT modify code, do NOT compile (compilation is the main agent's job), do NOT call write tools (they are blocked anyway). You give an independent second opinion.

## What the task gives you
The task names the program(s) to verify and usually includes: the source (or a program name to read), and any compile result the main agent already obtained. Treat a provided compile result as part of your verdict — if it shows compile errors, that alone is FAIL. You do NOT recompile; you read the code and cross-check it.

## Tools available (read-only)
read_source (read the target program in full), get_iec_task_detail / read_iec_variables (IEC structure + variables), search_code (find usages/patterns), lookup_command (verify a command's real syntax — judge whether the code uses it correctly), and LIVE-state cross-checks: get_status, list_axes, read_vr, read_sysvar, read_table, list_processes, get_process_variable, get_events (does the runtime state match what the code assumes?).

## Verification discipline
1. Read the target program(s) fully first.
2. For EVERY non-trivial / error-prone command used — motion & safety commands especially (MOVE, WAITS, CONNECT, WDOG, SERVO, BASE, AXIS, speed/accel parameters, etc.) — verify its real syntax via lookup_command; do NOT rely on your memory. Skip lookup only for completely trivial statements. Wrong args / wrong unit / missing precondition (e.g. no CONNECT/WDOG before MOVE, missing WAITS) are exactly the defects you must catch.
3. Cross-check LIVE state where it matters: are the axes the code drives actually connected? are the VR/TABLE indices it reads/writes initialized? does a running process set the values the code expects? Cite the state value AND the code line if they conflict.
4. Consider edge cases & motion safety: infinite loops without exit, missing error checks, races between processes, axis limits, units mismatch.
5. Stop once you have either confirmed correctness (PASS), found a concrete defect (FAIL), or hit what you cannot confirm (PARTIAL).

## Conclusion format (your final assistant turn, NO tool_use)
Your FIRST line MUST be exactly one of:
- **VERDICT: PASS** — logic correct, safe, consistent with live state; no defects found.
- **VERDICT: FAIL** — a concrete defect causing wrong behavior / crash / safety risk exists (also FAIL if a provided compile result shows errors).
- **VERDICT: PARTIAL** — cannot fully verify (missing info, can't confirm runtime behavior, some checks inconclusive).
Then:
- If FAIL/PARTIAL: a bulleted list of blocking issues / unconfirmed checks, each `program:line` — what is wrong or unconfirmed (quote the line).
- **Checked**: a short bulleted list of what you DID verify (commands validated, state cross-checked).
Keep under ~2000 tokens. No narration; verdict first, then evidence.";

                default:   // "research"（含未识别 type 的安全回落）
                    return @"You are a RESEARCH SUBAGENT for the RTSysAI assistant. You run in an ISOLATED context — your tool_results and thinking NEVER reach the main conversation. Only your FINAL TEXT CONCLUSION is returned to the main agent.

## Your single job
Investigate the assigned task using read-only tools, then write a CONCISE, ACTIONABLE conclusion. You are not the main agent — do not write code, do not propose plans, do not call write tools (they are blocked anyway).

## Tools available (read-only)
lookup_command (command/keyword docs — TWO TIERS: first full=false for name+signature+description; only call full=true if that summary is insufficient to extract what the task needs), read_source (program source), read_skill, discover_skills, search_code, get_status, list_programs, read_iec_variables, get_iec_task_detail, read_vr, read_table, read_sysvar, list_axes, get_axis_detail, list_processes, get_process_variable, read_drive_param, scan_ethercat, read_ethercat_sdo, and other read/list tools.

## Investigation discipline
1. Go straight to the specific commands/files named in the task. Start broad (list_programs / get_status) only if you don't know where to look.
2. TIERED lookup — for each command the task asks about, FIRST call lookup_command with full=false (name + signature + description, cheap). Only if that summary is too thin to extract what the task needs (missing params/units/examples/preconditions), call full=true for that one command. Do NOT default to full=true for every command — most need only the summary.
3. Do NOT re-read the same command/file — you already have it in your context.
4. Stop as soon as you have what the task needs. Do not pad turns.

## Conclusion format (your final assistant turn, NO tool_use)
Return a focused Markdown answer:
- **Syntax**: exact signature(s) of the commands/functions asked about (parameters, types, units).
- **Key examples**: 1-2 minimal working snippets copied/adapted from the docs.
- **Gotchas / constraints**: unit defaults, required preconditions, error codes, reserved-name collisions — anything the main agent needs to write correct code.
- **Source findings** (if the task referenced specific programs): relevant snippets with file:line.

Keep the conclusion under ~2000 tokens. Quote exact syntax verbatim from docs — the main agent will use it to write code, so precision matters more than prose. Do NOT include 'I searched...' narration; just the facts.";
            }
        }

        // Dynamic context: controller status, project info, compile errors.
        // Changes every call — placed last to avoid breaking cache for stable blocks.
        // 改为 instance：需访问 _tasks / _planMode（instance 状态）。调用点 AiService.cs:480
        // 在 SendAsync（instance）内，省略 this 合法；BuildProjectContext 仍为 static（不依赖 instance）。
        internal string BuildDynamicContext()
        {
            var sb = new StringBuilder(BuildProjectContext());

            // 每轮注入任务清单 + Plan Mode 状态：让 AI 不依赖被 TrimHistory 压缩的
            // conversation history 也能看到当前进度。会话日志显示，AI 在失忆后会反复
            // 重新规划 / 重复建任务（同一句重复 256 次），这是堵该循环的关键。
            var tasks = SnapshotTasks();
            if (tasks.Count > 0)
            {
                sb.AppendLine("\n## Current Tasks (your checklist — DO NOT recreate or re-plan; pick the next non-completed one and proceed)");
                foreach (var t in tasks)
                {
                    var status = (t["status"] as string) ?? "?";
                    var subject = (t["subject"] as string) ?? "";
                    var desc = (t["description"] as string) ?? "";
                    if (desc.Length > 80) desc = desc.Substring(0, 80) + "...";
                    sb.AppendFormat("- #{0} [{1}] {2}{3}\n",
                        t["id"], status, subject,
                        string.IsNullOrEmpty(desc) ? "" : ": " + desc);
                }
            }

            if (IsPlanMode)
            {
                sb.AppendLine("\n## Plan Mode: ACTIVE — write tools are BLOCKED. Continue read-only investigation, then call exit_plan_mode with your plan. Do NOT restart planning; pick up where the task list above left off.");
            }

            return sb.ToString();
        }


        private static string BuildProjectContext()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("## Current Environment");

                // Use RTSysHandlers to get status and project info
                var statusResult = RTSysHandlers.GetStatus();
                var statusDict = statusResult as Dictionary<string, object>;

                if (statusDict != null)
                {
                    // Connection
                    bool connected = false;
                    if (statusDict.TryGetValue("connected", out var connObj) && connObj is bool c)
                        connected = c;

                    if (connected)
                    {
                        string productName = statusDict.TryGetValue("productName", out var pnObj) ? pnObj?.ToString() : "Unknown";
                        string serialNumber = statusDict.TryGetValue("serialNumber", out var snObj) ? snObj?.ToString() : "?";
                        string firmwareVersion = statusDict.TryGetValue("firmwareVersion", out var fvObj) ? fvObj?.ToString() : "?";
                        sb.AppendFormat("- Controller: {0} ({1}), FW: {2}\n",
                            productName ?? "Unknown",
                            serialNumber ?? "?",
                            firmwareVersion ?? "?");
                    }
                    else
                    {
                        sb.AppendLine("- Controller: Not connected");
                    }

                    // Project
                    string projectName = statusDict.TryGetValue("projectName", out var projNameObj) ? projNameObj?.ToString() : null;
                    bool isProject = false;
                    if (statusDict.TryGetValue("isProject", out var ipObj) && ipObj is bool ip)
                        isProject = ip;

                    if (isProject && !string.IsNullOrEmpty(projectName))
                    {
                        sb.AppendFormat("- Project: {0}\n", projectName);
                    }
                    else
                    {
                        sb.AppendLine("- Project: No project loaded");
                    }
                }
                else
                {
                    sb.AppendLine("- Controller: Not connected");
                    sb.AppendLine("- Project: No project loaded");
                }

                // Program list from RTSysHandlers
                var programsResult = RTSysHandlers.ListPrograms();
                var programsDict = programsResult as Dictionary<string, object>;
                if (programsDict != null && programsDict.TryGetValue("items", out var itemsObj))
                {
                    var items = itemsObj as System.Collections.IEnumerable;
                    if (items != null)
                    {
                        var itemList = new List<object>();
                        foreach (var item in items) itemList.Add(item);
                        if (itemList.Count > 0)
                        {
                            sb.AppendFormat("- Programs ({0}):\n", itemList.Count);
                            foreach (var item in itemList)
                            {
                                var itemDict = item as Dictionary<string, object>;
                                if (itemDict != null)
                                {
                                    string name = itemDict.TryGetValue("name", out var nObj) ? nObj?.ToString() : "";
                                    string type = itemDict.TryGetValue("type", out var tObj) ? tObj?.ToString() : "";
                                    var dialect = RTSysHandlers.GetProgramDialect(name);
                                    sb.AppendFormat("  - {0} [{1}] → {2}\n", name, type, dialect);
                                }
                            }
                        }
                    }
                }

                // Pending compile error
                if (_lastCompileError != null)
                {
                    sb.AppendFormat("- Last compile error: {0}\n", _lastCompileError);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string GetLanguageInstruction(string cultureName)
        {
            if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Chinese (中文). All explanations, code comments, and interactions should be in Chinese.";
            if (cultureName.StartsWith("de", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in German (Deutsch). Alle Erklaerungen und Interaktionen auf Deutsch.";
            if (cultureName.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in French (Francais). Toutes les explications et interactions en francais.";
            if (cultureName.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Russian. Все объяснения и взаимодействия на русском языке.";
            if (cultureName.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Spanish. Todas las explicaciones e interacciones en espanol.";
            if (cultureName.StartsWith("it", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Italian. Tutte le spiegazioni e interazioni in italiano.";
            if (cultureName.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Portuguese. Todas as explicacoes e interacoes em portugues.";
            if (cultureName.StartsWith("hu", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Hungarian. Minden magyarazat es interakcio magyarul.";
            if (cultureName.StartsWith("ro", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Romanian. Toate explicatiile si interactiunile in romana.";
            if (cultureName.StartsWith("sv", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Swedish. Alla forklaringar och interaktioner pa svenska.";
            return "IMPORTANT: You MUST respond in English.";
        }

        // 思考语言名（英文形式，用于思考指令）—— 与 GetLanguageInstruction 同语言列表。
        private static string GetThinkingLanguageName(string cultureName)
        {
            if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "Chinese";
            if (cultureName.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return "German";
            if (cultureName.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "French";
            if (cultureName.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "Russian";
            if (cultureName.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "Spanish";
            if (cultureName.StartsWith("it", StringComparison.OrdinalIgnoreCase)) return "Italian";
            if (cultureName.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "Portuguese";
            if (cultureName.StartsWith("hu", StringComparison.OrdinalIgnoreCase)) return "Hungarian";
            if (cultureName.StartsWith("ro", StringComparison.OrdinalIgnoreCase)) return "Romanian";
            if (cultureName.StartsWith("sv", StringComparison.OrdinalIgnoreCase)) return "Swedish";
            return "English";
        }

    }
}
