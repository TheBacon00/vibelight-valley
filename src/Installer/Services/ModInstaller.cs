using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VibelightValley.Installer.Services;

/// <summary>What is currently in the game folder.</summary>
public sealed class InstallState
{
    public string GameDir;
    public bool BepInExInstalled;
    public bool ModInstalled;
    public string ModVersion;      // null when installed by the old zip scripts (no install record)
    public bool Enabled;           // BepInEx loads at game start (doorstop_config.ini)

    public override string ToString() =>
        $"BepInEx={(BepInExInstalled ? "yes" : "no")}, mod={(ModInstalled ? "v" + (ModVersion ?? "?") : "no")}, enabled={Enabled}";
}

/// <summary>
/// Installs, updates, removes and switches the mod in a game folder. Everything it writes lives in:
///   BepInEx\plugins\VibelightValley\     the mod (wholly ours: replaced on update, deleted on uninstall)
///   BepInEx\config\vibelight.valley.cfg  the mod's settings (written by the mod)
///   BepInEx\VibelightValley\             the mod's logs (written by the mod)
///   BepInEx itself                       only when it was not installed yet; removed again on uninstall unless
///                                        other BepInEx plugins are installed
/// The mod used to be called DDLV-VR; an install under the old names counts as an earlier version and is migrated
/// (settings kept) on the next install/update.
/// </summary>
public sealed class ModInstaller
{
    private const string PluginRel = @"BepInEx\plugins\VibelightValley";
    private const string RecordName = "install.json";
    private const string ZipPluginPrefix = "BepInEx/plugins/VibelightValley/";

    /// <summary>Exactly what the BepInEx IL2CPP package adds to the game folder.</summary>
    private static readonly string[] BepInExItems = { "BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };

    public string GameDir { get; }
    public ModInstaller(string gameDir) { GameDir = gameDir; }

    private string PluginDir => Path.Combine(GameDir, PluginRel);
    private string DoorstopIni => Path.Combine(GameDir, "doorstop_config.ini");
    public string LogDir => Path.Combine(GameDir, @"BepInEx\VibelightValley");
    public string ConfigFile => Path.Combine(GameDir, @"BepInEx\config\vibelight.valley.cfg");

    // Old names (DDLV-VR, up to v0.1.x).
    private string LegacyPluginDir => Path.Combine(GameDir, @"BepInEx\plugins\DDLV-VR");
    private string LegacyLogDir => Path.Combine(GameDir, @"BepInEx\DDLV-VR");
    private string LegacyConfigFile => Path.Combine(GameDir, @"BepInEx\config\ddlv.vr.cfg");

    [DataContract]
    private sealed class Record
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "installedAt")] public string InstalledAt { get; set; }
        [DataMember(Name = "installer")] public string Installer { get; set; }
    }

    // ------------------------------------------------------------------ state

    public InstallState Read()
    {
        var s = new InstallState
        {
            GameDir = GameDir,
            BepInExInstalled = File.Exists(Path.Combine(GameDir, @"BepInEx\core\BepInEx.Unity.IL2CPP.dll")) && File.Exists(DoorstopIni),
            ModInstalled = File.Exists(Path.Combine(PluginDir, "VibelightValley.dll"))
                           || File.Exists(Path.Combine(LegacyPluginDir, "DDLV-VR.dll")),   // ModVersion stays null: "earlier version"
        };
        if (s.ModInstalled)
        {
            try
            {
                var rec = Path.Combine(PluginDir, RecordName);
                if (File.Exists(rec))
                    using (var fs = File.OpenRead(rec))
                        s.ModVersion = ((Record)new DataContractJsonSerializer(typeof(Record)).ReadObject(fs)).Version;
            }
            catch (Exception ex) { Log.Warn($"Install record unreadable: {ex.Message}"); }
        }
        if (s.BepInExInstalled)
        {
            try { s.Enabled = ReadEnabled(); }
            catch (Exception ex) { Log.Warn($"doorstop_config.ini unreadable: {ex.Message}"); }
        }
        return s;
    }

    public static bool GameRunning() => Process.GetProcessesByName(GameLocator.ProcessName).Length > 0;

    private void EnsureGameClosed(string action)
    {
        if (GameRunning()) throw new InvalidOperationException($"Disney Dreamlight Valley is running. Close the game, then {action} again.");
    }

    /// <summary>False when the game folder needs administrator rights to change (e.g. a locked-down Program Files).</summary>
    public bool CanWrite()
    {
        try
        {
            var probe = Path.Combine(GameDir, ".vibelight-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    // ------------------------------------------------------------------ install / update

    /// <param name="status">Short user-facing step text.</param>
    /// <param name="progress">0..1 overall progress.</param>
    public async Task InstallAsync(Manifest m, IProgress<string> status, IProgress<double> progress)
    {
        EnsureGameClosed("install");
        var before = Read();
        Log.Info($"Install v{m.Version} into {GameDir} (before: {before})");

        // BepInEx: only when missing; an existing install (maybe shared with other mods) is left alone.
        double modStart = 0;
        if (!before.BepInExInstalled)
        {
            status.Report($"Downloading BepInEx {m.BepInEx.Version}…");
            var bep = await Downloader.DownloadAsync(m.BepInEx, "BepInEx", Scale(progress, 0, 0.45)).ConfigureAwait(false);
            status.Report("Installing BepInEx…");
            int n = Extract(bep, _ => true, e => e);
            Log.Info($"BepInEx {m.BepInEx.Version}: {n} files extracted");
            modStart = 0.5;
        }

        status.Report($"Downloading Vibelight Valley v{m.Version}…");
        var mod = await Downloader.DownloadAsync(m.Mod, "Vibelight Valley", Scale(progress, modStart, 0.95)).ConfigureAwait(false);

        status.Report("Installing Vibelight Valley…");
        // Our folder is replaced as a whole so files dropped by a newer version do not linger.
        if (Directory.Exists(PluginDir)) Directory.Delete(PluginDir, true);
        int files = Extract(mod, e => e.StartsWith(ZipPluginPrefix, StringComparison.OrdinalIgnoreCase), e => e);
        if (!File.Exists(Path.Combine(PluginDir, "VibelightValley.dll")))
            throw new InvalidDataException("The downloaded package does not contain VibelightValley.dll.");
        WriteRecord(m.Version);
        Log.Info($"Vibelight Valley v{m.Version}: {files} files extracted to {PluginDir}");
        MigrateLegacy();

        SetEnabled(true);
        progress.Report(1);
        Log.Info($"Install complete (after: {Read()})");
    }

    /// <summary>Extracts the selected zip entries into the game folder, refusing any path that escapes it.</summary>
    private int Extract(byte[] zip, Func<string, bool> include, Func<string, string> map)
    {
        var root = Path.GetFullPath(GameDir).TrimEnd('\\') + "\\";
        int count = 0;
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!include(name)) continue;
            var target = Path.GetFullPath(Path.Combine(root, map(name).Replace('/', '\\')));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Refusing to extract '{entry.FullName}' outside the game folder.");
            if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            entry.ExtractToFile(target, true);
            count++;
        }
        return count;
    }

    /// <summary>Removes a DDLV-VR install (both would load otherwise), keeping its settings under the new name.</summary>
    private void MigrateLegacy()
    {
        if (File.Exists(LegacyConfigFile) && !File.Exists(ConfigFile))
        {
            File.Move(LegacyConfigFile, ConfigFile);
            Log.Info($"Settings migrated: {LegacyConfigFile} -> {ConfigFile}");
        }
        Remove(LegacyPluginDir);
        Remove(LegacyConfigFile);
        Remove(LegacyLogDir);
    }

    private void WriteRecord(string version)
    {
        var rec = new Record { Version = version, InstalledAt = DateTime.UtcNow.ToString("o"), Installer = App.Version };
        using var fs = File.Create(Path.Combine(PluginDir, RecordName));
        new DataContractJsonSerializer(typeof(Record)).WriteObject(fs, rec);
    }

    private static IProgress<double> Scale(IProgress<double> p, double from, double to) =>
        new Progress<double>(v => p.Report(from + (to - from) * v));

    // ------------------------------------------------------------------ uninstall

    /// <returns>A note for the user (e.g. why BepInEx was kept), or null.</returns>
    public string Uninstall()
    {
        EnsureGameClosed("uninstall");
        Log.Info($"Uninstall from {GameDir} (before: {Read()})");
        Remove(PluginDir);
        Remove(ConfigFile);
        Remove(LogDir);
        Remove(LegacyPluginDir);
        Remove(LegacyConfigFile);
        Remove(LegacyLogDir);

        string note = null;
        var plugins = Path.Combine(GameDir, @"BepInEx\plugins");
        var others = Directory.Exists(plugins) ? Directory.GetFileSystemEntries(plugins).Select(Path.GetFileName).ToArray() : new string[0];
        if (others.Length > 0)
        {
            note = $"BepInEx was kept because other mods use it ({string.Join(", ", others)}).";
            Log.Info(note);
        }
        else
        {
            foreach (var item in BepInExItems) Remove(Path.Combine(GameDir, item));
        }
        Log.Info($"Uninstall complete (after: {Read()})");
        return note;
    }

    private static void Remove(string path)
    {
        if (Directory.Exists(path)) { Directory.Delete(path, true); Log.Info($"Removed {path}"); }
        else if (File.Exists(path)) { File.Delete(path); Log.Info($"Removed {path}"); }
    }

    // ------------------------------------------------------------------ on / off

    private static readonly Regex EnabledLine = new(@"(?m)^(\s*enabled\s*=\s*)(\w+)");

    /// <summary>The first "enabled = ..." line in doorstop_config.ini is the [General] switch for BepInEx.</summary>
    private bool ReadEnabled()
    {
        var m = EnabledLine.Match(File.ReadAllText(DoorstopIni));
        return m.Success && m.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns BepInEx (and with it the mod) on or off for the next game start. Off = an unmodified game.</summary>
    public void SetEnabled(bool on)
    {
        if (!File.Exists(DoorstopIni)) throw new InvalidOperationException("BepInEx is not installed.");
        var text = File.ReadAllText(DoorstopIni);
        var updated = EnabledLine.Replace(text, "${1}" + (on ? "true" : "false"), 1);
        if (updated == text) return;
        File.WriteAllText(DoorstopIni, updated, new UTF8Encoding(false));   // keep it as Doorstop wrote it (no BOM)
        Log.Info($"Mod {(on ? "enabled" : "disabled")} (doorstop_config.ini)");
    }
}
