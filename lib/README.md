# Library Dependencies

This folder should contain the required DLLs for building the mod. These are NOT included in the repository - you must copy them from your game installation.

## Required DLLs

### From `Crawlspace 2_Data/Managed/`:
- `Assembly-CSharp.dll` - Game code
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.AudioModule.dll`
- `UnityEngine.PhysicsModule.dll`
- `UnityEngine.AIModule.dll`
- `UnityEngine.IMGUIModule.dll`
- `UnityEngine.UI.dll`
- `UnityEngine.VideoModule.dll`
- `UnityEngine.ImageConversionModule.dll`
- `UnityEngine.InputLegacyModule.dll`
- `UnityEngine.AssetBundleModule.dll`
- `UnityEngine.XRModule.dll`
- `UnityEngine.UnityWebRequestModule.dll`
- `Unity.InputSystem.dll`
- `Unity.XR.Management.dll`

### From `BepInEx/core/`:
- `BepInEx.dll`
- `0Harmony.dll`

## Setup

1. Install the game via Steam
2. Install BepInEx to the game folder
3. Copy the DLLs listed above to this `lib/` folder
4. Build with `dotnet build --configuration Release`
