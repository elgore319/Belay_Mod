## Belay 0.7.13

Bug fixes found by going through the code. None of these have been tested in a real co-op run yet.

- **The mass no longer races ahead when the host is up in a faster area.** The game sets the mass's pace from the level the host is in. In co-op the mass chases the lowest climber, so it now uses the pace of the level that climber is in. A host who had climbed into a faster region made the mass tear through the slower levels everyone else was still on.
- **The mass no longer jumps onto climbers after a scripted rise.** When a level moved the mass up in one go, other players' games kept pushing it up at that speed for up to a second, which could put it right under, or on top of, the lowest climber. Teammates' games now only run normal climbing speeds forward.
- **A teammate whose game freezes while loading a level still counts.** If a teammate's game froze for more than 2 to 3 seconds (a level load), Belay treated them as gone: the mass sped up toward the next player, doors shut and the elevator could leave without them, and if everyone else was dead the run could end. Belay now waits 15 seconds before it stops counting a silent teammate. Players who really leave are still removed right away.
- **Pitons, placed handholds and settled dropped items now show up for every teammate.** With three or more players, things one client placed or hammered only reached the host, not the other clients.
- **Reaching the ending now ends the run for everyone.** When a client reached the ending, only the host got the win. The other clients kept playing.

No network changes, so it works with teammates on 0.7.12, but everyone should update.
