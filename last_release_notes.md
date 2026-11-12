## 0.6.16

- The mass no longer gets held back by its own speed-ups. Levels that push the mass faster (and slow-zone perks) now work in co-op the way they do solo; only real scripted jumps (interlude goo moves) still wait until everyone is above them.
- A teammate who is already under the mass no longer counts as the lowest climber, so a dying player can't slow the mass down for everyone else.
- The far-behind 4x catch-up now looks at the lowest climber instead of the host.
- The host's log now writes a `[Mass]` line every 15 s with how fast the mass is climbing and how far the lowest climber is above it, to tune the pace.
