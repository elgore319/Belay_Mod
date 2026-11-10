Fixes from the first playtest. Your game updates to this automatically on the next launch, then asks you to restart.

## Fixed
- **Instant deaths at the interlude.** When one player reached an interlude or breakroom, its scripts jumped the shared death goo up to that room, killing everyone still climbing below. The goo now waits until every living player is above that point.
- **Spectating.** When you died, the camera stayed where you died. It now follows the teammate you're spectating (Tab switches teammates).
- **Levels looked different for each player.** The level order matched, but mirroring, room variants, props and textures were randomized separately on each PC. Each level's randomness now comes from the lobby seed, so everyone climbs the same tower.
- **Different towers for players with different progress.** Some areas only spawn depending on your save (beaten the game, unlocks, facility upgrades, flags). For a lobby run, everyone now uses the host's progress, so the tower matches.

## New
- **Interludes wait for the team.** Interlude and breakroom buttons can't be pressed until every living teammate is inside the room. The HUD shows how many you're waiting for.
- **Shared buttons and controls.** Pressing a level button presses it for everyone, so hatches, lifts, doors and the events they set off (like the elevator drop) happen for the whole team. Hold buttons, cranks, levers and touch panels are shared too: levers swing to match on every screen. Shop and roach buttons stay personal.
- **Level sync checker.** A few seconds after each level loads, every player's copy is compared with the host's (layout, mirroring, variants and textures). The HUD shows "Levels synced: N", and any difference pops up on screen and is written to LogOutput.log with both versions.
- **Mass distance on the HUD**, so you can see how far below you the mass is.
- A warning on the main menu if another multiplayer mod is switched on.
- During a run, the Belay menu (F7) only opens while the game is paused, so it never fights the game for the mouse. It closes when you unpause.

## Install (new players)
1. Download **Belay-0.5.1-installer.zip** and extract it.
2. Close the game and double-click **Install Belay.bat**.
3. Start the game and click **Belay (F7)** on the main menu.

Everyone in a lobby needs the same Belay version.
