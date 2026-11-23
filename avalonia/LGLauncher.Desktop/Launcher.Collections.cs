using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private void RenderCollectionCards()
    {
        PageBody.Children.Add(Actions(ActionButton("New collection", () => EditCollection(null), "primary"),
            IconButton("New dynamic collection", "LG.Icon.Brain", () => EditCollection(null, true))));
        var collections = _library.Collections.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (collections.Length == 0)
        {
            PageBody.Children.Add(Section("No collections yet", Label("Create a collection, then use Add to collection from a game's menu.", muted: true)));
            return;
        }

        const int pageSize = 24;
        _offset = Math.Min(_offset, (collections.Length - 1) / pageSize * pageSize);
        var grid = new UniformGrid { ColumnSpacing = 12, RowSpacing = 14, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var collection in collections.Skip(_offset).Take(pageSize))
            grid.Children.Add(CollectionBrowseCard(collection));
        ShowCardGrid(grid);
        AddPagination(collections.Length, pageSize);
    }

    private CollectionCard BuildCollectionCard(GameCollection collection)
    {
        var games = _library.Games.Where(collection.Includes).ToArray();
        var count = games.Length == 1 ? "1 game" : $"{games.Length} games";
        var card = new CollectionCard
        {
            Title = collection.Name, GameCount = count, IsDynamic = collection.IsDynamic,
            Content = CollectionMosaic(games)
        };
        AutomationProperties.SetName(card, collection.Name);
        return card;
    }

    private CollectionCard CollectionBrowseCard(GameCollection collection)
    {
        var card = BuildCollectionCard(collection);
        card.Command = new LauncherCommand(() => Navigate(Page.Collections, collection));
        AutomationProperties.SetHelpText(card, collection.IsDynamic ? $"Dynamic collection, {card.GameCount}" : card.GameCount);
        if (collection.IsDynamic) ToolTip.SetTip(card, DescribeCollectionRules(collection));
        card.ContextMenu = CollectionMenu(collection);
        return card;
    }

    private async Task ShowCollectionPicker(Game game)
    {
        var collections = _library.Collections.Where(collection => !collection.IsDynamic)
            .OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var manualIds = collections.Select(collection => collection.Id).ToHashSet();
        var original = game.CollectionIds.Where(manualIds.Contains).ToHashSet();
        var selected = new HashSet<string>(original);
        var search = ModalTextField(null, "Search collections");
        search.PlaceholderText = "Search collections";
        var searchIcon = Symbol("LG.Icon.Search", 15);
        searchIcon.Margin = new Thickness(10, 0, 5, 0);
        search.InnerLeftContent = searchIcon;
        var dialog = new ModalDialog("Add to collection", 620, search);
        var grid = new UniformGrid { Columns = 3, ColumnSpacing = 10, RowSpacing = 10 };
        var empty = Label("", muted: true);
        var error = Label("", muted: true);
        error.IsVisible = false;
        dialog.Body.Children.Add(grid);
        dialog.Body.Children.Add(empty);
        dialog.Body.Children.Add(error);
        var save = DialogActions(dialog, "Save changes", () =>
        {
            // Preserve older or currently unavailable collection IDs while editing manual memberships.
            var previous = game.CollectionIds;
            var previouslyUnsaved = _saveFailed;
            game.CollectionIds = previous.Where(id => !manualIds.Contains(id) || selected.Contains(id))
                .Concat(collections.Where(collection => selected.Contains(collection.Id)).Select(collection => collection.Id))
                .Distinct().ToList();
            if (!Save())
            {
                game.CollectionIds = previous;
                _saveFailed = previouslyUnsaved;
                error.Text = "Collection changes could not be saved. Try again.";
                error.IsVisible = true;
                return;
            }
            Status("Collection membership updated.");
            dialog.Close(true);
        }, defaultAction: false);
        save.IsEnabled = false;
        dialog.InitialFocus = search;

        var cards = collections.Select(collection =>
        {
            var card = BuildCollectionCard(collection);
            SetSelection(card, selected.Contains(collection.Id));
            card.Command = new LauncherCommand(() =>
            {
                var added = selected.Add(collection.Id);
                if (!added) selected.Remove(collection.Id);
                SetSelection(card, added);
                save.IsEnabled = !selected.SetEquals(original);
            });
            return card;
        }).ToArray();
        search.TextChanged += (_, _) => Filter();
        Filter();
        if (!await ShowModal(dialog)) return;
        var scroll = ContentScroll.Offset;
        Render();
        if (ContentScroll.IsVisible)
        {
            ContentScroll.UpdateLayout();
            ContentScroll.Offset = scroll;
        }

        void Filter()
        {
            var terms = (search.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            grid.Children.Clear();
            foreach (var match in cards.Select(card => (Card: card, Score: CollectionSearchScore(card.Title, terms)))
                .Where(match => match.Score.HasValue).OrderBy(match => match.Score))
                grid.Children.Add(match.Card);
            empty.IsVisible = grid.Children.Count == 0;
            empty.Text = collections.Length == 0 ? "Create a manual collection to add games to it." : "No matching collections.";
        }
    }

    private static void SetSelection(CollectionCard card, bool selected)
    {
        card.Classes.Set("selected", selected);
        AutomationProperties.SetHelpText(card, selected ? "In collection. Activate to remove." : "Activate to add to this collection.");
    }

    private static int? CollectionSearchScore(string name, string[] terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (name.Contains(term, StringComparison.CurrentCultureIgnoreCase)) continue;
            var next = 0;
            foreach (var character in name)
            {
                if (char.ToUpperInvariant(character) == char.ToUpperInvariant(term[next]) && ++next == term.Length)
                    break;
            }
            if (next == term.Length)
            {
                score++;
                continue;
            }
            if (term.Length < 4 || !name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(word => IsCollectionTypo(word, term))) return null;
            score += 2;
        }
        return score;
    }

    private static bool IsCollectionTypo(ReadOnlySpan<char> word, ReadOnlySpan<char> term)
    {
        if (word.Length < term.Length) return IsCollectionTypo(term, word);
        if (word.Length - term.Length > 1) return false;
        var mismatch = 0;
        while (mismatch < term.Length && SameLetter(word[mismatch], term[mismatch])) mismatch++;
        if (mismatch == term.Length) return true;
        if (word.Length != term.Length)
            return word[(mismatch + 1)..].Equals(term[mismatch..], StringComparison.OrdinalIgnoreCase);
        return word[(mismatch + 1)..].Equals(term[(mismatch + 1)..], StringComparison.OrdinalIgnoreCase) ||
            (mismatch + 1 < term.Length && SameLetter(word[mismatch], term[mismatch + 1]) &&
                SameLetter(word[mismatch + 1], term[mismatch]) &&
                word[(mismatch + 2)..].Equals(term[(mismatch + 2)..], StringComparison.OrdinalIgnoreCase));

        static bool SameLetter(char left, char right) => char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }

    private Panel CollectionMosaic(Game[] games)
    {
        var previews = games.OrderByDescending(game => game.CustomPortraitId != null)
            .ThenByDescending(game => game.SteamId > 0)
            .ThenByDescending(game => game.LastPlayedUtc).Take(4).ToArray();
        var backdrop = this.FindResource("LG.Line") as IBrush;
        var mosaic = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse(previews.Length > 1 ? "*,*" : "*"),
            RowDefinitions = RowDefinitions.Parse(previews.Length > 2 ? "*,*" : "*"),
            ColumnSpacing = 2,
            RowSpacing = 2,
            Background = previews.Length > 1 ? backdrop : null
        };
        if (previews.Length == 0)
        {
            var icon = Symbol("LG.Icon.Library", 45);
            icon.Margin = new Avalonia.Thickness(13, 11, 0, 0);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Opacity = .25;
            mosaic.Children.Add(icon);
            return mosaic;
        }

        var fallback = this.FindResource("LG.ArtworkFallback") as IBrush;
        for (var index = 0; index < previews.Length; index++)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            var tile = new Panel { ClipToBounds = true, Background = fallback, Children = { image } };
            if (previews.Length == 3)
            {
                Grid.SetColumn(tile, index == 0 ? 0 : 1);
                Grid.SetRow(tile, index == 2 ? 1 : 0);
                Grid.SetRowSpan(tile, index == 0 ? 2 : 1);
            }
            else
            {
                Grid.SetColumn(tile, index % 2);
                Grid.SetRow(tile, index / 2);
            }
            mosaic.Children.Add(tile);
            LoadCollectionArtwork(image, previews[index]);
        }
        return mosaic;
    }

    private async void LoadCollectionArtwork(Image target, Game game)
    {
        var (picture, _) = await ResolveCardArtwork(game, _background.Token);
        if (picture != null) target.Source = picture;
    }
}
