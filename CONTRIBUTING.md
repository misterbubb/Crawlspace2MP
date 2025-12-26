# Contributing to Crawlspace 2 Multiplayer

Thanks for your interest in contributing! This guide will help you get started.

## Getting Started

### Prerequisites
- .NET SDK with .NET Framework 4.7.2 support
- Crawlspace 2 installed via Steam
- BepInEx 5.x installed in the game folder
- A code editor (VS Code, Visual Studio, Rider, etc.)

### Setting Up the Development Environment

1. **Clone the repository**
   ```bash
   git clone https://github.com/misterbubb/Crawlspace2MP.git
   cd Crawlspace2MP
   ```

2. **Copy required DLLs to `lib/`**
   
   From `Crawlspace 2_Data/Managed/`:
   - `Assembly-CSharp.dll` (game code)
   - `UnityEngine.dll`
   - `UnityEngine.CoreModule.dll`
   - `UnityEngine.PhysicsModule.dll`
   - `UnityEngine.AudioModule.dll`
   - `UnityEngine.AIModule.dll`
   - `UnityEngine.IMGUIModule.dll`
   - `UnityEngine.InputLegacyModule.dll`
   - `UnityEngine.VideoModule.dll`
   - `UnityEngine.XRModule.dll`
   - `UnityEngine.UI.dll`
   - `Unity.InputSystem.dll`
   - `Unity.XR.Management.dll`
   
   From `BepInEx/core/`:
   - `BepInEx.dll`
   - `0Harmony.dll`

3. **Configure game path** (optional)
   
   Edit `Crawlspace2MP.csproj` and update `<GamePath>` to your install location for auto-deploy on build.

4. **Build**
   ```bash
   dotnet build
   ```

## Code Structure

| File | Purpose |
|------|---------|
| `Plugin.cs` | BepInEx plugin entry, UI (IMGUI), Harmony patches |
| `SteamTransport.cs` | Steam lobby management, P2P networking, ping, version check |
| `PlayerSync.cs` | Syncs player positions, game state, puzzles, monsters |
| `RemotePlayer.cs` | Creates visual representation of remote players |
| `VoiceChat.cs` | Steam Voice capture/playback with 3D positioning |
| `INetworkTransport.cs` | `PacketWriter`/`PacketReader` for network serialization |

## Making Changes

### Adding a New Synced Feature

1. Add a packet type constant in `PlayerSync.cs`:
   ```csharp
   private const byte PACKET_MY_FEATURE = 25;
   ```

2. Add a send method:
   ```csharp
   public void SendMyFeature(int data)
   {
       _writer.Reset();
       _writer.Put(PACKET_MY_FEATURE);
       _writer.Put(data);
       SendToAllPeers(true);  // true = reliable
   }
   ```

3. Add handler in `OnDataReceived` switch:
   ```csharp
   case PACKET_MY_FEATURE:
       HandleMyFeature(reader);
       break;
   ```

4. Implement the handler:
   ```csharp
   private void HandleMyFeature(PacketReader reader)
   {
       int data = reader.GetInt();
       // Apply the synced data
   }
   ```

### Adding a Harmony Patch

```csharp
[HarmonyPatch(typeof(TargetClass), "MethodName")]
public class MyPatch
{
    static void Postfix(TargetClass __instance)
    {
        // Runs after the original method
    }
    
    static bool Prefix(TargetClass __instance)
    {
        // Return false to skip original method
        return true;
    }
}
```

### UI Changes

UI is in `Plugin.cs` using Unity IMGUI (`OnGUI`, `GUILayout`). The main window is drawn in `DrawWindow()`.

## Testing

1. Build the mod
2. Launch two instances of the game (you may need two Steam accounts or use Steam Family Sharing)
3. Host on one, join on the other
4. Test your changes

For solo testing of non-networking features, you can test with a single instance.

## Pull Request Guidelines

- Keep PRs focused on a single feature or fix
- Test your changes before submitting
- Update README if adding user-facing features
- Follow existing code style (C# conventions)
- Add comments for complex logic

## Reporting Issues

When reporting bugs, please include:
- What you were doing when it happened
- The error message (if any) from `BepInEx/LogOutput.log`
- Your mod version
- Whether you're host or client

## Questions?

Open an issue with the "question" label or reach out to the maintainers.
