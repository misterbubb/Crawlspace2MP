# Crawlspace 2 Multiplayer Mod

Basic multiplayer mod for Crawlspace 2 VR using BepInEx and LiteNetLib.

## Setup

1. Create a `lib` folder in this project directory
2. Copy these DLLs from your game:

**From `D:\SteamLibrary\steamapps\common\Crawlspace 2\BepInEx\core\`:**
- BepInEx.dll
- 0Harmony.dll

**From `D:\SteamLibrary\steamapps\common\Crawlspace 2\Crawlspace 2_Data\Managed\`:**
- UnityEngine.dll
- UnityEngine.CoreModule.dll
- UnityEngine.PhysicsModule.dll
- UnityEngine.InputLegacyModule.dll
- UnityEngine.XRModule.dll
- Assembly-CSharp.dll

3. Build the project:
```
dotnet build -c Release
```

4. Copy `bin\Release\net472\Crawlspace2MP.dll` and `bin\Release\net472\LiteNetLib.dll` to:
   `D:\SteamLibrary\steamapps\common\Crawlspace 2\BepInEx\plugins\`

## Usage

- **F8** - Host a game (port 7777)
- **F9** - Connect to localhost:7777
- **F10** - Disconnect

## Next Steps

After testing basic connectivity, we'll need to:
1. Find the actual player object names in Crawlspace 2 (use dnSpy to inspect Assembly-CSharp.dll)
2. Sync VR hand positions
3. Add proper player spawning
4. Sync game state (items, doors, etc.)
