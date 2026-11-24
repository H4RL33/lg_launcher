namespace LGLauncher;

internal sealed partial class Launcher
{
    private readonly HashSet<int> _steamRefreshes = [];

    private async Task RefreshGameFromSteam(Game game)
    {
        var id = game.SteamId;
        if (id <= 0 || !CanFetch || !_steamRefreshes.Add(id)) return;
        if (_game?.SteamId == id) RefreshSteamButton.IsEnabled = false;
        Status($"Refreshing Steam metadata and artwork for {game.Title}…");
        var token = _background.Token;
        try
        {
            var genres = Fetch(() => _steam.GetGenres(id, token));
            var tags = Fetch(() => _steam.GetStoreTags(id, token));
            var details = Fetch(() => _steam.GetGameDetails(id, token));
            var hero = Fetch(() => _steam.GetBackdrop(id, token, refresh: true));
            var header = Fetch(() => _steam.DownloadArtwork(id, token, refresh: true));
            var portrait = Fetch(() => _steam.GetPortrait(id, token, refresh: true));
            var icon = Fetch(() => _steam.GetIcon(id, token, refresh: true));
            var logo = Fetch(() => _steam.GetLogo(id, token, refresh: true));
            await Task.WhenAll(genres, tags, details, hero, header, portrait, icon, logo);

            if (_closed || token.IsCancellationRequested || game.SteamId != id || !_library.Games.Contains(game)) return;
            var logoPath = logo.Result;
            var logoUpdated = false;
            if (logoPath != null)
            {
                var picture = await Task.Run(() => ReadGameLogo(logoPath), token);
                if (_closed || token.IsCancellationRequested) { picture?.Dispose(); return; }
                if (picture != null)
                {
                    RememberGameLogo(id, picture);
                    logoUpdated = true;
                }
            }

            if (header.Result != null) await RefreshCardCache((id, false));
            if (portrait.Result != null) await RefreshCardCache((id, true));
            if (_closed || token.IsCancellationRequested || game.SteamId != id || !_library.Games.Contains(game)) return;
            var tagsSaved = tags.Result == null ||
                SaveStoreTags(_library.Games.Where(entry => entry.SteamId == id).ToArray(), id, tags.Result);
            var genresSaved = genres.Result == null || game.ManualGenres || SaveSteamGenres([game], id, genres.Result);
            var detailsSaved = details.Result == null || SaveGameDetails(id, details.Result, refresh: true);
            if (icon.Result != null)
            {
                foreach (var entry in _library.Games.Where(entry => entry.SteamId == id))
                    InvalidateEntry(entry);
            }
            var updated = genres.Result != null || tags.Result != null || details.Result != null || hero.Result != null || header.Result != null ||
                portrait.Result != null || icon.Result != null || logoUpdated;
            if (updated && (icon.Result != null || _page is Page.Home or Page.Collections or Page.Library or Page.Game)) Render();
            if ((hero.Result != null || header.Result != null) && _page == Page.Game && _game is { } selected && selected.SteamId == id && !_editingArtwork)
                LoadDetailsArtwork(selected, reload: true);
            if (tagsSaved && genresSaved && detailsSaved)
                Status(!updated ? $"Steam could not refresh {game.Title}."
                    : !logoUpdated && _library.Preferences.UseGameLogos
                        ? $"Refreshed {game.Title}. Steam did not return a new game logo."
                        : $"Refreshed Steam data for {game.Title}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _steamRefreshes.Remove(id);
            if (!_closed && _page == Page.Game && _game?.SteamId == id)
                RefreshSteamButton.IsEnabled = _playing.Count == 0;
        }

        async Task<T?> Fetch<T>(Func<Task<T>> request) where T : class
        {
            try { return await request(); }
            catch (Exception error) when (IsArtworkError(error) || error is OperationCanceledException) { return null; }
        }
    }

    private async Task RefreshCardCache((int Id, bool Portrait) key)
    {
        if (_cardImageRequests.TryGetValue(key, out var pending)) await pending;
        _cardImages.Remove(key);
        _attemptedCardImages.Remove(key);
    }
}
