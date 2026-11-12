## 0.6.23

Progress-gated doors now follow the furthest-along player.

- **Team progress decides what's unlocked.** Doors and other things that open by themselves once you've unlocked them (like the Habitation blast door) now use the progress of everyone in the lobby put together. If any one player has the unlock, it behaves as unlocked for the whole team, on every PC. Before, each PC was checking its own save, so the door could stay shut for some players.
- This also covers the other save-based checks in a run: progression unlocks, facility upgrades and "has beaten this mode" count if anyone on the team has them.
- Keypad codes and radio settings that the game randomizes per save now use the host's values, so a code one player reads works for everyone.
- Nothing is written to anyone's save: a teammate's unlocks only apply for the co-op run.
