# Crawlspace 2 Multiplayer

Online co-op for Crawlspace 2. Play through the game with friends using Steam networking. Supports up to 10 players.

## Features

- Steam lobby system with world-space VR UI panel
- Host, invite friends, join via friends list, or paste a lobby code
- Real-time player sync (head, hands, body position)
- Proximity voice chat (Steam Voice)
- Puzzle sync with per-player batteries
- Monster sync (Henry, Harold, Clown, Paintings)
- Ghost spectator mode on death
- Minimap indicators for all players (distinct colors)
- Night progression saves for all players
- Host migration if the host disconnects
- Up to 10 players per lobby

## Monster Behavior

- **Henry & Harold** - Host-controlled, synced to all players. Target nearest alive player.
- **Smile, Jeff & Sparky** - Independent per player
- **Clown** - Synced state, respects each player's line of sight
- **Paintings** - Independent deaths per player

## Ghost Mode

When you die, you become a ghost spectator:
- You stay in the same scene (no reload)
- Teleported to the main room in standing mode
- Ghost vision light so you can see in the dark
- Can move around freely but can't interact with anything
- Monsters ignore ghosts
- Game continues until all players are dead

## Installation

1. Install [BepInEx 5](https://thunderstore.io/c/crawlspace-2/p/BepInEx/BepInExPack/)
2. Drop mod files into `BepInEx/plugins/Crawlspace2MP/` and `BepInEx/patchers/Crawlspace2MP/`
3. Launch through Steam
4. Both players must complete Night 0 (tutorial) solo before multiplayer works

All players need the same mod version.

## Configuration

Config file: `BepInEx/config/com.crawlspace2.multiplayer.cfg`

- **VerboseLogging** - Enable detailed logs for bug reports (default: off)

## Voice Chat

Uses Steam Voice for proximity-based 3D audio. Toggle on/off from the multiplayer panel. Voice comes from the remote player's position in the world.

## Feedback & Bugs

Discord: https://discord.com/invite/BDZ6NjegeR

Enable VerboseLogging in the config, reproduce the issue, and share `BepInEx/LogOutput.log` when reporting bugs.
