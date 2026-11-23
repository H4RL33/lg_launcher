using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private async Task VerifyCollectionEditor(GameCollection legacy, string preview)
    {
        EditCollection(legacy);
        UpdateLayout();
        Require(AutomationProperties.GetHelpText(Field<Button>("Rule attribute")) == "Current value Genre" &&
            AutomationProperties.GetHelpText(Field<Button>("Rule operator")) == "Current value is" &&
            Field<TextBox>("Rule value").Text == "Racing",
            "A saved genre filter did not open as an exact genre rule.");
        DialogHost.GetVisualDescendants().OfType<ModalDialog>().Single().MaxWidth = 380;
        await WaitFor(() => Grid.GetRow(Field<TextBox>("Rule value")) == 1,
            "The qualifier did not move below the other controls in a narrow rules dialog.");
        await ChoosePreference(Field<Button>("Rule attribute"), "Steam tag");
        Require(Field<TextBox>("Rule value").PlaceholderText == "Search Steam tags", "The Steam tag rule did not update the search prompt.");
        RequireSteamTagFits();
        var reports = Path.GetDirectoryName(preview)!;
        Capture(Path.Combine(reports, "collection-rules-compact.png"));
        Field<TextBox>("Rule value").Text = "Changed";
        DialogHost.DismissTop();
        Require(legacy.GenreFilters?.Single() == "Racing",
            "Cancelling the rule editor changed a legacy filter.");

        var suggestions = new Game
        {
            Title = "Collection suggestions fixture", SteamId = 42, TagSteamId = 42,
            Genres = ["Horror", "Horror Action"], SteamTags = ["Open World", "open world", "Open Exploration", "Space Sim"]
        };
        _library.Games.Add(suggestions);
        try { EditCollection(null, dynamicCollection: true); }
        finally { _library.Games.Remove(suggestions); }
        UpdateLayout();
        const string name = "Collection rule smoke check";
        Field<TextBox>("Collection name").Text = name;
        ClickButton(ButtonWithContent("Create collection"));
        Require(DialogHost.HasModal && !_library.Collections.Any(collection => collection.Name == name),
            "A dynamic collection was saved with an empty rule.");

        await ChoosePreference(Field<Button>("Match rules"), "Match any rule");
        await ChoosePreference(Field<Button>("Rule attribute"), "Steam tag");
        await ChoosePreference(Field<Button>("Rule operator"), "contains");
        await VerifyRuleSuggestions(Field<TextBox>("Rule value"), reports);
        ClickButton(Field<Button>("Add rule"));
        ClickButton(Field<Button>("Add rule"));
        UpdateLayout();
        ClickButton(Field<Button>("Remove rule", 2));
        UpdateLayout();
        await ChoosePreference(Field<Button>("Rule operator", 1), "is not");
        var genre = Field<TextBox>("Rule value", 1);
        genre.Focus();
        genre.Text = "hrrr";
        var genrePopup = SuggestionPopup(genre);
        await WaitFor(() => genrePopup.IsOpen && SuggestionList(genrePopup).Items.OfType<string>().Contains("Horror"),
            "The genre field did not suggest an in-library fuzzy match.");
        KeyIn(genre, Key.Down);
        KeyIn(genre, Key.Enter);
        Require(genre.Text == "Horror" && !genrePopup.IsOpen && DialogHost.HasModal,
            "Picking a genre with the keyboard did not fill the rule and keep the editor open.");
        var windowSize = new Size(Width, Height);
        try
        {
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(85);
                RequireSteamTagFits();
                var firstValue = Field<TextBox>("Rule value").TranslatePoint(default, DialogHost);
                var secondValue = Field<TextBox>("Rule value", 1).TranslatePoint(default, DialogHost);
                Require(firstValue is { } first && secondValue is { } second && Math.Abs(first.X - second.X) < 1,
                    "The rule fields did not align across rows.");
                Capture(Path.Combine(reports, $"collection-rules-{size.Width}.png"));
            }
            foreach (var (field, label) in new[] { ("Match rules", "matching"), ("Rule attribute", "attribute"), ("Rule operator", "operator") })
            {
                var picker = Field<Button>(field);
                var menu = PreferenceMenu(picker);
                ClickButton(picker);
                await WaitFor(() => menu.IsOpen && menu.RevealProgress == 1, "A collection rule picker did not open.");
                var pickerLeft = picker.PointToScreen(default).X;
                var menuLeft = menu.PointToScreen(default).X;
                Require(Math.Abs(menuLeft - pickerLeft) <= 1,
                    $"The {label} picker did not align its menu to the button's left edge ({menuLeft} vs {pickerLeft}).");
                Require(menu.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "FrostedBackdrop").Background is ImageBrush,
                    "A collection rule picker did not use the shared frosted material.");
                CaptureControl(menu, Path.Combine(reports, $"collection-rules-picker-{label}.png"));
                menu.Items.OfType<MenuItem>().First().RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
                await WaitFor(() => !menu.IsOpen, "A collection rule picker remained open.");
                Require(DialogHost.HasModal, "Dismissing a rule picker closed the collection editor.");
            }
        }
        finally
        {
            Width = windowSize.Width;
            Height = windowSize.Height;
            UpdateLayout();
        }
        Capture(Path.Combine(reports, "collection-rules.png"));
        ClickButton(ButtonWithContent("Create collection"));
        var added = _library.Collections.Single(collection => collection.Name == name);
        try
        {
            await WaitFor(() => _collection == added, "Saving rules did not open the resulting collection.");
            var stored = _store.Load().Collections.Single(collection => collection.Id == added.Id);
            var rules = stored.EffectiveRules().ToArray();
            Require(stored.IsDynamic && stored.MatchAnyGenre && rules.Length == 2 &&
                rules[0].Attribute == CollectionAttribute.SteamTag && rules[0].Operator == CollectionOperator.Contains &&
                rules[0].Qualifier == "Open World" &&
                rules[1].Attribute == CollectionAttribute.Genre && rules[1].Operator == CollectionOperator.IsNot &&
                rules[1].Qualifier == "Horror",
                "The rule editor did not persist both independently configured rules and Match any.");
        }
        finally
        {
            _library.Collections.Remove(added);
            Require(Save(), "The smoke test's temporary collection could not be removed.");
            Navigate(Page.Collections, legacy);
        }

        T Field<T>(string label, int index = 0) where T : Control => DialogHost.GetVisualDescendants()
            .OfType<T>().Where(control => AutomationProperties.GetName(control) == label).ElementAt(index);
        Button ButtonWithContent(string label) => DialogHost.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, label));
        static void ClickButton(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void RequireSteamTagFits()
        {
            UpdateLayout();
            var label = Field<Button>("Rule attribute").GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Steam tag");
            var text = new FormattedText(label.Text!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight), label.FontSize, label.Foreground!);
            Require(label.Bounds.Width >= text.Width - 1, "The rule field was too narrow for the full Steam tag label.");
        }
    }

    private async Task VerifyRuleSuggestions(TextBox field, string previews)
    {
        var popup = SuggestionPopup(field);
        var results = SuggestionList(popup);
        var dropdown = SuggestionDropdown(popup);
        var previousMotion = _motion;
        var windowSize = (Width, Height);
        try
        {
            _motion = true;
            field.Focus();
            field.Text = "open";
            await WaitFor(() => popup.IsOpen && results.Items.OfType<string>().SequenceEqual(["Open Exploration", "Open World"]) &&
                dropdown.Bounds.Height > 65, "Steam tag suggestions did not use the deduplicated library values.");
            var expanded = dropdown.Bounds.Height;
            field.Text = "opne wrld";
            await WaitFor(() => results.ItemCount == 1 && dropdown.Bounds.Height < expanded && dropdown.Bounds.Height > 43,
                "The Steam tag results did not resize between frames as the query changed.");
            await WaitFor(() => dropdown.Bounds.Height <= 43 && Equals(results.Items[0], "Open World"),
                "The Steam tag list did not settle on the fuzzy match.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(65);
                UpdateLayout();
                Capture(Path.Combine(previews, $"collection-rule-suggestions-{size.Width}.png"));
                await WaitFor(() => Math.Abs(field.PointToScreen(default).X - dropdown.PointToScreen(default).X) < 2 &&
                    Math.Abs(field.Bounds.Width - dropdown.Bounds.Width) < 1, "Suggestions did not align with the rule field.");
            }
            KeyIn(field, Key.Escape);
            Require(!popup.IsOpen && DialogHost.HasModal, "Escape did not dismiss just the suggestions.");
            KeyIn(field, Key.Down);
            KeyIn(field, Key.Enter);
            Require(field.Text == "Open World" && !popup.IsOpen && DialogHost.HasModal,
                "Selecting a Steam tag did not use its library casing and keep the editor open.");
        }
        finally
        {
            _motion = previousMotion;
            (Width, Height) = windowSize;
        }
    }

    private static Popup SuggestionPopup(TextBox field) =>
        ((Grid)field.Parent!).Children.OfType<Popup>().Single(popup => popup.Name == "RuleSuggestions");

    private static Grid SuggestionDropdown(Popup popup) => (Grid)((LayoutTransformControl)popup.Child!).Child!;

    private static ListBox SuggestionList(Popup popup) => ((Panel)((Border)SuggestionDropdown(popup).Children[0]).Child!).Children.OfType<ListBox>().Single();

    private static void KeyIn(TextBox field, Key key) => field.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key });
}
