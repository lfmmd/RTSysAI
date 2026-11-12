using System;
using System.Collections.Generic;
using System.Linq;

namespace RTSys.PlugIn
{
    internal static class AiToolPolicy
    {
        internal const string ReadOnly = "read_only";
        internal const string Development = "development";
        internal const string Full = "full";

        private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_status", "list_programs", "list_backups", "read_source", "read_vr", "read_table",
            "list_axes", "list_digital_io", "read_digital_io", "list_analogue_io", "read_analogue_io",
            "list_alarms", "validate_basic", "lookup_command", "read_skill", "discover_skills",
            "search_code", "get_axis_detail", "read_drive_param", "read_ethercat_sdo", "list_plugins",
            "list_project_items", "hmi_list_windows", "hmi_read_window", "hmi_get_feature",
            "task_list", "enter_plan_mode",
            "exit_plan_mode", "research", "review", "debug", "explore", "verify", "controller_status"
        };

        private static readonly HashSet<string> DevelopmentTools = new HashSet<string>(ReadOnlyTools, StringComparer.OrdinalIgnoreCase)
        {
            "write_source", "patch_source", "create_program", "copy_program", "open_program", "save_project", "open_project",
            "restore_backup"
        };

        internal static string Normalize(string mode)
        {
            if (string.Equals(mode, ReadOnly, StringComparison.OrdinalIgnoreCase)) return ReadOnly;
            if (string.Equals(mode, Full, StringComparison.OrdinalIgnoreCase)) return Full;
            return Development;
        }

        internal static bool IsEnabled(string name, string mode)
        {
            mode = Normalize(mode);
            return mode == Full || (mode == ReadOnly ? ReadOnlyTools : DevelopmentTools).Contains(name);
        }

        internal static List<Dictionary<string, object>> Filter(
            IEnumerable<Dictionary<string, object>> definitions, string mode)
        {
            return definitions.Where(tool =>
            {
                object value;
                return tool.TryGetValue("name", out value) && IsEnabled(value?.ToString(), mode);
            }).ToList();
        }
    }
}
