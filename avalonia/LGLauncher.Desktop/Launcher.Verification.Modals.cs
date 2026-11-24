using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private async Task VerifyLauncherModals(Game game, string preview)
    {
        await VerifyAutomaticUpdatePrompt(preview);
        SelectGame(game);
        EditGameButton.Focus();
        var steamResult = new TaskCompletionSource<List<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedSteamId = 0;
        var originalGenres = game.Genres?.ToArray() ?? [];
        var editing = ShowGameEditor(game, false, (id, _) =>
        {
            requestedSteamId = id;
            return steamResult.Task;
        });
        UpdateLayout();
        Require(DialogHost.HasModal && !MainShell.IsEffectivelyEnabled, "The game editor did not block the launcher behind it.");
        var fields = DialogHost.GetVisualDescendants().OfType<TextBox>().ToArray();
        Require(fields.Length == 6 && fields.All(field => field.VerticalContentAlignment == VerticalAlignment.Center),
            "Expected six vertically centered Edit game text fields. Found " +
            String.Join(", ", fields.Select(field => $"{AutomationProperties.GetName(field)} ({field.VerticalContentAlignment})")) + ".");
        var title = fields.Single(field => AutomationProperties.GetName(field) == "Game title");
        var app = fields.Single(field => AutomationProperties.GetName(field) == "Steam App ID");
        var genres = fields.Single(field => AutomationProperties.GetName(field) == "Genres");
        Require(fields.Any(field => AutomationProperties.GetName(field) == "Tags") &&
            DialogHost.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Use Steam tags")),
            "The game editor did not offer manual and Steam tags.");
        await WaitFor(() => title.IsFocused, "Opening Edit game did not focus the title.");
        var useSteam = DialogHost.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Use Steam genres"));
        app.Text = "invalid";
        useSteam.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(requestedSteamId == 0 && genres.Text == String.Join(", ", originalGenres), "An invalid Steam ID changed the genres or triggered a lookup.");
        app.Text = "4200";
        useSteam.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(requestedSteamId == 4200 && genres.Text == String.Join(", ", originalGenres), "The genre action did not use the current App ID or cleared the field while loading.");
        steamResult.SetResult(["Action", "Adventure", "Racing", "Simulation"]);
        await WaitFor(() => genres.Text == "Action, Adventure, Racing, Simulation", "The complete Steam genres were not populated before saving.");
        Require((game.Genres ?? []).SequenceEqual(originalGenres), "A Steam lookup changed the saved game before confirmation.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "edit-game-modal.png"));

        var manage = DialogHost.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Manage metadata"));
        var restoreSize = (Width, Height);
        manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UpdateLayout();
        var metadata = DialogHost.GetVisualDescendants().OfType<ModalDialog>()
            .Single(dialog => AutomationProperties.GetName(dialog) == "Manage metadata");
        var labels = metadata.GetVisualDescendants().OfType<TextBlock>().ToArray();
        var heading = labels.Single(text => text.Text == "Manage metadata");
        var gameTitle = labels.Single(text => text.Text == game.Title);
        var uploads = metadata.GetVisualDescendants().OfType<MediaFileField>().ToArray();
        foreach (var size in new[] { (730, 510), (1230, 805), (1600, 900) })
        {
            (Width, Height) = size;
            await Task.Delay(85);
            var headingPosition = heading.TranslatePoint(default, metadata)!.Value;
            var gameTitlePosition = gameTitle.TranslatePoint(default, metadata)!.Value;
            Require(gameTitlePosition.X >= headingPosition.X + heading.Bounds.Width &&
                Math.Abs(metadata.Bounds.Width - gameTitlePosition.X - gameTitle.Bounds.Width - headingPosition.X) < 1 &&
                VerticallyCentred(heading, gameTitle, metadata), "The game title was not aligned at the right of the metadata heading.");
            foreach (var field in uploads.Take(3))
            {
                var fieldLabels = field.GetVisualDescendants().OfType<TextBlock>().ToArray();
                var fieldHeading = fieldLabels.Single(text => text.Text == field.FileLabel);
                var recommendation = fieldLabels.Single(text => text.Text?.Contains("recommended", StringComparison.Ordinal) == true);
                Require(VerticallyCentred(fieldHeading, recommendation, field),
                    $"The recommendation for {field.FileLabel} was not centred beside its heading.");
            }
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"manage-metadata-{size.Item1}.png"));
        }
        Require(uploads.Select(field => field.FileLabel).SequenceEqual(["Backdrop artwork", "Portrait artwork", "Game logo", "Theme music"]),
            "Manage metadata did not show upload controls for all supported media.");
        var advice = uploads.Take(3).Select(field => field.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text?.Contains("recommended", StringComparison.Ordinal) == true).Text).ToArray();
        Require(advice[0]!.Contains("16:9", StringComparison.Ordinal) && advice[1]!.Contains("2:3", StringComparison.Ordinal) &&
            advice[2]!.Contains("transparent PNG", StringComparison.Ordinal), "The image fields did not explain their recommended sizes or format.");
        metadata.Close();
        Require(DialogHost.HasModal && title.Text == game.Title && app.Text == "4200", "Managing metadata discarded pending game edits.");
        (Width, Height) = restoreSize;
        UpdateLayout();

        genres.Focus();
        var confirming = Confirm("Test nested confirmation", "The editor should stay open after this closes.");
        UpdateLayout();
        var dismiss = DialogHost.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyEnabled && Equals(button.Content, "Cancel"));
        await WaitFor(() => dismiss.IsFocused, "The nested confirmation did not focus Cancel.");
        dismiss.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        Require(!await confirming && DialogHost.HasModal && genres.IsFocused, "Dismissing a nested confirmation did not restore the game editor's focus.");
        genres.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        await editing;
        Require(!DialogHost.HasModal && MainShell.IsEffectivelyEnabled && EditGameButton.IsFocused && (game.Genres ?? []).SequenceEqual(originalGenres),
            "Cancelling Edit game did not discard the fetched values and return focus to the launcher.");
        await VerifyGameEditorSaveFailure(game, preview);

        static bool VerticallyCentred(Control first, Control second, Control relativeTo)
        {
            var firstCentre = first.TranslatePoint(new Point(0, first.Bounds.Height / 2), relativeTo);
            var secondCentre = second.TranslatePoint(new Point(0, second.Bounds.Height / 2), relativeTo);
            return firstCentre is { } firstPoint && secondCentre is { } secondPoint && Math.Abs(firstPoint.Y - secondPoint.Y) < 1;
        }
    }

    private async Task VerifyGameEditorSaveFailure(Game game, string preview)
    {
        const int steamId = 4200;
        await WaitFor(() => _scan == null, "The folder scan did not finish before the save failure checks.");
        var libraryPath = Path.Combine(Program.DataDirectory, "library.json");
        var discovery = _discoveries.Single(item => Path.GetFileName(item.Path) == "New Discovery.exe");
        var saved = File.ReadAllText(libraryPath);
        var originalGames = _library.Games.ToArray();
        var originalDiscoveries = _discoveries.ToArray();
        var originalIgnored = _library.IgnoredPaths.ToArray();
        var originalDetails = (game.Title, game.Path, game.Arguments, game.SteamId, game.Genres, game.ManualGenres,
            game.GenreSteamId, game.SteamGenresComplete, game.SteamTags, game.TagSteamId);
        var originalSaveFailed = _saveFailed;
        var originalGenreAttempt = _attemptedGenres.Contains(steamId);
        var originalTagAttempt = _attemptedTags.Contains(steamId);
        var originalSize = (Width, Height);
        var originalWatcher = _watcher;
        var added = new Game { Title = discovery.Title, Path = discovery.Path };
        var retrySaved = false;

        try
        {
            // Keep the fixture's overlapping ignored and discovered path stable while the dialog is open.
            _watcher = null;
            _library.IgnoredPaths.Add(discovery.Path);
            _attemptedGenres.Add(steamId);
            _attemptedTags.Add(steamId);
            foreach (var adding in new[] { true, false })
            {
                _saveFailed = !adding;
                var editing = ShowGameEditor(adding ? added : game, adding, (_, _) => Task.FromResult<List<string>?>(null));
                UpdateLayout();
                var dialog = DialogHost.GetVisualDescendants().OfType<ModalDialog>().Single();
                var title = Field("Game title");
                var path = Field("Executable");
                var arguments = Field("Launch arguments");
                var app = Field("Steam App ID");
                var genres = Field("Genres");
                title.Text = "Save failure fixture";
                path.Text = discovery.Path;
                arguments.Text = "--fixture";
                app.Text = steamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                genres.Text = "";

                using (File.Open(libraryPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var sizes = adding ? new[] { originalSize } : new[] { (730d, 510d), (1230d, 805d), (1600d, 900d) };
                    foreach (var size in sizes)
                    {
                        (Width, Height) = size;
                        await Task.Delay(65);
                        dialog.DefaultButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(DialogHost.HasModal && !editing.IsCompleted,
                            "The game editor closed after a failed save.");
                        Require(title.Text == "Save failure fixture" && path.Text == discovery.Path && arguments.Text == "--fixture" &&
                            app.Text == "4200" && genres.Text == "", "The editor discarded the unsaved draft.");
                        Require(originalDetails == (game.Title, game.Path, game.Arguments, game.SteamId, game.Genres, game.ManualGenres,
                                game.GenreSteamId, game.SteamGenresComplete, game.SteamTags, game.TagSteamId) &&
                            _library.Games.SequenceEqual(originalGames) && File.ReadAllText(libraryPath) == saved,
                            "A failed game save changed the library.");
                        Require(_discoveries.SequenceEqual(originalDiscoveries) && _library.IgnoredPaths.SequenceEqual(originalIgnored.Append(discovery.Path)),
                            "A failed game save changed discoveries or ignored paths.");
                        Require(_saveFailed == !adding && _attemptedGenres.Contains(steamId) && _attemptedTags.Contains(steamId),
                            "A failed game save changed the previous save failure or metadata retry state.");
                        await WaitFor(() => dialog.Body.Children.OfType<TextBlock>().Any(label => label.Text?.Contains("try again", StringComparison.Ordinal) == true),
                            "The editor did not explain how to retry a failed save.");
                        if (!adding)
                        {
                            await Task.Delay(65);
                            Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"edit-game-save-failure-{size.Item1}.png"));
                        }
                    }
                }

                if (adding) dialog.Close();
                else dialog.DefaultButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!DialogHost.HasModal, "The game editor did not close after a successful retry or cancellation.");
                await editing;
                if (adding) continue;
                retrySaved = true;
                var persisted = _store.Load().Games.Single(entry => entry.Id == game.Id);
                Require(persisted.Title == "Save failure fixture" && persisted.Path == discovery.Path && persisted.Arguments == "--fixture" &&
                    persisted.SteamId == steamId && !_discoveries.Contains(discovery) && !_library.IgnoredPaths.Contains(discovery.Path) &&
                    !_attemptedGenres.Contains(steamId) && !_attemptedTags.Contains(steamId) && !_saveFailed,
                    "Retrying a game save did not persist the draft and clear the matching discovery.");

                TextBox Field(string name) => dialog.GetVisualDescendants().OfType<TextBox>()
                    .Single(field => AutomationProperties.GetName(field) == name);
            }
        }
        finally
        {
            (Width, Height) = originalSize;
            _library.Games.Remove(added);
            (game.Title, game.Path, game.Arguments, game.SteamId, game.Genres, game.ManualGenres, game.GenreSteamId,
                game.SteamGenresComplete, game.SteamTags, game.TagSteamId) = originalDetails;
            _library.IgnoredPaths = [.. originalIgnored];
            _discoveries = [.. originalDiscoveries];
            _watcher = originalWatcher;
            if (!originalGenreAttempt) _attemptedGenres.Remove(steamId);
            else _attemptedGenres.Add(steamId);
            if (!originalTagAttempt) _attemptedTags.Remove(steamId);
            else _attemptedTags.Add(steamId);
            if (retrySaved)
            {
                InvalidateEntry(game);
                Require(Save(), "The game editor fixture could not restore the library.");
                Render();
            }
            _saveFailed = originalSaveFailed;
        }
        await WaitFor(() => _detailsRequest == null && _artwork.Picture != null, "The fixture game's artwork did not return after the save checks.");
    }

    private async Task VerifyAutomaticUpdatePrompt(string preview)
    {
        Require(!new LauncherPreferences().AutomaticUpdates && !_library.Preferences.AutomaticUpdates,
            "Automatic installation was enabled for a new or migrated profile.");
        var windowWidth = Width;
        var windowHeight = Height;
        var offer = OfferAutomaticUpdates();
        foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
        {
            Width = size.Width;
            Height = size.Height;
            await Task.Delay(100);
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"automatic-updates-prompt-{size.Width}.png"));
        }
        var keepManual = PromptButton("Keep manual updates");
        await WaitFor(() => keepManual.IsFocused, "The automatic-update prompt did not focus its manual choice.");
        keepManual.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        Require(!await offer && !_store.Load().Preferences.AutomaticUpdates,
            "Dismissing the prompt enabled automatic updates.");

        offer = OfferAutomaticUpdates();
        UpdateLayout();
        PromptButton("Keep manual updates").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(!await offer && !_store.Load().Preferences.AutomaticUpdates,
            "Keeping manual updates enabled automatic updates.");

        offer = OfferAutomaticUpdates();
        UpdateLayout();
        PromptButton("Enable automatic updates").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await offer && _store.Load().Preferences.AutomaticUpdates, "Opting in did not persist the automatic-update preference.");
        _library.Preferences.AutomaticUpdates = false;
        Require(Save(), "The update prompt fixture could not reset the preference.");
        Width = windowWidth;
        Height = windowHeight;
        UpdateLayout();

        Button PromptButton(string label) => DialogHost.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyEnabled && Equals(button.Content, label));
    }
}
