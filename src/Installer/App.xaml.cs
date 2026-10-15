using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using VibelightValley.Installer.Services;

namespace VibelightValley.Installer;

/// <summary>
/// No arguments: the installer window. With a command: runs it without a window (for scripts).
///   VibelightValley-Installer.exe status | on | off | toggle | install | uninstall  [--game-dir "path"]
/// Exit codes: 0 = ok ("status": mod is on), 1 = "status": mod is off, 2 = error / not installed.
/// </summary>
public partial class App : Application
{
    public static string Version { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Start($"Vibelight Valley Installer {Version} ({string.Join(" ", e.Args)})");
        SelfUpdater.CleanupOldExe();

        string gameDirArg = null;
        var args = e.Args.ToList();
        int gi = args.FindIndex(a => a.Equals("--game-dir", StringComparison.OrdinalIgnoreCase));
        if (gi >= 0 && gi + 1 < args.Count) { gameDirArg = args[gi + 1]; args.RemoveRange(gi, 2); }

        if (args.Count == 0)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            new MainWindow(gameDirArg).Show();
            return;
        }

        AttachConsole(-1);   // print into the console we were started from (this is a GUI exe)
        int code;
        try { code = Task.Run(() => RunCommand(args[0].ToLowerInvariant(), gameDirArg)).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            Console.Error.WriteLine("Error: " + ex.Message);
            code = 2;
        }
        Shutdown(code);
    }

    private static async Task<int> RunCommand(string cmd, string gameDirArg)
    {
        var dir = Settings.ResolveGameDir(gameDirArg);
        if (dir == null) { Console.Error.WriteLine("Disney Dreamlight Valley not found. Pass --game-dir \"<folder containing ddv.exe>\"."); return 2; }
        var inst = new ModInstaller(dir);
        var s = inst.Read();

        switch (cmd)
        {
            case "status":
                if (!s.ModInstalled) { Console.WriteLine("not installed"); return 2; }
                Console.WriteLine(s.Enabled ? "on" : "off");
                return s.Enabled ? 0 : 1;
            case "on":
            case "off":
            case "toggle":
                if (!s.ModInstalled) { Console.Error.WriteLine($"Vibelight Valley is not installed in '{dir}'."); return 2; }
                inst.SetEnabled(cmd == "on" || (cmd == "toggle" && !s.Enabled));
                Console.WriteLine($"Vibelight Valley is now {(inst.Read().Enabled ? "on" : "off")}");
                if (ModInstaller.GameRunning()) Console.WriteLine("Note: the game is running; the change applies the next time it starts.");
                return 0;
            case "install":
            case "update":
                var m = await Downloader.FetchManifestAsync();
                await inst.InstallAsync(m, new Progress<string>(Console.WriteLine), new Progress<double>(_ => { }));
                Console.WriteLine($"Vibelight Valley v{m.Version} installed in {dir}");
                return 0;
            case "uninstall":
                var note = inst.Uninstall();
                Console.WriteLine("Vibelight Valley uninstalled." + (note != null ? " " + note : ""));
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command '{cmd}'. Usage: VibelightValley-Installer [status|on|off|toggle|install|uninstall] [--game-dir \"path\"]");
                return 2;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
