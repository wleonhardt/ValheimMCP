# 0004 Running the live checks on default world rules (2026-10-11)

The family server runs with world modifiers (combat easy, death penalty casual, resources much more, portals casual, raids none) and the keys passivemobs and nobuildcost (see its start parameters). Several checks cannot show anything under them: `plan_materials` sums (everything is free), raids, hostile creatures, the default death rule.

How the game stores them (decompiled `FejdStartup.ParseServerArguments`, `World`, `ZoneSystem.SetStartingGlobalKeys`): the modifiers and keys are the world's `m_startingGlobalKeys`, saved in the world file (`.fwl2`). `-resetmodifiers` clears them; `-modifier` and `-setkey` add to them; every start re-applies them. So one start with only `-resetmodifiers` gives default rules, and the original start parameters bring the original ones back.

What was done on 2026-10-11, with the server empty:
1. `world_rules` saved as a snapshot; an online backup (`backup-online.sh`) and a copy of `vhserver.cfg`.
2. `startparameters` in `vhserver.cfg` rewritten: `-modifier ...` and `-setkey ...` removed, `-resetmodifiers` added; restart. `world_rules` then showed no rates and no preset (NoBuildingFall alone stayed, it is a saved key).
3. Baseline checks run (`accept.py` at the test spot: 31 pass, 0 fail, 6 skipped; `plan_materials` gave real costs; `raid_state` rolls enabled every 2760 s at 20 percent, as in the code; `threats_near` hostile).
4. Restore: server stopped, the world directory replaced by the backup, `vhserver.cfg` put back, start. `world_rules` was identical to the snapshot.

Do not rely on the restart alone to restore: restoring the world files from the backup is what makes the state exact (TeleportAll was dropped by the reset). The directory `worlds_local/AxGardDeepNorth.baseline-run-20261011` is the world as it was after the baseline run and can be deleted.
