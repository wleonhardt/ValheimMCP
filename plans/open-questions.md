# Open questions

## Open

1. `run_command` now needs `tools.write: true`. The upstream default let the console run whenever the listener was up. Is a read-only console (for example `pos`, `list_commands`) worth a separate switch, or is `commands.allow` plus `tools.write` enough?
2. `hubner.sandboxNames` defaults to empty, so a fresh install never gets god mode. The author's own configs in `config/` still list their characters. Should the example configs also be empty?
3. `world_save` is not a write tool (it only saves) but it is rate-limited to one request per 10 s. Should it need the server write gate?
4. `log_tail` (reads `LogOutput.log`) and `get_log` (core ring buffer) overlap, and `render_ex` has its own camera next to the core's `render_view` camera. Merge, or keep for upstream compatibility?
5. Undo of a delete restores position, rotation, scale, plan tags, text, tag, creator, the extra fields the operation touched and a chest's contents. Other ZDO fields (health, custom mod data) are lost. Is a full ZDO snapshot in the journal wanted (bigger journal, version-specific serialisation)?
6. CI cannot compile the plugin. A NuGet package of publicized game libraries (ValheimGameLibs) might let it; it needs to track the current game version.
7. `Assembly.Load(bytes)` leaks one assembly per hot reload (Mono cannot unload). Acceptable for a development server, or restart after N reloads?

## Resolved

- 2026-10-11: does the console's `skipAllowedCheck` bypass cheat gating? No. In the decompiled `ConsoleCommand.IsValid` it replaces only the session's allowed-command check; `IsCheat` still needs devcommands. Documented in the README.
