using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VibelightValley.Installer.Services;

namespace VibelightValley.Installer;

public partial class MainWindow : Window
{
    private string _gameDir;
    private ModInstaller _installer;
    private InstallState _state;
    private Manifest _latest;
    private string _manifestError;
    private string _notice;      // result of the last action, shown until the next one
    private bool _busy;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow(string gameDirArg)
    {
        InitializeComponent();
        VersionText.Text = App.Version == "0.0.0" ? "dev build" : "v" + App.Version;
        SetGameDir(Settings.ResolveGameDir(gameDirArg));
        // Pick up changes made outside (game started/closed, files changed) without a Refresh button.
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        Activated += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            await CheckForUpdatesAsync();
            var shot = Environment.GetEnvironmentVariable("VIBELIGHT_SCREENSHOT");
            if (string.IsNullOrEmpty(shot)) return;
            await Task.Delay(500);   // let styles/templates swapped by Refresh() lay out
            UpdateLayout();
            SaveScreenshot(shot);
        };
    }

    /// <summary>Docs/testing aid: VIBELIGHT_SCREENSHOT=&lt;file.png&gt; saves the window once it has loaded, then closes it.</summary>
    private void SaveScreenshot(string path)
    {
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(this);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) png.Save(fs);
        Log.Info($"Screenshot saved to {path}");
        Close();
    }

    // ------------------------------------------------------------------ state -> view

    private void SetGameDir(string dir)
    {
        _gameDir = dir;
        _notice = null;
        _installer = dir != null ? new ModInstaller(dir) : null;
        Log.Info($"Game folder: {dir ?? "(not found)"}");
        Refresh();
    }

    private async Task CheckForUpdatesAsync()
    {
        _manifestError = null;
        StatusText.Text = "Checking for updates…";
        try { _latest = await Downloader.FetchManifestAsync(); }
        catch (Exception ex)
        {
            _latest = null;
            _manifestError = ex.Message;
            Log.Error($"Update check failed: {ex}");
        }
        if (SelfUpdater.IsUpdateAvailable(_latest))
        {
            SelfUpdateText.Text = $"A new installer is available (v{_latest.Installer.Version}).";
            SelfUpdateBanner.Visibility = Visibility.Visible;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (_busy) return;
        _state = _installer?.Read();
        bool running = ModInstaller.GameRunning();
        bool found = _state != null;
        string latest = _latest?.Version;

        GameDirText.Text = found ? _gameDir : "Disney Dreamlight Valley was not found — choose its folder.";
        GameDirText.Foreground = (Brush)FindResource(found ? "Text" : "Warn");
        OpenFolderButton.IsEnabled = found;

        string status, detail, primary = null;
        Brush dot;
        bool primaryProminent = true;
        if (!found)
        {
            (status, dot, detail, primary) = ("Game not found", Res("Warn"), "Click Choose game folder and select ddv.exe.", "Choose game folder…");
        }
        else if (!_state.ModInstalled)
        {
            status = "Not installed";
            dot = Res("Muted");
            detail = latest != null ? $"Latest version: v{latest}. Installs BepInEx too if needed." : UpdateCheckNote();
            primary = latest != null ? $"Install v{latest}" : "Retry";
        }
        else if (latest != null && (_state.ModVersion == null || SemVer.IsNewer(latest, _state.ModVersion)))
        {
            status = "Update available";
            dot = Res("Warn");
            detail = $"Installed: {(_state.ModVersion != null ? "v" + _state.ModVersion : "an earlier version")}  →  latest: v{latest}";
            primary = $"Update to v{latest}";
        }
        else
        {
            status = _state.ModVersion != null ? $"Installed  ·  v{_state.ModVersion}" : "Installed";
            dot = Res("Good");
            detail = latest != null ? "You have the latest version." : UpdateCheckNote();
            primary = latest != null ? "Reinstall" : "Retry";
            primaryProminent = latest == null;
        }
        if (found && _state.ModInstalled && !_state.Enabled) { detail += "  VR is switched off."; dot = Res("Muted"); }
        if (_notice != null) detail = _notice;
        if (running) detail ="The game is running — close it to install, update or uninstall.";

        StatusText.Text = status;
        StatusDot.Fill = dot;
        DetailText.Text = detail;
        PrimaryButton.Content = primary;
        PrimaryButton.Style = (Style)FindResource(primaryProminent ? "Primary" : "BaseButton");
        PrimaryButton.IsEnabled = !running || !found;
        UninstallButton.Visibility = found && (_state.ModInstalled || _state.BepInExInstalled) ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = !running;

        EnabledSwitch.IsEnabled = found && _state.ModInstalled && _state.BepInExInstalled;
        EnabledSwitch.IsChecked = found && _state.ModInstalled && _state.Enabled;
        LogsButton.IsEnabled = found && Directory.Exists(_installer.LogDir);
        ConfigButton.IsEnabled = found && File.Exists(_installer.ConfigFile);
        LaunchButton.IsEnabled = found && !running;
    }

    private string UpdateCheckNote() =>
        _manifestError != null ? $"Couldn't check for updates ({_manifestError})." : "Checking for updates…";

    private Brush Res(string key) => (Brush)FindResource(key);

    private void SetBusy(bool busy, string text = null)
    {
        _busy = busy;
        PrimaryButton.IsEnabled = UninstallButton.IsEnabled = BrowseButton.IsEnabled = EnabledSwitch.IsEnabled = LaunchButton.IsEnabled = !busy;
        SelfUpdateButton.IsEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = text ?? "";
        Progress.Value = 0;
        if (!busy) Refresh();
    }

    /// <summary>Runs a change in the game folder: busy UI, errors shown as a message, admin rights if needed.</summary>
    private async Task RunAsync(string what, Func<Task> action)
    {
        _notice = null;
        if (!_installer.CanWrite() && !RestartElevated()) return;
        SetBusy(true, what);
        try { await action(); }
        catch (Exception ex)
        {
            Log.Error($"{what} failed: {ex}");
            SetBusy(false);
            MessageBox.Show(this, ex.Message + $"\n\nDetails: {Log.FilePath}", "Vibelight Valley", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        SetBusy(false);
    }

    private bool RestartElevated()
    {
        var answer = MessageBox.Show(this,
            "Windows needs administrator permission to change files in the game folder.\n\nRestart the installer as administrator?",
            "Vibelight Valley", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return false;
        try
        {
            Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, $"--game-dir \"{_gameDir}\"")
            { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { Log.Warn("Elevation cancelled"); }
        return false;
    }

    // ------------------------------------------------------------------ actions

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) { Browse_Click(sender, e); return; }
        if (_latest == null) { await CheckForUpdatesAsync(); return; }

        var m = _latest;
        await RunAsync("Preparing…", async () =>
        {
            var status = new Progress<string>(s => ProgressText.Text = s);
            var progress = new Progress<double>(v => Progress.Value = v);
            await Task.Run(() => _installer.InstallAsync(m, status, progress));
        });
        if (_installer.Read().ModVersion == m.Version)
        {
            _notice = "Done! Start SteamVR, then launch the game. The first start with BepInEx takes a minute or two.";
            Refresh();
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Remove Vibelight Valley, its settings and logs from the game?\n\nBepInEx is removed too, unless other mods use it.",
            "Uninstall Vibelight Valley", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        string note = null;
        await RunAsync("Uninstalling…", () => Task.Run(() => { note = _installer.Uninstall(); }));
        if (!_installer.Read().ModInstalled)
        {
            _notice = "Vibelight Valley was removed." + (note != null ? " " + note : "");
            Refresh();
        }
    }

    private async void EnabledSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = EnabledSwitch.IsChecked == true;
        await RunAsync(on ? "Switching VR on…" : "Switching VR off…", () => Task.Run(() => _installer.SetEnabled(on)));
    }

    private async void SelfUpdate_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Downloading the new installer…");
        try
        {
            var progress = new Progress<double>(v => Progress.Value = v);
            await Task.Run(() => SelfUpdater.ApplyAsync(_latest, progress));
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error($"Self-update failed: {ex}");
            SetBusy(false);
            MessageBox.Show(this, $"Updating the installer failed: {ex.Message}\n\nYou can download it from GitHub instead.", "Vibelight Valley",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ddv.exe in the Disney Dreamlight Valley folder",
            Filter = "Disney Dreamlight Valley (ddv.exe)|ddv.exe",
            InitialDirectory = _gameDir ?? @"C:\Program Files (x86)\Steam\steamapps\common",
        };
        if (dlg.ShowDialog(this) != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName);
        if (!GameLocator.IsGameDir(dir))
        {
            MessageBox.Show(this, $"That folder doesn't contain {GameLocator.ExeName}.", "Vibelight Valley", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Settings.GameDir = dir;
        SetGameDir(dir);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(_gameDir);
    private void Logs_Click(object sender, RoutedEventArgs e) => Open(_installer.LogDir);
    private void Config_Click(object sender, RoutedEventArgs e) => Open(_installer.ConfigFile);
    private void Releases_Click(object sender, RoutedEventArgs e) => Open(_latest?.ReleaseUrl ?? Downloader.RepoUrl + "/releases");
    private void InstallerLog_Click(object sender, RoutedEventArgs e) => Open(Log.FilePath);
    private void Launch_Click(object sender, RoutedEventArgs e) => Open($"steam://rungameid/{GameLocator.SteamAppId}");

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Opening {target} failed: {ex.Message}"); }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
