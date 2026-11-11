## Belay 0.5.11

**Shortcut votes now happen at terminals.**

- When someone sits at a terminal, the team votes on every shortcut between that terminal and the next one. If several shortcuts come before the next terminal (for example terminal, shortcut, shortcut), that terminal runs one vote for each, one after another.
- Vote with F1 (shortcut) or F2 (normal path). A tie or no votes means the normal path.
- If the team picks the shortcut, it opens for everyone as soon as someone reaches it, with no second vote. The normal path is blocked when it opens.
- If the team picks the normal path, the shortcut is blocked off.
- A shortcut that nobody voted on yet, because there was no terminal before it or nobody sat at one, still asks the team when someone tries to open it, as in 0.5.10.

Everyone in the lobby needs 0.5.11 (network protocol changed).
