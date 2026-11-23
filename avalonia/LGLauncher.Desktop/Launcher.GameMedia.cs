using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly FilePickerFileType AudioFiles = new("Theme music (MP3, OGG, Opus, WAV)") { Patterns = ["*.mp3", "*.ogg", "*.opus", "*.wav"] };

    private async Task ShowGameMetadata(Game game)
    {
        if (!_library.Games.Contains(game)) return;
        var gameTitle = Label(game.Title, muted: true);
        gameTitle.TextWrapping = TextWrapping.NoWrap;
        gameTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(gameTitle, game.Title);
        var dialog = new ModalDialog("Manage metadata", 540, headingTrailing: gameTitle)
        {
            MaxWidth = 540,
            Width = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
        var token = lifetime.Token;
        dialog.Closed += (_, _) => lifetime.Cancel();
        var initial = GameMedia.From(game);
        var imagesDefault = game.SteamId > 0 ? "Automatic Steam artwork" : "No custom artwork";
        var backdrop = FieldFor("Backdrop artwork", imagesDefault, initial.Backdrop, Images, "1920 × 1080 px recommended (16:9)");
        var portrait = FieldFor("Portrait artwork", imagesDefault, initial.Portrait, Images, "600 × 900 px recommended (2:3)");
        var logo = FieldFor("Game logo", game.SteamId > 0 ? "Automatic Steam logo" : "Use the game title", initial.Logo, Images,
            "Wide transparent PNG recommended (e.g. 720 × 192 px)");
        var music = FieldFor("Theme music", game.SteamId > 0 ? "Use an installed Steam soundtrack when matched" : "No theme music", initial.Music, AudioFiles);
        var fields = new StackPanel { Spacing = 13, Children = { backdrop, portrait, logo, music } };
        dialog.Body.Children.Add(fields);
        var message = Label("", muted: true);
        message.IsVisible = false;
        dialog.Body.Children.Add(message);

        Button? submit = null;
        submit = DialogActions(dialog, "Save changes", async () =>
        {
            if (submit == null || !submit.IsEnabled) return;
            var choices = new MediaChoices(Choice(backdrop), Choice(portrait), Choice(logo), Choice(music));
            if (!choices.HasChanges)
            {
                dialog.Close(true);
                return;
            }

            submit.IsEnabled = fields.IsEnabled = false;
            message.IsVisible = true;
            message.Text = "Importing files…";
            try
            {
                if (await SaveGameMedia(game, choices, token))
                {
                    dialog.Close(true);
                    Render();
                    Status("Game metadata updated.");
                }
                else if (!token.IsCancellationRequested)
                    message.Text = "The metadata could not be saved. Check that your profile is writable and try again.";
            }
            catch (Exception error) when (IsArtworkError(error))
            {
                if (!token.IsCancellationRequested) message.Text = error.Message;
            }
            finally
            {
                if (!token.IsCancellationRequested) submit.IsEnabled = fields.IsEnabled = true;
            }
        });
        await ShowModal(dialog);

        MediaFileField FieldFor(string label, string defaultLabel, MediaAsset existing, FilePickerFileType format, string? advice = null) =>
            new(label, defaultLabel, existing.Id != null, existing.FileName, advice, async () =>
            {
                try
                {
                    var path = await PickFile(this, "Choose " + label.ToLowerInvariant(), format, null);
                    return token.IsCancellationRequested ? null : path;
                }
                catch (Exception error) when (IsArtworkError(error))
                {
                    if (!token.IsCancellationRequested) await Notice("Could not open file picker", error.Message);
                    return null;
                }
            });

        static MediaChoice Choice(MediaFileField field) => new(field.SelectedPath, field.ResetRequested);
    }

    private async Task<bool> SaveGameMedia(Game game, MediaChoices choices, CancellationToken token)
    {
        if (_replacingArtwork) return false;
        _replacingArtwork = true;
        var original = GameMedia.From(game);
        var originalPosition = game.ArtworkPosition;
        var stagedFiles = new List<string>();
        var committed = false;
        try
        {
            var updated = await Task.Run(() => new GameMedia(
                Import(choices.Backdrop, original.Backdrop, "Backdrop artwork", 2560),
                Import(choices.Portrait, original.Portrait, "Portrait artwork", PortraitBitmapWidth, PortraitBitmapHeight),
                Import(choices.Logo, original.Logo, "Game logo", 800),
                Import(choices.Music, original.Music, "Theme music", null)), token);
            if (token.IsCancellationRequested || _closed || !_library.Games.Contains(game)) return false;
            updated.Apply(game);
            if (updated.Backdrop.Id != original.Backdrop.Id) game.ArtworkPosition = null;
            var previouslyUnsaved = _saveFailed;
            committed = Save();
            if (!committed)
            {
                original.Apply(game);
                game.ArtworkPosition = originalPosition;
                _saveFailed = previouslyUnsaved;
            }
            return committed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        finally
        {
            _replacingArtwork = false;
            if (!committed)
            {
                foreach (var destination in stagedFiles)
                {
                    try { File.Delete(destination); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        MediaAsset Import(MediaChoice choice, MediaAsset current, string label, int? maximumWidth, int? maximumHeight = null)
        {
            token.ThrowIfCancellationRequested();
            if (choice.Reset) return default;
            if (choice.Source == null) return current;
            var id = Guid.NewGuid().ToString("N");
            var audio = !maximumWidth.HasValue;
            if (audio) id += ThemeAudioImport.StorageExtension(choice.Source);
            var destination = audio ? _store.ThemeMusicPath(id) : _store.CustomArtworkPath(id);
            if (destination == null) throw new InvalidDataException("Could not prepare profile storage for " + label.ToLowerInvariant() + ".");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                stagedFiles.Add(destination);
                ImportGameMediaFile(choice.Source, destination, maximumWidth, token, maximumHeight);
                return new MediaAsset(id, Path.GetFileName(choice.Source));
            }
            catch (Exception error) when (IsArtworkError(error))
            {
                throw new InvalidDataException($"{label} could not be imported. {error.Message}", error);
            }
        }
    }

    private static void ImportGameMediaFile(string source, string destination, int? maximumImageWidth, CancellationToken token,
        int? maximumImageHeight = null)
    {
        if (maximumImageWidth == null && ThemeAudioImport.NeedsDecoding(source))
        {
            ThemeAudioImport.DecodeOgg(source, destination, token);
        }
        else
        {
            using var input = File.OpenRead(source);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }
        token.ThrowIfCancellationRequested();
        if (maximumImageWidth is { } width)
        {
            using var picture = ReadBitmap(destination, width, maximumImageHeight);
        }
        else if (!ThemeMusicPlayer.CanOpen(destination))
        {
            throw new InvalidDataException("Windows could not open the audio file.");
        }
    }

    private sealed record GameMedia(MediaAsset Backdrop, MediaAsset Portrait, MediaAsset Logo, MediaAsset Music)
    {
        internal static GameMedia From(Game game) => new(
            new MediaAsset(game.CustomArtworkId, game.CustomArtworkFileName),
            new MediaAsset(game.CustomPortraitId, game.CustomPortraitFileName),
            new MediaAsset(game.CustomLogoId, game.CustomLogoFileName),
            new MediaAsset(game.ThemeMusicFile, game.ThemeMusicFileName));

        internal void Apply(Game game)
        {
            (game.CustomArtworkId, game.CustomArtworkFileName) = Backdrop;
            (game.CustomPortraitId, game.CustomPortraitFileName) = Portrait;
            (game.CustomLogoId, game.CustomLogoFileName) = Logo;
            (game.ThemeMusicFile, game.ThemeMusicFileName) = Music;
        }
    }

    private readonly record struct MediaAsset(string? Id, string? FileName);

    private readonly record struct MediaChoices(MediaChoice Backdrop, MediaChoice Portrait, MediaChoice Logo, MediaChoice Music)
    {
        internal bool HasChanges => Backdrop.Changed || Portrait.Changed || Logo.Changed || Music.Changed;
    }

    private readonly record struct MediaChoice(string? Source, bool Reset)
    {
        internal bool Changed => Source != null || Reset;
    }
}
