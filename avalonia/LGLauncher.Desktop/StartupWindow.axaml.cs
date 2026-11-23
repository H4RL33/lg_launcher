using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LGLauncher;

internal sealed partial class StartupWindow : Window
{
    public StartupWindow(FontFamily font, bool animate)
    {
        InitializeComponent();
        WindowsDesktop.UseCompositorFrame(this, 0);
        FontFamily = font;
        using var art = typeof(StartupWindow).Assembly.GetManifestResourceStream("LGLauncher.Welcome.jpg");
        if (art != null)
        {
            var picture = new Bitmap(art);
            SplashArtwork.Source = picture;
            Closed += (_, _) => picture.Dispose();
        }
        Classes.Set("lg-motion", animate);
        Opened += (_, _) => WindowsDesktop.ApplySystemCorners(this);
    }

    internal void ShowProgress(double percent, string operation)
    {
        SplashProgress.Value = percent;
        SplashOperation.Text = operation;
    }
}
