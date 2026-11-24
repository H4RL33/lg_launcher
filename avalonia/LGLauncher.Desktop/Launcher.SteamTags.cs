using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private readonly HashSet<int> _attemptedTags = [];
    private readonly HashSet<int> _attemptedGameDetails = [];
    private bool _loadingTags;
    private bool _loadingGameDetails;

    private void QueueSteamMetadata()
    {
        _ = QueueGenres();
        _ = QueueStoreTags();
        _ = QueueGameDetails();
    }

    private async Task QueueGameDetails()
    {
        if (_loadingGameDetails || !CanFetch) return;
        _loadingGameDetails = true;
        try
        {
            while (CanFetch)
            {
                var id = _library.Games.Where(game => game.SteamId > 0 && !game.HasSteamDetails)
                    .Select(game => game.SteamId).FirstOrDefault(id => !_attemptedGameDetails.Contains(id) && !_steamRefreshes.Contains(id));
                if (id == 0) break;
                _attemptedGameDetails.Add(id);
                var token = _background.Token;
                SteamGameDetails? details = null;
                try { details = await _steam.GetGameDetails(id, token); }
                catch (Exception error) when (error is OperationCanceledException or HttpRequestException) { }
                if (token.IsCancellationRequested)
                {
                    _attemptedGameDetails.Remove(id);
                    break;
                }
                if (details == null || !CanFetch || _steamRefreshes.Contains(id)) continue;
                if (!SaveGameDetails(id, details, refresh: false)) break;
                if (_page is Page.Home or Page.Collections or Page.Library || SearchDropdown.IsVisible) Render();
                else RenderSidebar();
            }
        }
        finally { _loadingGameDetails = false; }
    }

    private bool SaveGameDetails(int id, SteamGameDetails details, bool refresh)
    {
        var entries = _library.Games.Where(game => game.SteamId == id && (refresh || !game.HasSteamDetails))
            .Select(game => (Game: game, game.DetailsSteamId, game.Developers, game.Publishers, game.ReleaseDate)).ToArray();
        if (entries.Length == 0) return true;
        var previouslyUnsaved = _saveFailed;
        foreach (var entry in entries)
            (entry.Game.DetailsSteamId, entry.Game.Developers, entry.Game.Publishers, entry.Game.ReleaseDate) =
                (id, details.Developers, details.Publishers, details.ReleaseDate);
        if (Save()) return true;
        foreach (var entry in entries)
            (entry.Game.DetailsSteamId, entry.Game.Developers, entry.Game.Publishers, entry.Game.ReleaseDate) =
                (entry.DetailsSteamId, entry.Developers, entry.Publishers, entry.ReleaseDate);
        _saveFailed = previouslyUnsaved;
        _attemptedGameDetails.Remove(id);
        return false;
    }

    private async Task QueueStoreTags()
    {
        if (_loadingTags || !CanFetch) return;
        _loadingTags = true;
        var homeChanged = false;
        try
        {
            while (CanFetch)
            {
                var next = _game is { } selected && NeedsTags(selected) ? selected : _library.Games.FirstOrDefault(NeedsTags);
                if (next == null) break;
                var id = next.SteamId;
                _attemptedTags.Add(id);
                var token = _background.Token;
                List<string>? tags = null;
                try { tags = await _steam.GetStoreTags(id, token); }
                catch (Exception error) when (error is OperationCanceledException or HttpRequestException) { }
                if (token.IsCancellationRequested)
                {
                    _attemptedTags.Remove(id);
                    continue;
                }
                if (tags == null || _closed || _steamRefreshes.Contains(id)) continue;

                var entries = _library.Games.Where(game => game.SteamId == id && !game.HasSteamTags).ToArray();
                var affected = _library.Collections.Where(collection => collection.Rules?.Any(rule =>
                    rule?.Attribute == CollectionAttribute.SteamTag) == true).ToArray();
                var changes = entries.SelectMany(game => affected.Select(collection =>
                    (Game: game, Collection: collection, Included: collection.Includes(game)))).ToArray();
                if (!SaveStoreTags(entries, id, tags)) break;
                if (SearchDropdown.IsVisible) ShowSearchResults();
                if (_page == Page.Game && _game?.SteamId == id) RenderStoreTags(_game);
                if (!changes.Any(change => change.Included != change.Collection.Includes(change.Game))) continue;
                homeChanged = true;
                if (_page == Page.Collections) Render();
            }
        }
        finally
        {
            _loadingTags = false;
            if (homeChanged && !_closed && _page == Page.Home) Render();
        }

        bool NeedsTags(Game game) => game.SteamId > 0 && !game.HasSteamTags &&
            !_attemptedTags.Contains(game.SteamId) && !_steamRefreshes.Contains(game.SteamId);
    }

    private bool SaveStoreTags(Game[] entries, int id, List<string> tags)
    {
        if (entries.Length == 0) return true;
        var previous = entries.Select(game => (Game: game, game.TagSteamId, game.SteamTags)).ToArray();
        var previouslyUnsaved = _saveFailed;
        foreach (var game in entries)
        {
            game.TagSteamId = id;
            game.SteamTags = tags;
        }
        if (Save()) return true;
        foreach (var entry in previous)
        {
            entry.Game.TagSteamId = entry.TagSteamId;
            entry.Game.SteamTags = entry.SteamTags;
        }
        _saveFailed = previouslyUnsaved;
        return false;
    }

    private void RenderStoreTags(Game game)
    {
        StoreTags.Children.Clear();
        if (!_library.Preferences.ShowStoreTags)
        {
            StoreTags.IsVisible = false;
            return;
        }
        foreach (var tag in game.TagNames)
        {
            var label = Label(tag.ToUpperInvariant(), muted: true);
            label.FontWeight = FontWeight.SemiBold;
            label.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("LG.Font.10"));
            StoreTags.Children.Add(label);
        }
        StoreTags.IsVisible = StoreTags.Children.Count > 0;
    }

}
