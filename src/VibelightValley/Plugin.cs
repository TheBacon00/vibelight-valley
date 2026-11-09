using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using VibelightValley.Core;
using HarmonyLib;
using VrLog = VibelightValley.Core.Log;

namespace VibelightValley;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "vibelight.valley";
    public const string Name = "Vibelight Valley";
    // BepInEx wants a version here. It is not the release version: releases are numbered by the release workflow
    // after this DLL was built, and the installer records that number in install.json (see DescribeBuild).
    public const string Version = "0.1.0";

    public static Plugin Instance { get; private set; }
    public static string PluginDir { get; private set; }

    public override void Load()
    {
        Instance = this;
        PluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        VrLog.Init(Path.Combine(Paths.BepInExRootPath, "VibelightValley"), base.Log);
        try
        {
            MigrateLegacyConfig();
            Settings.Bind(Config);
            VrLog.VerboseEnabled = Settings.VerboseLogging.Value;
            VrLog.Info($"{Name} loading: {DescribeBuild()}. Mode={Settings.Mode.Value} pluginDir={PluginDir}");
            VrLog.Info($".NET {Environment.Version}, process {Environment.ProcessId}, 64bit={Environment.Is64BitProcess}, cmdline: {Environment.CommandLine}");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                VrLog.Error($"UNHANDLED .NET exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => VrLog.Info("ProcessExit");

            var harmony = new Harmony(Guid);
            Diagnostics.QuitTrace.Install(harmony);

            AddComponent<VrBehaviour>();
            VrLog.Info("VrBehaviour component added");
        }
        catch (Exception ex)
        {
            VrLog.Error("Plugin.Load failed", ex);
        }
    }

    /// <summary>
    /// Which build this is, for the log: the release the installer put here (it records the version in install.json
    /// next to the plugin), or a development build (tools/build.ps1 marks the record "-dev" when it deploys).
    /// </summary>
    private static string DescribeBuild()
    {
        string stamp = $"plugin file dated {File.GetLastWriteTime(typeof(Plugin).Assembly.Location):yyyy-MM-dd HH:mm}";
        try
        {
            string record = Path.Combine(PluginDir, "install.json");
            if (!File.Exists(record)) return $"development build ({stamp})";
            string version = Regex.Match(File.ReadAllText(record), "\"version\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            if (version.Length == 0) return $"unknown build ({stamp})";
            return version.EndsWith("-dev", StringComparison.Ordinal) ? $"development build {version} ({stamp})" : $"release {version} ({stamp})";
        }
        catch (Exception ex) { return $"unknown build ({stamp}; {ex.Message})"; }
    }

    /// <summary>The mod used to be called DDLV-VR (config ddlv.vr.cfg): carry the user's settings over once.</summary>
    private void MigrateLegacyConfig()
    {
        var legacy = Path.Combine(Paths.ConfigPath, "ddlv.vr.cfg");
        if (!File.Exists(legacy) || File.Exists(Config.ConfigFilePath)) return;
        File.Copy(legacy, Config.ConfigFilePath);
        Config.Reload();
        VrLog.Info($"Settings migrated from {legacy}");
    }
}
