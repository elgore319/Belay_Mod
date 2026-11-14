## Belay 0.6.27

Fixes from the last playtest.

- **Teeth:** being revived after Teeth (or a Phage) killed you no longer leaves you stuck in place with the Teeth effect on your screen. Teeth backs off after a revive.
- **Teeth hunts the whole team.** It used to chase only the host, and kept going after the host even when the host was dead. It now goes after the nearest living player.
- **The Hadal diver works for everyone.** It got a different ID on each PC, so the way on (the "shortcut in the void") only opened for the host. Everyone also rides the same diver now.
- **The two buttons right after the Teeth climb, and the Hadal diver's buttons, wait for the team.** They do nothing until every living player is gathered at them, so nobody gets stranded below.
- **Spectating keeps the map building.** When you were dead, your game stopped building the map at the next load point, so teammates further up were floating in nothing and revives up there had no level. It now builds on to wherever the team is.
- **Revives wait for the ground.** If a teammate revives you somewhere your game hasn't built yet (after the Hadal diver or a shortcut), you stay down until the map there is built, then get up. Before, you fell through and died.
- **A shortcut someone opens before your game has built that part of the map** now opens for you once it's built, instead of never.
- **Dev menu teleport** builds the map up to a teammate who's far ahead before moving you there.

Everyone in the lobby should update.
