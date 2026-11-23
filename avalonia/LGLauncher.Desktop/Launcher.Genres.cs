namespace LGLauncher;

internal sealed partial class Launcher
{
    private const int SteamGenreBatchSize = 8;
    private readonly HashSet<int> _attemptedGenres = [];
    private readonly Dictionary<int, List<string>> _pendingSteamGenres = [];
    private bool _loadingGenres;

    private static string[] ParseGenreInput(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool NeedsSteamGenres(Game game) => game.SteamId > 0 && !game.ManualGenres &&
        (game.SteamId != game.GenreSteamId || !game.SteamGenresComplete || game.Genres == null);

    private async Task QueueGenres()
    {
        if (_loadingGenres || !CanFetch) return;
        _loadingGenres = true;
        try
        {
            while (CanFetch)
            {
                var token = _background.Token;
                var appIds = _library.Games.Where(NeedsSteamGenres).Select(game => game.SteamId)
                    .Where(id => !_attemptedGenres.Contains(id)).Distinct().ToArray();
                if (appIds.Length == 0) return;
                foreach (var id in appIds)
                {
                    if (!CanFetch || token.IsCancellationRequested) break;
                    _attemptedGenres.Add(id);
                    List<string>? genres = null;
                    try { genres = await _steam.GetGenres(id, token); }
                    catch (Exception error) when (error is OperationCanceledException or HttpRequestException) { }
                    if (token.IsCancellationRequested)
                    {
                        _attemptedGenres.Remove(id);
                        break;
                    }
                    if (genres == null) continue;
                    _pendingSteamGenres[id] = genres;
                    if (_pendingSteamGenres.Count >= SteamGenreBatchSize && !FlushPendingSteamGenres()) return;
                }
                if (_closed || !FlushPendingSteamGenres()) return;
            }
        }
        finally { _loadingGenres = false; }
    }

    private void RenderGenres(Game game)
    {
        var genres = game.GenreNames;
        GenreChips.Children.Clear();
        foreach (var genre in genres)
        {
            var chip = ActionButton(genre, () =>
            {
                SearchBox.Text = "genre: " + genre;
                SearchBox.Focus();
                ShowSearchResults();
            });
            chip.Classes.Add("genre");
            GenreChips.Children.Add(chip);
        }
        GenreChips.IsVisible = GenreChips.Children.Count > 0;
    }

    private bool FlushPendingSteamGenres()
    {
        if (_pendingSteamGenres.Count == 0) return true;
        // Resolve the batch against the current games because edits and removals can happen during requests.
        var changes = new List<(Game Game, int AppId, List<string> Genres)>();
        foreach (var game in _library.Games)
            if (_pendingSteamGenres.TryGetValue(game.SteamId, out var genres) && NeedsSteamGenres(game))
                changes.Add((game, game.SteamId, genres));

        var saved = SaveSteamGenreChanges(changes);
        if (!saved) _attemptedGenres.ExceptWith(_pendingSteamGenres.Keys);
        _pendingSteamGenres.Clear();
        if (saved && changes.Count > 0 && CanFetch && _page is Page.Home or Page.Collections or Page.Game) Render();
        return saved;
    }

    private bool SaveSteamGenres(Game[] entries, int id, List<string> genres) =>
        SaveSteamGenreChanges(entries.Select(game => (Game: game, AppId: id, Genres: genres)).ToArray());

    private bool SaveSteamGenreChanges(IReadOnlyList<(Game Game, int AppId, List<string> Genres)> changes)
    {
        if (changes.Count == 0) return true;
        var previous = changes.Select(change => (change.Game, change.Game.Genres, change.Game.GenreSteamId,
            change.Game.SteamGenresComplete)).ToArray();
        var previouslyUnsaved = _saveFailed;
        foreach (var (game, id, genres) in changes)
        {
            game.Genres = genres;
            game.GenreSteamId = id;
            game.SteamGenresComplete = true;
        }
        if (Save()) return true;
        foreach (var entry in previous)
            (entry.Game.Genres, entry.Game.GenreSteamId, entry.Game.SteamGenresComplete) =
                (entry.Genres, entry.GenreSteamId, entry.SteamGenresComplete);
        _saveFailed = previouslyUnsaved;
        return false;
    }
}
