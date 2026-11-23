using System.Security;
using Avalonia.Automation;
using Avalonia.Media.Imaging;
using Microsoft.Win32;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private readonly HashSet<int> _unavailableGameLogos = [];
    private readonly List<(int Id, Bitmap Picture)> _recentGameLogos = [];
    private const int RecentGameLogoLimit = 12;
    private CancellationTokenSource? _gameLogoRequest;
    private int _gameLogoSteamId;
    private string? _gameLogoCustomId;
    private Bitmap? _customGameLogo;
    private bool _gameLogoRequestedOnline;

    private async void LoadGameLogo(Game game)
    {
        var id = game.SteamId;
        var customId = game.CustomLogoId;
        if (!_library.Preferences.UseGameLogos || id <= 0 && customId == null)
        {
            ClearGameLogo();
            return;
        }

        AutomationProperties.SetName(GameLogo, game.Title);
        var online = CanFetch;
        if (_gameLogoSteamId == id && _gameLogoCustomId == customId && (GameLogo.Source != null ||
            (!online || _gameLogoRequestedOnline) && _gameLogoRequest?.IsCancellationRequested != true)) return;

        ClearGameLogo();
        _gameLogoSteamId = id;
        _gameLogoCustomId = customId;
        var cachedIndex = customId == null ? _recentGameLogos.FindIndex(entry => entry.Id == id) : -1;
        if (cachedIndex >= 0)
        {
            var cached = _recentGameLogos[cachedIndex];
            _recentGameLogos.RemoveAt(cachedIndex);
            _recentGameLogos.Add(cached);
            ShowGameLogo(cached.Picture);
            return;
        }

        // A saved logo can decode off the UI thread without flashing the fallback title.
        if (_steam.CachedLogo(id) != null) GameTitle.IsVisible = false;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
        _gameLogoRequest = request;
        _gameLogoRequestedOnline = online;
        var token = request.Token;
        try
        {
            if (_store.CustomArtworkPath(customId) is { } customPath && File.Exists(customPath))
            {
                var custom = await Task.Run(() => ReadGameLogo(customPath), token);
                if (!StillSelected()) { custom?.Dispose(); return; }
                if (custom != null)
                {
                    _customGameLogo = custom;
                    ShowGameLogo(custom);
                    return;
                }
            }
            if (id <= 0) return;
            var picture = await Task.Run(() => ReadCachedGameLogo(id), token);
            if (picture == null && online && StillSelected() && !_unavailableGameLogos.Contains(id))
            {
                var path = await _steam.GetLogo(id, token);
                if (path != null) picture = await Task.Run(() => ReadGameLogo(path), token);
                if (picture == null && StillSelected()) _unavailableGameLogos.Add(id);
            }
            if (!StillSelected()) { picture?.Dispose(); return; }
            if (picture == null) return;
            RememberGameLogo(id, picture);
            if (customId != null) ShowGameLogo(picture);
        }
        catch (OperationCanceledException)
        {
            if (_gameLogoRequest == request) _gameLogoSteamId = 0;
        }
        catch (Exception error) when (IsArtworkError(error) || error is SecurityException) { }
        finally
        {
            if (_gameLogoRequest == request)
            {
                _gameLogoRequest = null;
                if (GameLogo.Source == null) GameTitle.IsVisible = true;
            }
            request.Dispose();
        }

        bool StillSelected() => !_closed && !token.IsCancellationRequested && _gameLogoRequest == request &&
            _page == Page.Game && _game?.SteamId == id && _game.CustomLogoId == customId && _library.Preferences.UseGameLogos;
    }

    private void RememberGameLogo(int id, Bitmap picture)
    {
        var previous = _recentGameLogos.FindIndex(entry => entry.Id == id);
        var retired = previous >= 0 ? _recentGameLogos[previous].Picture : null;
        if (previous >= 0) _recentGameLogos.RemoveAt(previous);
        _recentGameLogos.Add((id, picture));
        _unavailableGameLogos.Remove(id);
        if (_page == Page.Game && _game?.SteamId == id && _library.Preferences.UseGameLogos &&
            (_game.CustomLogoId == null || retired != null && GameLogo.Source == retired))
        {
            _gameLogoRequest?.Cancel();
            _gameLogoRequest = null;
            _gameLogoSteamId = id;
            _gameLogoCustomId = _game.CustomLogoId;
            ShowGameLogo(picture);
        }
        retired?.Dispose();

        if (_recentGameLogos.Count <= RecentGameLogoLimit) return;
        var eviction = _recentGameLogos.FindIndex(entry => entry.Picture != GameLogo.Source);
        _recentGameLogos[eviction].Picture.Dispose();
        _recentGameLogos.RemoveAt(eviction);
    }

    private void ShowGameLogo(Bitmap picture)
    {
        GameLogo.Source = picture;
        GameLogo.IsVisible = true;
        GameTitle.IsVisible = false;
    }

    private Bitmap? ReadCachedGameLogo(int id)
    {
        if (_steam.CachedLogo(id) is { } cached)
        {
            if (ReadGameLogo(cached) is { } picture) return picture;
        }

        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var path in SteamCatalog.LocalLogoPaths(id, ReadSteamDirectory()))
                if (ReadGameLogo(path) is { } picture) return picture;
        }
        catch (Exception error) when (IsArtworkError(error) || error is SecurityException) { }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ReadSteamDirectory()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return String.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
            : path;
    }

    private static Bitmap? ReadGameLogo(string path)
    {
        try { return ReadBitmap(path, 800); }
        catch (Exception error) when (IsArtworkError(error)) { return null; }
    }

    private void ClearGameLogo()
    {
        _gameLogoRequest?.Cancel();
        _gameLogoRequest = null;
        _gameLogoSteamId = 0;
        _gameLogoCustomId = null;
        GameLogo.Source = null;
        _customGameLogo?.Dispose();
        _customGameLogo = null;
        GameLogo.IsVisible = false;
        GameTitle.IsVisible = true;
    }

    private void DisposeGameLogos()
    {
        ClearGameLogo();
        foreach (var logo in _recentGameLogos) logo.Picture.Dispose();
        _recentGameLogos.Clear();
    }
}
