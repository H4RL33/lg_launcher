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
    private static readonly string[] GameSortLabels = ["Title A–Z", "Last launched", "Playtime", "Title Z–A", "Added date", "Release date"];
    private static readonly string[] CollectionSortLabels = ["A–Z", "Z–A", "Cumulative playtime", "Created date", "Qty. games"];
    private static readonly string[] SidebarGameSortLabels = ["A–Z", "Last played", "Playtime", "Z–A", "Added", "Released"];
    private static readonly string[] SidebarCollectionSortLabels = ["A–Z", "Z–A", "Playtime", "Created", "Games"];
    private readonly HashSet<Border> _collapsedSidebarSections = [];
    private readonly Dictionary<Border, object> _sidebarReveals = [];

    private IEnumerable<int> GameSortChoices() => Enumerable.Range(0, GameSortLabels.Length).Where(index => TrackPlaytime || index != 2);

    private IEnumerable<int> CollectionSortChoices() => Enumerable.Range(0, CollectionSortLabels.Length).Where(index => TrackPlaytime || index != 2);

    private void InitializeSidebarSections()
    {
        SidebarLists.RowDefinitions[1].Bind(RowDefinition.MaxHeightProperty, LibrarySectionClip.GetObservable(MaxHeightProperty));
        SidebarLists.RowDefinitions[3].Bind(RowDefinition.MaxHeightProperty, CollectionsSectionClip.GetObservable(MaxHeightProperty));
        UpdateHeaders();
        PreferencesChanged += UpdateHeaders;

        void UpdateHeaders()
        {
            LibrarySectionHeader.Content = SidebarSectionHeader("Library", LibrarySectionClip, LibrarySection, GameSortChoices(), _librarySort,
                GameSortLabels, SidebarGameSortLabels, index => SetLibrarySort(index, fromSidebar: true));
            CollectionsSectionHeader.Content = SidebarSectionHeader("Collections", CollectionsSectionClip, CollectionsSection, CollectionSortChoices(), _collectionsSort,
                CollectionSortLabels, SidebarCollectionSortLabels, index => SetCollectionsSort(index, fromSidebar: true));
        }
    }

    private Grid SidebarSectionHeader(string title, Border clip, Grid section, IEnumerable<int> choices, int current,
        string[] labels, string[] shortLabels, Func<int, bool> select)
    {
        var arrow = Symbol(Expanded() ? "LG.Icon.ChevronDown" : "LG.Icon.Chevron", 12);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        var label = Label(title.ToUpperInvariant());
        label.Classes.Add("sidebar-heading");
        label.VerticalAlignment = VerticalAlignment.Center;
        var toggle = new Button { Classes = { "lg", "quiet" } };
        Click(toggle, () =>
        {
            if (Expanded()) _collapsedSidebarSections.Add(clip);
            else _collapsedSidebarSections.Remove(clip);
            arrow.Data = (Geometry?)this.FindResource(Expanded() ? "LG.Icon.ChevronDown" : "LG.Icon.Chevron");
            UpdateToggleName();
            RevealSidebarSection(clip, section, Expanded());
        });
        toggle.Padding = new Thickness(4, 0);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { arrow, label }
        };
        UpdateToggleName();
        var sort = BuildChoicePicker($"Sort {title.ToLowerInvariant()}", choices, current, index => labels[index], select,
            textOnly: true, currentLabel: index => shortLabels[index]);
        if (sort.Content is TextBlock sortLabel) sortLabel.Classes.Add("sidebar-sort");
        Grid.SetColumn(sort, 1);
        return new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Children = { toggle, sort } };

        bool Expanded() => !_collapsedSidebarSections.Contains(clip);
        void UpdateToggleName() => AutomationProperties.SetName(toggle, Expanded() ? $"Collapse {title}" : $"Expand {title}");
    }

    private double SidebarSectionHeight(Border clip)
    {
        var count = clip == LibrarySectionClip ? _shownEntries.Length : _shownCollections.Length;
        return (Math.Max(count, 1) + 1) * (double)this.FindResource("LG.SidebarRowHeight")!;
    }

    private void SettleSidebarSection(Border clip, Grid section)
    {
        if (_sidebarReveals.ContainsKey(clip)) return;
        section.ClearValue(HeightProperty);
        section.IsVisible = !_collapsedSidebarSections.Contains(clip);
        clip.MaxHeight = section.IsVisible ? SidebarSectionHeight(clip) : 0;
    }

    private void RevealSidebarSection(Border clip, Grid section, bool expanding)
    {
        var from = clip.Bounds.Height;
        var reveal = new object();
        _sidebarReveals[clip] = reveal;
        CancelMotion(clip);
        if (!CanAnimate(clip))
        {
            _sidebarReveals.Remove(clip);
            SettleSidebarSection(clip, section);
            return;
        }

        // Measure the expanded viewport before revealing it, so its footer and scroll extent stay put.
        if (expanding)
        {
            section.ClearValue(HeightProperty);
            section.IsVisible = true;
            clip.MaxHeight = SidebarSectionHeight(clip);
            SidebarLists.UpdateLayout();
        }
        var target = expanding ? clip.Bounds.Height : 0;
        section.Height = Math.Max(from, expanding ? target : section.Bounds.Height);
        clip.MaxHeight = from;
        AnimateMotion(clip, async token =>
        {
            try { await ViewMotion.RevealFromTop(clip, MaxHeightProperty, from, target, token); }
            finally
            {
                if (_sidebarReveals.GetValueOrDefault(clip) == reveal)
                {
                    _sidebarReveals.Remove(clip);
                    SettleSidebarSection(clip, section);
                }
            }
        });
    }

    private Button BuildLibrarySortPicker() => BuildChoicePicker("Sort library", GameSortChoices(), _librarySort,
        index => GameSortLabels[index], index => SetLibrarySort(index, fromSidebar: false));

    private bool SetLibrarySort(int index, bool fromSidebar)
    {
        _librarySort = index;
        RenderSidebar();
        if (_page == Page.Home) RefreshHomeLibrarySort(fromSidebar);
        if (!fromSidebar) PreferencesChanged?.Invoke();
        return true;
    }

    private bool SetCollectionsSort(int index, bool fromSidebar)
    {
        if (_page == Page.Collections && _collection == null) ReorderCards(() => _collectionsSort = index);
        else
        {
            _collectionsSort = index;
            RenderSidebar();
        }
        if (!fromSidebar) PreferencesChanged?.Invoke();
        return true;
    }

    private IEnumerable<GameCollection> SortedCollections() => _collectionsSort switch
    {
        1 => _library.Collections.OrderByDescending(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase),
        2 when TrackPlaytime => _library.Collections.OrderByDescending(collection => _library.Games.Where(collection.Includes).Sum(game => game.PlaySeconds))
            .ThenBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase),
        3 => _library.Collections.OrderByDescending(collection => collection.CreatedUtc).ThenBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase),
        4 => _library.Collections.OrderByDescending(collection => _library.Games.Count(collection.Includes))
            .ThenBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase),
        _ => _library.Collections.OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
    };

    private Task<bool> ChooseCollectionType()
    {
        var dialog = new ModalDialog("New collection", 440);
        dialog.Body.Children.Add(SettingsRow("Manual collection", "LG.Icon.Library", () => Choose(false),
            Label("Choose which games to include.", muted: true)));
        dialog.Body.Children.Add(SettingsRow("Dynamic collection", "LG.Icon.Brain", () => Choose(true),
            Label("Include games automatically using rules.", muted: true)));
        dialog.Footer.Children.Add(ActionButton("Cancel", () => dialog.Close(), "quiet"));
        return ShowModal(dialog);

        void Choose(bool dynamic)
        {
            dialog.Close(true);
            EditCollection(null, dynamic);
        }
    }

    private void RenderCollectionCards()
    {
        PageBody.Children.Add(Actions(ActionButton("New collection", () => EditCollection(null), "primary"),
            IconButton("New dynamic collection", "LG.Icon.Brain", () => EditCollection(null, true)),
            BuildChoicePicker("Sort collections", CollectionSortChoices(), _collectionsSort, index => CollectionSortLabels[index],
                index => SetCollectionsSort(index, fromSidebar: false))));
        var collections = SortedCollections().ToArray();
        if (collections.Length == 0)
        {
            PageBody.Children.Add(Section("No collections yet", Label("Create a collection, then use Add to collection from a game's menu.", muted: true)));
            return;
        }

        const int pageSize = 24;
        _offset = Math.Min(_offset, (collections.Length - 1) / pageSize * pageSize);
        var grid = new UniformGrid { ColumnSpacing = 12, RowSpacing = 14, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var collection in collections.Skip(_offset).Take(pageSize))
            grid.Children.Add(new Border { Child = CollectionBrowseCard(collection), Tag = collection });
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
