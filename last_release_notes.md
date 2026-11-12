## Belay 0.6.8

Enemy sync:
- **Kill sharing fixed.** Enemies in the levels that load with the run were never tracked, so a teammate's kill couldn't find them. They're tracked from the start now, and if one still isn't known, every loaded enemy is checked.
- **The host runs enemy movement.** Moving enemies near the team stay where the host's game has them, so everyone sees them in the same place. Each player's own game still handles attacks on that player.
- **Enemies chase any player.** An enemy looking for a target picks the nearest player it can see, not only the host.

Everyone in the lobby needs this version (protocol belay-16).
