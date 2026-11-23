using Avalonia.Controls;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private readonly HashSet<int> _attemptedGenres = [];
    private bool _loadingGenres;

    private bool MatchesLibrarySearch(Game game, string query)
    {
        var genres = game.GenreNames;
        if (query.StartsWith("genre:", StringComparison.OrdinalIgnoreCase))
        {
            var genre = query[6..].Trim();
            return genre.Length == 0 || genres.Any(value => value.Contains(genre, StringComparison.CurrentCultureIgnoreCase));
        }
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
            game.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
            genres.Any(genre => genre.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
    }

    private static string[] ParseGenreInput(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool NeedsSteamGenres(Game game) => game.SteamId > 0 && !game.ManualGenres &&
        (game.SteamId != game.GenreSteamId || !game.SteamGenresComplete || game.Genres == null);

    private async void QueueGenres()
    {
        if (_loadingGenres || !CanFetch) return;
        _loadingGenres = true;
        try
        {
            while (CanFetch)
            {
                var next = _library.Games.FirstOrDefault(game => NeedsSteamGenres(game) && !_attemptedGenres.Contains(game.SteamId));
                if (next == null) return;
                var id = next.SteamId;
                _attemptedGenres.Add(id);
                var token = _background.Token;
                List<string>? genres = null;
                try { genres = await _steam.GetGenres(id, token); }
                catch (OperationCanceledException) { }
                catch (HttpRequestException) { }
                if (token.IsCancellationRequested)
                {
                    _attemptedGenres.Remove(id);
                    continue;
                }
                if (genres == null || _closed) continue;
                var games = _library.Games.Where(game => game.SteamId == id && NeedsSteamGenres(game)).ToArray();
                if (games.Length == 0) continue;
                if (!SaveSteamGenres(games, id, genres)) return;
                if (_page is Page.Home or Page.Collections or Page.Game) Render();
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

    private bool SaveSteamGenres(Game[] entries, int id, List<string> genres)
    {
        var previous = entries.Select(game => (Game: game, game.Genres, game.GenreSteamId, game.SteamGenresComplete)).ToArray();
        var previouslyUnsaved = _saveFailed;
        foreach (var game in entries)
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
