using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LGLauncher.Components;

public sealed class ModalHost : Panel
{
    private readonly Border _backdrop = new() { Classes = { "lg-modal-backdrop" } };
    private readonly List<ModalEntry> _stack = [];
    private bool _underlayWasEnabled;

    public ModalHost()
    {
        IsVisible = false;
        Children.Add(_backdrop);
    }

    public Control? Underlay { get; set; }
    public bool HasModal => _stack.Count > 0;

    public Task<bool> Show(ModalDialog dialog)
    {
        if (dialog.Host != null) throw new InvalidOperationException("This dialog is already open.");

        var entry = new ModalEntry(dialog, TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());
        if (!HasModal)
        {
            if (Underlay != null)
            {
                _underlayWasEnabled = Underlay.IsEnabled;
                Underlay.SetCurrentValue(IsEnabledProperty, false);
            }
            IsVisible = true;
        }
        else
        {
            _stack[^1].Dialog.IsEnabled = false;
        }

        dialog.Host = this;
        _stack.Add(entry);
        Children.Add(dialog);
        if (!dialog.FocusInitial())
        {
            dialog.Focus();
            Dispatcher.UIThread.Post(() =>
            {
                if (HasModal && _stack[^1] == entry && (dialog.IsFocused || !dialog.IsKeyboardFocusWithin)) dialog.FocusInitial();
            }, DispatcherPriority.Loaded);
        }
        return entry.Completion.Task;
    }

    public void DismissTop()
    {
        if (HasModal) _stack[^1].Dialog.Close();
    }

    public void CloseAll()
    {
        while (HasModal) DismissTop();
    }

    internal void Close(ModalDialog dialog, bool accepted)
    {
        var index = _stack.FindIndex(entry => entry.Dialog == dialog);
        if (index < 0) return;

        var entry = _stack[index];
        var wasTop = index == _stack.Count - 1;
        if (!wasTop) _stack[index + 1].PreviousFocus = entry.PreviousFocus;
        _stack.RemoveAt(index);
        Children.Remove(dialog);
        dialog.Host = null;
        dialog.IsEnabled = true;
        if (wasTop)
        {
            if (HasModal) _stack[^1].Dialog.IsEnabled = true;
            else
            {
                IsVisible = false;
                Underlay?.SetCurrentValue(IsEnabledProperty, _underlayWasEnabled);
            }
            if (entry.PreviousFocus?.Focus() != true && HasModal)
            {
                var active = _stack[^1].Dialog;
                if (!active.FocusInitial()) active.Focus();
            }
        }
        dialog.NotifyClosed();
        entry.Completion.SetResult(accepted);
    }

    private sealed class ModalEntry(ModalDialog dialog, IInputElement? previousFocus)
    {
        public ModalDialog Dialog { get; } = dialog;
        public IInputElement? PreviousFocus { get; set; } = previousFocus;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class ModalDialog : Border
{
    private readonly Border _contentClip = new() { ClipToBounds = true };

    public ModalDialog(string title, double width, Control? header = null, Control? footerLeading = null, Control? content = null,
        Control? headingTrailing = null, Border? footerPanel = null)
    {
        Classes.Add("lg-modal");
        Width = width;
        MaxHeight = 680;
        Margin = new Thickness(24);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, false);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this, title);

        var heading = new TextBlock
        {
            Text = title, Classes = { "lg-heading" }, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 19, 22, 12)
        };
        Control headingContent = heading;
        if (headingTrailing != null)
        {
            headingContent = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                ColumnSpacing = 12,
                Margin = heading.Margin,
                Children = { heading, headingTrailing }
            };
            heading.Margin = default;
            heading.VerticalAlignment = VerticalAlignment.Center;
            headingTrailing.VerticalAlignment = VerticalAlignment.Center;
            headingTrailing.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(headingTrailing, 1);
        }
        var body = content ?? new ScrollViewer
        {
            Content = Body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var top = new StackPanel();
        if (title.Length > 0 || headingTrailing != null) top.Children.Add(headingContent);
        if (header != null)
        {
            header.Margin = new Thickness(22, 0, 22, 12);
            top.Children.Add(header);
        }
        Control footer = Footer;
        if (footerLeading != null)
        {
            footerLeading.Margin = new Thickness(22, 6, 8, 18);
            footerLeading.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(Footer, 1);
            footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Children = { footerLeading, Footer } };
        }
        if (footerPanel != null)
        {
            footerPanel.Child = footer;
            footer = footerPanel;
        }
        _contentClip.Child = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"), Children = { top, body, footer } };
        Child = _contentClip;
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
    }

    protected override Type StyleKeyOverride => typeof(Border);

    public StackPanel Body { get; } = new() { Spacing = 12, Margin = new Thickness(22, 0, 22, 10) };
    public StackPanel Footer { get; } = new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
        Margin = new Thickness(22, 6, 22, 18)
    };
    public Control? InitialFocus { get; set; }
    public Button? DefaultButton { get; set; }
    public event EventHandler? Closed;

    internal ModalHost? Host { get; set; }

    public void Close(bool accepted = false) => Host?.Close(this, accepted);

    internal bool FocusInitial() => (InitialFocus ?? this).Focus();
    internal void NotifyClosed() => Closed?.Invoke(this, EventArgs.Empty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_contentClip != null && change.Property == CornerRadiusProperty) UpdateContentClip();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateContentClip();
        return base.ArrangeOverride(finalSize);
    }

    private void UpdateContentClip()
    {
        var radius = CornerRadius;
        var stroke = BorderThickness;
        var padding = Padding;
        if (UseLayoutRounding)
        {
            var scale = LayoutHelper.GetLayoutScale(this);
            stroke = LayoutHelper.RoundLayoutThickness(stroke, scale);
            padding = LayoutHelper.RoundLayoutThickness(padding, scale);
        }
        // Avalonia draws the stroke on the radius centerline and arranges content inside its inner edge.
        _contentClip.CornerRadius = new CornerRadius(
            InnerRadius(radius.TopLeft, stroke.Left, stroke.Top, padding.Left, padding.Top),
            InnerRadius(radius.TopRight, stroke.Right, stroke.Top, padding.Right, padding.Top),
            InnerRadius(radius.BottomRight, stroke.Right, stroke.Bottom, padding.Right, padding.Bottom),
            InnerRadius(radius.BottomLeft, stroke.Left, stroke.Bottom, padding.Left, padding.Bottom));

        static double InnerRadius(double outerRadius, double horizontalStroke, double verticalStroke,
            double horizontalPadding, double verticalPadding) => Math.Max(0, outerRadius -
                Math.Min(horizontalStroke / 2 + horizontalPadding, verticalStroke / 2 + verticalPadding));
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Handled || args.KeyModifiers != KeyModifiers.None) return;

        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            Close();
        }
        else if (args.Key == Key.Enter && DefaultButton is { IsEffectivelyEnabled: true } button)
        {
            args.Handled = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
