using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher : Window
{
    private enum Page { Home, Library, Collections, Discoveries, Rss, RssArticle, Settings, Appearance, Widgets, Behaviour, RssSettings, LibrarySettings, Mods, About, Game }

    private readonly LibraryStore _store;
    private readonly Library _library;
    private readonly SteamCatalog _steam;
    private readonly GameSessions _sessions = new();
    private readonly HashSet<string> _playing = [];
    private readonly HashSet<Game> _pendingMatches = [];
    private readonly HashSet<Game> _matching = [];
    private readonly Dictionary<Game, LibraryEntry> _entries = [];
    private readonly FontFamily _defaultFont;
    private readonly double _defaultFontSize;
    private readonly Size _unscaledWindowMinimum;
    private readonly bool _verification = Program.VerificationMode != null;
    private LibraryEntry[] _shownEntries = [];
    private SidebarCollection[] _shownCollections = [];
    private List<Discovery> _discoveries = [];
    private LibraryWatcher? _watcher;
    private CancellationTokenSource? _scan;
    private CancellationTokenSource _background = new();
    private Page _page;
    private Game? _game;
    private GameCollection? _collection;
    private bool _updatingSidebar;
    private bool _ready;
    private bool _closed;
    private bool _allowClose;
    private bool _confirmingClose;
    private bool _saveFailed;
    private bool _rescan;
    private int _offset;
    private int _sort;
    private int _librarySort;
    private int _collectionsSort;
    private UniformGrid? _cardGrid;

    public Launcher(LibraryStore store, Library library)
    {
        InitializeComponent();
        DialogHost.Underlay = ShellSurface;
        WindowTitle.Text = Title = $"LG Launcher {UpdateFeed.ReleaseTag(UpdateFeed.CurrentVersion)}";
        _store = store;
        _library = library;
        _steam = new SteamCatalog(store.ArtworkDirectory, CanDecodeSteamArtwork);
        _rssReader = new RssReader(Program.DataDirectory);
        _rssTimer.Tick += (_, _) =>
        {
            if (!_rssRefreshing) RequestRssRefresh();
        };
        _defaultFont = FontFamily;
        _defaultFontSize = FontSize;
        _unscaledWindowMinimum = new Size(MinWidth, MinHeight);
        ApplyMotionPreference();
        SmoothScroll.EnableWheel(this);
        ApplyInterfaceScale();
        ApplyTranslucency();
        ApplyColourway();
        ApplyWidgetTheme();
        using var icon = typeof(Launcher).Assembly.GetManifestResourceStream("LGLauncher.Icon.ico");
        if (icon != null) Icon = new WindowIcon(icon);
        ArtworkHost.Content = _artwork;
        InitializeArtwork();
        if (OperatingSystem.IsWindows())
        {
            WindowsDesktop.UseCompositorFrame(this, DragBar.Height * EffectiveInterfaceScale);
            DragBar.IsVisible = true;
            Opened += (_, _) => WindowsDesktop.ApplySystemCorners(this);
        }
        Click(MinimizeWindow, () => WindowState = WindowState.Minimized);
        Click(MaximizeWindow, () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        Click(CloseWindow, Close);
        PropertyChanged += (_, args) =>
        {
            if (args.Property != WindowStateProperty) return;
            UpdateThemeMusic();
            var maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Data = (Geometry?)this.FindResource(maximized ? "LG.Icon.Restore" : "LG.Icon.Maximize");
            var verb = maximized ? "Restore" : "Maximize";
            ToolTip.SetTip(MaximizeWindow, verb);
            AutomationProperties.SetName(MaximizeWindow, verb);
        };
        Click(HomeNav, () => Navigate(Page.Home));
        Click(CollectionsNav, () => Navigate(Page.Collections));
        var collectionsIcon = Symbol("LG.Icon.Library", 19);
        collectionsIcon.Data = LucideIconCatalog.Find("grid-2x2")?.Geometry ?? collectionsIcon.Data;
        CollectionsNavIcon.Content = collectionsIcon;
        Click(LibraryNav, () => Navigate(Page.Library));
        Click(RssNav, () => Navigate(Page.Rss));
        Click(OpenReviews, () => Navigate(Page.Discoveries));
        Click(SettingsNav, () => Navigate(Page.Settings));
        Click(GameModsButton, () => { if (_game is { } game) _ = ShowGameMods(game); });
        Click(RequestModButton, () => OpenModNotes(ModRequestPage));
        Click(SupportButton, OpenSupportPage);
        Click(AddGame, () => EditGame(new Game { Title = "", Path = "" }, true));
        Click(AddCollection, () => _ = ChooseCollectionType());
        Click(ManageFolder, ManageLibraryFolder);
        Click(UpdateAction, () =>
        {
            if (_updateInstall != null) _updateInstall.Cancel();
            else _ = InstallUpdate(false);
        });
        Click(PlayGame, () => Launch(_game));
        Click(EditGameButton, () => { if (_game != null) EditGame(_game, false); });
        Click(GameOptions, () => { if (_game != null) GameMenu(GameOptions, _game); });
        Click(RefreshSteamButton, () => { if (_game != null) _ = RefreshGameFromSteam(_game); });
        if (!_verification && StartsMinimised) WindowState = WindowState.Minimized;
        LibraryGames.SelectionChanged += (_, _) =>
        {
            if (!_updatingSidebar && LibraryGames.SelectedItem is LibraryEntry item) SelectGame(item.Game);
        };
        LibraryCollections.SelectionChanged += (_, _) =>
        {
            if (!_updatingSidebar && LibraryCollections.SelectedItem is SidebarCollection item) Navigate(Page.Collections, item.Collection);
        };
        LibraryGames.DoubleTapped += (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { Content: LibraryEntry item })
                Launch(item.Game);
        };
        InitializeSearch();
        InitializeSidebarSections();
        RefreshManagedGameStatus();
        ContentScroll.PropertyChanged += (_, args) =>
        {
            if (args.Property == ScrollViewer.ViewportProperty) SizeCardGrid();
        };
        AddHandler(KeyDownEvent, HandleKeys, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, HandleNavigationPointer, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            if (_verification || _ready) return;
            var acceptedBeforeStartup = _library.UsageNoticeAccepted;
            if (!await ShowFirstUsagePrompts())
            {
                _allowClose = true;
                Close();
                return;
            }
            if (_closed) return;
            _ready = true;
            if (_library.Preferences.StartMinimised) WindowState = WindowState.Minimized;
            // A minimised launch skips the splash and can wait for a slow library scan.
            var updateMessage = StartsMinimised && Program.IsUpdateRestart ? Program.CompleteUpdateRestart() : null;
            if (!_startupComplete) await InitializeStartup(null);
            else if (!acceptedBeforeStartup) { StartWatcher(); Scan(); }
            if (_closed) return;
            Render();
            RecoverPendingModsOnStartup();
            StartModCatalogRefresh();
            if ((updateMessage ?? Program.CompleteUpdateRestart()) is { } message) Status(message);
            TryAutomaticUpdate();
            AnimateView(MainShell, fadeOnly: true);
            QueueSteamMetadata();
            RestartRssTimer();
            RequestRssRefresh();
        };
        Activated += (_, _) =>
        {
            if (!_verification) ApplyMotionPreference();
            if (_ready && _watcher == null && Directory.Exists(_library.Folder)) { StartWatcher(); Scan(); }
            if (_ready && !_modFileOperation) _ = RefreshSelectedModStatus();
            TryShowContributionReminder();
            UpdateThemeMusic();
        };
        Deactivated += (_, _) => StopThemeMusic();
        Closing += HandleClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            if (!_saveFailed) FlushPendingSteamGenres();
            DialogHost.CloseAll();
            _searchDelay.Stop();
            _rssTimer.Stop();
            _modCatalogTimer.Stop();
            _scan?.Cancel();
            _background.Cancel();
            _background.Dispose();
            _updateInstall?.Cancel();
            _modLifetime.Cancel();
            _watcher?.Dispose();
            CancelMotion();
            _tray?.Dispose();
            _sessions.Dispose();
            StopThemeMusic();
            DisposeGameLogos();
            DisposeImages();
        };
        _sessions.Ended += (id, seconds) => Dispatcher.UIThread.Post(() => EndSession(id, seconds));
        Render();
    }

    private static void Click(Button button, Action action) => button.Click += (_, _) => action();

    private void HandleKeys(object? sender, KeyEventArgs args)
    {
        if (_updateHandoff) { args.Handled = true; return; }
        if (DialogHost.HasModal) return;
        if (args.Key is Key.BrowserBack or Key.BrowserForward)
        {
            MoveInHistory(args.Key == Key.BrowserBack ? -1 : 1);
            args.Handled = true;
            return;
        }
        if (HandleArtworkKey(args.Key)) { args.Handled = true; return; }
        if (args.KeyModifiers == KeyModifiers.None && HandleSearchKey(args.Key)) { args.Handled = true; return; }
        if (args.KeyModifiers != KeyModifiers.Control) return;
        switch (args.Key)
        {
            case Key.F: (_page == Page.Appearance ? (Control?)_fontPicker : SearchBox)?.Focus(); break;
            case Key.O: AddFromFile(); break;
            default: return;
        }
        args.Handled = true;
    }

    private void Render()
    {
        RenderSidebar();
        GameCount.Text = _library.Games.Count == 1 ? "1 game in library" : _library.Games.Count + " games in library";
        RefreshDiscoveryIndicators();
        HomeNav.Classes.Set("selected", _page == Page.Home);
        CollectionsNav.Classes.Set("selected", _page == Page.Collections);
        LibraryNav.Classes.Set("selected", _page == Page.Library);
        var settingsPage = _page is Page.Settings or Page.Appearance or Page.Widgets or Page.Behaviour or Page.RssSettings or Page.LibrarySettings or Page.Mods or Page.About;
        RssNav.Classes.Set("selected", _page is Page.Rss or Page.RssArticle);
        SettingsNav.Classes.Set("selected", settingsPage);
        SearchSurface.IsVisible = !settingsPage && !_editingArtwork && _page != Page.RssArticle;
        var returningToMods = _page == Page.Mods && Details.IsVisible;
        Details.IsVisible = _page == Page.Game;
        UpdateThemeMusic();
        ContentScroll.IsVisible = !Details.IsVisible;
        RequestModButton.IsVisible = ModRequestScope.IsVisible = _page == Page.Mods;
        ApplyArtworkAccent();
        RefreshArtworkGlass();
        if (SearchDropdown.IsVisible) ShowSearchResults();
        if (Details.IsVisible)
        {
            _cardGrid = null;
            if (_game != null) RenderDetails(_game);
            return;
        }
        ClearGameLogo();
        var settingsTitle = _page switch
        {
            Page.Settings => "Settings",
            Page.Appearance => "Appearance",
            Page.Widgets => "Widget Customisation",
            Page.Behaviour => "App Behaviour",
            Page.RssSettings => "RSS settings",
            Page.LibrarySettings => "Library settings",
            Page.Mods => "Mods",
            Page.About => "About",
            _ => null
        };
        if (settingsTitle != null && PageTitle.Text == settingsTitle && PageBody.Children.Count > 0)
        {
            if (returningToMods) RefreshModsSettings();
            return;
        }
        PageTitle.Classes.Set("full-width", settingsPage || _page == Page.RssArticle);
        _cardGrid = null;
        SmoothScroll.Cancel(ContentScroll);
        PageBody.Children.Clear();
        switch (_page)
        {
            case Page.Home: RenderHome(); break;
            case Page.Library: RenderWholeLibrary(); break;
            case Page.Collections: RenderCollections(); break;
            case Page.Discoveries: RenderDiscoveries(); break;
            case Page.Rss: RenderRss(); break;
            case Page.RssArticle: RenderRssArticle(); break;
            case Page.Settings:
                PageTitle.Text = "Settings";
                PageBody.Children.Add(SettingsRow("Appearance", "LG.Icon.Palette", Page.Appearance));
                PageBody.Children.Add(SettingsRow("App Behaviour", "LG.Icon.Controls", Page.Behaviour));
                PageBody.Children.Add(SettingsRow("Library", "LG.Icon.Library", Page.LibrarySettings));
                PageBody.Children.Add(SettingsRow("RSS", "LG.Icon.Rss", Page.RssSettings));
                if (OperatingSystem.IsWindows()) PageBody.Children.Add(SettingsRow("Mods", "LG.Icon.Wrench", Page.Mods));
                PageBody.Children.Add(SettingsRow("About", "LG.Icon.Info", Page.About));
                break;
            case Page.Appearance:
                PageTitle.Text = "Appearance";
                PageBody.Children.Add(SettingsBack());
                PageBody.Children.Add(BuildAppearance());
                break;
            case Page.Widgets:
                PageTitle.Text = "Widget Customisation";
                PageBody.Children.Add(SettingsBack(Page.Appearance, "Appearance"));
                PageBody.Children.Add(BuildWidgetSettings());
                break;
            case Page.Behaviour:
                PageTitle.Text = "App Behaviour";
                PageBody.Children.Add(SettingsBack());
                PageBody.Children.Add(BuildBehaviour());
                break;
            case Page.RssSettings:
                PageTitle.Text = "RSS settings";
                PageBody.Children.Add(SettingsBack());
                PageBody.Children.Add(BuildRssSettings());
                break;
            case Page.LibrarySettings:
                PageTitle.Text = "Library settings";
                PageBody.Children.Add(SettingsBack());
                PageBody.Children.Add(BuildLibrarySettings());
                break;
            case Page.Mods:
                PageTitle.Text = "Mods";
                PageBody.Children.Add(SettingsBack());
                PageBody.Children.Add(BuildMods());
                break;
            case Page.About:
                PageTitle.Text = "About";
                PageBody.Children.Add(SettingsBack());
                PageBody.Children.Add(BuildAbout());
                break;
        }
    }

    private sealed record SidebarCollection(GameCollection Collection, string Title, Geometry? Icon);

    private void RenderSidebar()
    {
        var rows = SortedGames(_library.Games, _librarySort).Select(GetEntry).ToArray();
        var collections = SortedCollections()
            .Select(collection => new SidebarCollection(collection, collection.Name, CollectionSidebarIcon(collection))).ToArray();
        _updatingSidebar = true;
        if (!_shownEntries.SequenceEqual(rows)) LibraryGames.ItemsSource = _shownEntries = rows;
        if (!_shownCollections.SequenceEqual(collections)) LibraryCollections.ItemsSource = _shownCollections = collections;
        LibraryGames.SelectedItem = _page == Page.Game ? _shownEntries.FirstOrDefault(entry => entry.Game == _game) : null;
        LibraryCollections.SelectedItem = _page == Page.Collections ? _shownCollections.FirstOrDefault(entry => entry.Collection == _collection) : null;
        _updatingSidebar = false;

        SettleSidebarSection(LibrarySectionClip, LibrarySection);
        SettleSidebarSection(CollectionsSectionClip, CollectionsSection);
        LibraryEmpty.IsVisible = rows.Length == 0;
        CollectionsEmpty.IsVisible = collections.Length == 0;
        RefreshVisibleIcons();
    }

    private void RenderCollections()
    {
        PageTitle.Text = _collection?.Name ?? "Collections";
        if (_collection == null)
        {
            RenderCollectionCards();
            return;
        }
        var current = _collection;
        var sort = BuildChoicePicker("Sort collection", GameSortChoices(), _sort,
            index => GameSortLabels[index], index => SortCollection(current, index));
        PageBody.Children.Add(Actions(ActionButton("All collections", () => Navigate(Page.Collections), "quiet"), sort,
            ActionButton(current.IsDynamic ? "Edit rules" : "Rename", () => EditCollection(current)), ActionButton("Delete", () => DeleteCollection(current), "quiet")));
        if (current.IsDynamic) PageBody.Children.Add(Label(DescribeCollectionRules(current), muted: true));
        var members = SortedGames(_library.Games.Where(current.Includes), _sort).ToArray();
        if (members.Length == 0) PageBody.Children.Add(Section("No games to show", Label(
            current.IsDynamic ? "Games appear here when they match these rules." : "Use Add to collection from a game's menu to add it here.", muted: true)));
        else RenderCards(members);
    }

    private bool SortCollection(GameCollection collection, int sort)
    {
        if (_page != Page.Collections || _collection != collection) return false;
        return ReorderCards(() => _sort = sort);
    }

    private bool ReorderCards(Action applySort)
    {
        var previousPositions = _cardGrid is { } previous && CanAnimate(previous)
            ? previous.Children.Where(tile => tile.Tag != null).ToDictionary(tile => tile.Tag!, tile => tile.TranslatePoint(default, PageBody))
            : null;
        applySort();
        _offset = 0;
        Render();
        if (previousPositions == null || _cardGrid is not { } reordered) return true;

        UpdateLayout();
        foreach (var tile in reordered.Children)
        {
            if (tile.Tag == null || previousPositions.GetValueOrDefault(tile.Tag) is not { } from ||
                tile.TranslatePoint(default, PageBody) is not { } to) continue;
            Vector offset = from - to;
            if (offset.SquaredLength > .25) AnimateMotion(tile, token => ViewMotion.Move(tile, offset, token));
        }
        return true;
    }

    private IEnumerable<Game> SortedGames(IEnumerable<Game> games, int sort) => sort switch
    {
        1 => games.OrderByDescending(game => game.LastPlayedUtc).ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        2 when TrackPlaytime => games.OrderByDescending(game => game.PlaySeconds).ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        3 => games.OrderByDescending(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        4 => games.OrderByDescending(game => game.AddedUtc).ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        5 => games.OrderByDescending(game => game.SteamReleaseDate).ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => games.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
    };

    private void RenderCards(Game[] games)
    {
        const int pageSize = 24;
        _offset = Math.Min(_offset, (games.Length - 1) / pageSize * pageSize);
        var grid = new UniformGrid { Columns = GameTileColumns, ColumnSpacing = GameTileColumnSpacing, RowSpacing = 14 };
        grid.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged && args.NewSize.Width > 0) grid.Columns = FittingGameTileColumns(args.NewSize.Width);
        };
        foreach (var game in games.Skip(_offset).Take(pageSize))
        {
            // The host carries the reorder translation while the card retains its hover transform.
            var tile = new Border { Child = BuildGameCard(game), Tag = game };
            tile.DetachedFromVisualTree += (_, _) => CancelMotion(tile);
            grid.Children.Add(tile);
        }
        ShowCardGrid(grid);
        AddPagination(games.Length, pageSize);
    }

    private GameCard BuildGameCard(Game game, bool showPlaytime = true)
    {
        var card = new GameCard
        {
            Title = game.Title,
            IsModded = game.IsModded && !_library.Preferences.HideTileModIndicator,
            Playtime = game.PlaySeconds >= 3600 ? $"{game.PlaySeconds / 3600d:0.#} hr" : $"{game.PlaySeconds / 60} min",
            ShowPlaytime = TrackPlaytime && !_library.Preferences.HideTilePlaytime && showPlaytime && game.PlaySeconds >= 60,
            OpenCommand = new LauncherCommand(() => SelectGame(game)),
            ContextMenu = BuildGameMenu(game, PlacementMode.Pointer)
        };
        var lastLaunched = game.LastPlayedUtc.HasValue ? "Last launched " + FormatDate(game.LastPlayedUtc) : "Never launched";
        ToolTip.SetTip(card, game.Title + Environment.NewLine + lastLaunched + (game.IsModded ? Environment.NewLine + "Modded" : ""));
        LoadCardArtwork(card, game);
        return card;
    }

    private void ShowCardGrid(UniformGrid grid)
    {
        _cardGrid = grid;
        SizeCardGrid();
        PageBody.Children.Add(grid);
    }

    private void SizeCardGrid()
    {
        if (_cardGrid is not { Children.Count: > 0 } grid || _page != Page.Collections || _collection != null) return;
        const int cardWidth = 200;
        const int minimumGridCardWidth = 162;
        var viewport = ContentScroll.Viewport.Width > 0 ? ContentScroll.Viewport.Width : Width - 205;
        var available = Math.Max(minimumGridCardWidth, viewport - 54);
        var slots = Math.Max(1, (int)((available + grid.ColumnSpacing) / (minimumGridCardWidth + grid.ColumnSpacing)));
        grid.Columns = Math.Min(grid.Children.Count, slots);
        grid.Width = Math.Min(available, grid.Columns * cardWidth + (grid.Columns - 1) * grid.ColumnSpacing);
    }

    private void RenderDiscoveries()
    {
        PageTitle.Text = "Discoveries";
        if (_library.Folder == null)
        {
            PageBody.Children.Add(Section("Watch a library folder", ActionButton("Choose library folder", ChooseFolder, "primary")));
            return;
        }
        PageBody.Children.Add(Actions(ActionButton("Rescan", () => { StartWatcher(); Scan(); }),
            ActionButton("Change folder", ChooseFolder, "quiet")));
        if (_discoveries.Count == 0)
        {
            PageBody.Children.Add(Section("No discoveries to show", Label("New executables in the watched folder appear here for review.", muted: true)));
            return;
        }
        const int pageSize = 12;
        _offset = Math.Min(_offset, (_discoveries.Count - 1) / pageSize * pageSize);
        foreach (var item in _discoveries.Skip(_offset).Take(pageSize))
        {
            var contents = new StackPanel
            {
                Spacing = 8, Children = { Label(item.Path, muted: true),
                Actions(ActionButton("Review", () => EditGame(new Game { Title = item.Title, Path = item.Path }, true), "primary"),
                    ActionButton("Ignore", () => Ignore(item), "quiet")) }
            };
            PageBody.Children.Add(Section(item.Title, contents));
        }
        AddPagination(_discoveries.Count, pageSize);
    }

    private void AddPagination(int count, int pageSize)
    {
        if (count <= pageSize) return;
        var back = ActionButton("Previous", () => Navigate(_history[_historyIndex].View, _offset - pageSize));
        var next = ActionButton("Next", () => Navigate(_history[_historyIndex].View, _offset + pageSize));
        back.IsEnabled = _offset > 0;
        next.IsEnabled = _offset + pageSize < count;
        PageBody.Children.Add(Actions(back, Label($"{_offset + 1}–{Math.Min(_offset + pageSize, count)} of {count}", muted: true), next));
    }

    private void RenderDetails(Game game)
    {
        GameTitle.Text = game.Title;
        LoadGameLogo(game);
        RefreshSteamButton.IsEnabled = game.SteamId > 0 && !_steamRefreshes.Contains(game.SteamId) && _playing.Count == 0;
        var hasSteamMatch = game.SteamId > 0;
        SteamAppId.IsVisible = hasSteamMatch;
        SteamLabel.Text = hasSteamMatch ? game.SteamId.ToString(CultureInfo.InvariantCulture) : null;
        AutomationProperties.SetName(SteamLabel, hasSteamMatch ? $"Steam App ID {SteamLabel.Text}" : null);
        Playtime.Text = game.PlaySeconds >= 3600 ? $"{game.PlaySeconds / 3600} hr {game.PlaySeconds % 3600 / 60} min" : $"{game.PlaySeconds / 60} min";
        PlaytimeDetail.IsVisible = TrackPlaytime;
        LastPlayed.Text = FormatLastLaunched(game.LastPlayedUtc);
        ModdedStatus.Text = game.IsModded ? "Yes" : "No";
        RenderGenres(game);
        RenderStoreTags(game);
        var playing = _playing.Contains(game.Id);
        PlayGame.IsEnabled = !playing && CanPlayWithMods(game);
        PlayActionLabel.Text = playing ? "Running" : "Play";
        RenderGameModAction(game);
        if (!_editingArtwork) LoadDetailsArtwork(game);
    }

    private static string FormatDate(DateTime? utc) => utc?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "Never";

    private string FormatLastLaunched(DateTime? utc)
    {
        if (!_library.Preferences.FriendlyLaunchDates || utc == null) return FormatDate(utc);
        var date = utc.Value.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        var month = date.ToString("MMM", culture);
        if (culture.TwoLetterISOLanguageName == "en" && month != date.ToString("MMMM", culture)) month += ".";
        var day = date.ToString("%d", culture);
        return date.Year == DateTime.Now.Year ? $"{month} {day}" : $"{month} {day}, {date.ToString("yyyy", culture)}";
    }
    private void Status(string message) => StatusText.Text = message;

    private bool Save()
    {
        try { _store.Save(_library); _saveFailed = false; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _saveFailed = true;
            Status("Library could not be saved. " + error.Message);
            return false;
        }
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose || _verification) return;
        if (_updateHandoff) { args.Cancel = true; return; }
        if (_modFileOperation) { args.Cancel = true; Status("Wait for the mod file operation to finish."); return; }
        if (!_explicitQuit && args.CloseReason == WindowCloseReason.WindowClosing && DialogHost.HasModal)
        {
            args.Cancel = true;
            DialogHost.DismissTop();
            return;
        }
        if (!_explicitQuit && args.CloseReason == WindowCloseReason.WindowClosing && _library.Preferences.CloseToTray && HideToTray())
        {
            args.Cancel = true;
            return;
        }
        _explicitQuit = false;
        if (_playing.Count == 0 && !_saveFailed) return;
        args.Cancel = true;
        if (_confirmingClose) return;
        _confirmingClose = true;
        try
        {
            var closingMessage = TrackPlaytime ? "Close LG Launcher and save playtime so far? Your games will keep running."
                : "Close LG Launcher? Your games will keep running.";
            if (_playing.Count > 0 && !await Confirm("Games are running", closingMessage)) return;
            var snapshots = _sessions.CurrentTime();
            foreach (var game in _library.Games)
                if (snapshots.TryGetValue(game.Id, out var seconds)) game.PlaySeconds += seconds;
            if (!Save())
            {
                foreach (var game in _library.Games)
                    if (snapshots.TryGetValue(game.Id, out var seconds)) game.PlaySeconds -= seconds;
                if (!await Confirm("Library could not be saved", "Close LG Launcher and discard unsaved changes?")) return;
            }
            _allowClose = true;
            Dispatcher.UIThread.Post(Close);
        }
        finally { _confirmingClose = false; }
    }

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (Directory.Exists(_library.Folder))
        {
            try
            {
                var next = new LibraryWatcher(_library.Folder);
                _watcher = next;
                next.Changed += () => Dispatcher.UIThread.Post(() => { if (_watcher == next && !_closed) Scan(); });
                next.Start();
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _watcher?.Dispose();
                _watcher = null;
                Status("Folder watching is unavailable. " + error.Message);
            }
        }
        FolderStatus.Tone = _watcher != null ? StatusTone.Active : _library.Folder == null ? StatusTone.Neutral : StatusTone.Warning;
        FolderStatus.Label = _watcher != null ? "Folder watch active" : _library.Folder == null ? "No folder watched" : "Folder unavailable";
        ToolTip.SetTip(ManageFolder, _library.Folder ?? "Choose a library folder");
    }

    private void Scan() => _ = ScanLibrary();

    private void RefreshDiscoveryIndicators()
    {
        ReviewCount.Text = _discoveries.Count == 1 ? "1 item for review" : $"{_discoveries.Count} items for review";
        AutomationProperties.SetName(OpenReviews, ReviewCount.Text);
    }

    private void RefreshDiscoveriesAfterScan()
    {
        if (_page == Page.Discoveries) Render();
        else RefreshDiscoveryIndicators();
    }

    private async Task ScanLibrary()
    {
        if (_library.Folder is not { } folder || _closed) return;
        if (_playing.Count > 0) { _rescan = true; return; }
        _scan?.Cancel();
        if (!Directory.Exists(folder)) { StartWatcher(); _discoveries.Clear(); RefreshDiscoveriesAfterScan(); return; }
        if (_watcher == null) StartWatcher();
        var work = new CancellationTokenSource();
        _scan = work;
        Status("Scanning library folder…");
        try
        {
            var found = await Task.Run(() => Discovery.Scan(folder, work.Token), work.Token);
            if (_scan != work || _closed) return;
            var excluded = new HashSet<string>(_library.Games.Select(game => game.Path).Concat(_library.IgnoredPaths), StringComparer.OrdinalIgnoreCase);
            _discoveries = found.Where(item => !excluded.Contains(item.Path)).ToList();
            RefreshDiscoveriesAfterScan();
            if (StatusText.Text == "Scanning library folder…") Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (_scan == work && !_closed) Status("Could not scan this folder. " + error.Message);
        }
        finally { if (_scan == work) _scan = null; work.Dispose(); }
    }

    private void ChangeFolder(string? folder)
    {
        if (!String.Equals(_library.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            _library.Folder = folder;
            _scan?.Cancel();
            _scan = null;
            _discoveries.Clear();
            StartWatcher();
            Render();
            if (Save()) Status(folder == null ? "Folder watch turned off." : "Library folder updated.");
        }
        Scan();
    }

    private void Ignore(Discovery item)
    {
        _library.IgnoredPaths.Add(item.Path);
        _discoveries.Remove(item);
        if (Save()) Status("Discovery ignored.");
        Render();
    }

    private void Launch(Game? game)
    {
        if (_updateHandoff) return;
        if (game == null || _playing.Contains(game.Id)) return;
        if (!CanPlayWithMods(game)) { Status("Finish or recover the mod file operation before launching this game."); return; }
        try
        {
            _sessions.Launch(game.Id, game.Path, game.Arguments, _library.Preferences.SetProcessPriority, TrackPlaytime);
            _playing.Add(game.Id);
            game.LastPlayedUtc = DateTime.UtcNow;
            if (Save()) Status("Launched " + game.Title + ".");
            if (_scan != null) { _rescan = true; _scan.Cancel(); _scan = null; }
            _background.Cancel();
            _background.Dispose();
            _background = new CancellationTokenSource();
            Render();
            if (!_library.Preferences.CloseWhenGameLaunched || !HideToTray()) WindowState = WindowState.Minimized;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            Status("Could not launch " + game.Title + ".");
            _ = Notice("Could not launch " + game.Title, error.Message);
        }
    }

    private void EndSession(string id, long seconds)
    {
        if (_closed || _allowClose || !_playing.Remove(id)) return;
        seconds = _sessions.Acknowledge(id);
        var returnedFromPlay = _playing.Count == 0;
        if (returnedFromPlay) RecordGameReturn();
        var game = _library.Games.Find(entry => entry.Id == id);
        if (game != null) game.PlaySeconds += seconds;
        var saved = (game != null || returnedFromPlay) && Save();
        if (saved && game != null && TrackPlaytime) Status("Saved playtime for " + game.Title + ".");
        if (returnedFromPlay)
        {
            RestoreWindow();
            if (_rescan) { _rescan = false; Scan(); }
            foreach (var pending in _pendingMatches.ToArray()) TryMatch(pending);
            QueueSteamMetadata();
            if (_rssRefreshPending) RequestRssRefresh();
        }
        Render();
        if (returnedFromPlay) TryShowContributionReminder();
        TryAutomaticUpdate();
    }

    private bool CanFetch => _ready && !_closed && !_verification && _playing.Count == 0;

    private async void TryMatch(Game game)
    {
        if (!_library.Games.Contains(game) || game.SteamId > 0) { _pendingMatches.Remove(game); return; }
        if (!CanFetch) { _pendingMatches.Add(game); return; }
        if (!_matching.Add(game)) return;
        _pendingMatches.Remove(game);
        var title = game.Title;
        var token = _background.Token;
        try
        {
            var match = SteamCatalog.ExactMatch(title, await _steam.Search(title, token));
            if (token.IsCancellationRequested || !_library.Games.Contains(game) || title != game.Title || game.SteamId > 0) return;
            if (match == null) { Status("No exact Steam match. Use Edit game to select one."); return; }
            game.SteamId = match.Id;
            InvalidateEntry(game);
            if (Save()) Status("Matched " + title + " to Steam.");
            Render();
            QueueSteamMetadata();
        }
        catch (OperationCanceledException) { if (token.IsCancellationRequested && !_closed) _pendingMatches.Add(game); }
        catch (HttpRequestException) { if (!_closed) Status("Steam lookup is unavailable. Try again in Edit game."); }
        finally
        {
            _matching.Remove(game);
            if (CanFetch && _pendingMatches.Contains(game)) TryMatch(game);
        }
    }

    private static TextBlock Label(string value, bool muted = false)
    {
        var label = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        if (muted) label.Classes.Add("lg-muted");
        return label;
    }

    private static TextBlock Heading(string value) => new() { Text = value, Classes = { "lg-heading" }, TextWrapping = TextWrapping.Wrap };
    private static HeaderedContentControl Section(string title, Control content) => new() { Header = title, Content = content, Classes = { "lg-section" } };
    private static WrapPanel Actions(params Control[] controls)
    {
        var row = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static Button ActionButton(string title, Action action, string? variant = null)
    {
        var button = new Button { Content = title, Classes = { "lg" } };
        if (variant != null) button.Classes.Add(variant);
        Click(button, action);
        return button;
    }

    private sealed class LauncherCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
