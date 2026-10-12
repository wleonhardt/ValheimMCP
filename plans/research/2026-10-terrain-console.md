# Terrain editing through World Edit Commands (option C), analysis

Status: draft, written before any live check. Everything here comes from reading code and from decision 0006.

## Question

World Edit Commands (a separate client mod) adds a `terrain` console command that sets height and paint over a radius. Can ValheimMCP offer it safely, so an agent does not need the hoe-op route of `terrain_paint` and `terrain_level`?

## What is known

- The command is a console command, so it would go through `run_command`, which has an allow list in `config/valheimmcp.client.yml`. The list is a security setting. Do not widen it without the user saying so.
- Our own tools already apply the game's registered terrain ops and save the terrain data of each zone first (`ext/TerrainEdit.cs`). A console wrapper would need the same snapshot before and the same undo after, or it is an unguarded write.
- A console command knows nothing of `Plans.Protected`, guard stones or no-build locations, and it is not journaled.

## Plan

1. Check with `list_commands` on the live client whether the command exists in the installed build and what its arguments are. Not done yet.
2. If it exists, wrap it as a tool that (a) computes the zones its radius can reach, (b) snapshots their terrain data with `TerrainEdit`, (c) applies the same ward and no-build refusals, (d) runs the command through the existing command path with an exact, generated command line (no free text from the caller), (e) journals a kind `terrain` entry so `undo` works, (f) compares the terrain data before and after.
3. The allow list change is a single exact entry. Ask the user first.

## Open

- Whether the command is present in the installed mod set at all.
- Whether it edits through the terrain compiler (undo by data snapshot works) or by another route (then a wrapper cannot undo it and should not be built).
