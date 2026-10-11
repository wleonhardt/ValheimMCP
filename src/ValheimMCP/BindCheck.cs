using System;
using System.Collections.Generic;
using System.Reflection;

namespace ValheimMCP
{
    /// <summary>
    ///     Binary-contract check for hot-reloaded extensions. An extension built against a NEWER core can reference a core member the running core lacks;
    ///     the runtime only notices when the JIT first meets that call, in the middle of a tool. This walks the extension's metadata (TypeRef table 0x01,
    ///     MemberRef table 0x0A), resolves every reference and reports those that name the core and do not resolve, so the load can be refused while the
    ///     old extension is still in place. No game types: unit-tested with assemblies compiled on the fly.
    /// </summary>
    internal static class BindCheck
    {
        public static List<string> UnboundCoreRefs(Assembly asm, string coreName, int max)
        {
            var bad = new List<string>();
            bool refsCore = false;
            foreach (var r in asm.GetReferencedAssemblies()) if (r.Name == coreName) { refsCore = true; break; }
            if (!refsCore) return bad;
            foreach (var mod in asm.GetModules())
                for (var table = 0; table < 2 && bad.Count < max; table++)
                    for (int row = 1, unresolvable = 0; row < 1 << 20 && bad.Count < max && unresolvable < 512; row++)
                    {
                        var token = (table == 0 ? 0x01000000 : 0x0A000000) | row;
                        try
                        {
                            if (table == 0) mod.ResolveType(token); else mod.ResolveMember(token);
                            unresolvable = 0;
                        }
                        catch (ArgumentOutOfRangeException) { break; }       // past the end of the table
                        catch (ArgumentException) { unresolvable++; }        // needs a generic context: not ours to judge
                        catch (Exception e) when (e is MissingMemberException || e is TypeLoadException || e is BadImageFormatException || e is System.IO.FileNotFoundException)
                        {
                            if (Names(e.Message, coreName)) bad.Add(e.Message);
                        }
                    }
            return bad;
        }

        /// <summary>Does the resolver's message name the core (and not some game or engine type, which is the game's business)?</summary>
        public static bool Names(string message, string coreName)
        {
            return message != null && message.IndexOf(coreName, StringComparison.Ordinal) >= 0;
        }
    }
}
