using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VibelightValley.Installer.Services;

/// <summary>
/// Replaces the running installer exe with the latest release's. A running exe can be renamed but not overwritten,
/// so: rename ourselves to *.old.exe, write the new exe in our place, start it, exit. The next start deletes the old one.
/// </summary>
public static class SelfUpdater
{
    private static string ExePath => Process.GetCurrentProcess().MainModule.FileName;
    private static string OldPath => Path.ChangeExtension(ExePath, ".old.exe");

    /// <summary>Development builds (version 0.0.0) never offer to replace themselves.</summary>
    public static bool IsUpdateAvailable(Manifest m) =>
        m?.Installer != null && App.Version != "0.0.0" && SemVer.IsNewer(m.Installer.Version, App.Version);

    public static async Task ApplyAsync(Manifest m, IProgress<double> progress)
    {
        var bytes = await Downloader.DownloadAsync(m.Installer, "installer", progress).ConfigureAwait(false);
        string exe = ExePath, old = OldPath;
        Log.Info($"Self-update {App.Version} -> {m.Installer.Version}: {exe}");
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.WriteAllBytes(exe, bytes); }
        catch { File.Move(old, exe); throw; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Removes the previous exe left by an update (it may still be exiting for a moment).</summary>
    public static void CleanupOldExe()
    {
        var old = OldPath;
        if (!File.Exists(old)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); Log.Info($"Removed {old}"); }
                catch { Thread.Sleep(500); }
            }
        });
    }
}
