## Belay 0.5.15

**Diagnostics for getting tabbed out mid-run.**

- Belay now writes a line to `BepInEx\LogOutput.log` whenever the game loses or regains focus, or the mouse cursor's lock changes.
- Each line lists the keys pressed in the 2 seconds before it.
- On a lost-focus line it also names the window that took focus.
- Nothing about gameplay changes.
