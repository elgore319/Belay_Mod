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
| Safe areas and money | Breakrooms/interludes call `CL_GameManager.SetSafe(true)` (via `UT_GameStateController.SetSafe` scene events) and hold vendors (`ENV_RationVendor`, `ENV_Vendor_Disk`). Currency is roaches: `CL_GameManager.GetRoaches()`, `AddRoaches(int)`, `runRoaches`. Death is `ENT_Player.Kill()`; `ENT_Player.Revive()` restores health and unlocks the player. | A Harmony postfix on `SetSafe(true)` spawns a revive station in each safe area (deterministic spot, same on all clients). A dead player's game-over screen is suppressed and they spectate teammates instead. |
| Gamemodes | `M_Gamemode` assets, chosen in `UI_GamemodeScreen.LoadGamemode` (sets static `CL_GameManager.gamemode`, loads `gamemodeScene`). Modules: `GamemodeModule_Standard/Challenge/Training`. | Host's choice should be broadcast so clients auto-follow into the run. |
| Steam | `SteamManager` calls `SteamClient.Init(3195790)` and pumps `RunCallbacks()`. | Belay just waits for `SteamClient.IsValid`; no second init. |

## Belay today (0.5.0)

```
Plugin              BepInEx entry, config, Harmony
Net/NetSession      Steam lobby (create/join/browse/invite) + P2P transport, star topology via host
Net/Packets         Packet types and PlayerState wire format
Players/WorldSpace  Local <-> shared coordinate conversion (floating origin)
Players/PlayerSync  20 Hz local state broadcast, RemotePlayer lifecycle
Players/RemotePlayer Puppet body/hands/name tag with smoothing
Players/RemoteBody  Solid teammate capsule: CL_Handhold (grip/climb a teammate) + Damageable (hits -> knockback)
Players/PlayerContact Hammer-hit knockback packets, carrying (grip fields ride in PlayerState)
World/PropSync      CL_Prop sync: level-path ids, owner streams state (holder > nearest pusher), breaks shared
Rope/Tether         Chain rope in join order: spring pull + fall catch when partner grips
Run/RunFlow         Host's run (gamemode, settings, seed) in lobby data; clients follow; late-join catch-up
World/GooSync       Host-owned death goo, rubberbanded to the lowest climber, streamed at 4 Hz
World/DoorSync      Door ids, shared opens, host-run close countdown
Revive/ReviveSystem Spectate on death, revive stations in safeArea levels, host-validated revives, run over when all dead
Revive/SaveTerminalPatches In a lobby: App_SavePage reformat = paid revive, disk saves/loads off, OS_FloppyReader blocked, Item.Use on a "disk" item = free revive at your position
Patches/WorldLoaderPatches  Lobby seed into WorldLoader, block IncrementSeed
UI/BelayMenu        Tabbed IMGUI mod menu (Lobby, Players, Rules, Mods, Settings) + HUD; button on main/pause menu
UI/ModManager       Lists BepInEx plugins, toggles them by renaming DLLs (applies next launch)
```

Wire format: `[PacketType:byte][origin SteamId:ulong][payload]`. Channel 0 reliable, channel 1 unreliable state. Clients send only to the host; the host validates that `origin == sender` and relays. Lobbies are tagged `belay=1` and `belay_ver=<protocol>` so vanilla lobbies and other mods' lobbies are ignored.

## Roadmap

1. **Run flow** (done in 0.2.0): host's gamemode choice, options and seed go into lobby data (`belay_run`); clients auto-load once the host is in the run. Late joiners are walked up the tower with `WorldLoader.TeleportPlayerToNextLevel` (so levels generate) and then placed next to the host.
2. **Death goo and doors**: host-authoritative goo height. Door sync: when the first player crosses a door's close trigger, the host starts a shared countdown (default 15 s, host-configurable 10-20 s) shown on everyone's HUD, then closes the door for all players at once. Players still on the wrong side when it shuts are teleported to the group (or the gamemode decides).
3. **Death and revive stations**: dead players spectate instead of getting the game-over screen. Every breakroom/interlude gets a revive station. A living player uses it and pays roaches (default 10, host-configurable, from the payer's own run roaches) to bring back one dead teammate, who respawns at the station via `Revive()` + `Teleport()`. If everyone is dead, the run ends. **Currency is never shared**: every player has their own roach wallet, a roach pickup goes only to the player who grabbed it (and disappears for everyone else), and there is no team pot or splitting. The host validates the payment and broadcasts the revive.
4. **Belay mod menu**: replace the F7 debug window with a proper menu that stays out of other mods' way:
   - A "Belay" button added to the main menu and pause menu by a Harmony postfix that only appends our own button (`UI_MenuScreen`/`UI_MenuButton`), never replacing or reordering the game's or other mods' buttons. Hotkey stays as a fallback and is rebindable in config.
   - Its own Canvas (own sorting layer, high sort order) and its own GameObject tree under a `Belay` root, so no shared UI objects are modified.
   - Input is only captured while the menu is open (cursor unlock is restored on close), and nothing reads or eats keys while it's closed.
   - All settings live in Belay's own BepInEx config file (`elgore319.belay.cfg`) and lobby data keys prefixed `belay_`, so nothing collides with other mods' settings or lobbies.
   - Tabs: Lobby (host/browse/invite), Players (rope order, kick for host), Rules (rope length, door countdown, revive cost, gamemode), Mods, Settings (keys, rope visuals, HUD).
   - **Mods tab (mod manager)**: lists every installed BepInEx plugin (`BepInEx.Bootstrap.Chainloader.PluginInfos`: name, version, GUID, file) with an on/off switch. BepInEx can't unload a plugin or its Harmony patches once the game is running, so switching one off renames its DLL to `.dll.disabled` (switching on renames it back) and the change applies on the next launch. The tab says "restart required" and offers a Quit button. Belay itself and BepInEx core can't be switched off from here. Lobbies show a warning when players run different gameplay mods.
5. **Enemies**: network IDs for `GameEntity` spawns (deterministic from seed where possible), host AI with nearest-player targeting, client puppets, damage requests to host.
6. **Items and pitons**: pickup/drop/use events, placed pitons and rebar are shared climbable anchors.
7. **Stay-together mechanics**: rope stamina sharing, "anchor" bonus when a partner is gripping, goo speeds up when the group spreads out.
8. **Gamemodes**: Roped (default co-op), Free Climb (no rope), Race, Hunter vs Runners.
9. **Polish**: real player model, rope physics with wall collision, voice chat (Steam voice), in-game lobby UI matching the game's style.

## Known risks

- Level generation also draws from `UnityEngine.Random` during play; if anything consumes it differently per client between generation steps, later levels can diverge. Needs testing over long climbs; fallback is to resend per-level seeds from the host.
- Another installed MP mod (Shen-X-L's "Simple_Multi_Player") patches the same `WorldLoader` methods. Run one or the other.
