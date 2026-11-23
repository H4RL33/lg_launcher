using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace LGLauncher.Gallery;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var gallery = new MainWindow();
            desktop.MainWindow = gallery;
            if (desktop.Args is ["--smoke", var preview])
            {
                gallery.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        gallery.VerifyAndCapture(preview);
                        Console.WriteLine("Gallery controls passed.");
                        desktop.Shutdown();
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error);
                        desktop.Shutdown(1);
                    }
                }, DispatcherPriority.Background);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
