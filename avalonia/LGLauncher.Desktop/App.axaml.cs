using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace LGLauncher;

internal sealed partial class App : Application
{
    internal (LibraryStore Store, Library Library)? StartupProfile { get; init; }
    internal string? StartupNotice { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = StartupProfile is { } profile
                    ? new Launcher(profile.Store, profile.Library)
                    : StartupErrorWindow(desktop, StartupNotice ?? "The library could not be opened.");

                if (desktop.MainWindow is Launcher main && Program.ActivationSignal is { } signal)
                {
                    var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        if (desktop.MainWindow is StartupWindow splash) splash.Activate();
                        else main.RestoreWindow();
                    }), null, Timeout.Infinite, false);
                    main.Closed += (_, _) => registration.Unregister(null);
                }

                if (desktop.MainWindow is Launcher launcher && Program.PreviewPath is { } preview)
                {
                    launcher.Opened += VerifyOnOpen;

                    void VerifyOnOpen(object? sender, EventArgs args)
                    {
                        launcher.Opened -= VerifyOnOpen;
                        Dispatcher.UIThread.Post(async () =>
                        {
                            try
                            {
                                if (Program.VerificationMode == "--verify-mods-ui") await launcher.VerifyModsOnly(preview);
                                else if (Program.VerificationMode == "--verify-web") await launcher.VerifyWeb(preview);
                                else await launcher.VerifyMigration(preview, Program.VerificationMode == "--verify-migration");
                                desktop.Shutdown();
                            }
                            catch (Exception error)
                            {
                                File.WriteAllText(preview + ".error.txt", error.ToString());
                                desktop.Shutdown(1);
                            }
                        }, DispatcherPriority.Background);
                    }
                }
                if (desktop.MainWindow is Launcher mainLauncher &&
                    (Program.VerificationMode == "--verify-migration" || Program.VerificationMode == null && !mainLauncher.StartsMinimised))
                {
                    var splash = mainLauncher.CreateSplash();
                    desktop.MainWindow = splash;
                    splash.Opened += async (_, _) =>
                    {
                        try { await mainLauncher.InitializeStartup(splash); }
                        catch (Exception error) when (Program.PreviewPath != null)
                        {
                            File.WriteAllText(Program.PreviewPath + ".error.txt", error.ToString());
                            desktop.Shutdown(1);
                            return;
                        }
                        if (!splash.IsVisible) return;
                        desktop.MainWindow = mainLauncher;
                        mainLauncher.Show();
                        splash.Close();
                    };
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                desktop.MainWindow = StartupErrorWindow(desktop, error.Message);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static Window StartupErrorWindow(IClassicDesktopStyleApplicationLifetime desktop, string message)
    {
        if (Program.PreviewPath is not { } preview) return ErrorWindow(message);

        File.WriteAllText(preview + ".error.txt", message);
        var window = new Window();
        window.Opened += (_, _) => desktop.Shutdown(1);
        return window;
    }

    private static Window ErrorWindow(string message)
    {
        var window = new Window
        {
            Title = "LG Launcher", Width = 460, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Classes = { "lg", "primary" } };
        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 16, Children =
        {
            new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close
        }
        };
        return window;
    }
}
