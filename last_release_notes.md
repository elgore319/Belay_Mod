## Belay 0.7.14

More bug fixes found by going through the code. None of these have been tested in a real co-op run yet.

- **Shortcut votes don't wait on dead players.** A dead player can't get to a terminal to vote, but the vote still counted them, so any vote with someone down ran out its whole timer. Votes now count living players only.
- **Vent hands, phages and Trackers don't freeze when their player leaves.** These creatures run on the closest player's PC. If that player left or died with nobody else nearby, the creature froze on every PC. The host now takes it back.
- **A chase-killer stays around while a teammate is loading.** If the host was dead and the last living teammate's game froze while loading a level, the chase-killer gave up and vanished. It now waits 15 seconds, the same as everything else since 0.7.13.

No network changes, so it works with teammates on 0.7.13, but everyone should update.
