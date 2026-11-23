using System.Diagnostics;
using Avalonia.Controls;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private StartupWindow? _splash;
    private UpdateFeed.Result? _updateResult;
    private bool _startupScanning;
    private bool _startupUpdating;
    private bool _startupComplete;
    internal bool StartsMinimised => _library.UsageNoticeAccepted && _library.Preferences.StartMinimised;
    internal StartupWindow CreateSplash()
    {
        var splash = new StartupWindow(FontFamily, _motion) { Icon = Icon };
        if (Resources.TryGetValue("LG.Accent", out var accent)) splash.Resources["LG.Accent"] = accent;
        WindowsDesktop.SetWindowMotion(splash, _library.Preferences.ReduceMotion);
        return splash;
    }

    internal async Task InitializeStartup(StartupWindow? splash)
    {
        _splash = splash;
        _startupScanning = _startupUpdating = true;
        ShowStartupProgress();
        var token = _background.Token;
        try
        {
            if (splash != null && Program.VerificationMode == "--verify-migration" && Program.PreviewPath is { } preview)
                VerifySplash(splash, Path.Combine(Path.GetDirectoryName(preview)!, "splash.png"));
            var tasks = Task.WhenAll(StartupScan(), CheckUpdates(token, startup: true));
            if (splash == null || _verification) await tasks;
            else
            {
                var minimum = Task.Delay(380, token);
                await Task.WhenAny(tasks, Task.Delay(1700, token));
                await minimum;
                if (!tasks.IsCompleted) _ = CompleteStartupWork(tasks);
                else await tasks;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _startupComplete = true;
            _splash = null;
        }
    }

    private async Task StartupScan()
    {
        try
        {
            if (!_library.UsageNoticeAccepted) return;
            StartWatcher();
            await ScanLibrary();
        }
        finally { _startupScanning = false; ShowStartupProgress(); }
    }

    private async Task CompleteStartupWork(Task tasks)
    {
        try { await tasks; }
        catch (OperationCanceledException) { }
    }

    private async Task CheckUpdates(CancellationToken token, bool startup = false)
    {
        try
        {
            if (_verification) return;
            var result = await UpdateFeed.Check(token);
            if (_closed || token.IsCancellationRequested) return;
            _updateResult = result;
            if (result.UpdateAvailable) Status(result.Message);
            _automaticUpdatePending = startup && !Program.IsUpdateRestart && AvailableInstaller != null;
            RefreshUpdateActions();
            RefreshReleaseNotes();
            TryAutomaticUpdate();
        }
        finally { _startupUpdating = false; ShowStartupProgress(); }
    }

    private void ShowStartupProgress()
    {
        if (_closed || _splash is not { IsVisible: true } splash) return;
        var scanning = _startupScanning && _library.UsageNoticeAccepted && _library.Folder != null;
        var updating = _startupUpdating;
        var progress = _startupScanning && _startupUpdating ? 12 : _startupScanning || _startupUpdating ? 56 : 100;
        var operation = (scanning, updating) switch
        {
            (true, true) => "Rescanning library folder and checking for updates…",
            (true, false) => "Rescanning library folder…",
            (false, true) => "Checking for updates…",
            _ => _updateResult?.Message ?? "Opening library…"
        };
        splash.ShowProgress(progress, operation);
    }

    private Control BuildUpdateSettings()
    {
        var message = Label(UpdateMessage(), muted: true);
        var open = ActionButton("Open release page", () =>
        {
            if (_updateResult?.ReleasePage is not { } page) return;
            try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception error) { message.Text = error.Message; }
        });
        _updateReleaseAction = open;
        var installLabel = UpdateInstaller.IsSetupInstallation ? "Install update" : "Install and restart";
        _settingsInstallAction = ActionButton(installLabel, () => _ = InstallUpdate(false), "primary");
        RefreshUpdateActions();
        var check = new Button { Content = "Check for new updates", Classes = { "lg" } };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            message.Text = "Checking for updates…";
            try
            {
                await CheckUpdates(_background.Token);
                message.Text = UpdateMessage();
            }
            catch (OperationCanceledException) { message.Text = "Update check cancelled."; }
            finally { check.IsEnabled = true; }
        };
        return Section("Version & Updates", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Label($"Version {UpdateFeed.CurrentVersion.ToString(3)} · Build {UpdateFeed.CurrentVersion}", muted: true),
                Actions(check, _settingsInstallAction, open, IconButton("Support on Ko-fi", "LG.Icon.KoFi", OpenSupportPage)), message
            }
        });

        string UpdateMessage()
        {
            if (!UpdateInstaller.IsSetupInstallation || AvailableInstaller != null ||
                _updateResult is not { UpdateAvailable: true } available)
                return _updateResult?.Message ?? "";
            return $"{available.Message} Download Windows Setup from the release page.";
        }
    }
}
