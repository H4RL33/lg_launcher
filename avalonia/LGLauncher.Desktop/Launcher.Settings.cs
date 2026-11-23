using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private const string SupportUrl = "https://ko-fi.com/h4rl33";
    private const string UsageDisclosure =
        "LG Launcher is for legally obtained games. Do not use it to launch pirated copies. LG Launcher does not condone video game piracy.";
    private const int MinimumInterfaceScale = 75;
    private const int MaximumInterfaceScale = 200;
    private static readonly int[] FontSizes = [11, 12, 13, 14, 15, 16, 17, 18];
    private static readonly int[] InterfaceScales = [MinimumInterfaceScale, 100, 125, 150, 175, MaximumInterfaceScale];
    private event Action? PreferencesChanged;

    private int EffectiveFontSize => Math.Clamp(_library.Preferences.InterfaceFontSize is 0
        ? (int)Math.Round(_defaultFontSize) : _library.Preferences.InterfaceFontSize, 11, 18);
    private double EffectiveInterfaceScale => EffectiveInterfaceScalePercent / 100d;
    private Color BuiltInAccentColour => (Color)this.FindResource("LG.DefaultAccentColor")!;
    private Color? DefaultAccentColour =>
        ColourPicker.TryParseHex(_library.Preferences.DefaultAccentColour, out var colour) && colour != BuiltInAccentColour ? colour : null;

    private int EffectiveInterfaceScalePercent
    {
        get
        {
            var saved = _library.Preferences.InterfaceScalePercent;
            return saved == 0 ? 100 : Math.Clamp(saved, MinimumInterfaceScale, MaximumInterfaceScale);
        }
    }

    private Control BuildAppearance()
    {
        var prefs = _library.Preferences;
        bool SaveScale(int percent) => SavePreference(prefs.InterfaceScalePercent, percent, value => prefs.InterfaceScalePercent = value, ApplyInterfaceScale);
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("Interface & Font", new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        PreferenceRow("Interface font", BuildFontPicker()),
                        PreferenceChoice("Base interface font size", FontSizes, EffectiveFontSize, "",
                            requested => SavePreference(prefs.InterfaceFontSize, requested, value => prefs.InterfaceFontSize = value, ApplyFont)),
                        PreferenceChoice("Base interface scale", InterfaceScales, EffectiveInterfaceScalePercent, "%",
                            SaveScale, EditInterfaceScale)
                    }
                }),
                Section("Theme", new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        PreferenceToggle("Translucency", "Use frosted glass in the launcher.",
                            !prefs.DisableTranslucency, value => prefs.DisableTranslucency = !value, ApplyTranslucency),
                        PreferenceRow("Default accent colour", BuildDefaultAccentPicker()),
                        PreferenceToggle("Artwork accent colour", "Match the accent colour to the artwork on game details.",
                            !prefs.DisableArtworkAccent, value => prefs.DisableArtworkAccent = !value, ApplyArtworkAccent)
                    }
                }),
                Section("Motion", new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        PreferenceToggle("Reduce Motion", "Turn off launcher animations.", prefs.ReduceMotion,
                            value => prefs.ReduceMotion = value, ApplyMotionPreference),
                        PreferenceToggle("Interactive Artwork Shift", "Shift game artwork as the pointer moves across the window.",
                            !prefs.DisableArtworkShift, value => prefs.DisableArtworkShift = !value,
                            () => ResetArtworkParallax(immediate: true),
                            overrideReason: () => prefs.ReduceMotion ? "Overridden by Reduce Motion." : null)
                    }
                })
            }
        };
    }

    private Control BuildLibrarySettings()
    {
        var prefs = _library.Preferences;
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("Game Details", new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        PreferenceToggle("Use Game Logos", "Use a custom or Steam logo when one is available.",
                            prefs.UseGameLogos, value => prefs.UseGameLogos = value),
                        PreferenceToggle("Theme music", "Play a custom track or a matched, installed Steam theme when viewing a game.",
                            prefs.PlayThemeMusic, value => prefs.PlayThemeMusic = value, UpdateThemeMusic,
                            overrideReason: () => OperatingSystem.IsWindows() ? null : "Theme music playback is available on Windows."),
                        PreferenceToggle("Show Store Tags", "Show Steam store tags below genres on game details.",
                            prefs.ShowStoreTags, value => prefs.ShowStoreTags = value)
                    }
                }),
                Section("Home", PreferenceToggle("Display most-recent RSS articles on Home",
                    "Show a row of recent articles from enabled feeds.", prefs.ShowRecentRss, value => prefs.ShowRecentRss = value))
            }
        };
    }

    private Control BuildAbout()
    {
        var disclosure = Label(UsageDisclosure, muted: true);
        disclosure.Foreground = this.FindResource("LG.Neutral") as IBrush;
        disclosure.Margin = new Thickness(0, 8, 0, 0);
        disclosure.HorizontalAlignment = HorizontalAlignment.Center;
        disclosure.TextAlignment = TextAlignment.Center;
        return new StackPanel
        {
            Spacing = 12,
            Children = { BuildUpdateSettings(), BuildReleaseNotes(), disclosure }
        };
    }

    private static Grid PreferenceRow(string title, Control control, string? description = null)
    {
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { Label(title) } };
        if (description != null) labels.Children.Add(Label(description, muted: true));
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        return new Grid
        {
            Classes = { "lg-preference" },
            ColumnDefinitions = ColumnDefinitions.Parse(control is ToggleSwitch ? "*,Auto" : "*,*"),
            ColumnSpacing = 14, Children = { labels, control }
        };
    }

    private Control PreferenceToggle(string title, string description, bool current, Action<bool> save,
        Action? apply = null, Func<string?>? overrideReason = null)
    {
        var toggle = new ToggleSwitch { IsChecked = current, OnContent = "", OffContent = "" };
        AutomationProperties.SetName(toggle, title);
        var row = PreferenceRow(title, toggle, description);
        toggle.IsCheckedChanged += (_, _) =>
        {
            var requested = toggle.IsChecked == true;
            if (requested == current) return;
            if (toggle.IsEffectivelyEnabled && SavePreference(current, requested, save, apply)) current = requested;
            else toggle.IsChecked = current;
        };
        if (overrideReason != null) BindPreferenceAvailability(row, toggle, overrideReason);
        return row;
    }

    private void BindPreferenceAvailability(Grid row, Control control, Func<string?> disabledReason)
    {
        void UpdateAvailability()
        {
            var reason = disabledReason();
            row.IsEnabled = reason == null;
            ToolTip.SetTip(row, reason);
            AutomationProperties.SetHelpText(control, reason);
        }

        ToolTip.SetShowOnDisabled(row, true);
        UpdateAvailability();
        row.AttachedToVisualTree += (_, _) =>
        {
            UpdateAvailability();
            PreferencesChanged += UpdateAvailability;
        };
        row.DetachedFromVisualTree += (_, _) => PreferencesChanged -= UpdateAvailability;
    }

    private Button BuildDefaultAccentPicker()
    {
        var swatch = ColourPicker.Swatch(BuiltInAccentColour);
        var value = Label("");
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        value.VerticalAlignment = VerticalAlignment.Center;
        var chevron = Symbol("LG.Icon.Chevron", 14);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        Grid.SetColumn(chevron, 2);
        var picker = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Classes = { "lg", "choice-picker" },
            Content = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                ColumnSpacing = 8,
                Children = { swatch, value, chevron }
            }
        };
        AutomationProperties.SetName(picker, "Default accent colour");
        UpdateTrigger();
        Click(picker, () =>
        {
            var prefs = _library.Preferences;
            var editor = new ColourPicker(BuiltInAccentColour, DefaultAccentColour ?? BuiltInAccentColour);
            var dialog = new ModalDialog("Default accent colour", 420);
            editor.HexField.Classes.Add("lg-modal-field");
            dialog.Body.Children.Add(editor);
            DialogActions(dialog, "Apply", () =>
            {
                if (!editor.TryGetColour(out var colour)) return;
                dialog.Close(true);
                var requested = colour == BuiltInAccentColour ? null : ColourPicker.FormatHex(colour);
                if (requested != prefs.DefaultAccentColour &&
                    SavePreference(prefs.DefaultAccentColour, requested, saved => prefs.DefaultAccentColour = saved, ApplyArtworkAccent))
                    UpdateTrigger();
            });
            dialog.InitialFocus = editor.HexField;
            editor.HexField.SelectAll();
            _ = ShowModal(dialog);
        });
        return picker;

        void UpdateTrigger()
        {
            var colour = DefaultAccentColour;
            swatch.Background = new SolidColorBrush(colour ?? BuiltInAccentColour);
            value.Text = colour is { } custom ? ColourPicker.FormatHex(custom) : "Default purple";
            AutomationProperties.SetHelpText(picker, $"Current value {value.Text}");
        }
    }

    private void EditInterfaceScale(int current, Action<int> apply)
    {
        var dialog = new ModalDialog("Custom interface scale", 380);
        var field = ModalTextField(current.ToString(), "Interface scale percentage");
        var error = Label($"Enter a whole number from {MinimumInterfaceScale} to {MaximumInterfaceScale}.", muted: true);
        error.IsVisible = false;
        field.TextChanged += (_, _) => error.IsVisible = false;
        Field(dialog.Body, $"Scale ({MinimumInterfaceScale}%–{MaximumInterfaceScale}%)", field);
        dialog.Body.Children.Add(error);
        DialogActions(dialog, "Apply", () =>
        {
            if (!Int32.TryParse(field.Text, out var percent) || percent is < MinimumInterfaceScale or > MaximumInterfaceScale)
            {
                error.IsVisible = true;
                field.Focus();
                return;
            }
            dialog.Close(true);
            apply(percent);
        });
        dialog.InitialFocus = field;
        field.SelectAll();
        _ = ShowModal(dialog);
    }

    private Control PreferenceChoice(string title, int[] choices, int current, string suffix, Func<int, bool> select,
        Action<int, Action<int>>? editCustom = null)
    {
        var picker = BuildChoicePicker(title, choices, current, choice => $"{choice}{suffix}", select, editCustom: editCustom);
        picker.Width = 100;
        picker.HorizontalAlignment = HorizontalAlignment.Right;
        return PreferenceRow(title, picker);
    }

    private Button BuildChoicePicker<T>(string title, IEnumerable<T> choices, T current, Func<T, string> format, Func<T, bool> select,
        bool textSearch = false, bool wrapLabel = false, Action<T, Action<T>>? editCustom = null)
    {
        var value = Label("");
        value.VerticalAlignment = VerticalAlignment.Center;
        if (!wrapLabel)
        {
            value.TextWrapping = TextWrapping.NoWrap;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        var chevron = Symbol("LG.Icon.ChevronDown", 14);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 1);
        var menu = LauncherMenus.Create(PlacementMode.Custom);
        menu.CustomPopupPlacementCallback = placement =>
        {
            var alignRight = placement.AnchorRectangle.Left + placement.PopupSize.Width > ClientSize.Width;
            placement.Anchor = alignRight ? PopupAnchor.BottomRight : PopupAnchor.BottomLeft;
            placement.Gravity = alignRight ? PopupGravity.BottomLeft : PopupGravity.BottomRight;
            placement.Offset = new Point(0, 4);
        };
        if (textSearch)
        {
            menu.IsTextSearchEnabled = true;
            TextSearch.SetTextBinding(menu, new Binding(nameof(MenuItem.Header)));
            menu.SelectionChanged += (_, _) =>
            {
                if (menu.SelectedItem is MenuItem item) item.Focus();
            };
        }
        var picker = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "lg", "choice-picker" },
            Content = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8, Children = { value, chevron } },
            ContextMenu = menu
        };
        AutomationProperties.SetName(picker, title);
        MenuItem? customItem = null;
        Action? commit = null;
        foreach (var choice in choices)
        {
            var item = menu.AddAction(format(choice), () => ApplyAfterClose(() => SelectChoice(choice)));
            item.ToggleType = MenuItemToggleType.Radio;
            item.Tag = choice;
        }
        if (editCustom != null)
        {
            menu.AddSeparator();
            customItem = menu.AddAction("Custom…", () => ApplyAfterClose(() => editCustom(current, SelectChoice)));
            customItem.ToggleType = MenuItemToggleType.Radio;
        }
        menu.Closed += (_, _) =>
        {
            var apply = commit;
            commit = null;
            apply?.Invoke();
            UpdateChoice();
        };
        menu.Opening += (_, _) => SizeMenu();
        menu.Opened += (_, _) =>
        {
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsChecked) is { } selected)
            {
                selected.BringIntoView();
                selected.Focus();
            }
        };
        Click(picker, OpenChoices);
        picker.KeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Down or Key.F4)) return;
            OpenChoices();
            args.Handled = true;
        };
        UpdateChoice();
        return picker;

        void ApplyAfterClose(Action apply)
        {
            if (!menu.IsOpen || commit != null) return;
            commit = apply;
            menu.Close();
        }

        void SelectChoice(T choice)
        {
            if (!EqualityComparer<T>.Default.Equals(choice, current) && select(choice)) current = choice;
            UpdateChoice();
        }

        void OpenChoices()
        {
            if (menu.IsOpen) return;
            SizeMenu();
            menu.Open(picker);
        }

        void SizeMenu()
        {
            menu.MinWidth = picker.Bounds.Width * EffectiveInterfaceScale;
            menu.MaxHeight = 320 * EffectiveInterfaceScale;
        }

        void UpdateChoice()
        {
            value.Text = format(current);
            AutomationProperties.SetHelpText(picker, $"Current value {value.Text}");
            var items = menu.Items.OfType<MenuItem>();
            var selected = items.FirstOrDefault(item => item != customItem && Equals(item.Tag, current)) ?? customItem;
            foreach (var item in items) item.IsChecked = item == selected;
        }
    }

    private bool SavePreference<T>(T previous, T requested, Action<T> save, Action? apply)
    {
        var previouslyUnsaved = _saveFailed;
        save(requested);
        if (Save())
        {
            apply?.Invoke();
            PreferencesChanged?.Invoke();
            return true;
        }
        save(previous);
        _saveFailed = previouslyUnsaved;
        return false;
    }

    private void ApplyInterfaceScale()
    {
        var scale = EffectiveInterfaceScale;
        InterfaceRoot.LayoutTransform = new ScaleTransform(scale, scale);
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var workArea = screen?.WorkingArea.Size.ToSize(screen.Scaling) ?? Size.Infinity;
        MinWidth = Math.Min(_unscaledWindowMinimum.Width * scale, workArea.Width);
        MinHeight = Math.Min(_unscaledWindowMinimum.Height * scale, workArea.Height);
        if (OperatingSystem.IsWindows()) ExtendClientAreaTitleBarHeightHint = DragBar.Height * scale;
        ApplyFont();
    }

    private void ApplyTranslucency()
    {
        Classes.Set("lg-opaque", _library.Preferences.DisableTranslucency);
        RefreshArtworkGlass();
    }

    private void ApplyMotionPreference()
    {
        var capable = !_verification && WindowsDesktop.AllowAnimations();
        _motion = capable && !_library.Preferences.ReduceMotion;
        WindowsDesktop.SetWindowMotion(this, _library.Preferences.ReduceMotion);
        Classes.Set("lg-motion", _motion);
        var tooltipMotion = new Transitions();
        if (_motion) tooltipMotion.Add(new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) });
        Application.Current!.Resources["LG.TooltipMotion"] = tooltipMotion;
        if (_motion) return;
        CancelMotion();
        SmoothScroll.Cancel(ContentScroll);
        ResetArtworkParallax(immediate: true);
    }

    private void OpenSupportPage() => _ = OpenProjectPage(SupportUrl, "Ko-fi");

    private async Task OpenProjectPage(string address, string site)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await Notice($"Could not open {site}", error.Message);
        }
    }
}
