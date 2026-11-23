using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private async Task VerifyCollectionIcons(GameCollection legacy, string preview)
    {
        Require(LucideIconCatalog.Icons.Count == 1544 && LucideIconCatalog.Icons.All(icon => icon.Geometry.Bounds.Size != default),
            "The complete pinned Lucide pack did not parse.");
        var rules = new GameCollection
        {
            Rules =
            [
                new CollectionRule { Qualifier = "Racing", Operator = CollectionOperator.IsNot },
                new CollectionRule { Qualifier = "Unknown Steam tag", Attribute = CollectionAttribute.SteamTag },
                new CollectionRule { Qualifier = "Space Exploration", Attribute = CollectionAttribute.SteamTag, Operator = CollectionOperator.Contains }
            ]
        };
        Require(ReferenceEquals(CollectionSidebarIcon(rules), LucideIconCatalog.Find("rocket")?.Geometry),
            "The dynamic icon did not follow the first recognized positive rule.");
        rules.Rules.RemoveAt(2);
        rules.IconName = "unavailable-icon";
        Require(ReferenceEquals(CollectionSidebarIcon(rules), this.FindResource("LG.Icon.Brain")) &&
            ReferenceEquals(CollectionSidebarIcon(legacy), LucideIconCatalog.Find("flag-triangle-right")?.Geometry),
            "The sidebar did not fall back for unknown overrides or recognize a legacy racing rule.");

        var reports = Path.GetDirectoryName(preview)!;
        var originalSize = (Width, Height);
        var originalScale = _library.Preferences.InterfaceScalePercent;
        try
        {
            UpdateLayout();
            var row = CollectionRow()!;
            row.RaiseEvent(new ContextRequestedEventArgs());
            var menu = row.ContextMenu;
            Require(menu is FrostedContextMenu { IsOpen: true }, "The sidebar row did not open its context menu.");
            var changeIcon = menu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Change icon"));
            changeIcon.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            var search = DialogHost.GetVisualDescendants().OfType<TextBox>()
                .Single(field => AutomationProperties.GetName(field) == "Search icons");
            await WaitFor(() => search.IsFocused && FindIcon("a arrow down") != null,
                "The icon picker did not focus its search field and render the first grid row.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                (Width, Height) = size;
                await Task.Delay(85);
                UpdateLayout();
                var iconButtons = DialogHost.GetVisualDescendants().OfType<Button>().Count(button => button.Content is SymbolIcon);
                Require(iconButtons is > 0 and < 180, "The icon picker did not virtualize its grid.");
                CheckHorizontalFit();
                Capture(Path.Combine(reports, $"collection-icon-picker-{size.Width}.png"));
            }

            _library.Preferences.InterfaceScalePercent = 200;
            ApplyInterfaceScale();
            // A display's work area can cap the launcher below its scaled minimum.
            (MinWidth, MinHeight) = (730, 510);
            (Width, Height) = (730, 510);
            await Task.Delay(85);
            UpdateLayout();
            CheckHorizontalFit();
            Capture(Path.Combine(reports, "collection-icon-picker-200-percent.png"));
            _library.Preferences.InterfaceScalePercent = originalScale;
            ApplyInterfaceScale();
            (Width, Height) = originalSize;
            await Task.Delay(85);
            UpdateLayout();

            search.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Down });
            await WaitFor(() => FindIcon("a arrow down")?.IsFocused == true, "Down did not move focus from search into the grid.");
            FindIcon("a arrow down")!.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.End });
            await WaitFor(() => FindIcon("zoom out")?.IsFocused == true, "End did not move to the last virtualized icon.");
            search.Focus();
            search.Text = "no icon matches zzzz";
            await WaitFor(() => DialogHost.GetVisualDescendants().OfType<TextBlock>()
                .Any(label => label.IsVisible && label.Text == "No matching icons."), "The icon picker omitted the empty search state.");
            search.Text = "Rocket";
            await WaitFor(() => FindIcon("rocket") != null, "The icon picker did not find Rocket.");
            ClickButton(FindIcon("rocket")!);
            Capture(Path.Combine(reports, "collection-icon-picker-selected.png"));
            Require(legacy.IconName == null, "An unconfirmed icon choice changed the collection.");
            ClickButton(FooterButton("Save changes"));
            await WaitFor(() => !DialogHost.HasModal && CollectionRow()?.Content is SidebarCollection item && ReferenceEquals(item.Icon,
                LucideIconCatalog.Find("rocket")?.Geometry), "Saving the icon did not update the visible sidebar.");
            Require(_store.Load().Collections.Single(item => item.Id == legacy.Id).IconName == "rocket",
                "The selected icon was not persisted.");

            var reopened = ShowCollectionIconPicker(legacy);
            UpdateLayout();
            ClickButton(FooterButton("Reset to default"));
            ClickButton(FooterButton("Save changes"));
            await reopened;
            Require(_store.Load().Collections.Single(item => item.Id == legacy.Id).IconName == null,
                "Restoring the automatic icon did not remove the stored override.");
            await WaitFor(() => CollectionRow()?.Content is SidebarCollection item &&
                ReferenceEquals(item.Icon, LucideIconCatalog.Find("flag-triangle-right")?.Geometry),
                "Resetting the override did not restore the rule-derived sidebar symbol.");
        }
        finally
        {
            DialogHost.DismissTop();
            _library.Preferences.InterfaceScalePercent = originalScale;
            ApplyInterfaceScale();
            (Width, Height) = originalSize;
            UpdateLayout();
        }

        ListBoxItem? CollectionRow() => LibraryCollections.GetRealizedContainers().OfType<ListBoxItem>()
            .SingleOrDefault(row => row.Content is SidebarCollection item && item.Collection == legacy);
        Button? FindIcon(string name) => DialogHost.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Content is SymbolIcon && AutomationProperties.GetName(button) == name);
        Button FooterButton(string label) => DialogHost.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, label));
        static void ClickButton(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        void CheckHorizontalFit()
        {
            var scroll = DialogHost.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "CollectionIconScroll");
            var icons = scroll.GetVisualDescendants().OfType<Button>().Where(button => button.Tag is LucideIcon).ToArray();
            Require(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.Viewport.Height >= 39 &&
                scroll.Extent.Height > scroll.Viewport.Height &&
                scroll.Extent.Width <= scroll.Viewport.Width + 1 && icons.Length > 1 && icons.All(button =>
                    button.TranslatePoint(default, scroll) is { } point && point.X >= -.5 &&
                    point.X + button.Bounds.Width <= scroll.Viewport.Width - 11),
                "The icon picker clipped an icon horizontally or lost vertical overflow.");
        }
    }
}
