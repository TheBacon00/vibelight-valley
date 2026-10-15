using System;
using System.IO;

namespace VibelightValley.Installer.Services;

/// <summary>Plain-text log in %LOCALAPPDATA%\VibelightValley-Installer\installer.log (previous run kept as .prev.log).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibelightValley-Installer");
    public static readonly string FilePath = Path.Combine(Dir, "installer.log");

    public static event Action<string> Line;

    public static void Start(string header)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(FilePath)) File.Copy(FilePath, Path.Combine(Dir, "installer.prev.log"), true);
            File.WriteAllText(FilePath, "");
        }
        catch { /* logging must never stop the installer */ }
        Info(header);
    }

    public static void Info(string msg) => Write('I', msg);
    public static void Warn(string msg) => Write('W', msg);
    public static void Error(string msg) => Write('E', msg);

    private static void Write(char level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
        Line?.Invoke(line);
    }
}
