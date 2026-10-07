// Minimal stand-ins for game-side types the linked core sources mention, so they compile without Valheim/BepInEx.
using System.Collections.Generic;

namespace ValheimMCP
{
    internal sealed class CommandResult
    {
        public bool Ok;
        public string Error;
        public List<string> Output = new();
    }
}
