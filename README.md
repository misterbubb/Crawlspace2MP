# Crawlspace 2 Multiplayer Mod

A co-op multiplayer mod for [Crawlspace 2](https://store.steampowered.com/app/2258670/Crawlspace_2/) VR horror game. Play through the terrifying vents with a friend!

## Features

- **Steam Lobby System** - Easy matchmaking via Steam friends or lobby codes
- **Full Player Sync** - See your partner's position, head, and hand movements
- **Proximity Voice Chat** - Built-in 3D positional voice (hear them from their location)
- **Monster Sync** - Coordinated monster behavior across players
- **Puzzle Sync** - Progress syncs between players
- **Ghost System** - If you die, become a ghost and watch your partner
- **Streamer-Friendly** - Hide UI with Insert key, lobby codes hidden by default

## Monster Behavior in Co-op

Different monsters have different sync strategies:

| Monster | Behavior |
|---------|----------|
| **Smile, Jeff, Sparky** | Triggers at same time for all players, but each player has their OWN monster chasing THEM. Each player must defend themselves. |
| **Henry, Harold** | Fully synced - host controls position, same monster for everyone |
| **Clown** | Fully synced - which clown is active, attack state. Only kills players in the main room. |
| **Paintings** | Fully synced - which paintings are scary, timer, entity spawns. Only kills players in the main room. |

## Installation

### Requirements
- [Crawlspace 2](https://store.steampowered.com/app/2258670/Crawlspace_2/) on Steam
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) for Unity

### Steps
1. Install BepInEx to your Crawlspace 2 folder
2. Download the latest release from [NexusMods](https://www.nexusmods.com/crawlspace2/mods/1)
3. Copy files to your game folder:
   - `Crawlspace2MP.dll` → `BepInEx/plugins/`
   - `Facepunch.Steamworks.Win64.dll` → `BepInEx/plugins/`
   - `steam_api64.dll` → game root folder (next to the .exe)
4. Launch the game through Steam

Note: `steam_appid.txt` is auto-generated on first run.

## How to Play

1. **Host**: Click "Host Game" in the MP window
2. **Join**: Either:
   - Click "Invite Friends" to send Steam invites
   - Share the lobby code (click "Show Code" then "Copy")
   - Friend can right-click you in Steam and click "Join Game"
   - Use the Friends List in the UI to see friends playing and join directly

### Controls
- **Insert** - Toggle UI visibility (for recording/streaming)
- Voice chat is always-on when enabled

## Building from Source

### Requirements
- .NET SDK (for .NET Framework 4.7.2)
- Game DLLs in `lib/` folder (see below)

### Setup
1. Clone the repo
2. Copy required DLLs from your game installation to `lib/`:
   - From `VRFighter_Data/Managed/`: Unity DLLs, `Assembly-CSharp.dll`
   - From `BepInEx/core/`: `BepInEx.dll`, `0Harmony.dll`
3. Build: `dotnet build --configuration Release`

The mod auto-deploys to your game folder on build (configure path in `.csproj`).

## License

MIT License - see [LICENSE](LICENSE)

## Disclaimer

This is an unofficial mod and is not affiliated with the developers of Crawlspace 2.
