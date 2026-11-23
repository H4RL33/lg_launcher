using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace LGLauncher.Components;

public static class ViewMotion
{
    private static readonly TimeSpan TranslationDuration = TimeSpan.FromMilliseconds(230);
    public static TimeSpan FadeDuration { get; } = TimeSpan.FromMilliseconds(180);

    public static Task Reveal(Visual target, bool fadeOnly, CancellationToken cancellationToken)
    {
        var start = new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, fadeOnly ? .3 : .28) } };
        var finish = new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d) } };
        if (!fadeOnly)
        {
            start.Setters.Add(new Setter(TranslateTransform.YProperty, 12d));
            finish.Setters.Add(new Setter(TranslateTransform.YProperty, 0d));
        }
        var animation = new Animation
        {
            Duration = fadeOnly ? FadeDuration : TranslationDuration,
            Easing = new CubicEaseOut(),
            Children = { start, finish }
        };
        return animation.RunAsync(target, cancellationToken);
    }

    public static async Task Move(Visual target, Vector offset, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || TopLevel.GetTopLevel(target) is not { } host) return;

        var translation = new TranslateTransform(offset.X, offset.Y);
        var easing = new CubicEaseOut();
        var started = Stopwatch.GetTimestamp();
        var finished = new TaskCompletionSource();
        target.RenderTransform = translation;
        try
        {
            host.RequestAnimationFrame(ApplyFrame);
            await finished.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (target.RenderTransform == translation) target.ClearValue(Visual.RenderTransformProperty);
        }

        void ApplyFrame(TimeSpan _)
        {
            if (cancellationToken.IsCancellationRequested) return;

            // Write the translation directly so animation teardown cannot restore the starting offset.
            var progress = Math.Min(1, Stopwatch.GetElapsedTime(started) / TranslationDuration);
            var remaining = offset * (1 - easing.Ease(progress));
            translation.X = remaining.X;
            translation.Y = remaining.Y;
            if (progress < 1) host.RequestAnimationFrame(ApplyFrame);
            else finished.SetResult();
        }
    }

    internal static Task RevealFromTop(Animatable target, StyledProperty<double> property, double from, double to,
        CancellationToken cancellationToken)
    {
        var animation = new Animation
        {
            Duration = to > from ? FadeDuration : TimeSpan.FromMilliseconds(105),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(property, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(property, to) } }
            }
        };
        return animation.RunAsync(target, cancellationToken);
    }
}
