using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VibelightValley.Installer.Services;

/// <summary>Finds Disney Dreamlight Valley in the user's Steam libraries.</summary>
public static class GameLocator
{
    public const string GameName = "Disney Dreamlight Valley";
    public const string ExeName = "ddv.exe";
    public const string ProcessName = "ddv";
    public const int SteamAppId = 1401590;

    public static bool IsGameDir(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ExeName));

    public static string Detect()
    {
        foreach (var lib in SteamLibraries())
        {
            var dir = Path.Combine(lib, "steamapps", "common", GameName);
            if (IsGameDir(dir)) { Log.Info($"Game found in Steam library {lib}"); return TrueCase(dir); }
        }
        Log.Info("Game not found in any Steam library");
        return null;
    }

    /// <summary>The path as spelled on disk (Steam stores its own path in lower case).</summary>
    private static string TrueCase(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (di.Parent == null) return di.FullName.ToUpperInvariant();
            return Path.Combine(TrueCase(di.Parent.FullName), di.Parent.GetDirectories(di.Name)[0].Name);
        }
        catch { return path; }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            @"C:\Program Files (x86)\Steam",
        };
        var libs = new List<string>();
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            libs.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (Exception ex) { Log.Warn($"Reading {vdf} failed: {ex.Message}"); }
        }
        return libs.Select(l => l.Replace('/', '\\').TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
