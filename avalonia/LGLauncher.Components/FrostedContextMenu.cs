using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace LGLauncher.Components;

public sealed class FrostedContextMenu : ContextMenu
{
    public static readonly StyledProperty<double> RevealProgressProperty =
        AvaloniaProperty.Register<FrostedContextMenu, double>(nameof(RevealProgress), 1d);

    private Panel? _surface;
    private Border? _backdrop;
    private RenderTargetBitmap? _snapshot;
    private CancellationTokenSource? _motion;
    private bool _collapsing;

    public FrostedContextMenu()
    {
        Opened += (_, _) => Present();
        Closing += Dismiss;
        Closed += (_, _) => Reset();
    }

    public double RevealProgress => GetValue(RevealProgressProperty);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs args)
    {
        if (_surface != null) _surface.SizeChanged -= ResizeClip;
        base.OnApplyTemplate(args);
        _surface = args.NameScope.Find<Panel>("RevealSurface");
        _backdrop = args.NameScope.Find<Border>("FrostedBackdrop");
        if (_surface == null) return;
        _surface.SizeChanged += ResizeClip;
        UpdateClip();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RevealProgressProperty) UpdateClip();
    }

    private void ResizeClip(object? sender, SizeChangedEventArgs args) => UpdateClip();

    private void UpdateClip()
    {
        if (_surface == null) return;
        if (RevealProgress >= 1)
        {
            _surface.Clip = null;
            return;
        }
        // Assign a new clip so Avalonia synchronizes the changed geometry to the compositor.
        _surface.Clip = new RectangleGeometry(
            new Rect(0, 0, _surface.Bounds.Width, _surface.Bounds.Height * Math.Max(0, RevealProgress)));
    }

    private void Present()
    {
        Reset();
        UpdateLayout();
        var target = (Parent as Popup)?.PlacementTarget;
        if (target == null || TopLevel.GetTopLevel(target) is not Window window) return;
        if (!window.Classes.Contains("lg-opaque") && window.Content is Control content) CaptureBackdrop(content);
        if (!window.Classes.Contains("lg-motion")) return;

        StartMotion(0d, 1d);
    }

    private void Dismiss(object? sender, CancelEventArgs args)
    {
        if (args.Cancel || !IsOpen || RevealProgress <= 0 ||
            Parent is not Popup { PlacementTarget: { IsEffectivelyVisible: true } target } popup ||
            TopLevel.GetTopLevel(target) is not Window { IsVisible: true } window ||
            !window.Classes.Contains("lg-motion")) return;
        args.Cancel = true;
        if (_collapsing) return;

        _collapsing = true;
        popup.IsHitTestVisible = false;
        StartMotion(RevealProgress, 0d, popup);
    }

    private void CaptureBackdrop(Control content)
    {
        if (_backdrop == null || !_backdrop.IsAttachedToVisualTree() || content.Bounds.Width <= 0 ||
            content.Bounds.Height <= 0 || _backdrop.Bounds.Width <= 0 || _backdrop.Bounds.Height <= 0) return;
        var size = content.Bounds.Size;
        var origin = content.PointToClient(_backdrop.PointToScreen(default));
        var sampledArea = new Rect(origin, _backdrop.Bounds.Size).Intersect(new Rect(size));
        if (sampledArea.Width <= 0 || sampledArea.Height <= 0) return;

        // Sample the owning window once at low resolution, including for menus hosted in a separate native popup.
        var scale = Math.Min(.5, 600 / Math.Max(size.Width, size.Height));
        _snapshot = new RenderTargetBitmap(PixelSize.FromSize(size, scale), new Vector(96 * scale, 96 * scale));
        _snapshot.Render(content);
        _backdrop.Background = new ImageBrush(_snapshot)
        {
            SourceRect = new RelativeRect(
                sampledArea.X / size.Width, sampledArea.Y / size.Height,
                sampledArea.Width / size.Width, sampledArea.Height / size.Height, RelativeUnit.Relative),
            DestinationRect = new RelativeRect(new Rect(sampledArea.Position - (Vector)origin, sampledArea.Size), RelativeUnit.Absolute),
            Stretch = Stretch.Fill
        };
    }

    private void StartMotion(double from, double to, Popup? dismiss = null)
    {
        StopMotion();
        SetValue(RevealProgressProperty, from);
        var motion = new CancellationTokenSource();
        _motion = motion;
        _ = Animate(motion, from, to, dismiss);
    }

    private async Task Animate(CancellationTokenSource motion, double from, double to, Popup? dismiss)
    {
        try { await ViewMotion.RevealFromTop(this, RevealProgressProperty, from, to, motion.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (_motion == motion)
            {
                _motion = null;
                SetValue(RevealProgressProperty, to);
                // Close the host directly because its IsOpen property may already have requested a close.
                dismiss?.Close();
            }
            motion.Dispose();
        }
    }

    private void StopMotion()
    {
        var motion = _motion;
        _motion = null;
        motion?.Cancel();
    }

    private void Reset()
    {
        StopMotion();
        _collapsing = false;
        if (Parent is Popup popup) popup.IsHitTestVisible = true;
        SetValue(RevealProgressProperty, 1d);
        if (_backdrop != null) _backdrop.Background = null;
        _snapshot?.Dispose();
        _snapshot = null;
    }
}
