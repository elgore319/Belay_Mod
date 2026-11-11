## Belay 0.5.14

**Fix: getting tabbed out to another monitor while playing.**

- Closing the Belay menu after opening it from the pause menu left the mouse cursor unlocked for the rest of the run. You can't see it, but it drifts, and on a two-monitor setup the next click can land on the other screen and pull the game out of focus.
- Belay now locks the cursor again whenever you're playing. While the menu is open in a run, the cursor is kept inside the game window.
