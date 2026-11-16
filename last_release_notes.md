## Tethered 0.7.31

- **Bots stop running in circles.** A climbing bot used to copy every step you took, so when you walked around a room (picking things up, looking around) it ran the same loops after you. Now it heads straight for the furthest point along your way that it can walk to directly, skipping your detours, and only follows your exact steps where it has to (climbs, jumps, narrow ledges).
- **Bots notice when they're going nowhere.** Moving used to count as progress, so a bot running in circles never gave up. Now only getting further along your way, or closer to where it's going, counts. After a few seconds without that, it hops ahead along your way, and if that keeps failing it comes to you.

Bots only run on the host, so only the host needs this update for the fix.
