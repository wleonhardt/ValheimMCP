// Tool registration filter: the same source files build into the client and the server extension; on the server only tools that make sense without a camera or local player register.
using System;
using System.Collections.Generic;
using ValheimMCP;

namespace HubnerExt
{
    internal static class Reg
    {
#if SERVER
        static readonly HashSet<string> ServerTools = new HashSet<string> { "terrain_info", "terrain_profile", "raycast", "surface_probe", "headroom", "stability_scan", "walk_check", "sign_check", "bed_check", "creatures", "object_info", "prefab_info" };
#endif
        /// <summary>Category per tool for tools.profile (core 0.4.1+): read | ops | build | control. Tools not listed count as misc (shown in every profile).</summary>
        internal static readonly Dictionary<string, string> Category = new Dictionary<string, string>();
        static Reg()
        {
            foreach (var n in "terrain_info terrain_grid raycast object_info prefab_info zone_state render_ex walk_check stability_scan inspect_type objects portals terraform_map verify_plan snapshot snapshot_diff prefab_search world_state ext_info validate_placement terrain_profile surface_probe room_view containers creatures sign_check bed_check headroom paint_at prefab_find visible_objects probe_fan base_survey player_status player_state ready_state nav_path menu_state env_state plans plan_stats doors zdo_dump locations find_text journal journal_groups chat_tail vpeer_list vpeer_probe".Split(' ')) Category[n] = "read";
            foreach (var n in "save_state players status selftest log_tail job_status zdo_audit".Split(' ')) Category[n] = "ops";
            foreach (var n in "spawn modify delete undo plan_apply clear_overlaps plan_remove zdo_set container_set zdo_delete doors_set undo_group world_save job_start journal_compact vpeer_add vpeer_remove".Split(' ')) Category[n] = "build";
            foreach (var n in "teleport walk_to walk_status walk_stop menu_join menu_password interact chat_send chat_bubble time_set weather_set env_release".Split(' ')) Category[n] = "control";
        }
        /// <summary>Hand the table to the core (reflectively: a core older than 0.4.1 has no SetCategories and must keep working).</summary>
        public static void Categorize(ToolRegistry r)
        {
            try { var m = typeof(ToolRegistry).GetMethod("SetCategories"); if (m != null) m.Invoke(r, new object[] { Category }); } catch (Exception ex) { U.Once("Categorize", ex); }
        }

        public static void Add(ToolRegistry r, string name, string description, string schemaJson, Func<Dictionary<string, object>, ToolOutput> handler, bool write = false)
        {
#if SERVER
            if (!ServerTools.Contains(name)) return;
#endif
            r.Add(name, description, schemaJson, handler, write);
        }
    }
}
