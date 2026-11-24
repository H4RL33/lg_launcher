using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private void RenderHome()
    {
        PageTitle.Text = "Home";
        RenderHomeRss();
        var games = SortedGames(_library.Games, _librarySort).ToArray();
        var recent = _library.Games.Where(game => game.LastPlayedUtc.HasValue).OrderByDescending(game => game.LastPlayedUtc)
            .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase).Take(3).ToArray();
        var collections = _library.Collections.OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        if (recent.Length == 0)
        {
            PageBody.Children.Add(Heading("Recently played"));
            PageBody.Children.Add(Label("Launch a game to see it here.", muted: true));
        }
        else AddHomeShelf("Recently played", "RecentlyPlayedRow", recent.Select(game => BuildGameCard(game, showPlaytime: false)), 182);

        if (collections.Length == 0)
        {
            PageBody.Children.Add(Heading("Collections"));
            PageBody.Children.Add(ActionButton("New collection", () => EditCollection(null)));
        }
        else AddHomeShelf("Collections", "HomeCollectionsRow", collections.Select(CollectionBrowseCard), 200,
            scrollbar: ScrollBarVisibility.Hidden);

        var libraryHeading = Heading("Library");
        libraryHeading.VerticalAlignment = VerticalAlignment.Center;
        var libraryHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Children = { libraryHeading } };
        if (games.Length > 0)
        {
            var librarySort = BuildChoicePicker("Sort library", GameSortChoices(), _librarySort,
                index => GameSortLabels[index], index => SetLibrarySort(index, fromSidebar: false));
            Grid.SetColumn(librarySort, 1);
            libraryHeader.Children.Add(librarySort);
        }
        PageBody.Children.Add(libraryHeader);
        if (games.Length == 0)
        {
            PageBody.Children.Add(Label("Add a game or watch a folder to get started.", muted: true));
            PageBody.Children.Add(Actions(ActionButton("Add game", AddFromFile, "primary"),
                ActionButton("Choose library folder", ChooseFolder)));
        }
        else PageBody.Children.Add(HomeLibraryGrid(games));
    }

    private void AddHomeShelf(string title, string name, IEnumerable<Control> cards, double cardWidth, Panel? host = null,
        ScrollBarVisibility scrollbar = ScrollBarVisibility.Auto, Control? headerContent = null)
    {
        const int spacing = 12;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var card in cards)
        {
            card.Width = cardWidth;
            row.Children.Add(card);
        }
        var viewer = new ScrollViewer
        {
            Name = name, Content = row,
            HorizontalScrollBarVisibility = scrollbar,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var previous = Arrow("Previous", "Scroll " + title + " left", "LG.Icon.ChevronLeft", -1);
        var next = Arrow("Next", "Scroll " + title + " right", "LG.Icon.Chevron", 1);
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false, Children = { previous, next } };
        headerContent ??= Heading(title);
        headerContent.VerticalAlignment = VerticalAlignment.Center;
        var heading = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Children = { headerContent, arrows } };
        Grid.SetColumn(arrows, 1);
        viewer.ScrollChanged += (_, args) =>
        {
            if (args.Source != viewer) return;
            var maximum = viewer.Extent.Width - viewer.Viewport.Width;
            arrows.IsVisible = viewer.Viewport.Width > 0 && maximum > .5;
            previous.IsEnabled = viewer.Offset.X > .5;
            next.IsEnabled = viewer.Offset.X < maximum - .5;
        };
        var shelfHost = host ?? PageBody;
        shelfHost.Children.Add(heading);
        shelfHost.Children.Add(viewer);

        Button Arrow(string suffix, string accessibleName, string icon, int direction)
        {
            var button = new Button
            {
                Name = name + suffix, Classes = { "lg" }, Content = Symbol(icon, 16),
                Width = 30, MinHeight = 30, Padding = new Thickness(0),
            };
            AutomationProperties.SetName(button, accessibleName);
            ToolTip.SetTip(button, accessibleName);
            Click(button, () =>
            {
                var stride = cardWidth + spacing;
                var visibleCards = Math.Max(1, Math.Floor((viewer.Viewport.Width + spacing) / stride));
                SmoothScroll.MoveBy(viewer, new Vector(direction * visibleCards * stride, 0), TimeSpan.FromMilliseconds(220));
            });
            return button;
        }
    }

    private ItemsControl HomeLibraryGrid(Game[] games)
    {
        const double columnSpacing = 12;
        const double minimumCardWidth = 145;
        var columns = 0;
        var library = new ItemsControl
        {
            Name = "HomeLibraryGrid",
            ItemsPanel = new FuncTemplate<Panel>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<Game[]>((row, _) =>
            {
                if (row == null) return null;
                var grid = new UniformGrid { Columns = columns, ColumnSpacing = columnSpacing, Margin = new Thickness(0, 4, 0, 10) };
                foreach (var game in row) grid.Children.Add(BuildGameCard(game));
                return grid;
            })
        };
        library.SizeChanged += (_, args) =>
        {
            if (!args.WidthChanged || args.NewSize.Width <= 0) return;
            var next = Math.Max(1, (int)((args.NewSize.Width + columnSpacing) / (minimumCardWidth + columnSpacing)));
            if (columns == next) return;
            columns = next;
            library.ItemsSource = games.Chunk(columns).ToArray();
        };
        return library;
    }
}
