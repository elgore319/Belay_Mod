## Not Alone 0.8.19

- **Fewer hitches in co-op.** Several checks that ran every second or two walked every object in the loaded levels (looking for terminals, buttons, room triggers and spawners a teammate used before your area loaded). They now only search when a new area loads, and remember what they found, so the regular stutters during runs should be gone.
- **Faster handling of teammates' actions.** Looking up the button, timer, spawner or breakable a teammate touched is now much quicker, and shared object names are built without throwaway text, which means less garbage collection mid-climb.
- **No more long freezes over missing effects.** When a teammate's thrown item, blast or placed object had no model loaded here, the game searched every loaded asset again for each one. It now waits a bit before searching again.
- Network sends no longer copy the player list for every packet.
