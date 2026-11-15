## Belay 0.7.22

- **Closing the pause menu no longer frees your camera at a terminal or during a cutscene.** Since 0.7.20 the co-op pause menu locks your camera while the game keeps running. Closing it always unlocked the camera, even when something else had it locked, such as sitting at a terminal or being held in a cutscene. Now it only unlocks the camera if the menu was what locked it. The console keeps its lock the same way.

I also went over tonight's other changes (teammate animations, the BELAY menu entry, co-op items, bot climbing, pause and carrying) and didn't find anything else that needed fixing. Not tested in a real co-op run yet.

No network changes, so it works with teammates on 0.7.21, but everyone should update.
