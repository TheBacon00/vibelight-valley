using System;
using System.IO;
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
            VrLog.Info($"{Name} {Version} loading. Mode={Settings.Mode.Value} pluginDir={PluginDir}");
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
