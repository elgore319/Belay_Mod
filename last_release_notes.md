## Belay 0.6.14

- **Shortcuts and main paths are blocked with the game's own doors.** If the team picks the main path, the doors on the shortcut's side never open. If the team picks the shortcut, the main path's doors (the elevator, or the doors at the level's exit) never open. Where there's no door to lock, the red barrier is still used.
- **Shortcut report:** when the game starts, Belay writes `BepInEx/cache/Belay/shortcut-report.txt` listing every shortcut in the game and whether each side has a door it can lock.
