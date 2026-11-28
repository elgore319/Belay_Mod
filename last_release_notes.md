## Not Alone 0.8.42

Works with the White Knuckle patch from November 27. Everyone in a lobby needs 0.8.42 (the network protocol changed).

- **Areas load again.** The November 27 game patch moved where each area keeps its list of levels, which broke the shared level picks. They read the new place now.
- **Shortcuts come back after a restart.** If you restart while the team has taken a shortcut, your game builds that shortcut's path again when you reach it or get revived there, instead of leaving you an empty void.
- **No black screen after a revive.** Being revived after the Face or the vent creature got you no longer leaves the screen dark.
- **The Face no longer throws errors on teammates' PCs.** A Face copied from the host now starts in its first state, instead of erroring every frame.
- **Teammates show up in the shop again.** Players in the same shop or artifact room could count as being in different rooms and were hidden from each other. They're now matched by where the room is, so everyone in the same room sees each other.
