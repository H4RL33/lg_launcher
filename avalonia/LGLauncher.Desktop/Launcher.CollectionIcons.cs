using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly Dictionary<string, string> CollectionRuleIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["action"] = "swords",
        ["action rpg"] = "swords",
        ["adventure"] = "compass",
        ["building"] = "hammer",
        ["card game"] = "club",
        ["casual"] = "smile",
        ["city builder"] = "building-2",
        ["co op"] = "users",
        ["crafting"] = "hammer",
        ["early access"] = "construction",
        ["exploration"] = "compass",
        ["fantasy"] = "wand-sparkles",
        ["farming"] = "wheat",
        ["fighting"] = "swords",
        ["first person shooter"] = "crosshair",
        ["flight"] = "plane",
        ["fps"] = "crosshair",
        ["free to play"] = "gift",
        ["horror"] = "ghost",
        ["indie"] = "gamepad-2",
        ["massively multiplayer"] = "users",
        ["metroidvania"] = "map",
        ["mmo"] = "users",
        ["multiplayer"] = "users",
        ["music"] = "music",
        ["open world"] = "globe",
        ["platformer"] = "footprints",
        ["puzzle"] = "puzzle",
        ["racing"] = "flag-triangle-right",
        ["rhythm"] = "music",
        ["roguelike"] = "dices",
        ["roguelite"] = "dices",
        ["role playing"] = "scroll",
        ["role playing game"] = "scroll",
        ["rpg"] = "scroll",
        ["sandbox"] = "boxes",
        ["sci fi"] = "orbit",
        ["shooter"] = "crosshair",
        ["simulation"] = "sliders-horizontal",
        ["singleplayer"] = "user-round",
        ["souls like"] = "swords",
        ["soulslike"] = "swords",
        ["space"] = "rocket",
        ["space exploration"] = "rocket",
        ["sports"] = "trophy",
        ["stealth"] = "eye-off",
        ["strategy"] = "brain",
        ["survival"] = "flame",
        ["tower defense"] = "castle",
        ["turn based"] = "repeat-2",
        ["turn based strategy"] = "brain",
        ["visual novel"] = "book-open",
        ["vr"] = "glasses"
    };

    private Geometry? CollectionSidebarIcon(GameCollection collection)
    {
        if (LucideIconCatalog.Find(collection.IconName) is { } chosen) return chosen.Geometry;
        if (!collection.IsDynamic) return this.FindResource("LG.Icon.Library") as Geometry;
        foreach (var rule in collection.EffectiveRules().OfType<CollectionRule>())
        {
            if (rule.Operator is not (CollectionOperator.Is or CollectionOperator.Contains) ||
                rule.Attribute is not (CollectionAttribute.Genre or CollectionAttribute.SteamTag) ||
                String.IsNullOrWhiteSpace(rule.Qualifier)) continue;

            var terms = rule.Qualifier.Split([' ', '-', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var name = CollectionRuleIcons.GetValueOrDefault(String.Join(' ', terms)) ?? String.Join('-', terms);
            if (LucideIconCatalog.Find(name) is { } automatic) return automatic.Geometry;
        }
        return this.FindResource("LG.Icon.Brain") as Geometry;
    }

    private void PrepareCollectionRow(object? sender, ContainerPreparedEventArgs args)
    {
        if (args.Container is ListBoxItem { Content: SidebarCollection item } row)
            row.ContextMenu = CollectionMenu(item.Collection);
    }

    private ContextMenu CollectionMenu(GameCollection collection)
    {
        var menu = LauncherMenus.Create();
        menu.AddAction(collection.IsDynamic ? "Edit rules" : "Rename", () => EditCollection(collection),
            collection.IsDynamic ? "LG.Icon.Brain" : "LG.Icon.Edit");
        menu.AddAction("Change icon", () => _ = ShowCollectionIconPicker(collection), "LG.Icon.Artwork");
        return menu;
    }

    private async Task ShowCollectionIconPicker(GameCollection collection)
    {
        const double cellSpacing = 7;
        const double cellWidth = 39;
        const double scrollbarGutter = 12;
        var original = collection.IconName;
        var selected = original;
        var icons = LucideIconCatalog.Icons.ToArray();
        var matches = icons;
        var columns = 1;
        var search = ModalTextField(null, "Search icons");
        search.PlaceholderText = "Search icons";
        var find = Symbol("LG.Icon.Search", 15);
        find.Margin = new Thickness(10, 0, 5, 0);
        search.InnerLeftContent = find;

        var items = new ItemsControl
        {
            Name = "CollectionIconGrid",
            Margin = new Thickness(0, 0, scrollbarGutter, 0),
            ItemsPanel = new FuncTemplate<Panel>(() => new VirtualizingStackPanel())
        };
        var scroll = new ScrollViewer
        {
            Name = "CollectionIconScroll",
            Content = items,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var empty = Label("No matching icons.", muted: true);
        empty.IsVisible = false;
        var error = Label("The collection icon could not be saved. Try again.", muted: true);
        error.Margin = new Thickness(0, 8, 0, 0);
        error.IsVisible = false;
        Grid.SetRow(error, 1);
        var content = new Grid
        {
            Margin = new Thickness(22, 0, 22, 6),
            RowDefinitions = RowDefinitions.Parse("*,Auto"),
            Children = { scroll, empty, error }
        };
        var reset = new Button
        {
            Content = "Reset to default", Classes = { "lg", "quiet" },
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = selected != null
        };
        var dialog = new ModalDialog("Change collection icon", 572, search, reset, content)
        {
            MaxWidth = 572,
            Width = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var save = DialogActions(dialog, "Save changes", () =>
        {
            var previous = collection.IconName;
            var previouslyUnsaved = _saveFailed;
            collection.IconName = selected;
            if (!Save())
            {
                collection.IconName = previous;
                _saveFailed = previouslyUnsaved;
                error.IsVisible = true;
                return;
            }
            Status("Collection icon updated.");
            dialog.Close(true);
        }, defaultAction: false);
        save.IsEnabled = false;
        dialog.InitialFocus = search;
        reset.Click += (_, _) => Select(null);
        items.ItemTemplate = new FuncDataTemplate<LucideIcon[]>((row, _) => row == null ? null : IconRow(row));

        scroll.SizeChanged += (_, _) => FitColumns();
        scroll.ScrollChanged += (_, _) => FitColumns();
        search.TextChanged += (_, _) =>
        {
            var terms = (search.Text ?? "").Split([' ', '-', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var fullName = String.Join(' ', terms);
            matches = terms.Length == 0 ? icons : icons
                .Where(icon => terms.All(term => icon.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    icon.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(icon => icon.Label.Equals(fullName, StringComparison.OrdinalIgnoreCase) ? 0 :
                    terms.All(term => icon.Label.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 1 : 2)
                .ToArray();
            ShowResults();
            scroll.Offset = default;
        };
        search.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Down || args.KeyModifiers != KeyModifiers.None || matches.Length == 0) return;
            args.Handled = true;
            FocusIcon(0);
        };
        items.KeyDown += (_, args) =>
        {
            if (args.KeyModifiers != KeyModifiers.None || args.Source is not Button { Tag: LucideIcon icon }) return;
            var index = Array.IndexOf(matches, icon);
            if (index < 0) return;
            var target = args.Key switch
            {
                Key.Up => index - columns,
                Key.Down => index + columns,
                Key.Left => index - 1,
                Key.Right => index + 1,
                Key.Home => 0,
                Key.End => matches.Length - 1,
                _ => index
            };
            if (target == index) return;
            args.Handled = true;
            if (target >= 0 && target < matches.Length) FocusIcon(target);
        };
        void SizePicker(object? sender, SizeChangedEventArgs args) => FitDialog();
        void FitDialog()
        {
            var hostSize = DialogHost.Bounds.Size;
            if (hostSize.Width <= 0 || hostSize.Height <= 0) hostSize = MainShell.Bounds.Size;
            var compact = hostSize.Width < 420 || hostSize.Height < 330;
            dialog.Margin = new Thickness(compact ? 10 : 24);
            reset.Margin = compact ? new Thickness(14, 4, 4, 12) : new Thickness(22, 6, 8, 18);
            dialog.Footer.Margin = compact ? new Thickness(4, 4, 14, 12) : new Thickness(22, 6, 22, 18);
            dialog.Height = Math.Clamp(hostSize.Height - dialog.Margin.Top - dialog.Margin.Bottom, 0, 448);
        }
        DialogHost.SizeChanged += SizePicker;
        try
        {
            FitDialog();
            ShowResults();
            if (await ShowModal(dialog)) RenderSidebar();
        }
        finally
        {
            DialogHost.SizeChanged -= SizePicker;
        }

        void ShowResults()
        {
            items.ItemsSource = matches.Chunk(columns).ToArray();
            empty.IsVisible = matches.Length == 0;
        }

        void FitColumns()
        {
            var viewportWidth = Math.Min(scroll.Bounds.Width, scroll.Viewport.Width);
            if (viewportWidth <= 0) return;
            var usableWidth = Math.Max(0, viewportWidth - scrollbarGutter);
            var next = Math.Max(1, (int)((usableWidth + cellSpacing) / (cellWidth + cellSpacing)));
            if (next == columns) return;
            columns = next;
            ShowResults();
        }

        void FocusIcon(int index)
        {
            var target = matches[index];
            items.ScrollIntoView(index / columns);
            Dispatcher.UIThread.Post(() => items.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => ReferenceEquals(button.Tag, target))?.Focus(NavigationMethod.Directional),
                DispatcherPriority.Loaded);
        }

        StackPanel IconRow(LucideIcon[] row)
        {
            var grid = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = cellSpacing, Margin = new Thickness(0, 0, 0, cellSpacing),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            foreach (var icon in row)
            {
                var button = ActionButton(icon.Label, () => Select(icon.Name));
                button.Content = new SymbolIcon { Data = icon.Geometry, Width = 19, Height = 19 };
                button.Tag = icon;
                button.Width = cellWidth;
                button.Height = cellWidth;
                button.Padding = new Thickness(0);
                AutomationProperties.SetName(button, icon.Label);
                ToolTip.SetTip(button, icon.Label);
                ApplySelection(button);
                grid.Children.Add(button);
            }
            return grid;
        }

        void Select(string? name)
        {
            selected = name;
            error.IsVisible = false;
            reset.IsEnabled = selected != null;
            save.IsEnabled = !String.Equals(original, selected, StringComparison.OrdinalIgnoreCase);
            foreach (var button in items.GetVisualDescendants().OfType<Button>()) ApplySelection(button);
        }

        void ApplySelection(Button button)
        {
            var isSelected = button.Tag is LucideIcon icon && String.Equals(icon.Name, selected, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("selected", isSelected);
            AutomationProperties.SetHelpText(button, isSelected ? "Selected icon" : null);
        }
    }
}
