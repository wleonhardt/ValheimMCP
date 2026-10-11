# Offline code review of the 0.7-0.9 work (2026-10-11)

The game was not available, so three read-only reviewers read the game-touching code of the new tools against the decompiled game (plans, journal and locations; walker, chat and events; the new readers). Findings that were checked and certain are fixed (see CHANGELOG, "review fixes"). This file records what was found and what was left.

## Most important find

Containers keep their inventory as a **byte array** under `items` since the chunked save format (`Container.Load` uses `GetByteArray`); only an old save holds the base64 string, which `ZDOMan.ConvertContainers` converts at load. Every reader of the string saw every chest empty, and `container_set` wrote a key the game never reads. Fixed with `ContainerData` (Common.cs). Lesson: read a ZDO value with the type the game's own writer uses. The ZDO key names were audited against `ZDOVars` the same day (no misspelling); their types were not, so the next audit should check type as well as name.

## Fixed

Chat: the id in a chat payload is written by the sender's client and relayed untouched, so it is now compared with the connection it arrived on (`id_mismatch`); ids of any other shape are dropped; player names in `players` and `world_events` are cleaned; invisible text is removed by Unicode category (the Tag block spelled whole instructions invisibly); positions are refused in any writing except a whisper to a trusted online player; the `to` argument is canonicalised and a global budget covers lines with no listener; the echo guard works for unspaced languages; one line relayed once per recipient is one row.
Plans and journal: a `y` that is not a number counts as missing; `seat:"ground"` falls back to the world generator's height; NaN `rot` is refused; error numbers are the caller's after the sort; the journal flush interval was 600 items, not 150; recreating a bad-prefab piece respects wards; prefabs without a ZNetView are refused; the collapse check refuses when it could not run; `zdo_delete` dedupes ids; undo refuses entries of another world for every kind and scans the world once per undo; `world_save` only on a server; ZDO ids with an unknown user are not looked up; `m_noSupportWear` respected; creatures are never instantiated to read colliders; `zdo_audit fix` moves a wrong-sector entry instead of dropping it (it could lose portals from the save).
Readers: `bed_check` tells no roof from too little cover; stations from the widened box; `stationsNeeded` counts pieces; `standing:true` in one pass; smelter fuel shortfall; cookers say whether they run; creature attacks include random sets, random items, unarmed; levels, not stars; `base_value` y; `raid_state` `rollsEnabled`; `level_check` volumes and the sum clamp; `Bill` cheapest recipe for "any one" recipes.
Walker: user-override clock reset per walk; `stuckSeconds` honoured; stall total clears after 10 m of progress; a goal without y stays without it through nav; doors that consume their key are not pressed; a failed install unpatches.
Runner: a skip never hides a failed check; an unreadable health check fails an acting case; cookers skip when every slot is empty.

## Not fixed (kept here so they are not lost)

- Walker: no portal avoidance on its own route (only after a stall); `Reroute` drops the remaining user waypoints and uses the Humanoid agent; `walk_to` clears the local debug-fly field but not the ZDO value other clients read; `RunRules.WantRun` and `RunStartMetres` are tested but the walker's run rule is inline and differs.
- `ResolveForUndo`: a modify entry for an untagged object cannot be found after a restart (positions are not trusted for modifies); the message says "gone".
- Plan adoption treats `UserID == 1` as "not worldgen" but every object loaded from a save has that user key, so old creator-0 pieces are never adoptable after a restart.
- `clear_overlaps` misses footprints with r over about 8 m; the default sweep covers +-10500 while `InWorld` accepts +-11000; `Remap` rewrites ids by string match across boots.
- `Wards()` rescans the ZDO table every 10 s while a long job runs; `LocationNoBuildAt` is linear in the number of locations per item.
- `verify_plan`: `extra` counts only creator-0 objects; description mentions a `yawTol` that does not exist; an item without x or z is read as 0. `paint_at` reads `[x,y,z]` as x,y. `objects digest` still builds a full record per ZDO before filtering.
- Env: `time_set` returns the state before the next frame; time and weather share one release timer and `DoRelease` clears a human's debug time; nothing releases on leaving a world.
- Chat: names are still text from the client in `chat_tail` rows beyond cleaning (the `name_suspicious` hint is the only signal); `Audit.Sent` is read without the rows lock; trust by name remains `name_only`.
- `plan_check`: `CollapseOf` uses the generator ground (edits invisible); a plan's collapse check counts only contacts through the support layers.
- Live cases that compare a tool with itself (farm, biome, stations, fuel, base_value) need an independent oracle from the first live session; `world_clock` can fail spuriously within 0.0005 of a phase boundary.
- Day length: `m_dayLengthSec` is read live; a mod-set value of another type is untested.
