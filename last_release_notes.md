## Belay 0.7.19

More co-op fixes. None of these have been tested in a real co-op run yet.

- **Enemy waves and tick egg sacs go off even when the host is far behind.** Only the host's game runs these. If the host hadn't built that part of the map yet, or had it switched off, nothing spawned for anyone. Now the host hands the area to the teammate who's there. That teammate's game runs it, and everyone else gets the same enemies.
- **Less work per frame with a full lobby.** Belay used to read the Steam lobby's member list again for every packet it passed on and every check that needed it, sometimes thousands of times a second with 8 players. It now reads it once per frame. Waiting spawns no longer scan the whole map once per spawn every second, and the mass's per-level pace check from 0.7.13 runs twice a second instead of every frame.

No network changes, so it works with teammates on 0.7.18, but everyone should update.
