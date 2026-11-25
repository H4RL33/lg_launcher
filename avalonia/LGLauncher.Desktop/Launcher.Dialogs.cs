using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly FilePickerFileType Executables = new("Windows executables") { Patterns = ["*.exe"] };
    private static readonly FilePickerFileType Images = new("Images") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp"] };
    private const int ContributionReminderInterval = 50;
    private const string ProjectRepositoryUrl = "https://github.com/H4RL33/lg_launcher";
    private bool _contributionReminderPending;
    private Button? _fontPicker;
    private string[]? _systemFonts;
    private sealed record FontOption(string? Name, string Label);

    private void ApplyFont()
    {
        var saved = _library.Preferences.InterfaceFont;
        FontFamily = String.IsNullOrWhiteSpace(saved) ? _defaultFont : new FontFamily(saved);
        FontSize = EffectiveFontSize;
        var resources = Application.Current!.Resources;
        resources["LG.InterfaceFont"] = FontFamily;
        var difference = FontSize - _defaultFontSize;
        foreach (var size in new[] { 10, 11, 12, 13, 15, 16, 17, 20, 30, 31, 41 })
            resources[$"LG.Font.{size}"] = size + difference;
        // Native popups sit outside the layout transform.
        resources["LG.PopupFont.12"] = (12 + difference) * EffectiveInterfaceScale;
        resources["LG.PopupFont.13"] = (13 + difference) * EffectiveInterfaceScale;
    }

    private Button BuildFontPicker()
    {
        _systemFonts ??= FontManager.Current.SystemFonts.Select(font => font.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var options = new List<FontOption> { new(null, "System default") };
        options.AddRange(_systemFonts.Select(name => new FontOption(name, name)));
        var saved = _library.Preferences.InterfaceFont;
        var current = options.Find(option => String.Equals(option.Name, saved, StringComparison.CurrentCultureIgnoreCase));
        if (current == null && !String.IsNullOrWhiteSpace(saved))
        {
            current = new FontOption(saved, saved);
            options.Insert(1, current);
        }
        current ??= options[0];
        var picker = BuildChoicePicker("Interface font", options, current, choice => choice.Label, selected =>
        {
            var preferences = _library.Preferences;
            if (!SavePreference(preferences.InterfaceFont, selected.Name, value => preferences.InterfaceFont = value, ApplyFont)) return false;
            Status(selected.Name == null ? "Restored the default interface font." : "Interface font updated.");
            return true;
        }, textSearch: true);
        picker.MinWidth = 100;
        picker.MaxWidth = 252;
        picker.HorizontalAlignment = HorizontalAlignment.Right;
        return _fontPicker = picker;
    }

    private Task<bool> ShowModal(ModalDialog dialog)
    {
        if (_closed) return Task.FromResult(false);
        dialog.Closed += (_, _) =>
        {
            CancelMotion(dialog);
            Dispatcher.UIThread.Post(() =>
            {
                TryShowContributionReminder();
                TryAutomaticUpdate();
            }, DispatcherPriority.Background);
        };
        var result = DialogHost.Show(dialog);
        AnimateView(dialog);
        return result;
    }

    private static TextBox TextField(string? text, string name)
    {
        var box = new TextBox { Text = text ?? "", Classes = { "lg" } };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static TextBox ModalTextField(string? text, string name)
    {
        var field = TextField(text, name);
        field.Classes.Add("lg-modal-field");
        return field;
    }

    private static void Field(StackPanel body, string label, Control control)
    {
        body.Children.Add(new StackPanel { Spacing = 5, Children = { Label(label, muted: true), control } });
    }

    private static Button DialogActions(ModalDialog dialog, string submit, Action action, bool defaultAction = true)
    {
        var cancel = ActionButton("Cancel", () => dialog.Close(), "quiet");
        dialog.InitialFocus = cancel;
        dialog.Footer.Children.Add(cancel);
        var save = ActionButton(submit, action, "primary");
        if (defaultAction) dialog.DefaultButton = save;
        dialog.Footer.Children.Add(save);
        return save;
    }

    private Task<bool> Confirm(string title, string message, string action = "Continue", bool destructive = false)
    {
        var dialog = new ModalDialog(title, 453);
        dialog.Body.Children.Add(Label(message, muted: true));
        var confirm = DialogActions(dialog, action, () => dialog.Close(true), defaultAction: false);
        if (destructive)
        {
            confirm.Classes.Remove("primary");
            confirm.Classes.Add("destructive");
        }
        return ShowModal(dialog);
    }

    private Task Notice(string title, string message)
    {
        var dialog = new ModalDialog(title, 453);
        dialog.Body.Children.Add(Label(message, muted: true));
        var close = ActionButton("OK", () => dialog.Close(), "primary");
        dialog.InitialFocus = dialog.DefaultButton = close;
        dialog.Footer.Children.Add(close);
        return ShowModal(dialog);
    }

    private async Task<bool> ShowFirstUsagePrompts()
    {
        if (_library.UsageNoticeAccepted) return true;
        if (!await UsageNotice()) return false;
        if (UpdateInstaller.SupportsAutomaticUpdates && !_library.Preferences.AutomaticUpdates) await OfferAutomaticUpdates();
        return true;
    }

    private void RecordGameReturn()
    {
        var returns = ++_library.GameReturns;
        if (!_library.Preferences.DisableContributionReminders && (returns == 1 || returns % ContributionReminderInterval == 0))
            _contributionReminderPending = true;
    }

    private void TryShowContributionReminder()
    {
        if (!_contributionReminderPending || _library.Preferences.DisableContributionReminders || _closed || _allowClose ||
            _updateHandoff || _playing.Count > 0 || !IsVisible || WindowState == WindowState.Minimized || DialogHost.HasModal) return;

        _contributionReminderPending = false;
        var dialog = new ModalDialog("Thanks for using LG Launcher", 453);
        dialog.Body.Children.Add(Label("If you find LG Launcher useful, you’re welcome to support its development on Ko-fi."));
        dialog.Body.Children.Add(Label("You can also suggest an idea or report a bug on GitHub."));
        var github = IconButton("View on GitHub", "LG.Icon.ExternalLink", () => _ = OpenProjectPage(ProjectRepositoryUrl, "GitHub"));
        var kofi = IconButton("Visit Ko-fi", "LG.Icon.KoFi", OpenSupportPage);
        AutomationProperties.SetName(github, "View on GitHub");
        AutomationProperties.SetName(kofi, "Visit Ko-fi");
        dialog.Body.Children.Add(Actions(github, kofi));
        var close = ActionButton("Close", () => dialog.Close(), "quiet");
        dialog.InitialFocus = dialog.DefaultButton = close;
        dialog.Footer.Children.Add(close);
        _ = ShowModal(dialog);
    }

    private Task<bool> UsageNotice()
    {
        var dialog = new ModalDialog("Use games you’re authorized to play", 475);
        dialog.Body.Children.Add(Label(UsageDisclosure));
        var warning = Label("", muted: true);
        dialog.Body.Children.Add(warning);
        var exit = ActionButton("Exit", () => dialog.Close(), "quiet");
        dialog.InitialFocus = exit;
        dialog.Footer.Children.Add(exit);
        dialog.Footer.Children.Add(ActionButton("I understand", () =>
        {
            _library.UsageNoticeAccepted = true;
            if (Save()) dialog.Close(true);
            else
            {
                _library.UsageNoticeAccepted = false;
                _saveFailed = false;
                warning.Text = "Your acknowledgment could not be saved. Check that your user profile is writable and try again.";
            }
        }, "primary"));
        return ShowModal(dialog);
    }

    private async Task<string?> PickFile(Window owner, string title, FilePickerFileType type, string? directory)
    {
        var start = Directory.Exists(directory) ? await owner.StorageProvider.TryGetFolderFromPathAsync(directory!) : null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = start,
            FileTypeFilter = [type]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void AddFromFile()
    {
        var path = await PickFile(this, "Choose a game executable", Executables, _library.Folder);
        if (path == null) return;
        var existing = _library.Games.Find(game => game.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectGame(existing);
            Status("This executable is already in your library.");
            return;
        }
        EditGame(new Game { Title = Discovery.FriendlyName(Path.GetFileNameWithoutExtension(path)), Path = path }, true);
    }

    private async void ChooseFolder()
    {
        var start = Directory.Exists(_library.Folder) ? await StorageProvider.TryGetFolderFromPathAsync(_library.Folder) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder that contains your installed games",
            SuggestedStartLocation = start
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        ChangeFolder(path);
        Navigate(Page.Discoveries);
    }

    private async void ManageLibraryFolder()
    {
        var dialog = new ModalDialog("Library folder", 470);
        var body = dialog.Body;
        body.Children.Add(Label(_library.Folder ?? "No folder selected", muted: true));
        Action? next = null;
        void Pick(Action action) { next = action; dialog.Close(); }
        var choose = ActionButton("Choose folder", () => Pick(ChooseFolder));
        var stop = ActionButton("Stop watching", () => Pick(() => ChangeFolder(null)), "quiet");
        stop.IsVisible = _library.Folder != null;
        body.Children.Add(Actions(choose, stop));
        if (_library.IgnoredPaths.Count > 0)
            body.Children.Add(ActionButton("Restore ignored discoveries", () => Pick(() =>
            {
                _library.IgnoredPaths.Clear();
                if (Save()) Status("Ignored discoveries restored.");
                Navigate(Page.Discoveries);
                Scan();
            })));
        var done = ActionButton("Done", () => dialog.Close(), "primary");
        dialog.InitialFocus = dialog.DefaultButton = done;
        dialog.Footer.Children.Add(done);
        await ShowModal(dialog);
        next?.Invoke();
    }

    private async void DeleteCollection(GameCollection collection)
    {
        if (!await Confirm("Delete collection", $"Delete “{collection.Name}”? The games will stay in your library.", "Delete collection")) return;
        _library.Collections.Remove(collection);
        foreach (var game in _library.Games) game.CollectionIds.Remove(collection.Id);
        if (Save()) Status("Collection deleted.");
        Navigate(Page.Collections);
    }

    private async void EditGame(Game game, bool adding) => await ShowGameEditor(game, adding,
        (id, token) => _steam.GetGenres(id, token));

    private async Task ShowGameEditor(Game game, bool adding, Func<int, CancellationToken, Task<List<string>?>> getSteamGenres)
    {
        var metadata = adding ? null : ActionButton("Manage metadata", () => _ = ShowGameMetadata(game), "quiet");
        if (metadata != null) metadata.HorizontalAlignment = HorizontalAlignment.Left;
        var dialog = new ModalDialog(adding ? "Add game" : "Edit game", 574, footerLeading: metadata);
        var body = dialog.Body;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
        var token = lifetime.Token;
        dialog.Closed += (_, _) => lifetime.Cancel();
        var title = ModalTextField(game.Title, "Game title");
        title.MaxLength = 150;
        Field(body, "Game title", title);
        var executable = ModalTextField(game.Path, "Executable");
        var browse = ActionButton("Browse", async () =>
        {
            string? start;
            try { start = Path.GetDirectoryName(executable.Text); }
            catch (ArgumentException) { start = _library.Folder; }
            var path = await PickFile(this, "Choose a game executable", Executables, start);
            if (path != null && !token.IsCancellationRequested) executable.Text = path;
        });
        Field(body, "Executable", Beside(executable, browse));
        var arguments = ModalTextField(game.Arguments, "Launch arguments");
        Field(body, "Launch arguments (optional)", arguments);
        var app = ModalTextField(game.SteamId > 0 ? game.SteamId.ToString(CultureInfo.InvariantCulture) : null, "Steam App ID");
        app.MaxLength = 10;
        var search = new Button { Content = "Search Steam", Classes = { "lg" }, IsEnabled = _playing.Count == 0 };
        Field(body, "Steam App ID (optional)", Beside(app, search));
        var matches = new ListBox { DisplayMemberBinding = new Binding(nameof(SteamMatch.Label)), MaxHeight = 120, IsVisible = false, Classes = { "lg" } };
        AutomationProperties.SetName(matches, "Steam search results");
        body.Children.Add(matches);
        var lookup = Label("", muted: true);
        lookup.IsVisible = false;
        body.Children.Add(lookup);
        search.Click += async (_, _) =>
        {
            lookup.IsVisible = true;
            var query = title.Text?.Trim() ?? "";
            if (query.Length == 0) { lookup.Text = "Enter a game title to search Steam."; return; }
            search.IsEnabled = false;
            matches.IsVisible = false;
            lookup.Text = "Searching Steam…";
            try
            {
                var found = await _steam.Search(query, token);
                if (token.IsCancellationRequested) return;
                matches.ItemsSource = found;
                matches.IsVisible = found.Count > 0;
                lookup.Text = found.Count > 0 ? "Select a match to fill in its App ID." : "No Steam matches found.";
            }
            catch (HttpRequestException) { if (!token.IsCancellationRequested) lookup.Text = "Steam is unavailable. You can enter an App ID manually."; }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) lookup.Text = "Steam search timed out."; }
            finally { if (!token.IsCancellationRequested) search.IsEnabled = true; }
        };
        matches.SelectionChanged += (_, _) =>
        {
            if (matches.SelectedItem is SteamMatch match) app.Text = match.Id.ToString(CultureInfo.InvariantCulture);
        };
        var savedGenreText = String.Join(", ", game.GenreNames);
        var genreField = ModalTextField(savedGenreText, "Genres");
        genreField.PlaceholderText = "Genres, separated by commas";
        var originalGenres = genreField.Text;
        var steamGenres = new Button { Content = "Use Steam genres", Classes = { "lg" } };
        var genreStatus = Label("", muted: true);
        genreStatus.IsVisible = false;
        var fetchedGenreId = 0;
        string? fetchedGenreText = null;
        List<string>? fetchedGenres = null;
        steamGenres.Click += async (_, _) =>
        {
            genreStatus.IsVisible = true;
            if (!TrySteamAppId(app.Text, out var requestedId))
            {
                genreStatus.Text = String.IsNullOrWhiteSpace(app.Text)
                    ? "Enter a Steam App ID to look up genres."
                    : "Steam App ID must be a positive number.";
                return;
            }

            var previousText = genreField.Text;
            var submit = dialog.DefaultButton!;
            if (steamGenres.IsFocused) genreField.Focus();
            steamGenres.IsEnabled = submit.IsEnabled = false;
            genreStatus.Text = "Loading Steam genres…";
            try
            {
                var found = await getSteamGenres(requestedId, token);
                if (token.IsCancellationRequested) return;
                if (!TrySteamAppId(app.Text, out var currentId) || currentId != requestedId || genreField.Text != previousText)
                    genreStatus.Text = "The App ID or genres changed. Use Steam genres again to refresh them.";
                else if (found == null)
                    genreStatus.Text = "Steam could not provide genres for this App ID.";
                else if (found.Count == 0)
                    genreStatus.Text = "Steam lists no genres for this App ID.";
                else
                {
                    fetchedGenreId = requestedId;
                    fetchedGenres = found;
                    fetchedGenreText = genreField.Text = String.Join(", ", found);
                    genreStatus.IsVisible = false;
                }
            }
            catch (HttpRequestException) { if (!token.IsCancellationRequested) genreStatus.Text = "Steam is unavailable. Try again or enter genres manually."; }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) genreStatus.Text = "Steam genre lookup timed out. Try again."; }
            finally { if (!token.IsCancellationRequested) steamGenres.IsEnabled = submit.IsEnabled = true; }
        };
        Field(body, "Genres", Beside(genreField, steamGenres));
        body.Children.Add(genreStatus);
        var tagsField = ModalTextField(String.Join(", ", game.TagNames), "Tags");
        tagsField.PlaceholderText = "Tags, separated by commas";
        var originalTags = tagsField.Text;
        var tagsStatus = Label("", muted: true);
        tagsStatus.IsVisible = false;
        (int Id, List<string> Values, string? Text)? fetchedTags = null;
        var steamTags = new Button { Content = "Use Steam tags", Classes = { "lg" } };
        steamTags.Click += (_, _) => _ = LoadSteamTags();
        Field(body, "Tags", Beside(tagsField, steamTags));
        body.Children.Add(tagsStatus);
        var scale = game.Scale;
        Field(body, "Scale", BuildChoicePicker("Game scale", Enum.GetValues<GameScale>(), scale,
            choice => choice == GameScale.Unspecified ? "Unspecified" : choice.ToString(), choice =>
            {
                scale = choice;
                return true;
            }));
        var error = Label("", muted: true);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        body.Children.Add(error);
        var previousSteamId = game.SteamId;
        DialogActions(dialog, adding ? "Add to library" : "Save changes", () =>
        {
            var name = title.Text?.Trim() ?? "";
            if (name.Length == 0) { error.Text = "Enter a game title."; return; }
            var typedPath = executable.Text?.Trim().Trim('"') ?? "";
            if (!Path.IsPathFullyQualified(typedPath)) { error.Text = "Choose an executable using its full path."; return; }
            string path;
            try { path = Path.GetFullPath(typedPath); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            { error.Text = "Enter a valid Windows executable path."; return; }
            if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ((adding || !path.Equals(game.Path, StringComparison.OrdinalIgnoreCase)) && !File.Exists(path)))
            { error.Text = "Choose an existing .exe file."; return; }
            if (_library.Games.Any(entry => entry != game && path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)))
            { error.Text = "This executable is already in the library."; return; }
            var id = 0;
            var typedId = app.Text?.Trim() ?? "";
            if (typedId.Length > 0 && !TrySteamAppId(typedId, out id))
            { error.Text = "Steam App ID must be a positive number."; return; }
            var genres = ParseGenreInput(genreField.Text);
            var usingFetchedGenres = fetchedGenres != null && genreField.Text == fetchedGenreText;
            var editedGenres = !usingFetchedGenres && genreField.Text != originalGenres;
            if (editedGenres && genres.Any(genre => genre.Length > 45))
            {
                error.Text = "Enter genres of up to 45 characters each.";
                return;
            }
            var previousDetails = (game.Title, game.Path, game.Arguments, game.SteamId);
            var previousGenres = (game.Genres, game.ManualGenres, game.GenreSteamId, game.SteamGenresComplete);
            var previousTags = (game.SteamTags, game.TagSteamId, game.ManualTags);
            var previousClassification = (game.Scale, game.AddedUtc);
            var previousIgnoredPaths = _library.IgnoredPaths;
            var previouslyUnsaved = _saveFailed;
            var steamIdChanged = game.SteamId != id;
            var retryGenres = false;
            if (usingFetchedGenres && fetchedGenreId == id)
            {
                game.Genres = [.. fetchedGenres!];
                game.ManualGenres = false;
                game.GenreSteamId = id;
                game.SteamGenresComplete = true;
            }
            else if (editedGenres && genres.Length > 0)
            {
                game.Genres = [.. genres];
                game.ManualGenres = true;
                game.GenreSteamId = 0;
                game.SteamGenresComplete = false;
            }
            else if (usingFetchedGenres || editedGenres || (!game.ManualGenres && steamIdChanged))
            {
                game.Genres = null;
                game.ManualGenres = false;
                game.GenreSteamId = 0;
                game.SteamGenresComplete = false;
                retryGenres = true;
            }
            game.Title = name;
            game.Path = path;
            game.Arguments = arguments.Text ?? "";
            if (steamIdChanged)
            {
                game.SteamTags = null;
                game.TagSteamId = 0;
            }
            if (fetchedTags is { } fetched && fetched.Id == id && tagsField.Text == fetched.Text)
            {
                game.SteamTags = fetched.Values;
                game.TagSteamId = id;
                game.ManualTags = null;
            }
            else if (tagsField.Text != originalTags && tagsField.Text != fetchedTags?.Text)
            {
                game.ManualTags = [.. ParseGenreInput(tagsField.Text)];
            }
            game.Scale = scale;
            game.SteamId = id;
            if (adding)
            {
                game.AddedUtc = DateTime.UtcNow;
                _library.Games.Add(game);
            }
            if (previousIgnoredPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                _library.IgnoredPaths = previousIgnoredPaths.Where(item => !path.Equals(item, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!Save())
            {
                (game.Title, game.Path, game.Arguments, game.SteamId) = previousDetails;
                (game.Genres, game.ManualGenres, game.GenreSteamId, game.SteamGenresComplete) = previousGenres;
                (game.SteamTags, game.TagSteamId, game.ManualTags) = previousTags;
                (game.Scale, game.AddedUtc) = previousClassification;
                if (adding) _library.Games.Remove(game);
                _library.IgnoredPaths = previousIgnoredPaths;
                _saveFailed = previouslyUnsaved;
                error.Text = "The library could not be saved. Check that the profile folder is writable and try again.";
                Dispatcher.UIThread.Post(() => error.BringIntoView(), DispatcherPriority.Loaded);
                return;
            }

            _discoveries.RemoveAll(item => path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
            if (retryGenres) _attemptedGenres.Remove(id);
            if (steamIdChanged)
            {
                _attemptedTags.Remove(id);
                _attemptedGameDetails.Remove(id);
            }
            Status(adding ? "Added " + name + " to your library." : "Game updated.");
            InvalidateEntry(game);
            dialog.Close(true);
        });
        dialog.InitialFocus = title;
        if (!await ShowModal(dialog)) return;
        RefreshManagedGameStatus();
        if (adding) SelectGame(game); else Render();
        if (adding && game.SteamId == 0) TryMatch(game);
        else if (game.SteamId > 0 && (adding || game.SteamId != previousSteamId)) _ = EnsureHeader(game.SteamId, _background.Token);
        QueueSteamMetadata();

        async Task LoadSteamTags()
        {
            tagsStatus.IsVisible = true;
            if (!TrySteamAppId(app.Text, out var requestedId))
            {
                tagsStatus.Text = "Enter a positive Steam App ID to look up tags.";
                return;
            }
            var submit = dialog.DefaultButton!;
            if (!submit.IsEnabled) return;
            var previousText = tagsField.Text;
            steamGenres.IsEnabled = steamTags.IsEnabled = submit.IsEnabled = false;
            tagsStatus.Text = "Loading Steam tags…";
            try
            {
                var tags = requestedId == game.SteamId && game.HasSteamTags ? game.SteamTags :
                    await _steam.GetStoreTags(requestedId, token);
                if (token.IsCancellationRequested) return;
                if (!TrySteamAppId(app.Text, out var currentId) || currentId != requestedId || tagsField.Text != previousText)
                    tagsStatus.Text = "The App ID or tags changed. Use Steam tags again to refresh them.";
                else if (tags == null)
                    tagsStatus.Text = "Steam could not provide tags for this App ID.";
                else
                {
                    tagsField.Text = String.Join(", ", tags);
                    fetchedTags = (requestedId, tags, tagsField.Text);
                    tagsStatus.IsVisible = false;
                }
            }
            catch (HttpRequestException) { if (!token.IsCancellationRequested) tagsStatus.Text = "Steam is unavailable. Try again or enter tags manually."; }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) tagsStatus.Text = "Steam tag lookup timed out. Try again."; }
            finally
            {
                if (!token.IsCancellationRequested) steamGenres.IsEnabled = steamTags.IsEnabled = submit.IsEnabled = true;
            }
        }
    }

    private static bool TrySteamAppId(string? value, out int id) =>
        Int32.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    private static Grid Beside(Control input, Button button)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8, Children = { input, button } };
        Grid.SetColumn(button, 1);
        return grid;
    }

    private void GameMenu(Control owner, Game game)
    {
        var menu = BuildGameMenu(game, PlacementMode.Bottom);
        owner.ContextMenu = menu;
        menu.Open(owner);
    }

    private ContextMenu BuildGameMenu(Game game, PlacementMode placement)
    {
        var menu = LauncherMenus.Create(placement);
        menu.AddAction("Open Folder", () => OpenGameFolder(game), "LG.Icon.FolderOpen");
        menu.AddAction("Edit game", () => EditGame(game, false), "LG.Icon.Edit");
        menu.AddAction("Manage metadata", () => _ = ShowGameMetadata(game), "LG.Icon.Artwork");
        var refresh = menu.AddAction("Refresh from Steam", () => _ = RefreshGameFromSteam(game), "LG.Icon.Refresh");
        menu.Opening += (_, _) => refresh.IsEnabled = game.SteamId > 0 && _playing.Count == 0 && !_steamRefreshes.Contains(game.SteamId);
        refresh.IsEnabled = game.SteamId > 0 && _playing.Count == 0 && !_steamRefreshes.Contains(game.SteamId);
        menu.AddAction("Add to collection", async () => await ShowCollectionPicker(game), "LG.Icon.Plus");
        menu.AddSeparator();
        menu.AddAction("Remove from library", async () =>
        {
            if (_playing.Contains(game.Id)) { Status("Close the game before removing it."); return; }
            if (!await Confirm("Remove game", $"Remove “{game.Title}” from your library? Its executable will stay on disk.", "Remove game")) return;
            _library.Games.Remove(game);
            _pendingMatches.Remove(game);
            _entries.Remove(game);
            if (!_library.IgnoredPaths.Contains(game.Path, StringComparer.OrdinalIgnoreCase)) _library.IgnoredPaths.Add(game.Path);
            if (Save()) Status("Game removed.");
            Navigate(Page.Home);
            Scan();
        }, "LG.Icon.Delete", destructive: true);
        return menu;
    }

    private async void OpenGameFolder(Game game)
    {
        try
        {
            var path = Path.GetDirectoryName(game.Path);
            if (!Directory.Exists(path))
            {
                await Notice("Folder unavailable", "The game’s folder could not be found. Edit the game to update its executable path.");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path!) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            await Notice("Could not open folder", error.Message);
        }
    }

}
