using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LGLauncher.Components;

public static class SmoothScroll
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollAnimation> Animations = new();
    private static readonly TimeSpan WheelDuration = TimeSpan.FromMilliseconds(120);

    static SmoothScroll()
    {
        InputElement.PointerWheelChangedEvent.AddClassHandler<PopupRoot>((_, args) => OnWheel(args), RoutingStrategies.Tunnel);
    }

    public static void EnableWheel(Window window) =>
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, args) => OnWheel(args), RoutingStrategies.Tunnel);

    public static void Cancel(ScrollViewer viewer)
    {
        if (Animations.TryGetValue(viewer, out var animation)) animation.Cancel();
    }

    public static bool MoveBy(ScrollViewer viewer, Vector distance, TimeSpan duration)
    {
        var animation = Animations.GetValue(viewer, static target => new ScrollAnimation(target));
        var destination = animation.Destination;
        var target = Clamp(viewer, destination + distance);
        if (target == destination) return target != viewer.Offset;

        var host = TopLevel.GetTopLevel(viewer);
        if (MotionEnabled(host) && viewer.Content is not ILogicalScrollable { IsLogicalScrollEnabled: true })
            animation.Start(host!, target, duration);
        else
        {
            animation.Cancel();
            viewer.SetCurrentValue(ScrollViewer.OffsetProperty, target);
        }
        return true;
    }

    private static void OnWheel(PointerWheelEventArgs args)
    {
        if (args.Pointer.Type != PointerType.Mouse || args.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift) ||
            args.Delta == default || args.Delta.X != 0 && args.Delta.Y != 0 || args.Source is not Visual source) return;

        var shifted = args.KeyModifiers == KeyModifiers.Shift && args.Delta.X == 0;
        var horizontal = shifted || args.Delta.X != 0;
        var delta = args.Delta.X != 0 ? args.Delta.X : args.Delta.Y;
        var units = GetSystemWheelUnits(horizontal);
        // Elsewhere, keep Avalonia's native handling of precision deltas and horizontal gestures.
        if (units == null && (horizontal || delta != Math.Truncate(delta) || !MotionEnabled(TopLevel.GetTopLevel(source)))) return;

        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            if (visual is NumericUpDown { IsKeyboardFocusWithin: true } or ComboBox { IsDropDownOpen: false, IsKeyboardFocusWithin: true })
                return;
            if (visual is not ScrollViewer viewer || !CanScroll(viewer, horizontal)) continue;
            if (units == 0)
            {
                args.Handled = true;
                return;
            }
            if ((horizontal ? viewer.HorizontalSnapPointsType : viewer.VerticalSnapPointsType) != SnapPointsType.None ||
                units == null && viewer.Content is ILogicalScrollable { IsLogicalScrollEnabled: true }) return;

            var line = horizontal ? viewer.SmallChange.Width : viewer.SmallChange.Height;
            var page = horizontal ? viewer.LargeChange.Width : viewer.LargeChange.Height;
            var step = units switch
            {
                null => 50,
                uint.MaxValue => page,
                uint count => Math.Min(page, count * line)
            };
            var direction = horizontal && !shifted && viewer.FlowDirection == FlowDirection.RightToLeft ? 1 : -1;
            var distance = direction * delta * step;
            if (!MoveBy(viewer, horizontal ? new Vector(distance, 0) : new Vector(0, distance), WheelDuration) &&
                viewer.IsScrollChainingEnabled) continue;
            args.Handled = true;
            return;
        }
    }

    private static bool CanScroll(ScrollViewer viewer, bool horizontal) => horizontal
        ? viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && viewer.ScrollBarMaximum.X > 0
        : viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && viewer.ScrollBarMaximum.Y > 0;

    public static uint? GetSystemWheelUnits(bool horizontal)
    {
        const uint getWheelScrollLines = 0x0068;
        const uint getWheelScrollChars = 0x006C;
        return OperatingSystem.IsWindows() &&
            SystemParametersInfo(horizontal ? getWheelScrollChars : getWheelScrollLines, 0, out var units, 0)
                ? units : null;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    private static bool MotionEnabled(TopLevel? host)
    {
        while (host is PopupRoot popup) host = popup.ParentTopLevel;
        return host is Window window && window.Classes.Contains("lg-motion");
    }

    private static Vector Clamp(ScrollViewer viewer, Vector offset) => new(
        Math.Clamp(offset.X, 0, Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width)),
        Math.Clamp(offset.Y, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));

    private sealed class ScrollAnimation(ScrollViewer viewer)
    {
        private bool _scheduled;
        private bool _running;
        private long _started;
        private TimeSpan _duration;
        private Vector _from;
        private Vector _lastOffset;
        private Vector _target;

        public Vector Destination => _running && viewer.Offset == _lastOffset ? Clamp(viewer, _target) : viewer.Offset;

        public void Cancel() => _running = false;

        public void Start(TopLevel host, Vector target, TimeSpan duration)
        {
            _running = true;
            _started = Stopwatch.GetTimestamp();
            _duration = duration;
            _from = _lastOffset = viewer.Offset;
            _target = target;
            if (_scheduled) return;
            _scheduled = true;
            host.RequestAnimationFrame(Step);
        }

        private void Step(TimeSpan frameTime)
        {
            var host = TopLevel.GetTopLevel(viewer);
            // A scrollbar drag, focus change or navigation can reposition the view directly.
            if (!_running || !MotionEnabled(host) || !viewer.IsEffectivelyVisible ||
                !viewer.IsEffectivelyEnabled || viewer.Offset != _lastOffset)
            {
                _running = _scheduled = false;
                return;
            }

            var progress = Math.Min(1, Stopwatch.GetElapsedTime(_started) / _duration);
            var remaining = 1 - progress;
            var amount = 1 - remaining * remaining * remaining;
            var target = Clamp(viewer, _target);
            viewer.SetCurrentValue(ScrollViewer.OffsetProperty, progress == 1 ? target : _from + (target - _from) * amount);
            _lastOffset = viewer.Offset;
            _running = _scheduled = progress < 1 && _lastOffset != target;
            if (_scheduled) host!.RequestAnimationFrame(Step);
        }
    }
}
