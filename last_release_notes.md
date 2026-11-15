## Belay 0.7.18

Bots now climb on their own. This is the first step toward bots as real AI teammates.

- **Climb mode** is the new default for bots (F7 > Bot). A bot has its own body with gravity and collision. It walks, steps up, hops onto ledges, jumps gaps, grabs the same holds you can, pulls up over ledges and can fall. It follows the way you went a few meters behind you, but it makes the moves itself instead of copying yours on a delay.
- **Grip stamina**: each of the bot's hands runs out of grip while it hangs and recovers when it rests, like yours. When grip runs out it lets go. It can catch itself on a hold while falling. Long falls hurt it, and very long ones kill it.
- **Playstyle**: Careful, Steady or Bold. This changes how fast it climbs, how far behind you it stays, how long a gap it will jump and how early it rests.
- **Revive run**: if you go down while a bot is alive, it heads for the nearest terminal (or the next interlude) and revives you there for free. If it can't find a way within about a minute, it revives you where it stands, so you're never stuck spectating.
- If the bot gets stuck it tries a short hop along your route. A shut blast door still keeps it out, and "Bring to me" fetches it. If it keeps failing on an open route, it comes to you.
- **Retrace** (the old "copy my path" bot) and **Hold here** are still there. If the climbing ever hits an error, that bot switches to Retrace by itself instead of breaking your game.
- Not yet: bots don't use or pick up items, and barnacles and enemies don't target them on purpose. That comes next.
