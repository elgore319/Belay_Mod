# Belay

Seamless co-op for **White Knuckle**: up to 8 climbers in one tower, roped together.

Built on BepInEx 5 + Harmony, using Steam lobbies and Steam P2P (the game already ships Facepunch.Steamworks).

## What works in 0.1.0

- **Steam lobbies**: host as Public, Friends-only or Private (up to 8). Browse public Belay lobbies in-game, or join friends through Steam invites / "Join game".
- **Same tower for everyone**: the lobby carries a world seed, fed into White Knuckle's level generator at the start of every run.
- **Teammates in the world**: each player shows up as a colored body with two hands and a name tag. Hands turn white while gripping.
- **The rope**: players are roped in a chain in join order (1-2, 2-3, ...). Past the rope length you get reeled back toward your partner, and if your partner is gripping a wall, your fall is caught (no fall damage). The host sets rope length and can turn it off.

Not synced yet: enemies, items, pitons, the rising death goo, deaths/respawns, or gamemode selection. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the plan.

## Playing

1. Install BepInEx 5 (x64) into the game folder if it isn't there yet.
2. Put `Belay.dll` in `BepInEx\plugins\Belay\`.
3. Start the game and press **F7** to open the Belay window.
4. Host a lobby (or join one), then everyone picks the **same gamemode** from the main menu. Same seed, same tower.

If another White Knuckle multiplayer mod is installed, disable it while using Belay.

## Building

Requires the .NET SDK (6 or newer) and a White Knuckle install with BepInEx.

```
dotnet build src/Belay -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\White Knuckle" -p:DeployToGame=true
```

`GameDir` defaults to `D:\SteamLibrary\steamapps\common\White Knuckle`. With `DeployToGame=true` the DLL is copied into `BepInEx\plugins\Belay` after each build. Game DLLs are referenced from your install and are never committed.

## Config

`BepInEx\config\elgore319.belay.cfg`: menu key, default rope length, rope visibility, network send rate.
