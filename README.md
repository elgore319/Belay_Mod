# Belay

Seamless co-op for **White Knuckle**: up to 8 climbers in one tower, roped together.

Built on BepInEx 5 + Harmony, using Steam lobbies and Steam P2P (the game already ships Facepunch.Steamworks).

## What works in 0.5.0

- **Steam lobbies**: host as Public, Friends-only or Private (up to 8). Browse public Belay lobbies in-game, or join friends through Steam invites / "Join game".
- **Same tower for everyone**: the lobby carries a world seed, fed into White Knuckle's level generator at the start of every run.
- **Run flow**: the host picks a gamemode from the normal menu and everyone in the lobby is pulled into the same run (same gamemode options, same fresh seed). Only the host can start runs; in a lobby "Continue" starts a new shared run instead of a save. When the host restarts, everyone gets a new tower.
- **Late join**: joining a lobby mid-run loads you straight into it, and if you spawn far below the host you're carried up level by level and placed next to them.
- **One death goo**: the host's goo is the real one and rises for everyone. It chases the lowest living climber, not the host, and still kills each player who falls into it.
- **Shared doors**: when someone opens a door, it opens for everyone. When the first player goes through a door that shuts behind them, everyone sees "DOOR CLOSING IN 15" and it shuts for all players at once. The host sets the countdown (10-20 s).
- **Death and revives**: dying in a lobby doesn't end your run. You spectate your teammates (Tab switches who you watch). A living teammate brings you back at a save terminal (paying the revive cost in roaches from **their own wallet**, default 10, host sets 0-50) or by using a disk they're holding (free, the disk is used up). Money is never shared. The run ends only when everyone is dead.
- **Save terminals and disks in co-op**: disk saves are off in a lobby (loading one put you back at the terminal you saved at, usually below your team and in the mass's path, and regenerated the map out of sync). The terminal's disk page revives a dead teammate next to you for the usual roach cost. Disks can't be inserted into terminals. Instead, using a disk you're holding revives a dead teammate right where you are, for free, and uses up the disk. Solo play is unchanged.
- **Belay menu**: a "Belay" button on the main menu and pause menu (or F7) opens a tabbed menu: Lobby, Players (rope order, flags players running different mods than the host), Rules (host sets rope, door timer, revive cost), Mods and Settings (rebind keys). It draws on its own and doesn't touch the game's menus or other mods' UI.
- **Mod manager**: the Mods tab lists every installed BepInEx mod with an on/off switch. Mods can't be unloaded while the game runs, so a switch applies the next time you start the game (the menu says so and has a Quit button). It works by renaming the mod's DLL to `.dll.disabled` and back.
- **Teammates in the world**: each player shows up as a colored body with two hands and a name tag. Hands turn white while gripping.
- **The rope**: players are roped in a chain in join order (1-2, 2-3, ...). Past the rope length you get reeled back toward your partner, and if your partner is gripping a wall, your fall is caught (no fall damage). The host sets rope length and can turn it off.

Not synced yet: enemies, items, pitons, or crank-operated doors. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the plan.

## Playing

1. Install BepInEx 5 (x64) into the game folder if it isn't there yet.
2. Put `Belay.dll` in `BepInEx\plugins\Belay\`.
3. Start the game and click **Belay** in the bottom-right of the main menu (or press **F7**).
4. Host a lobby (or join one). The host picks a gamemode from the main menu and everyone follows automatically.

If another White Knuckle multiplayer mod is installed, switch it off in the Belay menu's Mods tab and restart the game.

## Building

Requires the .NET SDK (6 or newer) and a White Knuckle install with BepInEx.

```
dotnet build src/Belay -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\White Knuckle" -p:DeployToGame=true
```

`GameDir` defaults to `D:\SteamLibrary\steamapps\common\White Knuckle`. With `DeployToGame=true` the DLL is copied into `BepInEx\plugins\Belay` after each build. Game DLLs are referenced from your install and are never committed.

## Config

`BepInEx\config\elgore319.belay.cfg`: menu key, default rope length, rope visibility, network send rate.
