using System;
using System.Linq;
using System.Reflection;

namespace ValheimMCP
{
    /// <summary>
    ///     Which build is loaded: "commit[.dirty] branch @worktree", read from the AssemblyMetadata attributes the build writes (see the VhBuildStamp target in the
    ///     csproj files). With two worktrees and hot reload, "whose DLL is in the game" is otherwise a guess. Empty parts when built without git.
    /// </summary>
    public static class BuildStamp
    {
        public static string Of(Assembly a)
        {
            try
            {
                string Get(string key) { return a.GetCustomAttributes<AssemblyMetadataAttribute>().Where(m => m.Key == key).Select(m => m.Value).FirstOrDefault() ?? ""; }
                return Format(Get("hubner.commit"), Get("hubner.branch"), Get("hubner.dirty"), Get("hubner.worktree"));
            }
            catch (Exception) { return "unknown"; }
        }

        public static string Format(string commit, string branch, string dirty, string worktree)
        {
            if (string.IsNullOrEmpty(commit)) return "no build stamp";
            return commit + (dirty == "true" ? ".dirty" : "") + (string.IsNullOrEmpty(branch) ? "" : " " + branch) + (string.IsNullOrEmpty(worktree) ? "" : " @" + worktree);
        }
    }
}
