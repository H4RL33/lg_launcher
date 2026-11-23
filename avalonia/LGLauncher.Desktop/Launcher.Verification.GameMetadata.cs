using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LGLauncher.Components;
using SkiaSharp;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private async Task VerifyGameMetadata(string preview)
    {
        VerifyArtworkDecodeLimits(Path.GetDirectoryName(preview)!);
        await VerifySharedSteamHeaderCancellation();
        await VerifyGameMedia(preview);
        var preferences = _library.Preferences;
        var restoreLogos = preferences.UseGameLogos;
        var restoreStoreTags = preferences.ShowStoreTags;
        var restoreSize = new Size(Width, Height);
        var view = new HistoryEntry(new ViewLocation(_page, _collection, _game, _rssOpenArticle), _offset, ContentScroll.Offset);
        var game = new Game { Title = "Logo verification", SteamId = Int32.MaxValue, Genres = ["Action", "Adventure", "Racing", "Simulation"] };
        game.TagSteamId = game.SteamId;
        game.SteamTags = ["Story Rich", "Sci-fi"];
        var cache = Path.Combine(_store.ArtworkDirectory, "logos", game.SteamId.ToString(CultureInfo.InvariantCulture) + ".png");
        Require(!File.Exists(cache), "The reserved logo fixture file already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        using (var image = new SKBitmap(120, 30, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            image.Erase(SKColors.Transparent);
            image.SetPixel(60, 15, SKColors.White);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(cache);
            png.SaveTo(file);
        }

        try
        {
            preferences.UseGameLogos = false;
            preferences.ShowStoreTags = true;
            ShowHistoryEntry(new HistoryEntry(new ViewLocation(Page.Game, Game: game)), animate: false);
            Require(GameTitle.IsVisible && !GameLogo.IsVisible && _gameLogoRequest == null,
                "Disabling game logos did not display the text title without a lookup.");
            Require(GenreChips.Children.Count == 4 && CreateLibrarySearch("genre: Simulation")(game),
                "The fourth genre was missing from details or search.");
            UpdateLayout();
            var appId = SteamLabel.TranslatePoint(default, Details);
            var lastLaunched = LastPlayed.TranslatePoint(default, Details);
            var genres = GenreChips.TranslatePoint(default, Details);
            var tags = StoreTags.TranslatePoint(default, Details);
            Require(SteamAppId.IsVisible && !SteamAppId.IsHitTestVisible && !SteamAppId.Focusable &&
                SteamLabel.Text == Int32.MaxValue.ToString(CultureInfo.InvariantCulture) &&
                SteamAppId.Parent == LastPlayed.Parent?.Parent && SteamLabel.FontWeight == LastPlayed.FontWeight &&
                appId is { } idPoint && lastLaunched is { } playedPoint && idPoint.X > playedPoint.X &&
                Math.Abs(idPoint.Y - playedPoint.Y) < 1 && genres is { } genrePoint && tags is { } tagPoint &&
                genrePoint.Y < tagPoint.Y && tagPoint.Y < idPoint.Y &&
                StoreTags.Children.OfType<TextBlock>().Select(text => text.Text).SequenceEqual(["STORY RICH", "SCI-FI"]),
                "The Steam App ID was not in the stats row after Last Launched, or store tags were out of place.");
            game.SteamTags.AddRange(["Cute", "Action", "Third-Person Shooter", "Funny", "Adventure", "Robots",
                "Futuristic", "Singleplayer", "Action-Adventure", "Third Person", "Hacking", "Space", "3D",
                "Artificial Intelligence", "PvE", "Realistic", "Female Protagonist", "Souls-like"]);
            RenderStoreTags(game);
            UpdateLayout();
            var tagLabels = StoreTags.Children.OfType<TextBlock>().ToArray();
            Require(GameInfo.Bounds.Width <= 510 && GameInfo.Bounds.Left < 35 &&
                Math.Abs(GameInfo.Bounds.Bottom - (Details.Bounds.Height - 36)) < 1 &&
                tagLabels.Length == 20 && tagLabels[^1].Bounds.Y > tagLabels[0].Bounds.Y &&
                tagLabels.All(label => label.Bounds.Right <= StoreTags.Bounds.Width + 1),
                "The bottom-left game details did not limit their width or wrap all the Steam tags.");

            preferences.UseGameLogos = true;
            preferences.ShowStoreTags = false;
            Render();
            await WaitFor(() => _gameLogoRequest == null, "The cached game logo did not finish loading.");
            Require(GameLogo.IsVisible && GameLogo.Source != null && !GameTitle.IsVisible &&
                AutomationProperties.GetName(GameLogo) == game.Title, "The cached logo did not replace the game title accessibly.");
            Require(!StoreTags.IsVisible && game.SteamTagsFor().Count() == 20 && GenreChips.Children.Count == 4,
                "Hiding store tags changed the cached metadata or hid genres.");
            preferences.ShowStoreTags = true;
            var cachedLogo = GameLogo.Source;
            ShowHistoryEntry(view, animate: false);
            ShowHistoryEntry(new HistoryEntry(new ViewLocation(Page.Game, Game: game)), animate: false);
            Require(GameLogo.IsVisible && ReferenceEquals(GameLogo.Source, cachedLogo) && !GameTitle.IsVisible && _gameLogoRequest == null,
                "Revisiting the game did not show its cached logo immediately.");
            Require(RefreshSteamButton.IsEnabled && BuildGameMenu(game, Avalonia.Controls.PlacementMode.Pointer).Items
                .OfType<Avalonia.Controls.MenuItem>().Any(item => Equals(item.Header, "Refresh from Steam") && item.IsEnabled),
                "Steam refresh was unavailable from the game details button or context menu.");
            Require(StoreTags.IsVisible && StoreTags.Children.Count == 20,
                "Re-enabling store tags did not show them again.");
            await CaptureStats("matched");

            game.GenreSteamId = game.SteamId;
            Require(NeedsSteamGenres(game), "A previously capped Steam record did not qualify for a backfill.");
            game.SteamGenresComplete = true;
            Require(!NeedsSteamGenres(game), "A complete Steam record was scheduled for another lookup.");
            game.ManualGenres = true;
            game.SteamGenresComplete = false;
            Require(!NeedsSteamGenres(game), "Manual genres were scheduled for an automatic Steam lookup.");

            game.SteamId = 0;
            Render();
            Require(GameTitle.IsVisible && !GameLogo.IsVisible && GameLogo.Source == null,
                "The previous game's logo remained visible on a local game.");
            Require(!RefreshSteamButton.IsEnabled, "An unmatched game could refresh from Steam.");
            Require(!SteamAppId.IsVisible && SteamLabel.Text == null && !StoreTags.IsVisible,
                "A local game retained metadata for a previous Steam app.");
            await CaptureStats("unmatched");
        }
        finally
        {
            preferences.UseGameLogos = restoreLogos;
            preferences.ShowStoreTags = restoreStoreTags;
            Width = restoreSize.Width;
            Height = restoreSize.Height;
            ShowHistoryEntry(view, animate: false);
            File.Delete(cache);
        }

        async Task CaptureStats(string match)
        {
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(85);
                Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"game-stats-{match}-{size.Width}.png"));
            }
        }
    }

    private async Task VerifySharedSteamHeaderCancellation()
    {
        const int steamId = Int32.MaxValue - 1;
        var key = (steamId, false);
        using var artwork = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96));
        using var details = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
        var shared = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cardImageRequests.Add(key, shared.Task);
        try
        {
            var detailsWait = EnsureHeader(steamId, details.Token);
            var cardWait = EnsureHeader(steamId, _background.Token);
            details.Cancel();

            var released = await Task.WhenAny(detailsWait, Task.Delay(TimeSpan.FromSeconds(2)));
            Require(released == detailsWait && await detailsWait == null,
                "Leaving Game Details did not release its wait for a shared Steam header.");
            Require(!cardWait.IsCompleted, "Leaving Game Details interrupted the card's wait for the Steam header.");

            shared.SetResult(artwork);
            Require(ReferenceEquals(await cardWait, artwork), "The card did not receive the shared Steam header.");
        }
        finally
        {
            shared.TrySetResult(null);
            _cardImageRequests.Remove(key);
        }
    }

    private static void VerifyArtworkDecodeLimits(string folder)
    {
        var path = Path.Combine(folder, "artwork-decode-fixture.png");
        try
        {
            WriteImage(48, 1200);
            using (var thumbnail = ReadBitmap(path, 420))
                Require(thumbnail.PixelSize.Height == 420 && thumbnail.PixelSize.Width > 0,
                    "A narrow source exceeded the thumbnail height limit.");

            WriteImage(1, 1200);
            using (var narrow = ReadBitmap(path, 420))
                Require(narrow.PixelSize == new PixelSize(1, 420), "A very narrow thumbnail lost its short side.");

            WriteImage(600, 900);
            using (var portrait = ReadBitmap(path, PortraitBitmapWidth, PortraitBitmapHeight))
                Require(portrait.PixelSize == new PixelSize(420, 630), "A 2:3 portrait lost its intended thumbnail resolution.");
            Require(CanDecodeSteamArtwork(path), "The Steam cache validator rejected a complete image.");
            using (var truncated = File.OpenWrite(path)) truncated.SetLength(48);
            Require(!CanDecodeSteamArtwork(path), "The Steam cache validator accepted a truncated image.");

            WriteImage(16385, 1);
            Require(!CanDecodeSteamArtwork(path), "The Steam cache validator accepted an oversized image.");
            try
            {
                using var oversized = ReadBitmap(path, 420);
                throw new InvalidOperationException("The artwork decoder accepted an oversized source dimension.");
            }
            catch (InvalidDataException) { }
        }
        finally { File.Delete(path); }

        void WriteImage(int width, int height)
        {
            using var image = new SKBitmap(width, height);
            image.Erase(SKColors.White);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            png.SaveTo(file);
        }
    }

    private async Task VerifyGameMedia(string preview)
    {
        var view = new HistoryEntry(new ViewLocation(_page, _collection, _game, _rssOpenArticle), _offset, ContentScroll.Offset);
        var manual = new GameCollection { Name = "Media mosaic" };
        var dynamic = new GameCollection { Name = "Media mosaic by genre", GenreFilters = ["Media fixture"] };
        var game = new Game
        {
            Title = "Custom media verification", Path = Path.Combine(Program.DataDirectory, "fixture-game.exe"),
            CollectionIds = [manual.Id], Genres = ["Media fixture"]
        };
        var fixture = Path.Combine(Program.DataDirectory, "metadata-verification");
        Directory.CreateDirectory(fixture);
        var imagePath = Path.Combine(fixture, "custom-image.png");
        var badPath = Path.Combine(fixture, "invalid.png");
        var audioPath = Path.Combine(fixture, "theme.wav");
        var customDirectory = Path.Combine(_store.ArtworkDirectory, "custom");
        using (var image = new SKBitmap(120, 60))
        {
            image.Erase(SKColors.DarkCyan);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(imagePath);
            png.SaveTo(file);
        }
        File.WriteAllText(badPath, "This is not an image.");
        WriteSilentTheme(audioPath);
        var player = new ThemeMusicPlayer();
        try { Require(player.Play(audioPath), "The Windows theme music player could not start a silent WAV fixture."); }
        finally { player.Stop(); }
        await VerifyThemeMusicTransitions(audioPath);
        foreach (var extension in new[] { ".mp3", ".ogg", ".opus" })
        {
            var sample = Path.Combine(Program.DataDirectory, "theme-audio-samples", "tone" + extension);
            var destination = Path.Combine(fixture, "imported-" + extension[1..] + ThemeAudioImport.StorageExtension(sample));
            ImportGameMediaFile(sample, destination, null, CancellationToken.None);
            var decodedLength = new FileInfo(destination).Length;
            Require(extension == ".mp3" ? decodedLength == new FileInfo(sample).Length : decodedLength == 12044,
                $"The {extension} fixture did not preserve the track length.");
        }
        var steam = Path.Combine(fixture, "Steam");
        var album = Path.Combine(steam, "steamapps", "music", game.Title + " Soundtrack");
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"path\" \"{steam.Replace('\\', '/')}/\"");
        var theme = Path.Combine(album, "01 - Main Theme.wav");
        File.Copy(audioPath, theme, overwrite: true);
        Require(FindInstalledSteamTheme(steam, game.Title, CancellationToken.None) == theme &&
            FindInstalledSteamTheme(steam, "Other game", CancellationToken.None) == null,
            "Installed Steam music did not match the game's album and main theme.");
        var otherTheme = Path.Combine(album, "02 - Title Theme.wav");
        File.Copy(audioPath, otherTheme, overwrite: true);
        Require(FindInstalledSteamTheme(steam, game.Title, CancellationToken.None) == null, "An ambiguous theme was chosen automatically.");
        var initialFiles = Directory.Exists(customDirectory) ? Directory.GetFiles(customDirectory).Length : 0;
        var showLogos = _library.Preferences.UseGameLogos;
        _library.Preferences.UseGameLogos = true;
        File.SetAttributes(imagePath, FileAttributes.ReadOnly);
        _library.Games.Add(game);
        _library.Collections.AddRange([manual, dynamic]);

        try
        {
            ShowHistoryEntry(new HistoryEntry(new ViewLocation(Page.Home)), animate: false);
            UpdateLayout();
            Require(MosaicImages() is { Length: 2 } emptyMosaics && emptyMosaics.All(image => image.Source == null),
                "A collection preview unexpectedly found artwork before a portrait was imported.");
            var menu = BuildGameMenu(game, PlacementMode.Pointer);
            var manage = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Manage metadata"));
            manage.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitFor(() => DialogHost.HasModal, "The game context menu did not open Manage metadata.");
            UpdateLayout();
            var fields = DialogHost.GetVisualDescendants().OfType<MediaFileField>().ToDictionary(field => field.FileLabel);
            fields["Backdrop artwork"].SelectFile(imagePath);
            fields["Portrait artwork"].SelectFile(imagePath);
            fields["Game logo"].SelectFile(badPath);
            fields["Theme music"].SelectFile(audioPath);
            var submit = DialogHost.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes"));
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => submit.IsEnabled && DialogHost.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text?.StartsWith("Game logo could not be imported", StringComparison.Ordinal) == true),
                "An invalid image did not report an import error.");
            Require(GameMedia.From(game) == new GameMedia(default, default, default, default) && Directory.GetFiles(customDirectory).Length == initialFiles,
                "An incomplete metadata import left staged files or changed the profile.");

            fields["Game logo"].SelectFile(imagePath);
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !DialogHost.HasModal, "Valid game metadata did not save.");
            var saved = _store.Load().Games.Single(entry => entry.Id == game.Id);
            Require(GameMedia.From(saved) == GameMedia.From(game) && File.Exists(_store.CustomArtworkPath(saved.CustomArtworkId)) &&
                File.Exists(_store.CustomArtworkPath(saved.CustomPortraitId)) && File.Exists(_store.CustomArtworkPath(saved.CustomLogoId)) &&
                File.Exists(_store.ThemeMusicPath(saved.ThemeMusicFile)), "Custom game media did not survive a profile reload.");
            Require(saved.CustomArtworkFileName == "custom-image.png" && saved.CustomPortraitFileName == "custom-image.png" &&
                saved.CustomLogoFileName == "custom-image.png" && saved.ThemeMusicFileName == "theme.wav",
                "The original basenames did not survive a profile reload.");
            await WaitFor(() => MosaicImages() is { Length: 2 } mosaics && mosaics.All(image => image.Source != null),
                "Visible manual and dynamic collection mosaics did not pick up the newly imported portrait.");
            var size = new Size(Width, Height);
            var captures = Path.GetDirectoryName(preview)!;
            foreach (var viewport in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                (Width, Height) = (viewport.Width, viewport.Height);
                await Task.Delay(50);
                Capture(Path.Combine(captures, $"collection-custom-portrait-{viewport.Width}.png"));
            }
            (Width, Height) = (size.Width, size.Height);
            ShowHistoryEntry(new HistoryEntry(new ViewLocation(Page.Game, Game: game)), animate: false);
            await WaitFor(() => _detailsRequest == null && _gameLogoRequest == null, "Imported artwork and logo did not finish loading.");
            var card = BuildGameCard(game);
            await WaitFor(() => card.Artwork != null, "An unmatched game did not use its custom portrait.");
            Require(_artwork.Picture != null && GameLogo.IsVisible && !GameTitle.IsVisible, "An unmatched game did not use its custom backdrop and logo.");

            ShowHistoryEntry(new HistoryEntry(new ViewLocation(Page.Home)), animate: false);
            var clearing = ShowGameMetadata(game);
            UpdateLayout();
            var fileLabels = DialogHost.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Require(fileLabels.Count(text => text == "custom-image.png") == 3 && fileLabels.Contains("theme.wav"),
                "Reopening Manage metadata did not show the imported filenames.");
            Capture(Path.Combine(captures, "manage-metadata-saved-filenames.png"));
            var buttons = DialogHost.GetVisualDescendants().OfType<Button>().ToArray();
            foreach (var button in buttons.Where(button => Equals(button.Content, "Clear")))
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons.Single(button => Equals(button.Content, "Save changes")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await clearing;
            Require(GameMedia.From(_store.Load().Games.Single(entry => entry.Id == game.Id)) == new GameMedia(default, default, default, default),
                "Clearing custom media did not restore the automatic settings.");
            await WaitFor(() => MosaicImages() is { Length: 2 } mosaics && mosaics.All(image => image.Source == null),
                "Clearing the imported portrait did not refresh its collection mosaics.");
        }
        finally
        {
            DialogHost.CloseAll();
            File.SetAttributes(imagePath, FileAttributes.Normal);
            _library.Preferences.UseGameLogos = showLogos;
            _library.Games.Remove(game);
            _library.Collections.Remove(manual);
            _library.Collections.Remove(dynamic);
            ShowHistoryEntry(view, animate: false);
            Require(Save(), "Could not remove the game media verification fixture from the library.");
        }

        Image[] MosaicImages() => PageBody.GetVisualDescendants().OfType<CollectionCard>()
            .Where(card => card.Title == manual.Name || card.Title == dynamic.Name)
            .SelectMany(card => ((Panel)card.Content!).GetVisualDescendants().OfType<Image>()).ToArray();

        static void WriteSilentTheme(string path)
        {
            const int samples = 8000;
            using var audio = new BinaryWriter(File.Create(path));
            audio.Write("RIFF"u8);
            audio.Write(36 + samples * 2);
            audio.Write("WAVEfmt "u8);
            audio.Write(16);
            audio.Write((short)1);
            audio.Write((short)1);
            audio.Write(samples);
            audio.Write(samples * 2);
            audio.Write((short)2);
            audio.Write((short)16);
            audio.Write("data"u8);
            audio.Write(samples * 2);
            audio.Write(new byte[samples * 2]);
        }
    }

    private async Task VerifyThemeMusicTransitions(string sample)
    {
        var theme = Guid.NewGuid().ToString("N") + ".wav";
        var path = _store.ThemeMusicPath(theme) ?? throw new InvalidOperationException("Could not resolve the WAV fixture path.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(sample, path);
        var first = new Game { Title = "First theme", ThemeMusicFile = theme };
        var second = new Game { Title = "Second theme", ThemeMusicFile = theme };
        var noTheme = new Game { Title = "No theme" };

        try
        {
            SelectThemeMusic(first);
            await Task.Delay(700);
            Require(!_themeMusic.IsPlaying, "Theme music started before the selection dwell elapsed.");
            await WaitFor(() => _themeMusic.Volume is > 0 and < .3f, "The first theme did not fade in.");
            await WaitFor(() => _themeMusic.Volume == .3f, "The first theme did not reach its usual volume.");

            SelectThemeMusic(second);
            Require(_themeMusic.IsPlaying, "Switching games stopped the first theme without fading out.");
            await WaitFor(() => _themeMusic.Volume is > 0 and < .3f, "The first theme did not fade out.");
            SelectThemeMusic(noTheme);
            await WaitFor(() => !_themeMusic.IsPlaying, "The first theme did not stop at the end of the fade.");
            await Task.Delay(1000);
            Require(!_themeMusic.IsPlaying, "A skipped game's delayed theme started playing.");

            SelectThemeMusic(first);
            await WaitFor(() => _themeMusic.IsPlaying, "Theme playback did not resume for a new selection.");
            SelectThemeMusic(second);
            StopThemeMusic();
            Require(!_themeMusic.IsPlaying, "Stopping the theme left the outgoing player active.");
            await Task.Delay(1100);
            Require(!_themeMusic.IsPlaying, "A stopped theme started after its selection delay.");
        }
        finally
        {
            StopThemeMusic();
            File.Delete(path);
        }
    }
}
