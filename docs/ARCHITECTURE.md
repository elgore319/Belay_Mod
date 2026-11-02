# Belay architecture and game notes

Findings from decompiling White Knuckle's `Assembly-CSharp.dll` (Unity 6000.3.9, Mono, Steam app 3195790), plus how Belay is built on top of them.

## Game facts that shape the design

| Area | What the game does | Consequence for multiplayer |
|---|---|---|
| Player | `ENT_Player` (~190 KB of code) is a singleton: `ENT_Player.playerObject`, `CL_GameManager.gMan.localPlayer`. Movement is a `CharacterController` driven by `vel` (`cCon.Move(vel * fixedDeltaTime * 175)`). Two hands in `ENT_Player.hands[]`, each with `interactState` (`hanging`, `grab`, `hold`, ...) and `handModel` transform. | Remote players can't be extra `ENT_Player`s: 174 game files assume one player. Teammates are lightweight puppets (`RemotePlayer`). |
| World generation | `WorldLoader.Initialize()` takes `presetSeed` (set via `WorldLoader.SetPresetSeed`), then each level is `Initialize(seed + i*999)`. Continuing generation adds `seedOffset` and re-seeds `UnityEngine.Random`. `CL_SaveManager` can call `WorldLoader.IncrementSeed`. | Feeding the same preset seed gives the same tower. Belay patches `Initialize` (prefix) and blocks `IncrementSeed` while in a lobby. |
| Floating origin | When a level passes y=10000, `CL_GameManager.SetWorldOffset` shifts the whole world, every `GameEntity`, and rescans A*. Each client does this independently. | All network positions are sent in "corrected" space (`CL_GameManager.GetCorrectedPosition`) and converted back on arrival (`WorldSpace`). |
| Enemies | `Denizen : AIGameEntity : GameEntity` with movement/attack modules and `AIGameEntity.target`. ~45 `DEN_*` types. All `GameEntity`s are in `GameEntity.entities`. Many AIs read `ENT_Player.playerObject` directly. | Plan: host-authoritative enemies. Host runs AI, retargets to the nearest player, streams transforms; clients disable AI on their copies. Each `DEN_*` that hard-codes the local player needs a small Harmony patch. |
| Death goo | `DEN_DeathFloor` (singleton `instance`) rises with `speed` + `speedIncreaseRate`, starts when the player passes `playerStartHeight`. | Host-owned: host streams goo height; clients pin theirs to it. Rises based on the lowest (or average) player depending on gamemode. |
| Items / props | `Item`, `Inventory`, `HandItem_*` (piton, grappling hook, shoot...), physics props `CL_Prop` (Rigidbody). | Item pickups/drops and placed pitons become reliable events with network IDs. |
| Doors | `UT_Door` moves between open/closed via `Open()`, `Close()`, `SetOpenAmount()`. It's triggered by scene `UnityEvent`s (trigger volumes, levers), so the first player through a trigger fires it locally. | Doors get IDs from their level and hierarchy path (identical on every client thanks to the shared seed). A local `Close()` becomes a request to the host instead of closing at once. |
| Gamemodes | `M_Gamemode` assets, chosen in `UI_GamemodeScreen.LoadGamemode` (sets static `CL_GameManager.gamemode`, loads `gamemodeScene`). Modules: `GamemodeModule_Standard/Challenge/Training`. | Host's choice should be broadcast so clients auto-follow into the run. |
| Steam | `SteamManager` calls `SteamClient.Init(3195790)` and pumps `RunCallbacks()`. | Belay just waits for `SteamClient.IsValid`; no second init. |

## Belay today (0.1.0)

```
Plugin              BepInEx entry, config, Harmony
Net/NetSession      Steam lobby (create/join/browse/invite) + P2P transport, star topology via host
Net/Packets         Packet types and PlayerState wire format
Players/WorldSpace  Local <-> shared coordinate conversion (floating origin)
Players/PlayerSync  20 Hz local state broadcast, RemotePlayer lifecycle
Players/RemotePlayer Puppet body/hands/name tag with smoothing
Rope/Tether         Chain rope in join order: spring pull + fall catch when partner grips
Patches/WorldLoaderPatches  Lobby seed into WorldLoader, block IncrementSeed
UI/LobbyWindow      F7 IMGUI window + HUD line
```

Wire format: `[PacketType:byte][origin SteamId:ulong][payload]`. Channel 0 reliable, channel 1 unreliable state. Clients send only to the host; the host validates that `origin == sender` and relays. Lobbies are tagged `belay=1` and `belay_ver=<protocol>` so vanilla lobbies and other mods' lobbies are ignored.

## Roadmap

1. **Run flow**: host's gamemode choice and "start run" broadcast; clients auto-load. Late-join teleports to the host. Death and respawn sync (spectate teammates while dead).
2. **Death goo and doors**: host-authoritative goo height. Door sync: when the first player crosses a door's close trigger, the host starts a shared countdown (default 15 s, host-configurable 10-20 s) shown on everyone's HUD, then closes the door for all players at once. Players still on the wrong side when it shuts are teleported to the group (or the gamemode decides).
3. **Enemies**: network IDs for `GameEntity` spawns (deterministic from seed where possible), host AI with nearest-player targeting, client puppets, damage requests to host.
4. **Items and pitons**: pickup/drop/use events, placed pitons and rebar are shared climbable anchors.
5. **Stay-together mechanics**: rope stamina sharing, "anchor" bonus when a partner is gripping, goo speeds up when the group spreads out, revive by reaching a downed teammate.
6. **Gamemodes**: Roped (default co-op), Free Climb (no rope), Race, Hunter vs Runners.
7. **Polish**: real player model, rope physics with wall collision, voice chat (Steam voice), in-game lobby UI matching the game's style.

## Known risks

- Level generation also draws from `UnityEngine.Random` during play; if anything consumes it differently per client between generation steps, later levels can diverge. Needs testing over long climbs; fallback is to resend per-level seeds from the host.
- Another installed MP mod (Shen-X-L's "Simple_Multi_Player") patches the same `WorldLoader` methods. Run one or the other.
