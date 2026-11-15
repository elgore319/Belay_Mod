## Belay 0.7.23

Small fixes from going over the older co-op systems (doors, room gates, elevators, room sequences, props, placed objects, level checks, team progress, trinkets). Not tested in a real co-op run yet.

- **Less stutter while room sequences catch up.** When a teammate started an interlude or breakroom sequence somewhere your game hadn't built yet, Belay checked for it once a second for up to 15 minutes, scanning every trigger in the map once per waiting sequence. It now scans once per check for all of them.
- **Team progress can't fail to save at run start.** If the host's team progress snapshot was too big for the lobby and had nowhere tidy to cut it, building it could throw an error. It now just cuts it.
- The level-mismatch warning now names a local test client correctly instead of showing a blank name.

The doors, gates, elevators, props, placed objects and trinket budget code looked sound.

No network changes, so it works with teammates on 0.7.22, but everyone should update.
