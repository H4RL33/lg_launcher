using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly (CollectionAttribute Value, string Label)[] RuleAttributes =
        [(CollectionAttribute.Genre, "Genre"), (CollectionAttribute.SteamTag, "Steam tag"),
         (CollectionAttribute.Developer, "Developer"), (CollectionAttribute.Publisher, "Publisher"), (CollectionAttribute.Modded, "Modded")];
    private static readonly (CollectionOperator Value, string Label)[] RuleOperators =
        [(CollectionOperator.Is, "is"), (CollectionOperator.IsNot, "is not"), (CollectionOperator.Contains, "contains")];

    private static string DescribeCollectionRules(GameCollection collection)
    {
        var clauses = collection.EffectiveRules().OfType<CollectionRule>().Select(rule =>
            $"{RuleAttributes.First(item => item.Value == rule.Attribute).Label} " +
            $"{RuleOperators.First(item => item.Value == rule.Operator).Label} “{rule.Qualifier}”");
        var join = collection.MatchAnyGenre ? " or " : " and ";
        return "If " + String.Join(join, clauses);
    }

    private async void EditCollection(GameCollection? collection, bool dynamicCollection = false)
    {
        var creating = collection == null;
        var entry = collection ?? new GameCollection { CreatedUtc = DateTime.UtcNow };
        var dynamic = dynamicCollection || entry.IsDynamic;
        var title = dynamic ? (creating ? "New dynamic collection" : "Edit dynamic collection")
            : (creating ? "New collection" : "Rename collection");
        var dialog = new ModalDialog(title, dynamic ? 572 : 465);
        if (dynamic)
        {
            dialog.MaxWidth = dialog.Width;
            dialog.Width = double.NaN;
            dialog.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        var name = ModalTextField(entry.Name, "Collection name");
        name.MaxLength = 65;
        Field(dialog.Body, "Collection name", name);

        var rows = new List<RuleFields>();
        var suggestions = new Dictionary<CollectionAttribute, (string Label, string[] Values)>();
        if (dynamic)
        {
            suggestions[CollectionAttribute.Genre] = ("genres", SuggestionValues(_library.Games.SelectMany(game => game.GenreNames)));
            suggestions[CollectionAttribute.SteamTag] = ("Steam tags", SuggestionValues(_library.Games.SelectMany(game => game.SteamTagsFor())));
            suggestions[CollectionAttribute.Developer] = ("developers", SuggestionValues(_library.Games.SelectMany(game => game.DeveloperNames)));
            suggestions[CollectionAttribute.Publisher] = ("publishers", SuggestionValues(_library.Games.SelectMany(game => game.PublisherNames)));
            suggestions[CollectionAttribute.Modded] = ("modded values", ["Yes", "No"]);
        }
        var rulesHost = new StackPanel { Spacing = 8 };
        Grid.SetIsSharedSizeScope(rulesHost, true);
        var matchAny = entry.MatchAnyGenre;
        var matching = BuildChoicePicker("Match rules", new[] { false, true }, matchAny, any => any ? "Match any rule" : "Match all rules", any =>
        {
            matchAny = any;
            return true;
        });
        var error = Label("", muted: true);
        error.IsVisible = false;
        name.TextChanged += (_, _) => error.IsVisible = false;
        var addRule = IconButton("Add rule", "LG.Icon.Plus", () =>
        {
            var row = AddRule(null);
            row.Value.Focus();
        });
        AutomationProperties.SetName(addRule, "Add rule");
        addRule.HorizontalAlignment = HorizontalAlignment.Left;

        if (dynamic)
        {
            Field(dialog.Body, "Match rules", matching);
            dialog.Body.Children.Add(rulesHost);
            foreach (var rule in entry.EffectiveRules().OfType<CollectionRule>()) AddRule(rule);
            if (rows.Count == 0) AddRule(null);
            dialog.Body.Children.Add(addRule);
        }

        dialog.Body.Children.Add(error);
        var submit = creating ? "Create collection" : dynamic ? "Save changes" : "Save name";
        DialogActions(dialog, submit, () =>
        {
            var value = name.Text?.Trim() ?? "";
            if (value.Length == 0) { ShowError("Enter a collection name.", name); return; }
            if (_library.Collections.Any(item => item != entry && String.Equals(item.Name, value, StringComparison.CurrentCultureIgnoreCase)))
            {
                ShowError("A collection with this name already exists.", name);
                return;
            }

            var previous = (entry.Name, entry.Rules, entry.GenreFilters, entry.MatchAnyGenre);
            var previouslyUnsaved = _saveFailed;
            if (dynamic)
            {
                if (rows.Count == 0) { ShowError("Add a rule to create a dynamic collection.", addRule); return; }
                var blank = rows.Find(row => String.IsNullOrWhiteSpace(row.Value.Text));
                if (blank != null) { ShowError("Enter a value for each rule.", blank.Value); return; }
                var invalidModded = rows.Find(row => row.Attribute == CollectionAttribute.Modded &&
                    !suggestions[CollectionAttribute.Modded].Values.Contains(row.Value.Text?.Trim(), StringComparer.OrdinalIgnoreCase));
                if (invalidModded != null) { ShowError("Enter Yes or No for Modded.", invalidModded.Value); return; }
                entry.Rules = rows.Select(row => new CollectionRule
                {
                    Attribute = row.Attribute,
                    Operator = row.Operator,
                    Qualifier = row.Value.Text!.Trim()
                }).ToList();
                entry.GenreFilters = null;
                entry.MatchAnyGenre = matchAny;
            }

            entry.Name = value;
            if (creating) _library.Collections.Add(entry);
            if (!Save())
            {
                if (creating) _library.Collections.Remove(entry);
                (entry.Name, entry.Rules, entry.GenreFilters, entry.MatchAnyGenre) = previous;
                _saveFailed = previouslyUnsaved;
                ShowError("The collection could not be saved. Try again.");
                return;
            }
            Status(creating ? "Collection created." : dynamic ? "Collection rules updated." : "Collection renamed.");
            dialog.Close(true);
        });
        dialog.InitialFocus = name;
        name.SelectAll();
        if (await ShowModal(dialog)) Navigate(Page.Collections, entry);

        RuleFields AddRule(CollectionRule? rule)
        {
            var value = ModalTextField(rule?.Qualifier, "Rule value");
            value.MaxLength = 120;
            var savedAttribute = rule?.Attribute ?? CollectionAttribute.Genre;
            var savedOperator = rule?.Operator ?? CollectionOperator.Is;
            var row = new RuleFields(value)
            {
                Attribute = RuleAttributes.Any(item => item.Value == savedAttribute) ? savedAttribute : CollectionAttribute.Genre,
                Operator = RuleOperators.Any(item => item.Value == savedOperator) ? savedOperator : CollectionOperator.Is
            };
            var attribute = BuildChoicePicker("Rule attribute", RuleAttributes.Select(item => item.Value), row.Attribute,
                choice => RuleAttributes.First(item => item.Value == choice).Label, choice =>
                {
                    row.Attribute = choice;
                    UpdateSearchLabel();
                    return true;
                }, wrapLabel: true);
            var operation = BuildChoicePicker("Rule operator", RuleOperators.Select(item => item.Value), row.Operator,
                choice => RuleOperators.First(item => item.Value == choice).Label, choice =>
                {
                    row.Operator = choice;
                    return true;
                }, wrapLabel: true);
            value.TextChanged += (_, _) => error.IsVisible = false;
            UpdateSearchLabel();

            void UpdateSearchLabel() => value.PlaceholderText = row.Attribute == CollectionAttribute.Modded
                ? "Yes or No" : $"Search {suggestions[row.Attribute].Label}";

            var condition = Label("If", muted: true);
            condition.VerticalAlignment = VerticalAlignment.Center;
            var line = new Grid
            {
                ColumnDefinitions = RuleColumns(compact: false),
                RowDefinitions = RowDefinitions.Parse("Auto,Auto"),
                ColumnSpacing = 8
            };
            var remove = ActionButton("Remove rule", () =>
            {
                rows.Remove(row);
                rulesHost.Children.Remove(line);
                error.IsVisible = false;
                ((Control?)rows.FirstOrDefault()?.Value ?? matching).Focus();
            }, "quiet");
            remove.Content = Symbol("LG.Icon.Delete", 16);
            remove.Padding = new Thickness(8, 0);
            AutomationProperties.SetName(remove, "Remove rule");
            ToolTip.SetTip(remove, "Remove rule");
            var controls = new Control[] { condition, attribute, operation, value, remove };
            for (var column = 0; column < controls.Length; column++)
            {
                Grid.SetColumn(controls[column], column);
                line.Children.Add(controls[column]);
            }
            line.Children.Add(RuleValueSuggestions(value, () => suggestions[row.Attribute]));
            line.SizeChanged += (_, args) =>
            {
                if (!args.WidthChanged) return;
                var compact = args.NewSize.Width < 405;
                if ((Grid.GetRow(value) == 1) == compact) return;
                line.ColumnDefinitions = RuleColumns(compact);
                line.RowSpacing = compact ? 6 : 0;
                Grid.SetRow(value, compact ? 1 : 0);
                Grid.SetColumn(value, compact ? 0 : 3);
                Grid.SetColumnSpan(value, compact ? 4 : 1);
                Grid.SetColumn(remove, compact ? 3 : 4);
            };
            rows.Add(row);
            rulesHost.Children.Add(line);
            return row;
        }

        void ShowError(string message, Control? focus = null)
        {
            error.Text = message;
            error.IsVisible = true;
            focus?.Focus();
        }
    }

    private static string[] SuggestionValues(IEnumerable<string> values) => values.Where(value => !String.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private Popup RuleValueSuggestions(TextBox field, Func<(string Label, string[] Values)> suggestions)
    {
        var results = new ListBox
        {
            Classes = { "lg", "lg-search-results" }, MaxHeight = 192,
            ItemTemplate = new FuncDataTemplate<string>((value, _) =>
            {
                var label = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(label, value);
                return label;
            })
        };
        KeyboardNavigation.SetTabNavigation(results, KeyboardNavigationMode.None);
        var empty = Label("", muted: true);
        empty.Margin = new Thickness(8, 0);
        empty.VerticalAlignment = VerticalAlignment.Center;
        var frame = new Border
        {
            Classes = { "lg-search-frame" },
            Child = new Panel { ClipToBounds = true, Children = { results, empty } }
        };
        var dropdown = new Grid { Classes = { "lg-search-dropdown" }, Height = 0, Children = { frame } };
        var popupContent = new LayoutTransformControl
        {
            Child = dropdown, LayoutTransform = new ScaleTransform(EffectiveInterfaceScale, EffectiveInterfaceScale)
        };
        var popup = new Popup
        {
            Name = "RuleSuggestions", PlacementTarget = this, Child = popupContent,
            Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 6,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipY |
                PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY,
            ShouldUseOverlayLayer = true, IsLightDismissEnabled = true, OverlayDismissEventPassThrough = true
        };
        string? selectedValue = null;

        field.GotFocus += (_, _) =>
        {
            if (selectedValue == null) ShowSuggestions();
        };
        field.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(field).Properties.IsLeftButtonPressed) ShowSuggestions();
        };
        field.TextChanged += (_, _) =>
        {
            if (!field.IsFocused || field.Text == selectedValue) return;
            selectedValue = null;
            ShowSuggestions();
        };
        field.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!field.IsFocused && !results.IsKeyboardFocusWithin) popup.IsOpen = false;
        });
        field.LayoutUpdated += (_, _) =>
        {
            if (popup.IsOpen) PositionSuggestions();
        };
        field.DetachedFromVisualTree += (_, _) => popup.IsOpen = false;
        popup.Closed += (_, _) => dropdown.Height = 0;
        field.AddHandler(KeyDownEvent, HandleKeys, RoutingStrategies.Tunnel);
        results.AddHandler(KeyDownEvent, HandleKeys, RoutingStrategies.Tunnel);
        results.Tapped += (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Content is string value) Choose(value);
        };
        return popup;

        void ShowSuggestions()
        {
            var (label, source) = suggestions();
            var query = field.Text?.Trim() ?? "";
            var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var matches = source.Select(candidate => (Value: candidate, Score: CollectionSearchScore(candidate, terms)))
                .Where(match => match.Score.HasValue)
                .OrderBy(match => match.Value.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
                    : match.Value.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 1 : 2)
                .ThenBy(match => match.Score).Select(match => match.Value).ToArray();
            results.ItemsSource = matches;
            results.SelectedIndex = -1;
            results.IsVisible = matches.Length > 0;
            empty.IsVisible = matches.Length == 0;
            empty.Text = source.Length == 0 ? $"No {label} in your library" : $"No {label} match";
            AutomationProperties.SetName(results, $"{label} suggestions");
            dropdown.Classes.Set("lg-motion", _motion);
            PositionSuggestions();
            popup.IsOpen = true;
            ResizeSearchResults(dropdown, frame, results, matches.Length);
        }

        void PositionSuggestions()
        {
            // The dialog can move on resize or scroll while the field's bounds inside its row stay the same.
            if (field.TranslatePoint(default, this) is not { } origin ||
                field.TranslatePoint(new Point(field.Bounds.Width, field.Bounds.Height), this) is not { } end) return;
            dropdown.Width = field.Bounds.Width;
            popup.PlacementRect = new Rect(origin, end);
        }

        void Choose(string value)
        {
            selectedValue = value;
            field.Text = value;
            field.CaretIndex = value.Length;
            field.Focus();
            popup.IsOpen = false;
        }

        void HandleKeys(object? sender, KeyEventArgs args)
        {
            if (args.KeyModifiers != KeyModifiers.None) return;
            if (args.Key is Key.Up or Key.Down)
            {
                if (!popup.IsOpen) ShowSuggestions();
                if (results.ItemCount > 0)
                {
                    var delta = args.Key == Key.Down ? 1 : -1;
                    results.SelectedIndex = results.SelectedIndex < 0 && delta < 0 ? results.ItemCount - 1
                        : Math.Clamp(results.SelectedIndex + delta, 0, results.ItemCount - 1);
                    results.ScrollIntoView(results.SelectedIndex);
                }
                args.Handled = true;
            }
            else if (popup.IsOpen && args.Key is Key.Enter or Key.Escape)
            {
                if (args.Key == Key.Enter && results.ItemCount > 0)
                    Choose((string)(results.SelectedItem ?? results.Items[0])!);
                else popup.IsOpen = false;
                args.Handled = true;
            }
        }
    }

    private static ColumnDefinitions RuleColumns(bool compact)
    {
        if (compact) return ColumnDefinitions.Parse("Auto,*,*,Auto");
        var columns = ColumnDefinitions.Parse("Auto,Auto,Auto,*,Auto");
        columns[1].SharedSizeGroup = "RuleAttribute";
        columns[2].SharedSizeGroup = "RuleOperator";
        return columns;
    }

    private sealed class RuleFields(TextBox value)
    {
        internal CollectionAttribute Attribute { get; set; }
        internal CollectionOperator Operator { get; set; }
        internal TextBox Value { get; } = value;
    }
}
