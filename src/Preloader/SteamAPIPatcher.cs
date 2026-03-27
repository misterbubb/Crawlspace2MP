using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace Crawlspace2MP.Preloader
{
    /// <summary>
    /// BepInEx Patcher that runs BEFORE the game loads.
    /// Copies steam_api64.dll and steam_appid.txt to the game root directory.
    /// 
    /// To use: Place the compiled DLL in BepInEx/patchers/ folder
    /// The Steam API files should be in the same folder as the patcher DLL
    /// </summary>
    public static class SteamAPIPatcher
    {
        // Required by BepInEx patcher system
        public static IEnumerable<string> TargetDLLs { get; } = Array.Empty<string>();

        // Called before any assemblies are patched
        public static void Initialize()
        {
            try
            {
                // Get paths
                string patcherDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                
                // Find the actual game directory by locating the game executable
                // This works with both manual installs and mod managers (r2modman, etc.)
                // Mod managers use virtual profile directories, so navigating up from the
                // patcher folder doesn't reach the real game root.
                string gameDir = null;
                
                try
                {
                    string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    gameDir = Path.GetDirectoryName(exePath);
                }
                catch (Exception ex)
                {
                    Log($"Could not get game dir from process: {ex.Message}");
                }
                
                // Fallback: navigate up from patcher dir (works for manual installs)
                if (gameDir == null)
                {
                    gameDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(patcherDir)));
                }
                
                Log($"SteamAPIPatcher initializing...");
                Log($"Patcher directory: {patcherDir}");
                Log($"Game directory: {gameDir}");
                
                // Files to copy
                string[] filesToCopy = { "steam_api64.dll", "steam_appid.txt" };
                
                foreach (string fileName in filesToCopy)
                {
                    string sourceFile = Path.Combine(patcherDir, fileName);
                    string destFile = Path.Combine(gameDir, fileName);
                    
                    // Check if source exists
                    if (!File.Exists(sourceFile))
                    {
                        Log($"  {fileName}: Not found in patcher folder, skipping");
                        continue;
                    }
                    
                    // Check if destination already exists and is same version
                    if (File.Exists(destFile))
                    {
                        // Compare file sizes as a quick check
                        var sourceInfo = new FileInfo(sourceFile);
                        var destInfo = new FileInfo(destFile);
                        
                        if (sourceInfo.Length == destInfo.Length)
                        {
                            Log($"  {fileName}: Already exists and matches, skipping");
                            continue;
                        }
                        
                        Log($"  {fileName}: Exists but different, updating...");
                    }
                    
                    // Copy the file
                    try
                    {
                        File.Copy(sourceFile, destFile, true);
                        Log($"  {fileName}: Copied successfully!");
                    }
                    catch (Exception ex)
                    {
                        Log($"  {fileName}: Failed to copy - {ex.Message}");
                    }
                }
                
                Log("SteamAPIPatcher complete!");
            }
            catch (Exception ex)
            {
                Log($"SteamAPIPatcher error: {ex}");
            }
        }

        // Required by BepInEx patcher system - we don't actually patch anything
        public static void Patch(AssemblyDefinition assembly)
        {
            // No patching needed - we just use Initialize() to copy files
        }
        
        private static void Log(string message)
        {
            // Write to BepInEx preloader log
            Console.WriteLine($"[SteamAPIPatcher] {message}");
        }
    }
}
