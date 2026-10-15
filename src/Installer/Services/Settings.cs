using System;
using System.IO;

namespace VibelightValley.Installer.Services;

/// <summary>The one thing the installer remembers: a game folder the user picked by hand.</summary>
public static class Settings
{
    private static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibelightValley-Installer", "gamedir.txt");

    public static string GameDir
    {
        get
        {
            try { return File.Exists(File_) ? File.ReadAllText(File_).Trim() : null; }
            catch { return null; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File_));
                File.WriteAllText(File_, value ?? "");
            }
            catch (Exception ex) { Log.Warn($"Saving the game folder failed: {ex.Message}"); }
        }
    }

    /// <summary>Command line --game-dir, then the remembered folder, then Steam detection.</summary>
    public static string ResolveGameDir(string commandLine)
    {
        if (GameLocator.IsGameDir(commandLine)) return commandLine;
        var saved = GameDir;
        if (GameLocator.IsGameDir(saved)) return saved;
        return GameLocator.Detect();
    }
}
