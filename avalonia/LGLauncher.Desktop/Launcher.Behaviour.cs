using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private TrayIcon? _tray;
    private bool _explicitQuit;
    private bool _motion;
    private readonly Dictionary<Control, CancellationTokenSource> _viewMotion = [];

    private Control SettingsRow(string title, string icon, Page page) => SettingsRow(title, icon, () => Navigate(page));

    private Button SettingsRow(string title, string icon, Action open, TextBlock? description = null) =>
        SettingsRow(title, Symbol(icon, 19), open, description);

    private Button SettingsRow(string title, SymbolIcon icon, Action open, TextBlock? description = null, SymbolIcon? indicator = null)
    {
        var arrow = Symbol("LG.Icon.Chevron");
        Control text = description == null ? Label(title) : new StackPanel { Spacing = 2, Children = { Label(title), description } };
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse(indicator == null ? "22,*,18" : "22,*,Auto,18"),
            ColumnSpacing = 11,
            Children = { icon, text, arrow }
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(arrow, indicator == null ? 2 : 3);
        if (indicator != null)
        {
            Grid.SetColumn(indicator, 2);
            row.Children.Add(indicator);
        }
        var button = ActionButton(title, open);
        AutomationProperties.SetName(button, title);
        button.Classes.Add("settings-row");
        button.Content = row;
        return button;
    }

    private Button SettingsBack(Page destination = Page.Settings, string name = "Settings")
    {
        var back = IconButton(name, "LG.Icon.Back", () => Navigate(destination), "quiet");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Padding = new Thickness(0, 5, 11, 5);
        return back;
    }

    private Button IconButton(string name, string icon, Action action, string? variant = null)
    {
        var button = ActionButton(name, action, variant);
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Symbol(icon), Label(name) }
        };
        return button;
    }

    private Control BuildBehaviour()
    {
        var prefs = _library.Preferences;
        var stack = new StackPanel { Spacing = 10 };
        AddToggle("Start with System", "Open LG Launcher when you sign in to Windows.", prefs.StartWithSystem,
            value => prefs.StartWithSystem = value, true);
        AddToggle("Start Minimised", "Start in the taskbar after the usage notice has been accepted.", prefs.StartMinimised,
            value => prefs.StartMinimised = value);
        AddToggle("Close to Tray", "Keep LG Launcher in the notification area when you close its window.", prefs.CloseToTray,
            value => prefs.CloseToTray = value);
        AddToggle("Close when Game Launched", "Hide the window in the tray during play. Restore it when the game exits.", prefs.CloseWhenGameLaunched,
            value => prefs.CloseWhenGameLaunched = value);
        Action<bool>? syncAutomaticUpdates = null;
        if (UpdateInstaller.IsSetupInstallation && !UpdateInstaller.IsUserSetupInstallation)
        {
            const string setupUpdates = "In-app updates are available after moving to the per-user Windows Setup.";
            stack.Children.Add(PreferenceToggle("Automatic updates", setupUpdates, false,
                value => prefs.AutomaticUpdates = value, overrideReason: () => setupUpdates));
        }
        else
        {
            syncAutomaticUpdates = AddToggle("Automatic updates", "Download and install official updates at startup. The launcher restarts when ready.",
                prefs.AutomaticUpdates, value => prefs.AutomaticUpdates = value);
        }
        var sections = new StackPanel { Spacing = 12, Children = { Section("Startup and window behaviour", stack) } };
        if (OperatingSystem.IsWindows())
        {
            var rendering = PreferenceToggle("Hardware rendering",
                "Hardware rendering uses GPU resources and may slightly affect game performance on lower-end devices. Quit and reopen LG Launcher to apply changes.",
                !prefs.DisableHardwareRendering, value => prefs.DisableHardwareRendering = !value,
                () => _ = ShowRenderingRestart());
            var priority = PreferenceToggle("Set process priority",
                "Give the game executable High CPU priority on Windows. This applies to new game launches.",
                prefs.SetProcessPriority, value => prefs.SetProcessPriority = value);
            sections.Children.Add(Section("Performance", new StackPanel { Spacing = 10, Children = { rendering, priority } }));
        }

        var reset = ActionButton("Reset", () =>
        {
            if (ResetFirstUsagePrompts()) syncAutomaticUpdates?.Invoke(false);
        });
        AutomationProperties.SetName(reset, "Reset first-usage prompts");
        var row = PreferenceRow("Reset first-usage prompts", reset,
            "Show the first-run prompts again the next time you open LG Launcher. Automatic updates will be turned off.");
        row.ColumnDefinitions[1].Width = GridLength.Auto;
        BindPreferenceAvailability(row, reset, () => !_library.UsageNoticeAccepted && !prefs.AutomaticUpdates
            ? "The first-usage prompts will appear the next time you open LG Launcher." : null);
        var contributions = PreferenceToggle("Contribution reminders",
            "Show ways to support LG Launcher after your first game and every 50 returns from a game.",
            !prefs.DisableContributionReminders, value => prefs.DisableContributionReminders = !value,
            () =>
            {
                if (prefs.DisableContributionReminders) _contributionReminderPending = false;
            });
        sections.Children.Add(Section("Miscellaneous", new StackPanel { Spacing = 10, Children = { row, contributions } }));
        return sections;

        Action<bool> AddToggle(string name, string help, bool selected, Action<bool> update, bool system = false)
        {
            var toggle = CompactToggle(selected);
            Avalonia.Automation.AutomationProperties.SetName(toggle, name);
            toggle.IsEnabled = !system || (OperatingSystem.IsWindows() && !_verification);
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(new StackPanel { Spacing = 3, Children = { Label(name), Label(help, muted: true) } });
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            stack.Children.Add(row);
            var changing = false;
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (changing) return;
                var requested = toggle.IsChecked == true;
                if (requested == selected) return;
                string? previousCommand = null;
                var registryChanged = false;
                var previousSaveFailure = _saveFailed;
                try
                {
                    if (system && OperatingSystem.IsWindows())
                    {
                        previousCommand = WindowsDesktop.GetStartupCommand();
                        WindowsDesktop.SetStartupCommand(requested ? WindowsDesktop.LauncherStartupCommand() : null);
                        registryChanged = true;
                    }
                    update(requested);
                    if (Save())
                    {
                        selected = requested;
                        PreferencesChanged?.Invoke();
                        if (!prefs.AutomaticUpdates && _automaticInstall && !_updateHandoff) _updateInstall?.Cancel();
                        Status("App behaviour updated.");
                        return;
                    }
                    update(selected);
                    _saveFailed = previousSaveFailure;
                    if (registryChanged && OperatingSystem.IsWindows()) WindowsDesktop.SetStartupCommand(previousCommand);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    await Notice("Could not update app behaviour", error.Message);
                }
                Synchronize(selected);
            };
            return Synchronize;

            void Synchronize(bool value)
            {
                selected = value;
                changing = true;
                toggle.IsChecked = value;
                changing = false;
            }
        }
    }

    private Task<bool> ShowRenderingRestart()
    {
        var dialog = new ModalDialog("Restart LG Launcher?", 453);
        dialog.Body.Children.Add(Label("Restart LG Launcher to apply the hardware rendering change.", muted: true));
        var feedback = Label("", muted: true);
        feedback.IsVisible = false;
        dialog.Body.Children.Add(feedback);
        var later = ActionButton("Later", () => dialog.Close(), "quiet");
        dialog.InitialFocus = later;
        dialog.Footer.Children.Add(later);
        dialog.Footer.Children.Add(ActionButton("Restart now", () =>
        {
            var message = RestartForRendering();
            if (message == null) return;
            feedback.Text = message;
            feedback.IsVisible = true;
        }, "primary"));
        return ShowModal(dialog);
    }

    private string? RestartForRendering()
    {
        if (_playing.Count > 0) return "Close running games before restarting LG Launcher.";
        if (_modFileOperation) return "Wait for the mod file operation to finish before restarting.";
        if (_updateInstall != null || _updateHandoff) return "Wait for the launcher update to finish before restarting.";
        if (_editingArtwork) return "Finish editing artwork before restarting.";
        if (_saveFailed && !Save()) return "The library could not be saved. Try again before restarting.";

        try { Program.StartRestart(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"Could not restart LG Launcher. {error.Message}";
        }
        _allowClose = true;
        Close();
        return null;
    }

    private bool ResetFirstUsagePrompts()
    {
        var preferences = _library.Preferences;
        var previous = (_library.UsageNoticeAccepted, preferences.AutomaticUpdates);
        var reset = (UsageNoticeAccepted: false, AutomaticUpdates: false);
        if (!SavePreference(previous, reset, choice =>
        {
            _library.UsageNoticeAccepted = choice.UsageNoticeAccepted;
            preferences.AutomaticUpdates = choice.AutomaticUpdates;
        }, null)) return false;

        _automaticUpdatePending = false;
        if (_automaticInstall && !_updateHandoff) _updateInstall?.Cancel();
        Status("First-usage prompts reset. Quit and reopen LG Launcher to see them again.");
        return true;
    }

    internal void RestoreWindow()
    {
        if (_closed) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (_tray != null) _tray.IsVisible = false;
        Activate();
    }

    private bool HideToTray()
    {
        if (!OperatingSystem.IsWindows() || Icon == null) return false;
        if (_tray == null)
        {
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open LG Launcher");
            var exit = new NativeMenuItem("Quit");
            open.Click += (_, _) => RestoreWindow();
            exit.Click += (_, _) =>
            {
                RestoreWindow();
                _explicitQuit = true;
                Close();
            };
            menu.Items.Add(open);
            menu.Items.Add(exit);
            _tray = new TrayIcon { Icon = Icon, ToolTipText = "LG Launcher", Menu = menu, IsVisible = false };
            _tray.Clicked += (_, _) => RestoreWindow();
        }
        _tray.IsVisible = true;
        StopThemeMusic();
        Hide();
        return true;
    }

    private void AnimateView(Control control, bool fadeOnly = false) =>
        AnimateMotion(control, token => ViewMotion.Reveal(control, fadeOnly, token));

    private bool CanAnimate(Control control) =>
        _motion && !_closed && control.IsEffectivelyVisible && WindowState != WindowState.Minimized;

    private void AnimateMotion(Control control, Func<CancellationToken, Task> animate)
    {
        CancelMotion(control);
        if (!CanAnimate(control)) return;
        var work = new CancellationTokenSource();
        _viewMotion.Add(control, work);
        _ = RunMotion(control, animate, work);
    }

    private void CancelMotion(Control control)
    {
        if (_viewMotion.Remove(control, out var work)) work.Cancel();
    }

    private void CancelMotion()
    {
        foreach (var control in _viewMotion.Keys.ToArray()) CancelMotion(control);
    }

    private async Task RunMotion(Control control, Func<CancellationToken, Task> animate, CancellationTokenSource work)
    {
        try { await animate(work.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (_viewMotion.GetValueOrDefault(control) == work) _viewMotion.Remove(control);
            work.Dispose();
        }
    }

    private SymbolIcon Symbol(string key, double size = 16)
    {
        var data = this.FindResource(key) as Avalonia.Media.Geometry ?? Application.Current?.FindResource(key) as Avalonia.Media.Geometry;
        return new SymbolIcon { Data = data, Width = size, Height = size };
    }
}
