# Belay

Seamless co-op for **White Knuckle**: up to 8 climbers in one tower, roped together.

Built on BepInEx 5 + Harmony, using Steam lobbies and Steam P2P (the game already ships Facepunch.Steamworks).

## What works in 0.2.0

- **Steam lobbies**: host as Public, Friends-only or Private (up to 8). Browse public Belay lobbies in-game, or join friends through Steam invites / "Join game".
- **Same tower for everyone**: the lobby carries a world seed, fed into White Knuckle's level generator at the start of every run.
- **Run flow**: the host picks a gamemode from the normal menu and everyone in the lobby is pulled into the same run (same gamemode options, same fresh seed). Only the host can start runs; in a lobby "Continue" starts a new shared run instead of a save. When the host restarts, everyone gets a new tower.
- **Late join**: joining a lobby mid-run loads you straight into it, and if you spawn far below the host you're carried up level by level and placed next to them.
- **Teammates in the world**: each player shows up as a colored body with two hands and a name tag. Hands turn white while gripping.
- **The rope**: players are roped in a chain in join order (1-2, 2-3, ...). Past the rope length you get reeled back toward your partner, and if your partner is gripping a wall, your fall is caught (no fall damage). The host sets rope length and can turn it off.

Not synced yet: enemies, items, pitons, doors, the rising death goo, or deaths/revives. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the plan.

## Playing

1. Install BepInEx 5 (x64) into the game folder if it isn't there yet.
2. Put `Belay.dll` in `BepInEx\plugins\Belay\`.
3. Start the game and press **F7** to open the Belay window.
4. Host a lobby (or join one). The host picks a gamemode from the main menu and everyone follows automatically.

If another White Knuckle multiplayer mod is installed, disable it while using Belay.

## Building

Requires the .NET SDK (6 or newer) and a White Knuckle install with BepInEx.

```
dotnet build src/Belay -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\White Knuckle" -p:DeployToGame=true
```

`GameDir` defaults to `D:\SteamLibrary\steamapps\common\White Knuckle`. With `DeployToGame=true` the DLL is copied into `BepInEx\plugins\Belay` after each build. Game DLLs are referenced from your install and are never committed.

## Config

`BepInEx\config\elgore319.belay.cfg`: menu key, default rope length, rope visibility, network send rate.
