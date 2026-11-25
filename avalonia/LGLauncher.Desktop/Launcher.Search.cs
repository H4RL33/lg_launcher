using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly DispatcherTimer _searchExamplesTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private static readonly string[] SearchExamples =
        ["Search games or genres", "genre: RPG", "developer: Rockstar", "publisher: Capcom", "tag: Co-op", "year: 2024", "scale: Indie"];
    private string? _displayedSearchQuery;

    private void InitializeSearch()
    {
        var example = 0;
        _searchExamplesTimer.Tick += (_, _) =>
        {
            if (!SearchSurface.IsVisible || SearchBox.IsFocused || !String.IsNullOrEmpty(SearchBox.Text)) return;
            example = (example + 1) % SearchExamples.Length;
            SearchBox.PlaceholderText = SearchExamples[example];
        };
        Opened += (_, _) => _searchExamplesTimer.Start();
        Closed += (_, _) => _searchExamplesTimer.Stop();
        _searchDelay.Tick += (_, _) => ShowSearchResults();
        SearchBox.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            if (String.IsNullOrWhiteSpace(SearchBox.Text)) CloseSearch();
            else if (SearchBox.IsFocused) _searchDelay.Start();
        };
        SearchBox.GotFocus += (_, _) => ShowSearchResults();
        SearchSurface.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!SearchSurface.IsKeyboardFocusWithin) CloseSearch();
        });
        Deactivated += (_, _) => CloseSearch();
        AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (args.Source is not Visual hit || !hit.GetSelfAndVisualAncestors().Contains(SearchSurface))
                CloseSearch();
            else if (hit.FindAncestorOfType<TextBox>(includeSelf: true) == SearchBox)
                ShowSearchResults();
        }, RoutingStrategies.Tunnel);
        SearchResults.Tapped += (_, args) =>
        {
            if (_displayedSearchQuery != SearchBox.Text?.Trim())
            {
                ShowSearchResults();
                return;
            }
            if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { Content: LibraryEntry result })
                OpenSearchResult(result);
        };
    }

    private void ShowSearchResults()
    {
        _searchDelay.Stop();
        var query = SearchBox.Text?.Trim();
        if (!SearchSurface.IsVisible || String.IsNullOrEmpty(query))
        {
            CloseSearch();
            return;
        }

        var selection = _displayedSearchQuery == query ? SearchResults.SelectedItem : null;
        var matchesQuery = CreateLibrarySearch(query);
        var matches = _shownEntries.Where(entry => matchesQuery(entry.Game)).ToArray();
        var changed = SearchResults.ItemsSource is not LibraryEntry[] previous || !previous.SequenceEqual(matches);
        if (changed)
            SearchResults.ItemsSource = matches;
        SearchResults.SelectedItem = selection;
        SearchResults.IsVisible = matches.Length > 0;
        SearchEmpty.IsVisible = matches.Length == 0;
        if (changed || !SearchDropdown.IsVisible)
        {
            SearchDropdown.IsVisible = true;
            ResizeSearchResults(SearchDropdown, SearchResultsFrame, SearchResults, matches.Length);
        }
        _displayedSearchQuery = query;
    }

    private static Func<Game, bool> CreateLibrarySearch(string query)
    {
        const StringComparison comparison = StringComparison.CurrentCultureIgnoreCase;
        var separator = query.IndexOf(':');
        if (separator > 0)
        {
            var prefix = query[..separator].Trim().ToLowerInvariant();
            var qualifier = query[(separator + 1)..].Trim();
            Func<Game, IEnumerable<string>>? values = prefix switch
            {
                "genre" => game => game.GenreNames,
                "tag" => game => game.TagNames.Concat(game.SteamTagsFor()),
                "developer" => game => game.DeveloperNames,
                "publisher" => game => game.PublisherNames,
                _ => null
            };
            if (values != null) return game => qualifier.Length == 0 || values(game).Any(value => value.Contains(qualifier, comparison));
            if (prefix == "year") return game => qualifier.Length == 0 ||
                int.TryParse(qualifier, out var year) && game.SteamReleaseDate?.Year == year;
            if (prefix == "scale") return game => qualifier.Length == 0 ||
                game.Scale != GameScale.Unspecified && game.Scale.ToString().Equals(qualifier, comparison);
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return game => terms.All(term => game.Title.Contains(term, comparison) ||
            game.GenreNames.Any(genre => genre.Contains(term, comparison)));
    }

    private void ResizeSearchResults(Control dropdown, Border frame, ListBox results, int count)
    {
        var contentHeight = Math.Max(1, count) * (double)this.FindResource("LG.SearchResultRowHeight")!;
        var insets = frame.Padding + frame.BorderThickness;
        ScrollViewer.SetVerticalScrollBarVisibility(results,
            contentHeight > results.MaxHeight ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
        dropdown.Height = Math.Min(contentHeight, results.MaxHeight) + insets.Top + insets.Bottom;
    }

    private void CloseSearch()
    {
        _searchDelay.Stop();
        SearchDropdown.IsVisible = false;
        SearchDropdown.Height = 0;
        SearchResults.SelectedIndex = -1;
        _displayedSearchQuery = null;
    }

    private bool HandleSearchKey(Key key)
    {
        if (!SearchSurface.IsKeyboardFocusWithin) return false;
        if (key == Key.Escape && (SearchDropdown.IsVisible || _searchDelay.IsEnabled))
        {
            SearchBox.Focus();
            CloseSearch();
            return true;
        }
        if (key is not (Key.Down or Key.Up or Key.Enter) || String.IsNullOrWhiteSpace(SearchBox.Text)) return false;
        if (!SearchDropdown.IsVisible || _displayedSearchQuery != SearchBox.Text?.Trim()) ShowSearchResults();
        if (SearchResults.ItemCount == 0) return true;
        if (key == Key.Enter)
        {
            if ((SearchResults.SelectedItem ?? SearchResults.Items[0]) is LibraryEntry result) OpenSearchResult(result);
            return true;
        }
        var index = SearchResults.SelectedIndex;
        SearchResults.SelectedIndex = index < 0 && key == Key.Up
            ? SearchResults.ItemCount - 1
            : Math.Clamp(index + (key == Key.Down ? 1 : -1), 0, SearchResults.ItemCount - 1);
        SearchResults.ScrollIntoView(SearchResults.SelectedIndex);
        return true;
    }

    private void OpenSearchResult(LibraryEntry result)
    {
        if (!_library.Games.Contains(result.Game)) return;
        SearchBox.Clear();
        CloseSearch();
        SelectGame(result.Game);
        PlayGame.Focus();
    }
}
