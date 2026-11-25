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
    private const int DefaultTileColumns = 6;
    private const int MaximumTileColumns = 10;
    private const int DefaultTileMinimumWidth = 120;
    private static readonly int[] TileColumnChoices = Enumerable.Range(1, MaximumTileColumns).ToArray();
    private static readonly int[] TileWidthChoices = [32, 48, 64, 80, 96, 112, DefaultTileMinimumWidth, 128, 144, 160, 192, 224, 256];
    private const string UsageDisclosure =
        "LG Launcher is for legally obtained games. Do not use it to launch pirated copies. LG Launcher does not condone video game piracy.";
    private const int MinimumInterfaceScale = 75;
    private const int MaximumInterfaceScale = 200;
    private static readonly int[] FontSizes = [11, 12, 13, 14, 15, 16, 17, 18];
    private static readonly int[] InterfaceScales = [MinimumInterfaceScale, 100, 125, 150, 175, MaximumInterfaceScale];
    private sealed record Colourway(string Name, string Canvas, string Sidebar, string Surface, string Raised, string Line,
        string Text, string Muted, string Selection, string Folder);

    private static readonly Colourway[] Colourways =
    [
        new("Classic", "#0E111C", "#121624", "#191D2E", "#252A40", "#323950", "#F1F2FC", "#A9AEC7", "#3C365D", "#F0BC79"),
        new("Adwaita", "#222226", "#2E2E32", "#343438", "#39393D", "#4C4C51", "#FFFFFF", "#C0C0C6", "#45454A", "#F8E45C"),
        new("Catppuccin Mocha", "#1E1E2E", "#181825", "#242438", "#313244", "#45475A", "#CDD6F4", "#BAC2DE", "#45475A", "#F9E2AF"),
        new("Everforest", "#2D353B", "#232A2E", "#343F44", "#3D484D", "#4F585E", "#D3C6AA", "#B7BDA8", "#4F585E", "#DBBC7F"),
        new("Tokyo Night", "#1A1B26", "#16161E", "#222436", "#292E42", "#3B4261", "#C0CAF5", "#A9B1D6", "#394B70", "#E0AF68")
    ];

    private Colourway ActiveColourway => Colourways.FirstOrDefault(palette => palette.Name == _library.Preferences.Colourway) ?? Colourways[0];
    private event Action? PreferencesChanged;
    private bool TrackPlaytime => !_library.Preferences.DisablePlaytimeTracking;

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
        var colourway = BuildChoicePicker("Colourway", Colourways, ActiveColourway, palette => palette.Name,
            palette => SavePreference(prefs.Colourway, palette == Colourways[0] ? null : palette.Name,
                value => prefs.Colourway = value, ApplyColourway));
        colourway.HorizontalAlignment = HorizontalAlignment.Right;
        colourway.MinWidth = 154;
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
                        PreferenceRow("Colourway", colourway),
                        PreferenceToggle("Translucency", "Use frosted glass in the launcher.",
                            !prefs.DisableTranslucency, value => prefs.DisableTranslucency = !value, ApplyTranslucency),
                        PreferenceRow("Default accent colour", BuildDefaultAccentPicker()),
                        PreferenceToggle("Artwork accent colour", "Match the accent colour to the artwork on game details.",
                            !prefs.DisableArtworkAccent, value => prefs.DisableArtworkAccent = !value, ApplyArtworkAccent),
                        SettingsRow("Widget Customisation", "LG.Icon.Controls", Page.Widgets)
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

    private Control BuildWidgetSettings()
    {
        var prefs = _library.Preferences;
        var radius = BuildChoicePicker("Corner radius", new[] { -1, 0, 4, 8, 12, 16, 20 }, prefs.WidgetRadius ?? -1,
            value => value < 0 ? "Default" : $"{value} px",
            value => SavePreference(prefs.WidgetRadius, value < 0 ? (int?)null : value,
                radius => prefs.WidgetRadius = radius, ApplyWidgetTheme));
        var shadows = BuildChoicePicker("Shadow strength", new[] { 0, 25, 50, 75, 100 }, prefs.WidgetShadowStrength ?? 100,
            value => value == 0 ? "Off" : $"{value}%",
            value => SavePreference(prefs.WidgetShadowStrength, value == 100 ? (int?)null : value,
                strength => prefs.WidgetShadowStrength = strength, ApplyWidgetTheme));
        foreach (var picker in new[] { radius, shadows })
        {
            picker.HorizontalAlignment = HorizontalAlignment.Right;
            picker.MinWidth = 100;
        }
        return Section("Shape and shadows", new StackPanel
        {
            Spacing = 9,
            Children = { PreferenceRow("Corner radius", radius), PreferenceRow("Shadow strength", shadows) }
        });
    }

    private void ApplyWidgetTheme()
    {
        var prefs = _library.Preferences;
        foreach (var key in new[] { "LG.ControlRadius", "LG.PanelRadius", "LG.ArtworkInsetRadius" })
        {
            if (prefs.WidgetRadius is { } radius) Resources[key] = new CornerRadius(Math.Clamp(radius, 0, 20));
            else Resources.Remove(key);
        }

        var strength = Math.Clamp(prefs.WidgetShadowStrength ?? 100, 0, 100);
        SetShadow("LG.SmallShadow", "0 2 8", 0x55);
        SetShadow("LG.PopupShadow", "0 7 20", 0x55);
        SetShadow("LG.ModalShadow", "0 14 48", 0x90);

        void SetShadow(string key, string geometry, int opacity)
        {
            Resources[key] = strength == 0 ? default(BoxShadows)
                : BoxShadows.Parse($"{geometry} #{opacity * strength / 100:X2}000000");
        }
    }

    private void ApplyColourway()
    {
        var palette = ActiveColourway;
        var classic = palette == Colourways[0];
        var canvas = Color.Parse(palette.Canvas);
        var sidebar = Color.Parse(palette.Sidebar);
        var surface = Color.Parse(palette.Surface);
        var raised = Color.Parse(palette.Raised);
        var text = Color.Parse(palette.Text);
        SetColour("LG.Canvas", canvas);
        SetColour("LG.FieldHover", ArtworkAccent.Mix(canvas, Colors.Black, .35));
        SetColour("LG.Sidebar", sidebar);
        SetColour("LG.Surface", surface);
        SetColour("LG.SurfaceRaised", raised);
        SetColour("LG.Line", Color.Parse(palette.Line));
        SetColour("LG.Text", text);
        SetColour("LG.Muted", Color.Parse(palette.Muted));
        SetColour("LG.FolderIcon", Color.Parse(palette.Folder));
        SetColour("LG.ArtworkGlass", WithAlpha(sidebar, 0xB3));
        SetColour("LG.ArtworkGlassLine", WithAlpha(text, 0x38));
        if (classic)
        {
            foreach (var key in new[] { "LG.MainBackground", "LG.DetailShade", "LG.ArtworkFallback" }) Resources.Remove(key);
        }
        else
        {
            Resources["LG.MainBackground"] = new SolidColorBrush(canvas);
            Resources["LG.DetailShade"] = new LinearGradientBrush
            {
                StartPoint = RelativePoint.Parse("0%,100%"),
                EndPoint = RelativePoint.Parse("23%,0%"),
                GradientStops = { new(canvas, 0), new(WithAlpha(canvas, 0xC0), .32), new(WithAlpha(canvas, 0x4D), .7), new(WithAlpha(canvas, 3), 1) }
            };
            Resources["LG.ArtworkFallback"] = new SolidColorBrush(raised);
        }
        ApplyArtworkAccent();

        void SetColour(string key, Color colour)
        {
            if (classic) Resources.Remove(key);
            else Resources[key] = new SolidColorBrush(colour);
        }

        static Color WithAlpha(Color colour, byte alpha) => Color.FromArgb(alpha, colour.R, colour.G, colour.B);
    }

    private Control BuildLibrarySettings()
    {
        var prefs = _library.Preferences;
        var clearPlaytime = ModDeleteButton("Clear playtime data", () => _ = ClearPlaytime());
        clearPlaytime.HorizontalAlignment = HorizontalAlignment.Right;
        var dateFormat = BuildChoicePicker("Date format", new[] { false, true }, prefs.FriendlyLaunchDates,
            friendly => friendly ? "Friendly" : "Numeric",
            friendly => SavePreference(prefs.FriendlyLaunchDates, friendly, value => prefs.FriendlyLaunchDates = value, null));
        dateFormat.MinWidth = 112;
        dateFormat.HorizontalAlignment = HorizontalAlignment.Right;
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("General", new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        PreferenceToggle("Track playtime", "Record how long games run and show their playtime.",
                            TrackPlaytime, value => prefs.DisablePlaytimeTracking = !value, ApplyPlaytimeTracking),
                        PreferenceRow("Clear playtime data", clearPlaytime)
                    }
                }),
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
                        PreferenceToggle("Show Store Tags", "Show tags below genres on game details.",
                            prefs.ShowStoreTags, value => prefs.ShowStoreTags = value),
                        PreferenceRow("Date format", dateFormat)
                    }
                }),
                Section("Game Tiles", new StackPanel
                {
                    Spacing = 9,
                    Children =
                    {
                        PreferenceToggle("Show mod indicator", "Show a wrench over modded game artwork.",
                            !prefs.HideTileModIndicator, value => prefs.HideTileModIndicator = !value),
                        PreferenceToggle("Show playtime caption", "Show tracked time below game artwork.",
                            !prefs.HideTilePlaytime, value => prefs.HideTilePlaytime = !value,
                            overrideReason: () => TrackPlaytime ? null : "Turn on Track playtime to show captions."),
                        PreferenceChoice("Columns", TileColumnChoices, GameTileColumns, "",
                            value => SavePreference(prefs.TileColumns, value == DefaultTileColumns ? (int?)null : value,
                                count => prefs.TileColumns = count, null)),
                        PreferenceChoice("Minimum width", TileWidthChoices, GameTileMinimumWidth, " px",
                            value => SavePreference(prefs.TileMinimumWidth, value == DefaultTileMinimumWidth ? (int?)null : value,
                                width => prefs.TileMinimumWidth = width, null))
                    }
                }),
                Section("Home", PreferenceToggle("Display most-recent RSS articles on Home",
                    "Show a row of recent articles from enabled feeds.", prefs.ShowRecentRss, value => prefs.ShowRecentRss = value))
            }
        };
    }

    private int GameTileColumns => Math.Clamp(_library.Preferences.TileColumns ?? DefaultTileColumns, 1, MaximumTileColumns);
    private int GameTileMinimumWidth =>
        _library.Preferences.TileMinimumWidth is { } width && TileWidthChoices.Contains(width) ? width : DefaultTileMinimumWidth;

    private int FittingGameTileColumns(double width) => Math.Clamp(
        (int)((width + GameTileColumnSpacing) / (GameTileMinimumWidth + GameTileColumnSpacing)), 1, GameTileColumns);

    private void ApplyPlaytimeTracking()
    {
        if (TrackPlaytime) return;
        _sessions.StopTrackingPlaytime();
        if (_sort == 2) _sort = 0;
        if (_librarySort == 2) _librarySort = 0;
        if (_collectionsSort == 2) _collectionsSort = 0;
        RenderSidebar();
    }

    private async Task ClearPlaytime()
    {
        if (!await Confirm("Clear playtime data?", "Remove the recorded playtime for every game in your library.",
            "Clear playtime", destructive: true)) return;
        var previous = _library.Games.Where(game => game.PlaySeconds != 0).Select(game => (Game: game, game.PlaySeconds)).ToArray();
        var previouslyUnsaved = _saveFailed;
        foreach (var entry in previous) entry.Game.PlaySeconds = 0;
        if (!Save())
        {
            foreach (var entry in previous) entry.Game.PlaySeconds = entry.PlaySeconds;
            _saveFailed = previouslyUnsaved;
            return;
        }
        _sessions.ResetPlaytime();
        RenderSidebar();
        Status("Cleared playtime data.");
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

    private static ToggleSwitch CompactToggle(bool isChecked = false) => new()
    {
        Classes = { "lg-compact" },
        IsChecked = isChecked,
        OnContent = "",
        OffContent = "",
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Control PreferenceToggle(string title, string description, bool current, Action<bool> save,
        Action? apply = null, Func<string?>? overrideReason = null)
    {
        var toggle = CompactToggle(current);
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
        bool textSearch = false, bool wrapLabel = false, Action<T, Action<T>>? editCustom = null, bool textOnly = false,
        Func<T, string>? currentLabel = null, Func<T, SymbolIcon>? choiceIcon = null)
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
            Content = textOnly ? value : new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8, Children = { value, chevron } },
            ContextMenu = menu
        };
        if (textOnly)
        {
            picker.Classes.Remove("choice-picker");
            picker.Classes.Add("quiet");
            picker.Padding = new Thickness(7, 0);
        }
        AutomationProperties.SetName(picker, title);
        MenuItem? customItem = null;
        Action? commit = null;
        foreach (var choice in choices)
        {
            var item = menu.AddAction(format(choice), () => ApplyAfterClose(() => SelectChoice(choice)));
            item.ToggleType = MenuItemToggleType.Radio;
            item.Icon = choiceIcon?.Invoke(choice);
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
            value.Text = currentLabel?.Invoke(current) ?? format(current);
            AutomationProperties.SetHelpText(picker, $"Current value {format(current)}");
            var items = menu.Items.OfType<MenuItem>();
            var selected = items.FirstOrDefault(item => item != customItem && Equals(item.Tag, current)) ?? customItem;
            foreach (var item in items) item.IsChecked = item == selected;
        }
    }

    private static void WireNumberPreference(NumericUpDown number, Func<decimal, bool> save)
    {
        var previous = number.Value;
        number.Spinned += (_, _) => Commit();
        number.ValueChanged += (_, _) =>
        {
            if (!number.IsKeyboardFocusWithin) Commit();
        };
        number.LostFocus += (_, _) =>
        {
            if (!number.IsKeyboardFocusWithin) Commit();
        };
        number.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && !args.Handled) Commit();
        });

        void Commit()
        {
            if (number.Value == previous) return;
            if (number.Value is { } value && save(value)) previous = value;
            else number.Value = previous;
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
