## Belay 0.6.3

Playtest fixes:
- **Trinkets over budget:** in a lobby nobody but the host presses Play, so a pick over the trinket budget used to come along for free. Picks are now cut back to the budget when the run starts, and you're told which trinkets were left behind.
- **Items spawned loose in the world** (by spawners or creatures, outside a level's own objects) now vanish for everyone when someone picks them up.
- **Props collide with bots.** Bots only exist on your PC, so props now bump into them there. Real teammates still don't push props on your screen; their own PC handles that and the prop's movement syncs.
