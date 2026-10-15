using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace VibelightValley.Core;

/// <summary>
/// Thread-safe file logger. Every line: wall clock, ms since start, thread id, Unity frame, level, text.
/// Flushed on every write so the tail survives a hard crash. Safe to call from the render thread
/// (never touches IL2CPP objects). Also mirrors Info+ to the BepInEx log.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter _writer;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static BepInEx.Logging.ManualLogSource _bep;
    private static int _mainThreadId;
    private static readonly ConcurrentDictionary<string, int> Throttle = new();

    /// <summary>Unity frame number, updated by the main thread each frame (render thread reads it too).</summary>
    public static volatile int Frame;
    public static bool VerboseEnabled = true;
    public static string LogPath { get; private set; }
    public static string Dir { get; private set; }

    public static void Init(string dir, BepInEx.Logging.ManualLogSource bep)
    {
        _bep = bep;
        _mainThreadId = Environment.CurrentManagedThreadId;
        Dir = dir;
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "vibelight-valley.log");
        try
        {
            if (File.Exists(LogPath))
            {
                string prev = Path.Combine(dir, "vibelight-valley.prev.log");
                File.Copy(LogPath, prev, true);
            }
        }
        catch { /* ignore */ }
        var fs = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        Info($"==== Vibelight Valley log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
    }

    public static bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    private static void Write(char level, string msg, bool mirror)
    {
        string thread = IsMainThread ? "main" : $"t{Environment.CurrentManagedThreadId}";
        string line = $"{DateTime.Now:HH:mm:ss.fff} +{Clock.ElapsedMilliseconds,8} [{thread,-5}] f{Frame,-7} {level} {msg}";
        lock (Sync)
        {
            try { _writer?.WriteLine(line); } catch { /* disk full etc. */ }
        }
        if (!mirror || _bep == null) return;
        try
        {
            switch (level)
            {
                case 'E': _bep.LogError(msg); break;
                case 'W': _bep.LogWarning(msg); break;
                default: _bep.LogInfo(msg); break;
            }
        }
        catch { /* BepInEx logger is not guaranteed thread safe in all builds */ }
    }

    public static void Verbose(string msg) { if (VerboseEnabled) Write('V', msg, false); }
    public static void Info(string msg) => Write('I', msg, true);
    public static void Warn(string msg) => Write('W', msg, true);
    public static void Error(string msg) => Write('E', msg, true);

    public static void Error(string context, Exception ex)
    {
        Write('E', $"{context}: {ex.GetType().FullName}: {ex.Message}\n{ex}", true);
    }

    /// <summary>Logs at most <paramref name="max"/> times per key, then a single suppression notice.</summary>
    public static void Limited(string key, int max, string msg, char level = 'W')
    {
        int n = Throttle.AddOrUpdate(key, 1, (_, v) => v + 1);
        if (n <= max) Write(level, msg, level != 'V');
        else if (n == max + 1) Write(level, $"(further '{key}' messages suppressed)", false);
    }

    /// <summary>Raw multi-line block (dumps). Not mirrored to BepInEx to keep its log readable.</summary>
    public static void Block(string title, string body)
    {
        lock (Sync)
        {
            try
            {
                _writer?.WriteLine($"----- BEGIN {title} -----");
                _writer?.WriteLine(body);
                _writer?.WriteLine($"----- END {title} -----");
            }
            catch { }
        }
    }
}
