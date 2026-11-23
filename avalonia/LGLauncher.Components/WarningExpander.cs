using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;

namespace LGLauncher.Components;

public sealed class WarningExpander : Expander
{
    public static readonly StyledProperty<double> RevealProgressProperty =
        AvaloniaProperty.Register<WarningExpander, double>(nameof(RevealProgress));

    private Canvas? _viewport;
    private ContentPresenter? _detail;
    private CancellationTokenSource? _motion;

    public double RevealProgress => GetValue(RevealProgressProperty);

    protected override Type StyleKeyOverride => typeof(Expander);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs args)
    {
        if (_detail != null) _detail.SizeChanged -= DetailResized;
        base.OnApplyTemplate(args);
        _viewport = args.NameScope.Find<Canvas>("WarningReveal");
        _detail = args.NameScope.Find<ContentPresenter>("PART_ContentPresenter");
        if (_detail != null) _detail.SizeChanged += DetailResized;
        UpdateReveal();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty) StartReveal();
        else if (change.Property == RevealProgressProperty) UpdateReveal();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopReveal();
        SetValue(RevealProgressProperty, IsExpanded ? 1d : 0d);
        base.OnDetachedFromVisualTree(e);
    }

    private void DetailResized(object? sender, SizeChangedEventArgs e) => UpdateReveal();

    private void UpdateReveal()
    {
        if (_viewport == null || _detail == null) return;
        _detail.IsVisible = IsExpanded || RevealProgress > 0;
        _viewport.Height = _detail.Bounds.Height * RevealProgress;
    }

    private void StartReveal()
    {
        var from = RevealProgress;
        var to = IsExpanded ? 1d : 0d;
        StopReveal();
        SetValue(RevealProgressProperty, from);
        UpdateReveal();
        if (from == to || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not Window owner || !owner.Classes.Contains("lg-motion"))
        {
            SetValue(RevealProgressProperty, to);
            return;
        }

        var motion = new CancellationTokenSource();
        _motion = motion;
        _ = Animate(motion, from, to);
    }

    private async Task Animate(CancellationTokenSource motion, double from, double to)
    {
        try { await ViewMotion.RevealFromTop(this, RevealProgressProperty, from, to, motion.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (_motion == motion)
            {
                _motion = null;
                SetValue(RevealProgressProperty, to);
            }
            motion.Dispose();
        }
    }

    private void StopReveal()
    {
        var motion = _motion;
        _motion = null;
        motion?.Cancel();
    }
}
