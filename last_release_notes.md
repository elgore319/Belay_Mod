## Belay 0.7.16

Two co-op fixes. Neither has been tested in a real co-op run yet.

- **Two players grabbing the same item at the same time no longer both get it.** The host now decides who got there first, and the other player's copy goes away. Items that are one per player (the only one of their kind in a level) still give everyone their own.
- **If the host quits mid-run, held doors and shortcut votes carry on.** Every player now knows who has passed each held door, so whoever becomes host keeps holding the door until everyone is through, then shuts it. A shortcut vote that was running keeps the votes already cast and finishes on the new host. Shortcuts that were still queued for a vote get asked again at the next terminal.

This changes the network format, so everyone needs 0.7.16 to play together. Games update themselves at launch.
