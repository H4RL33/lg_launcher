using System.Security;
using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly Regex SteamLibraryPath = new("^\\s*\"path\"\\s*\"(?<path>[^\"]+)\"", RegexOptions.Multiline);
    private static readonly Regex TrackNumber = new(@"^\s*\d{1,3}[\s.\-_]+", RegexOptions.CultureInvariant);
    private readonly ThemeMusicPlayer _themeMusic = new();
    private ThemeMusicSelection? _themeMusicSelection;
    private CancellationTokenSource? _themeMusicTransition;

    private void UpdateThemeMusic()
    {
        if (!OperatingSystem.IsWindows() || _verification || !_ready || _closed || !IsVisible || !IsActive ||
            WindowState == WindowState.Minimized || !_library.Preferences.PlayThemeMusic || _playing.Count > 0 ||
            _page != Page.Game || _game is not { } game)
        {
            StopThemeMusic();
            return;
        }

        SelectThemeMusic(game);
    }

    private void SelectThemeMusic(Game game)
    {
        var selection = new ThemeMusicSelection(game.Id, game.Title, game.SteamId, game.ThemeMusicFile);
        if (selection == _themeMusicSelection) return;

        _themeMusicTransition?.Cancel();
        _themeMusicSelection = selection;
        _themeMusic.FadeOut();
        _ = StartThemeMusic(selection);
    }

    private async Task StartThemeMusic(ThemeMusicSelection selection)
    {
        using var transition = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
        _themeMusicTransition = transition;
        var token = transition.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            token.ThrowIfCancellationRequested();
            if (_store.ThemeMusicPath(selection.CustomFile) is { } localFile && _themeMusic.Play(localFile)) return;
            if (selection.SteamId <= 0 || !OperatingSystem.IsWindows()) return;

            var steamDirectory = ReadSteamDirectory();
            var music = await Task.Run(() => FindInstalledSteamTheme(steamDirectory, selection.Title, token), token);
            token.ThrowIfCancellationRequested();
            if (music != null) _themeMusic.Play(music);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsArtworkError(error) || error is SecurityException) { }
        finally
        {
            if (_themeMusicTransition == transition) _themeMusicTransition = null;
        }
    }

    private void StopThemeMusic()
    {
        _themeMusicTransition?.Cancel();
        _themeMusicTransition = null;
        _themeMusicSelection = null;
        _themeMusic.Stop();
    }

    private static string? FindInstalledSteamTheme(string steamDirectory, string title, CancellationToken token)
    {
        var gameName = Normalize(title ?? "");
        if (gameName.Length == 0) return null;

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLibraryRoot(steamDirectory);
        foreach (var settings in new[] { Path.Combine(steamDirectory, "steamapps", "libraryfolders.vdf"), Path.Combine(steamDirectory, "config", "libraryfolders.vdf") })
        {
            if (!File.Exists(settings)) continue;
            foreach (Match match in SteamLibraryPath.Matches(File.ReadAllText(settings)))
                AddLibraryRoot(match.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal));
        }

        var trackNames = new HashSet<string>(["maintheme", "titletheme", "maintitletheme", gameName, gameName + "maintheme", gameName + "theme"], StringComparer.Ordinal);
        var albumNames = new HashSet<string>([gameName, gameName + "ost", gameName + "soundtrack", gameName + "originalsoundtrack", gameName + "officialsoundtrack"], StringComparer.Ordinal);
        string? matchedTrack = null;
        foreach (var root in roots)
        {
            var library = Path.Combine(root, "steamapps", "music");
            if (!Directory.Exists(library)) continue;
            foreach (var album in Directory.EnumerateDirectories(library))
            {
                token.ThrowIfCancellationRequested();
                if (!albumNames.Contains(Normalize(Path.GetFileName(album)))) continue;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var path in Directory.EnumerateFiles(album, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    var extension = Path.GetExtension(path);
                    if (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!trackNames.Contains(Normalize(TrackNumber.Replace(Path.GetFileNameWithoutExtension(path), "")))) continue;
                    if (matchedTrack != null) return null;
                    matchedTrack = path;
                }
            }
        }
        return matchedTrack;

        void AddLibraryRoot(string path)
        {
            if (Path.IsPathFullyQualified(path)) roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }

        static string Normalize(string value) => new(value.Where(Char.IsLetterOrDigit).Select(Char.ToLowerInvariant).ToArray());
    }

    private sealed record ThemeMusicSelection(string Id, string Title, int SteamId, string? CustomFile);
}
