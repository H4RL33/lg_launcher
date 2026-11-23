using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    internal async Task VerifyMigration(string preview, bool legacyFixture)
    {
        UpdateLayout();
        Require(WindowTitle.Text == $"LG Launcher v{UpdateFeed.CurrentVersion.ToString(3)}" && WindowTitle.Text == Title,
            "The title bar did not show the running launcher version.");
        if (OperatingSystem.IsWindows())
            Require(WindowsDesktop.HasNativeCaption(this) &&
                this.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Text == Title && text.IsVisible) == 1,
                "The main window lost its native caption style or drew more than one title.");
        Require(LibraryGames.ItemCount == _library.Games.Count, "The sidebar did not load all games.");
        if (_library.Preferences.InterfaceFont is { } savedFont)
            Require(FontFamily.Name == savedFont, "The persisted interface font was not applied.");
        if (!legacyFixture)
        {
            var softwareRendering = OperatingSystem.IsWindows() && _library.Preferences.DisableHardwareRendering;
            if (softwareRendering)
            {
                var compositor = Compositor.TryGetDefaultCompositor()
                    ?? throw new InvalidOperationException("The Windows launcher did not start a compositor.");
                Require(await compositor.TryGetCompositionGpuInterop() == null,
                    "Restarting with hardware rendering disabled still exposed GPU rendering.");
            }
            StartWatcher();
            Require(ToolTip.GetTip(ManageFolder) as string == (_library.Folder ?? "Choose a library folder"),
                "The existing watched folder setting did not render.");
            if (_library.Games.FirstOrDefault() is { } existing)
            {
                SelectGame(existing);
                await WaitFor(() => _detailsRequest == null, "The existing game's artwork did not finish loading.");
                VerifyDetail(existing);
                if (File.Exists(_store.CustomArtworkPath(existing.CustomArtworkId)) ||
                    File.Exists(_steam.CachedArtwork(existing.SteamId)) ||
                    File.Exists(Path.Combine(_store.ArtworkDirectory, "backdrops", existing.SteamId.ToString(CultureInfo.InvariantCulture) + ".jpg")))
                    Require(_artwork.Picture != null, "The existing cached artwork was not rendered.");
            }
            if (softwareRendering)
            {
                Navigate(Page.Behaviour);
                UpdateLayout();
                Require(PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Single(toggle => AutomationProperties.GetName(toggle) == "Hardware rendering").IsChecked == false,
                    "App Behaviour did not restore the saved rendering preference.");
            }
            if (Program.VerificationMode == "--verify-setup") await VerifySetupUpdates(preview);
            Capture(preview);
            return;
        }

        var game = _library.Games.Single(entry => entry.Id == "manual");
        var older = _library.Games.Single(entry => entry.Id == "prezoom");
        var dynamicCollection = _library.Collections.Single(collection => collection.Id == "racing");
        Require(_library.UsageNoticeAccepted && _library.Preferences.InterfaceFont == "Segoe UI", "Legacy settings did not load.");
        Require(_library.Folder == Program.DataDirectory && _library.IgnoredPaths.Count == 1, "Folder settings did not load.");
        Require(_library.Collections.Single(collection => collection.Id == "retro").Includes(game), "Manual collection membership did not load.");
        Require(dynamicCollection.IsDynamic && dynamicCollection.Includes(game) && !dynamicCollection.Includes(older), "Dynamic collection membership is incorrect.");
        var multiple = new GameCollection { GenreFilters = ["racing", "Puzzle"] };
        Require(!multiple.Includes(game), "An all-genres collection included a partial match.");
        multiple.MatchAnyGenre = true;
        Require(multiple.Includes(game), "An any-genre collection ignored a matching genre.");
        Require(game.ManualGenres && game.Genres.SequenceEqual(["Racing", "Simulation"]) &&
            _library.Preferences.StartMinimised && _library.Preferences.CloseToTray &&
            _library.Preferences.CloseWhenGameLaunched, "Saved genre or app behaviour options did not load.");
        Require(game.Arguments == "--windowed" && game.SteamId == 2380050 && game.PlaySeconds == 4199 &&
            game.LastPlayedUtc == DateTimeOffset.FromUnixTimeMilliseconds(1763589600000).UtcDateTime, "Game details did not load.");
        Require(older.ArtworkPosition.X == .67 && older.ArtworkPosition.Y == .29 && older.ArtworkPosition.Zoom == 1,
            "Framing saved before zoom support changed.");
        Require(game.ArtworkPosition.X == .71 && game.ArtworkPosition.Y == .39 && game.ArtworkPosition.Zoom == 2.4,
            "The manual crop was not loaded.");
        var homeViews = PageBody.GetVisualDescendants().OfType<Control>().ToArray();
        Require(PageBody.Children.Select(child => child is TextBlock text ? text : (child as Grid)?.Children.FirstOrDefault() as TextBlock)
            .OfType<TextBlock>().Select(heading => heading.Text)
            .SequenceEqual(["Recently played", "Collections", "Library"]), "Home did not show all three sections in order.");
        var recentRow = homeViews.OfType<ScrollViewer>().Single(view => view.Name == "RecentlyPlayedRow");
        var collectionRow = homeViews.OfType<ScrollViewer>().Single(view => view.Name == "HomeCollectionsRow");
        var homeLibrary = homeViews.OfType<ItemsControl>().Single(view => view.Name == "HomeLibraryGrid");
        Require(recentRow.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
            collectionRow.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden &&
            collectionRow.GetVisualDescendants().OfType<CollectionCard>().Count() == _library.Collections.Count &&
            homeLibrary.Items.Cast<Game[]>().SelectMany(row => row).SequenceEqual(new[] { older, game }),
            "Home did not show scrollable shelves and a title-sorted full library.");
        var recentCard = recentRow.GetVisualDescendants().OfType<GameCard>().Single();
        await WaitFor(() => recentCard.Artwork != null, "The recent game's cached portrait did not load.");
        ToolTip.SetPlacement(recentCard, PlacementMode.Bottom);
        ToolTip.SetIsOpen(recentCard, true);
        var gameTip = OpenedPopups.Where(popup => popup.PlacementTarget == recentCard)
            .Select(popup => popup.Child).OfType<ToolTip>().Single();
        await WaitFor(() => gameTip.Opacity == 1, "The game tooltip did not open.");
        var styledGameTip = gameTip.CornerRadius.TopLeft == 8 && gameTip.BorderThickness == new Thickness(1) &&
            Equals(gameTip.Background, this.FindResource("LG.SurfaceRaised")) && Equals(gameTip.Foreground, this.FindResource("LG.Text")) &&
            gameTip.FontFamily.Name == FontFamily.Name && gameTip.Content is string tipText && tipText.Contains(game.Title);
        var tipDescription = $"{gameTip.Background}, {gameTip.Foreground}, {gameTip.CornerRadius}, {gameTip.BorderThickness}, {gameTip.FontFamily.Name} (expected {FontFamily.Name})";
        CaptureControl(gameTip, Path.Combine(Path.GetDirectoryName(preview)!, "game-tooltip.png"));
        ToolTip.SetIsOpen(recentCard, false);
        ToolTip.SetPlacement(recentCard, PlacementMode.Pointer);
        var tileControls = recentCard.GetVisualDescendants().OfType<Control>().ToArray();
        var portrait = tileControls.OfType<Grid>().SingleOrDefault(frame => frame.Name == "PortraitFrame");
        var tileActions = tileControls.OfType<Button>().ToArray();
        Require(portrait != null && Math.Abs(recentCard.Bounds.Height - portrait.Bounds.Height) < 1 &&
            tileControls.OfType<Border>().Any(border => border.Name == "PlaytimePill" && !border.IsVisible) &&
            portrait.Children[0].Classes.Contains("lg-artwork-clip") && portrait.Children[1].Classes.Contains("lg-artwork-outline"),
            "The recently played tile kept its playtime pill or lost its inset artwork outline.");
        Require(tileActions.Length == 1 && tileActions[0].Command != null &&
            Avalonia.Automation.AutomationProperties.GetName(tileActions[0]) == game.Title &&
            Math.Abs(recentCard.ArtworkHeight / (recentCard.Bounds.Width - 2) - 1.5) < .01,
            "The recently played tile did not preserve its detail action and portrait proportions.");
        tileActions[0].RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = ContextRequestedEvent });
        Require(recentCard.ContextMenu is { IsOpen: true }, "The portrait did not open its game context menu.");
        var collectionAction = recentCard.ContextMenu!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Add to collection"));
        recentCard.ContextMenu.Close();
        collectionAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitFor(() => DialogHost.HasModal, "The game menu did not open its collection picker.");
        UpdateLayout();
        var collectionSearch = DialogHost.GetVisualDescendants().OfType<TextBox>().Single();
        var pickerGrid = DialogHost.GetVisualDescendants().OfType<UniformGrid>().Single();
        var retro = pickerGrid.Children.OfType<CollectionCard>().Single();
        Require(retro.Title == "Retro" && retro.Classes.Contains("selected"), "The picker included dynamic collections or lost existing membership.");
        collectionSearch.Text = "zzzzz";
        await WaitFor(() => pickerGrid.Children.Count == 0, "The collection picker did not filter unmatched names.");
        collectionSearch.Text = "rto";
        await WaitFor(() => pickerGrid.Children.Count == 1 && pickerGrid.Children[0] == retro, "The picker did not match a fuzzy search.");
        collectionSearch.Text = "retor";
        await WaitFor(() => CollectionSearchScore("Retro", ["retor"]) == 2 && pickerGrid.Children.Count == 1,
            "The picker did not match a transposed letter.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "add-to-collection.png"));
        retro.Command!.Execute(null);
        Require(game.CollectionIds.Contains("retro"), "Toggling a collection changed membership before saving.");
        DialogHost.DismissTop();
        Require(game.CollectionIds.Contains("retro"), "Cancelling the picker changed the saved collection.");
        foreach (var shouldBeMember in new[] { false, true })
        {
            var change = ShowCollectionPicker(game);
            UpdateLayout();
            DialogHost.GetVisualDescendants().OfType<CollectionCard>().Single().Command!.Execute(null);
            var submit = DialogHost.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes"));
            Require(submit.IsEnabled, "Toggling a manual collection did not enable saving.");
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await change;
            Require(_store.Load().Games.Single(item => item.Id == game.Id).CollectionIds.Contains("retro") == shouldBeMember,
                "The collection picker did not save the new membership.");
        }
        await VerifyHomeOverflow(preview);
        Require(styledGameTip, $"The game tooltip did not use the launcher theme and selected font. Actual: {tipDescription}.");
        Require(SearchBox.VerticalContentAlignment == VerticalAlignment.Center, "The search field did not center its content.");
        SearchBox.Focus();
        SearchBox.Text = "racing";
        await WaitFor(() => SearchDropdown.IsVisible, "Typing did not open the search dropdown.");
        Require(LibraryGames.ItemCount == _library.Games.Count && PageTitle.Text == "Home" &&
            SearchResults.ItemCount == 1 && SearchResults.Items[0] is LibraryEntry result && result.Game == game,
            "Genre search did not show the matching game independently of the sidebar and page.");
        Require(HandleSearchKey(Key.Escape) && !SearchDropdown.IsVisible, "Escape did not dismiss the dropdown.");
        await VerifySearchMotion();
        SearchBox.Text = "no such game";
        await WaitFor(() => SearchDropdown.IsVisible && SearchEmpty.IsVisible, "Search did not show its empty state.");
        SearchBox.Text = "legacy";
        Require(HandleSearchKey(Key.Down) && SearchResults.SelectedItem is LibraryEntry selected && selected.Game == game &&
            HandleSearchKey(Key.Enter) && _page == Page.Game && _game == game && !SearchDropdown.IsVisible,
            "Keyboard search did not select and open the matching game.");
        await VerifyCollectionCards(dynamicCollection, preview);
        await VerifyCollectionIcons(dynamicCollection, preview);
        VerifyNavigationHistory(game, older, dynamicCollection);
        await VerifySidebarCollections(dynamicCollection, preview);
        var collectionGame = PageBody.Children.OfType<UniformGrid>().Single().GetVisualDescendants().OfType<GameCard>().Single();
        Require(collectionGame.Title == game.Title, "The dynamic collection page did not show its matching game.");
        UpdateLayout();
        var frame = collectionGame.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "PortraitFrame");
        var pill = collectionGame.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PlaytimePill");
        Require(pill.IsVisible && Math.Abs(frame.Bounds.Bottom - pill.Bounds.Center.Y) < 1 &&
            Math.Abs(frame.Bounds.Center.X - pill.Bounds.Center.X) < 1 &&
            pill.Child is TextBlock pillLabel && pillLabel.Text == $"{game.PlaySeconds / 3600d:0.#} hr" &&
            pillLabel.FontWeight == FontWeight.Light && pillLabel.FontFamily == FontFamily && pill.CornerRadius.TopLeft < pill.Bounds.Height / 2,
            "The collection portrait lost its centered playtime pill or changed its style.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "portrait-collection.png"));
        var previousGenres = game.Genres;
        game.Genres = ["Puzzle"];
        Render();
        Require(!PageBody.Children.OfType<UniformGrid>().Any(), "Dynamic collection did not react to a genre change.");
        game.Genres = previousGenres;

        var fakeGenres = SteamCatalog.ParseGenres(10, """{"10":{"success":true,"data":{"genres":[{"description":"Action"},{"description":"action"},{"description":"Adventure"},{"description":"Racing"},{"description":"Simulation"}]}}}""");
        Require(fakeGenres.SequenceEqual(["Action", "Adventure", "Racing", "Simulation"]), "Steam genres were not deduplicated and retained.");
        Require(!UpdateFeed.Read("""{"tag_name":"v2.0.0","prerelease":true}""", new Version(1, 1, 0)).UpdateAvailable &&
            !UpdateFeed.Read("""{"tag_name":"v2.0.0","draft":true}""", new Version(1, 1, 0)).UpdateAvailable &&
            !UpdateFeed.Read("""{"tag_name":"v1.1.0"}""", new Version(1, 1, 0, 0)).UpdateAvailable &&
            !UpdateFeed.Read("""{"version":"2.0.0","downloadUrl":"https://example.net/launcher.exe"}""", new Version(1, 1, 0)).UpdateAvailable,
            "A draft, pre-release, installed version, or custom manifest was advertised as an update.");
        Require(!UpdateFeed.Read("""{"tag_name":"v1.2.2"}""", new Version(0, 1, 6)).UpdateAvailable &&
            UpdateFeed.Read("""{"tag_name":"v0.1.7"}""", new Version(0, 1, 6)).UpdateAvailable,
            "The version reset offered a historical build or blocked a newer 0.1.x release.");
        const string asset = """{"tag_name":"v99.2.0","assets":[{"name":"LG-Launcher.exe","state":"uploaded","size":123,"digest":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","browser_download_url":"https://github.com/H4RL33/lg_launcher/releases/download/v99.2.0/LG-Launcher.exe"}]}""";
        Require(UpdateFeed.Read(asset, new Version(1, 1)).Installer?.Length == 123 &&
            UpdateFeed.Read(asset.Replace("H4RL33/lg_launcher", "other/lg_launcher"), new Version(1, 1)).Installer == null &&
            UpdateFeed.Read(asset.Replace("sha256:", "md5:"), new Version(1, 1)).Installer == null,
            "The installer accepted an unverified package or rejected the official asset.");
        using (var client = new HttpClient(new ReleaseFixture()))
        {
            var check = await UpdateFeed.Check(CancellationToken.None, client);
            Require(check.UpdateAvailable && check.ReleasePage?.AbsoluteUri == "https://github.com/H4RL33/lg_launcher/releases/tag/v99.2.0",
                "The official update check did not link to the expected GitHub release.");
        }

        Require(FolderStatus.Tone == StatusTone.Active, "The existing watched folder did not start.");
        Require(_discoveries.Single().Path == Path.Combine(Program.DataDirectory, "New Discovery.exe"),
            "The scan lost the saved ignored path.");
        var desktop = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        Require(_startupComplete && desktop.MainWindow == this && !desktop.Windows.OfType<StartupWindow>().Any() &&
            ReviewCount.Text == "1 item for review" &&
            Grid.GetColumn(ManageFolder) < Grid.GetColumn(OpenReviews) && Grid.GetColumn(OpenReviews) < Grid.GetColumn(GameCount),
            "Startup did not complete or the status line is out of order.");

        SelectGame(game);
        await WaitFor(() => _detailsRequest == null, "The custom artwork did not finish loading.");
        VerifyDetail(game);
        Require(_artwork.Picture != null, "Legacy replacement artwork was not decoded.");
        UpdateLayout();
        Require(_chromeArtwork.IsVisible && _chromeArtwork.Picture == _searchArtwork.Picture &&
            Equals(DragBar.Background, this.FindResource("LG.ArtworkGlass")) &&
            Equals(LibrarySurface.Background, DragBar.Background) && Equals(StatusSurface.Background, DragBar.Background),
            "The window chrome did not share the game's frosted artwork.");
        await VerifyArtworkParallax();
        Require(GenreChips.Children.Count == 2, "The detail view did not show the genres.");
        _motion = true;
        SendArtworkPointer(LibrarySurface, new Point(30, 100));
        await WaitFor(() => ArtworkHost.RenderTransform?.Value.M31 > 4,
            "Moving the pointer over the sidebar did not shift the artwork.");
        Classes.Add("lg-motion");
        GameMenu(GameOptions, game);
        Require(GameOptions.ContextMenu?.Items.OfType<MenuItem>().Any(item => item.Header as string == "Open Folder" && item.Icon is SymbolIcon) == true,
            "The game menu did not include the Open Folder action.");
        var gameMenu = (FrostedContextMenu)GameOptions.ContextMenu!;
        Require(gameMenu.RevealProgress < 1, "The game menu did not start its top-down reveal.");
        var openBounds = gameMenu.Bounds;
        var revealSurface = gameMenu.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "RevealSurface");
        var openingClip = revealSurface.Clip;
        RectangleGeometry? intermediateClip = null;
        var revealPreview = Path.Combine(Path.GetDirectoryName(preview)!, "game-menu-reveal.png");
        var capturedReveal = false;
        var previousProgress = gameMenu.RevealProgress;
        var revealWentBackwards = false;
        void ObserveReveal(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != FrostedContextMenu.RevealProgressProperty) return;
            var progress = gameMenu.RevealProgress;
            revealWentBackwards |= progress < previousProgress;
            previousProgress = progress;
            if (progress is <= 0 or >= 1) return;
            intermediateClip ??= revealSurface.Clip as RectangleGeometry;
            if (capturedReveal || progress is < .4 or > .85) return;
            CaptureControl(gameMenu, revealPreview);
            capturedReveal = true;
        }

        gameMenu.PropertyChanged += ObserveReveal;
        await WaitFor(() => gameMenu.RevealProgress == 1, "The game menu reveal did not finish.");
        var menuHost = TopLevel.GetTopLevel(gameMenu)!;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        menuHost.RequestAnimationFrame(_ => menuHost.RequestAnimationFrame(_ => settled.SetResult()));
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gameMenu.PropertyChanged -= ObserveReveal;
        Require(intermediateClip != null && !ReferenceEquals(openingClip, intermediateClip) &&
            intermediateClip.Rect.Height > 0 && intermediateClip.Rect.Height < openBounds.Height,
            "The menu did not publish an intermediate clip to the renderer.");
        Require(!revealWentBackwards && gameMenu.RevealProgress == 1 && revealSurface.Clip == null,
            "The menu flickered backwards before settling in its fully open state.");
        Require(gameMenu.Bounds == openBounds, "The menu moved or resized as its content was revealed.");
        Require(ArtworkHost.RenderTransform?.Value.M11 > 1, "Opening the menu reset the artwork shift.");
        VerifyMenuTheme(gameMenu, Path.Combine(Path.GetDirectoryName(preview)!, "game-menu.png"));
        SendArtworkPointer(gameMenu, new Point(gameMenu.Bounds.Width - 5, gameMenu.Bounds.Height - 5));
        await WaitFor(() => ArtworkHost.RenderTransform?.Value is { M31: < 4, M32: < -3 },
            "Artwork parallax stopped when the pointer moved inside the context menu.");
        var collapseFrames = new List<double> { 1 };
        void ObserveCollapse(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == FrostedContextMenu.RevealProgressProperty && gameMenu.IsOpen)
                collapseFrames.Add(gameMenu.RevealProgress);
        }
        gameMenu.PropertyChanged += ObserveCollapse;
        gameMenu.Close();
        Require(gameMenu.IsOpen, "The context menu closed before it could collapse.");
        gameMenu.Close();
        await WaitFor(() => !gameMenu.IsOpen, "The collapsed context menu remained open.");
        gameMenu.PropertyChanged -= ObserveCollapse;
        Require(collapseFrames.Any(progress => progress is > 0 and < 1) && collapseFrames[^1] == 0 &&
            collapseFrames.Zip(collapseFrames.Skip(1)).All(pair => pair.First >= pair.Second),
            "The menu did not collapse smoothly to the top before closing.");
        _motion = false;
        ResetArtworkParallax(immediate: true);
        Classes.Remove("lg-motion");
        Details.ContextMenu!.Placement = PlacementMode.Center;
        Details.ContextMenu.Open(Details);
        Require(((FrostedContextMenu)Details.ContextMenu).RevealProgress == 1,
            "The artwork menu animated with motion disabled.");
        VerifyMenuTheme(Details.ContextMenu, Path.Combine(Path.GetDirectoryName(preview)!, "artwork-menu.png"));
        Require(Details.ContextMenu.Items.OfType<MenuItem>().Count(item => item.Icon is SymbolIcon) == 3,
            "The artwork menu did not show an icon for each action.");
        Details.ContextMenu.Close();
        Require(!Details.ContextMenu.IsOpen, "The artwork menu animated its close with motion disabled.");
        Details.ContextMenu.Placement = PlacementMode.Pointer;
        await VerifyLauncherModals(game, preview);
        Capture(preview);
        SearchBox.Text = "genre: racing";
        ShowSearchResults();
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "search.png"));
        Require(SearchDropdown.IsVisible && _resultsArtwork.Picture == _chromeArtwork.Picture &&
            SearchDropdown.TranslatePoint(default, SearchBox) is { Y: var dropdownY } && dropdownY >= SearchBox.Bounds.Height,
            "The glass search results were not anchored below the search field.");
        var oldGlassCrop = _chromeArtwork.Crop;
        BeginArtworkMove();
        Require(_editingArtwork && !SearchSurface.IsVisible && !SearchDropdown.IsVisible &&
            ArtworkTools.ZoomLabel == 2.4.ToString("P0", CultureInfo.CurrentCulture),
            "The artwork toolbar did not show the current zoom or close search.");
        ZoomArtwork(1, _artwork.Bounds.Center);
        HandleArtworkKey(Avalonia.Input.Key.Left);
        Require(_chromeArtwork.Crop != oldGlassCrop, "The glass did not follow artwork panning and zoom.");
        var changed = _draftCenter;
        var zoom = _draftZoom;
        ArtworkTools.SaveCommand!.Execute(null);
        Require(!_editingArtwork, "The artwork toolbar did not save.");
        var saved = _store.Load();
        var savedGame = saved.Games.Single(entry => entry.Id == game.Id);
        Require(Math.Abs(savedGame.ArtworkPosition.X - changed.X) < 1e-9 && Math.Abs(savedGame.ArtworkPosition.Y - changed.Y) < 1e-9 &&
            Math.Abs(savedGame.ArtworkPosition.Zoom - zoom) < 1e-9 && savedGame.ArtworkPosition.Zoom > 2.4,
            "The updated position and zoom did not survive a save and reload.");
        Require(savedGame.CustomArtworkId == game.CustomArtworkId && savedGame.PlaySeconds == game.PlaySeconds &&
            savedGame.LastPlayedUtc == game.LastPlayedUtc && savedGame.CollectionIds.SequenceEqual(["retro"]) &&
            saved.Preferences.InterfaceFont == "Segoe UI" && saved.UsageNoticeAccepted,
            "Updating framing altered other saved library data.");
        VerifyDetail(game);
        BeginArtworkMove();
        ZoomArtwork(-2, _artwork.Bounds.Center);
        ArtworkTools.CancelCommand!.Execute(null);
        VerifyDetail(game);

        SearchBox.Focus();
        SearchBox.Text = older.Title;
        await WaitFor(() => SearchDropdown.IsVisible && SearchResults.ItemCount == 1 &&
            SearchResults.Items[0] is LibraryEntry match && match.Game == older, "The game's title did not open a search result.");
        UpdateLayout();
        var resultRow = (ListBoxItem)SearchResults.ContainerFromIndex(0)!;
        var pointer = new Pointer(0, PointerType.Mouse, true);
        var pointerAtRow = resultRow.TranslatePoint(resultRow.Bounds.Center - resultRow.Bounds.Position, this) ?? default;
        var released = new PointerReleasedEventArgs(resultRow, pointer, this, pointerAtRow, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left);
        resultRow.RaiseEvent(new TappedEventArgs(TappedEvent, released));
        Require(_page == Page.Game && _game == older && !SearchDropdown.IsVisible,
            "Clicking a result did not open the game and close the search dropdown.");
        await WaitFor(() => _detailsRequest == null, "The game without artwork did not finish loading.");
        Require(!_chromeArtwork.IsVisible && Equals(LibrarySurface.Background, this.FindResource("LG.Sidebar")),
            "The standard shell did not return for a game without artwork.");
        SelectGame(game);
        await WaitFor(() => _detailsRequest == null, "The previous artwork did not finish loading again.");
        Navigate(Page.Settings);
        UpdateLayout();
        Require(!_chromeArtwork.IsVisible && Equals(LibrarySurface.Background, this.FindResource("LG.Sidebar")),
            "Settings retained the game's frosted artwork.");
        Require(PageBody.Children.OfType<Button>().Count() == 6, "Settings did not show all six sections, including Mods.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "settings.png"));
        await VerifyMods(game, preview);
        await VerifyRss(preview);
        Navigate(Page.Appearance);
        UpdateLayout();
        Require(_viewMotion.Count == 0 && ContentScroll.Opacity == 1, "Navigation moved with animations disabled.");
        var fonts = _fontPicker!;
        Require(fonts.IsVisible && Avalonia.Automation.AutomationProperties.GetHelpText(fonts) == "Current value Segoe UI" &&
            PageBody.GetVisualDescendants().OfType<ListBox>().All(item => !item.IsVisible),
            "Appearance did not display the saved font in one dropdown.");
        await ChoosePreference(fonts, "System default");
        Require(_store.Load().Preferences.InterfaceFont == null && FontFamily == _defaultFont,
            "Choosing System default did not reset and save the font.");
        await ChoosePreference(fonts, "Segoe UI");
        Require(_store.Load().Preferences.InterfaceFont == "Segoe UI" && FontFamily.Name == "Segoe UI",
            "Choosing a font did not apply and save it immediately.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "appearance.png"));
        await VerifyPreferences(game, preview);
        await VerifyGameMetadata(preview);
        await VerifyCollectionEditor(dynamicCollection, preview);
        _motion = true;
        Navigate(Page.Behaviour);
        await WaitFor(() => ContentScroll.Opacity < .96 && ContentScroll.RenderTransform?.Value.M32 > .05,
            "The view did not animate both opacity and position.");
        Navigate(Page.Appearance);
        Navigate(Page.Behaviour);
        await WaitFor(() => _viewMotion.Count == 0, "The interrupted view transitions did not complete.");
        Require(ContentScroll.Opacity == 1 && Math.Abs(ContentScroll.RenderTransform?.Value.M32 ?? 0) < .01,
            "The new view retained an interrupted transform.");
        _motion = false;
        UpdateLayout();
        var switches = PageBody.GetVisualDescendants().OfType<ToggleSwitch>().ToArray();
        Require(switches.Any(toggle => AutomationProperties.GetName(toggle) == "Contribution reminders" && toggle.IsChecked == true),
            "App Behaviour did not enable contribution reminders by default.");
        var count = OperatingSystem.IsWindows() ? 8 : 6;
        var uncheckedCount = OperatingSystem.IsWindows() ? 3 : 2;
        Require(switches.Length == count && switches.Count(toggle => toggle.IsChecked != true) == uncheckedCount && !_library.Preferences.AutomaticUpdates,
            "App Behaviour did not display its persisted options or enable updates by opt-in.");
        Require(PageBody.GetVisualDescendants().OfType<Button>().All(button => button.Content as string != "Check for new updates"),
            "The manual update check was still in App Behaviour.");
        var automatic = switches.Single(toggle => Avalonia.Automation.AutomationProperties.GetName(toggle) == "Automatic updates");
        automatic.IsChecked = true;
        Require(_store.Load().Preferences.AutomaticUpdates, "The automatic update setting was not saved.");
        automatic.IsChecked = false;
        Require(!_store.Load().Preferences.AutomaticUpdates, "The automatic update setting could not be disabled.");
        if (OperatingSystem.IsWindows()) await VerifyRenderingPreference(preview);
        await VerifyFirstUsageReset(automatic, preview);
        var tool = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (OperatingSystem.IsWindows() && File.Exists(tool))
        {
            Require(WindowsDesktop.LauncherStartupCommand() == "\"" + Environment.ProcessPath + "\"",
                "The Windows startup command did not quote the executable path.");
            var closeToTray = _library.Preferences.CloseToTray;
            var hideWhilePlaying = _library.Preferences.CloseWhenGameLaunched;
            var testGame = new Game { Title = "Process test", Path = tool, Arguments = "/d /c exit 0" };
            var reminders = switches.Single(toggle => AutomationProperties.GetName(toggle) == "Contribution reminders");
            _library.Games.Add(testGame);
            try
            {
                Require(_store.Load().GameReturns == 0 && !DialogHost.HasModal, "Opening the launcher counted as a return from play.");
                _library.Preferences.CloseToTray = false;
                Launch(testGame);
                Require(!IsVisible && _tray?.IsVisible == true, "Launching with Close when Game Launched did not hide the window to the tray.");
                await WaitFor(() => !_playing.Contains(testGame.Id) && IsVisible && _tray?.IsVisible == false,
                    "The launcher did not restore itself from the tray after the process exited.");
                Require(_store.Load().GameReturns == 1, "The first return from a game was not saved.");
                var firstReminder = ContributionReminder();
                await VerifyContributionReminderLayout(firstReminder, reminders, preview);
                CloseReminder(firstReminder);

                _library.Preferences.CloseWhenGameLaunched = false;
                _library.GameReturns = 48;
                Require(Save(), "Could not seed the return count near the reminder interval.");
                _library.GameReturns = _store.Load().GameReturns;
                Launch(testGame);
                Require(WindowState == WindowState.Minimized, "Launching a game did not minimize the visible window.");
                await WaitFor(() => !_playing.Contains(testGame.Id) && WindowState == WindowState.Normal && IsVisible,
                    "The launcher did not restore itself from the taskbar after the process exited.");
                var savedAfterReturn = _store.Load();
                Require(savedAfterReturn.Games.Single(entry => entry.Id == testGame.Id).LastPlayedUtc.HasValue, "The launch date did not persist.");
                Require(savedAfterReturn.GameReturns == 49 && !DialogHost.HasModal, "An ordinary game return opened a reminder or was not saved.");

                var notice = Notice("Fixture notice", "The reminder should wait for this notice to close.");
                Launch(testGame);
                await WaitFor(() => !_playing.Contains(testGame.Id), "The fiftieth game process did not finish.");
                Require(_store.Load().GameReturns == 50 && DialogHost.Children.OfType<ModalDialog>().Count() == 1,
                    "The fiftieth return was not saved or the reminder interrupted the active notice.");
                DialogHost.DismissTop();
                await notice;
                await WaitFor(() => DialogHost.HasModal, "The contribution reminder was not shown after the notice closed.");
                var recurringReminder = ContributionReminder();
                Require(recurringReminder.Body.Children.OfType<TextBlock>().Select(label => label.Text)
                    .SequenceEqual(firstReminder.Body.Children.OfType<TextBlock>().Select(label => label.Text)),
                    "The recurring reminder used different copy from the first prompt.");
                CloseReminder(recurringReminder);

                reminders.IsChecked = false;
                Require(_store.Load().Preferences.DisableContributionReminders, "The contribution reminder opt-out was not saved.");
                _library.GameReturns = 99;
                Launch(testGame);
                await WaitFor(() => !_playing.Contains(testGame.Id), "The opted-out game process did not finish.");
                Require(_store.Load().GameReturns == 100 && !DialogHost.HasModal, "Turning reminders off did not suppress the next prompt.");
                reminders.IsChecked = true;
                Require(!_store.Load().Preferences.DisableContributionReminders && !DialogHost.HasModal,
                    "Re-enabling reminders prompted for a return that had already been skipped.");
            }
            finally
            {
                _library.Preferences.CloseToTray = closeToTray;
                _library.Preferences.CloseWhenGameLaunched = hideWhilePlaying;
                _library.Games.Remove(testGame);
                Require(Save(), "Could not remove the Windows process fixture.");
            }
        }

        ModalDialog ContributionReminder() => DialogHost.Children.OfType<ModalDialog>()
            .Single(dialog => AutomationProperties.GetName(dialog) == "Thanks for using LG Launcher");

        static void CloseReminder(ModalDialog dialog) =>
            dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Close"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private async Task VerifyContributionReminderLayout(ModalDialog dialog, ToggleSwitch setting, string preview)
    {
        UpdateLayout();
        var links = dialog.Body.GetVisualDescendants().OfType<Button>().Select(AutomationProperties.GetName);
        Require(links.SequenceEqual(["View on GitHub", "Visit Ko-fi"]) && dialog.InitialFocus is Button { Content: "Close" },
            "The contribution reminder did not have the expected links or initial focus.");
        var section = setting.GetVisualAncestors().OfType<HeaderedContentControl>()
            .Single(control => Equals(control.Header, "Miscellaneous"));

        var windowSize = (Width, Height);
        var previews = Path.GetDirectoryName(preview)!;
        try
        {
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                (Width, Height) = (size.Width, size.Height);
                await Task.Delay(45);
                setting.BringIntoView();
                UpdateLayout();
                Capture(Path.Combine(previews, $"contribution-reminder-{size.Width}.png"));
                CaptureControl(section, Path.Combine(previews, $"contribution-setting-{size.Width}.png"));
            }
        }
        finally { (Width, Height) = windowSize; }
    }

    private void VerifyDetail(Game game)
    {
        UpdateLayout();
        Require(Details.IsVisible && GameTitle.Text == game.Title && LastPlayed.Text == FormatDate(game.LastPlayedUtc),
            "The selected game's details did not render.");
        if (game.ArtworkPosition is not { } position || _artwork.Picture is not { } picture) return;
        var crop = ArtworkFraming.CenteredViewbox(picture.Size.AspectRatio, _artwork.Bounds.Size, new Point(position.X, position.Y), position.Zoom);
        Require(_artwork.Crop == crop && _artwork.Bounds.Width > 0, "The details view did not apply its saved crop.");
    }

    private async Task VerifyArtworkParallax()
    {
        var crop = _artwork.Crop;
        var glass = _chromeArtwork.Crop;
        _motion = true;
        try
        {
            SendArtworkPointer(LibrarySurface, new Point(5, LibrarySurface.Bounds.Height - 5));
            await WaitFor(() => ArtworkHost.RenderTransform?.Value is { M31: > 1, M32: < -1 },
                "Mouse parallax did not shift the artwork on both axes.");
            var transform = ArtworkHost.RenderTransform!.Value;
            Require(Math.Abs(transform.M12) < 1e-9 && Math.Abs(transform.M21) < 1e-9 &&
                Math.Abs(transform.M11 - transform.M22) < 1e-9,
                "Mouse parallax changed the artwork's proportions.");
            Require(_artwork.Crop == crop && _chromeArtwork.Crop != glass, "Parallax did not shift the glass with the artwork or changed the saved crop.");
            ResetArtworkParallax();
            await WaitFor(() => ArtworkHost.RenderTransform?.Value.IsIdentity == true,
                "The artwork did not return to its neutral position.");
            Require(_chromeArtwork.Crop == glass, "The glass did not return to its neutral position.");
            _motion = false;
            MoveArtworkParallax(new Point(0, ArtworkHost.Bounds.Height));
            Require(ArtworkHost.RenderTransform?.Value.IsIdentity == true, "Parallax ran with motion disabled.");
        }
        finally
        {
            _motion = false;
            ResetArtworkParallax(immediate: true);
        }
    }

    private void SendArtworkPointer(Control source, Point position)
    {
        var root = TopLevel.GetTopLevel(source)!;
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        source.RaiseEvent(new PointerEventArgs(PointerMovedEvent, source, pointer, root,
            source.TranslatePoint(position, root)!.Value, 0, new PointerPointProperties(), KeyModifiers.None));
    }

    private void VerifyMenuTheme(ContextMenu menu, string preview)
    {
        menu.UpdateLayout();
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Require(menu.IsOpen && menu.Classes.Contains("lg-menu") && menu.CornerRadius.TopLeft == 8 &&
            Equals(menu.Background, this.FindResource("LG.Surface")) &&
            items.All(item => item.Classes.Contains("lg-menu-item") && item.CornerRadius.TopLeft == 5 &&
                item.FontFamily == FontFamily),
            "A launcher context menu did not use the shared theme.");
        var backdrop = menu.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "FrostedBackdrop");
        Require(backdrop.Background is ImageBrush { Source: not null } && backdrop.Effect is BlurEffect,
            "The menu did not sample and blur the content underneath it.");
        items[0].IsSelected = true;
        var first = items[0].GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot");
        var last = items[^1].GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot");
        Require(Equals(first.Background, this.FindResource("LG.Selection")),
            "The selected item did not display the launcher highlight.");
        var topLeft = first.TranslatePoint(default, menu)!.Value;
        var bottomRight = last.TranslatePoint(new Point(last.Bounds.Width, last.Bounds.Height), menu)!.Value;
        var inset = menu.Padding.Left + menu.BorderThickness.Left;
        var gaps = new[] { topLeft.X, topLeft.Y, menu.Bounds.Width - bottomRight.X, menu.Bounds.Height - bottomRight.Y };
        Require(gaps.All(gap => Math.Abs(gap - inset) < 1),
            $"The menu insets were not uniform. Expected {inset}, measured {string.Join(", ", gaps)}.");
        CaptureControl(menu, preview);
    }

    private async Task VerifyHomeOverflow(string preview)
    {
        var originalWidth = Width;
        var extraGames = Enumerable.Range(1, 32).Select(index => new Game
        {
            Title = $"Sample game {index:00}", LastPlayedUtc = index <= 2 ? DateTime.UnixEpoch.AddDays(index) : null,
            PlaySeconds = index == 32 ? 60 : index == 31 ? 59 : 0
        }).ToArray();
        var extraCollections = Enumerable.Range(1, 5).Select(index => new GameCollection { Name = $"Sample collection {index}" }).ToArray();
        _library.Games.AddRange(extraGames);
        _library.Collections.AddRange(extraCollections);
        try
        {
            Width = MinWidth;
            Render();
            var library = PageBody.Children.OfType<ItemsControl>().Single(view => view.Name == "HomeLibraryGrid");
            var shelves = PageBody.Children.OfType<ScrollViewer>().ToArray();
            await WaitFor(() => library.Items.Cast<Game[]>().SelectMany(row => row).Count() == _library.Games.Count &&
                shelves.All(view => view.Extent.Width > view.Viewport.Width), "The narrow Home layout clipped rows or omitted library games.");
            var narrowColumns = ((Game[])library.Items[0]!).Length;
            var reports = Path.GetDirectoryName(preview)!;
            Capture(Path.Combine(reports, "home-sections.png"));
            await VerifySmoothHomeScrolling(shelves, preview);
            foreach (var width in new[] { 1230, 1600 })
            {
                Width = width;
                await Task.Delay(85);
                Capture(Path.Combine(reports, $"home-sections-{width}.png"));
            }
            Width = originalWidth;
            await WaitFor(() => ((Game[])library.Items[0]!).Length > narrowColumns,
                "The Library did not fit more portrait columns at a wider window size.");
            Require(!PageBody.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "RecentlyPlayedRowNext").IsEffectivelyVisible,
                "The recent shelf showed arrows when all cards fit.");
            ContentScroll.Offset = new Vector(0, ContentScroll.Extent.Height);
            await WaitFor(() => library.GetVisualDescendants().OfType<GameCard>().Any(card => card.Title == extraGames[^1].Title),
                "The last games could not be reached by scrolling down the Library.");
            var lastCard = library.GetVisualDescendants().OfType<GameCard>().Single(card => card.Title == extraGames[^1].Title);
            Require(lastCard.GetVisualDescendants().OfType<Border>().Any(border => border.Name == "PlaytimePill" && border.IsVisible),
                "The Home library lost its playtime pills.");
            var shortSession = library.GetVisualDescendants().OfType<GameCard>().Single(card => card.Title == extraGames[^2].Title);
            Require(shortSession.GetVisualDescendants().OfType<Border>().Any(border => border.Name == "PlaytimePill" && !border.IsVisible),
                "The Home library showed a playtime pill for a session under one minute.");
            Require(library.GetVisualDescendants().OfType<GameCard>().Count() < _library.Games.Count,
                "The scrollable Library realized every portrait at once.");
        }
        finally
        {
            Width = originalWidth;
            _library.Games.RemoveAll(extraGames.Contains);
            _library.Collections.RemoveAll(extraCollections.Contains);
            Render();
            ContentScroll.Offset = default;
        }
    }

    private async Task VerifySmoothHomeScrolling(ScrollViewer[] shelves, string preview)
    {
        Classes.Add("lg-motion");
        try
        {
            foreach (var shelf in shelves)
            {
                var arrows = PageBody.GetVisualDescendants().OfType<Button>().ToArray();
                var previous = arrows.SingleOrDefault(button => button.Name == shelf.Name + "Previous");
                var next = arrows.SingleOrDefault(button => button.Name == shelf.Name + "Next");
                Require(previous is { IsEffectivelyVisible: true, IsEnabled: false } &&
                    next is { IsEffectivelyVisible: true, IsEnabled: true }, "An overflowing shelf did not show its arrow controls.");
                next!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var maximum = shelf.Extent.Width - shelf.Viewport.Width;
                await WaitFor(() => shelf.Offset.X > 0 && shelf.Offset.X < maximum,
                    "The shelf arrow did not animate through intermediate offsets.");
                await WaitFor(() => previous!.IsEnabled, "The previous arrow did not enable after scrolling.");
                previous!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => shelf.Offset.X == 0 && !previous.IsEnabled, "The shelf did not scroll back to the start.");
            }

            ContentScroll.Offset = default;
            UpdateLayout();
            var initial = ContentScroll.Offset.Y;
            var target = Math.Min(initial + 2 * WheelStep(ContentScroll), ContentScroll.ScrollBarMaximum.Y);
            var offsets = new List<double> { initial };
            void ObserveOffset(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property == ScrollViewer.OffsetProperty) offsets.Add(ContentScroll.Offset.Y);
            }
            ContentScroll.PropertyChanged += ObserveOffset;
            var source = shelves[0].GetVisualDescendants().OfType<GameCard>().First();
            using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            void Wheel(Control element, Vector? delta = null) => element.RaiseEvent(new PointerWheelEventArgs(
                element, pointer, this, default, 0, new PointerPointProperties(), KeyModifiers.None, delta ?? new Vector(0, -1)));
            try
            {
                Wheel(source);
                Wheel(source);
                if (target > initial)
                    await WaitFor(() => ContentScroll.Offset.Y > initial && ContentScroll.Offset.Y < target,
                        "Mouse wheel input did not animate the vertical page.");
                await ScanLibrary();
                Require(source.IsAttachedToVisualTree(), "A background rescan rebuilt Home while it was scrolling.");
                await WaitFor(() => Math.Abs(ContentScroll.Offset.Y - target) < .01,
                    "Consecutive wheel inputs did not accumulate according to the OS scroll setting.");
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException($"{error.Message} Target {target}, offsets [{string.Join(", ", offsets)}].", error);
            }
            finally { ContentScroll.PropertyChanged -= ObserveOffset; }
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, "home-scroll-controls.png"));

            if (SmoothScroll.GetSystemWheelUnits(horizontal: true) != null)
            {
                foreach (var shelf in shelves)
                {
                    var horizontalTarget = Math.Min(WheelStep(shelf, horizontal: true) / 2, shelf.ScrollBarMaximum.X);
                    Wheel(((Panel)shelf.Content!).Children[0], new Vector(-.5, 0));
                    await WaitFor(() => Math.Abs(shelf.Offset.X - horizontalTarget) < .01,
                        $"A fractional horizontal wheel delta did not scroll {shelf.Name} as configured in Windows.");
                    shelf.Offset = default;
                }
            }

            var modal = new ModalDialog("Scroll verification", 400);
            var tallContent = new Border { Height = 1200 };
            modal.Body.Children.Add(tallContent);
            var dismissed = ShowModal(modal);
            UpdateLayout();
            try
            {
                var modalScroll = tallContent.GetVisualAncestors().OfType<ScrollViewer>().Single();
                var modalTarget = Math.Min(WheelStep(modalScroll), modalScroll.ScrollBarMaximum.Y);
                Wheel(tallContent);
                if (modalTarget > 0)
                    await WaitFor(() => modalScroll.Offset.Y > 0 && modalScroll.Offset.Y < modalTarget,
                        "Overflowing modal content did not scroll smoothly.");
                await WaitFor(() => Math.Abs(modalScroll.Offset.Y - modalTarget) < .01, "The modal wheel animation did not settle.");
                Classes.Remove("lg-motion");
                modalScroll.Offset = default;
                Wheel(tallContent);
                Require(Math.Abs(modalScroll.Offset.Y - modalTarget) < .01,
                    "Reduce Motion changed the wheel distance or animated the scroll.");
                Require(Math.Abs(ContentScroll.Offset.Y - target) < .01, "Scrolling the modal moved the underlying page.");
            }
            finally { modal.Close(); }
            await dismissed;
        }
        finally
        {
            Classes.Remove("lg-motion");
            ContentScroll.Offset = default;
        }

        static double WheelStep(ScrollViewer viewer, bool horizontal = false) =>
            SmoothScroll.GetSystemWheelUnits(horizontal) is { } units
                ? Math.Min(horizontal ? viewer.LargeChange.Width : viewer.LargeChange.Height,
                    units * (horizontal ? viewer.SmallChange.Width : viewer.SmallChange.Height))
                : 50;
    }

    private async Task VerifyCollectionCards(GameCollection dynamicCollection, string preview)
    {
        var manual = _library.Collections.Single(collection => !collection.IsDynamic);
        var extra = new[] { 620, 1623730, 3489700 }.Select(app => new Game
        {
            Title = $"Preview {app}", SteamId = app, CollectionIds = [manual.Id]
        }).ToArray();
        _library.Games.AddRange(extra);
        try
        {
            Navigate(Page.Collections);
            UpdateLayout();
            var tiles = PageBody.Children.OfType<UniformGrid>().Single().Children.OfType<CollectionCard>().ToArray();
            var manualTile = tiles.Single(tile => tile.Title == manual.Name);
            var dynamicTile = tiles.Single(tile => tile.Title == dynamicCollection.Name);
            var mosaic = (Grid)manualTile.Content!;
            var pictures = mosaic.Children.OfType<Panel>().SelectMany(cell => cell.Children.OfType<Image>()).ToArray();
            Require(tiles.Length == _library.Collections.Count && manualTile.GameCount == "4 games" && pictures.Length == 4 &&
                mosaic.RowDefinitions.Count == 2 && mosaic.ColumnDefinitions.Count == 2,
                "The collections overview did not use a two-by-two artwork mosaic for a multi-game collection.");
            await WaitFor(() => pictures.All(picture => picture.Source != null), "Cached collection mosaic artwork did not load.");
            Require(dynamicTile.IsDynamic && dynamicTile.GameCount == "1 game" &&
                dynamicTile.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DynamicBadge").IsVisible &&
                !manualTile.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DynamicBadge").IsVisible &&
                !dynamicTile.GetVisualDescendants().OfType<Button>().Any(),
                "The collection badges were incorrect or the collection card included a separate button.");
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, "collection-mosaic.png"));
            dynamicTile.Command!.Execute(null);
            Require(_collection == dynamicCollection, "The full collection card did not open the matching collection.");
        }
        finally
        {
            _library.Games.RemoveAll(extra.Contains);
        }
    }

    private void VerifyNavigationHistory(Game game, Game other, GameCollection collection)
    {
        ReleaseHistoryButton(forward: false);
        Require(_page == Page.Collections && _collection == null, "Mouse Back did not reopen the Collections overview.");
        ReleaseHistoryButton(forward: true);
        Require(_page == Page.Collections && _collection == collection, "Mouse Forward did not reopen the same collection.");

        SelectGame(game);
        SelectGame(other);
        ReleaseHistoryButton(forward: false);
        Require(_page == Page.Game && _game == game, "Mouse Back did not restore the previous game's details.");
        ReleaseHistoryButton(forward: true);
        Require(_page == Page.Game && _game == other, "Mouse Forward did not restore the next game's details.");
        ReleaseHistoryButton(forward: false);
        Navigate(Page.Settings);
        ReleaseHistoryButton(forward: true);
        Require(_page == Page.Settings, "Opening a new view did not discard the abandoned forward path.");

        Navigate(Page.Appearance);
        ReleaseHistoryButton(forward: false);
        Require(_page == Page.Settings, "Mouse Back did not leave Appearance.");
        ReleaseHistoryButton(forward: true);
        Require(_page == Page.Appearance, "Mouse Forward did not reopen Appearance.");

        var paged = new GameCollection { Name = "Paged collection" };
        var extras = Enumerable.Range(1, 30).Select(number =>
            new Game { Title = $"Game {number}", CollectionIds = [paged.Id] }).ToArray();
        _library.Collections.Add(paged);
        _library.Games.AddRange(extras);
        try
        {
            Navigate(Page.Collections, paged);
            UpdateLayout();
            ContentScroll.Offset = new Vector(0, 120);
            var scroll = ContentScroll.Offset;
            Require(scroll.Y > 0, "The pagination fixture could not scroll.");
            var next = PageBody.Children.OfType<WrapPanel>().SelectMany(panel => panel.Children).OfType<Button>()
                .Single(button => button.Content as string == "Next");
            next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(_offset == 24, "The collection did not open its next page.");
            ReleaseHistoryButton(forward: false);
            Require(_collection == paged && _offset == 0 && ContentScroll.Offset == scroll,
                "Mouse Back did not restore the prior collection page and scroll position.");
            ReleaseHistoryButton(forward: true);
            Require(_collection == paged && _offset == 24, "Mouse Forward did not restore the next page.");
            Navigate(Page.Settings);
            _library.Collections.Remove(paged);
            ReleaseHistoryButton(forward: false);
            Require(_page == Page.Appearance, "Mouse Back tried to reopen a removed collection.");
        }
        finally
        {
            _library.Collections.Remove(paged);
            _library.Games.RemoveAll(extras.Contains);
        }
        Navigate(Page.Collections, collection);
    }

    private async Task VerifySidebarCollections(GameCollection dynamicCollection, string preview)
    {
        var manual = _library.Collections.Single(collection => !collection.IsDynamic);
        var windowSize = (Width, Height);
        var motion = Classes.Contains("lg-motion");
        var motionEnabled = _motion;
        var sortMode = _sort;
        var played = new Game { Title = "ZZ Sidebar sample", PlaySeconds = 1000000, CollectionIds = [manual.Id] };
        _library.Games.Add(played);
        try
        {
            _sort = 0;
            Navigate(Page.Collections);
            LibraryCollections.SelectedItem = _shownCollections.Single(item => item.Collection == manual);
            Require(_collection == manual && PageTitle.Text == manual.Name && LibraryGames.SelectedItem == null,
                "Selecting a sidebar collection did not open it.");
            var previews = Path.GetDirectoryName(preview)!;
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                _motion = true;
                Classes.Add("lg-motion");
                (Width, Height) = (size.Width, size.Height);
                await Task.Delay(40);
                UpdateLayout();
                var gameRow = LibraryGames.GetVisualDescendants().OfType<ListBoxItem>().First();
                var collectionRows = LibraryCollections.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
                Require(collectionRows.Length == _library.Collections.Count && collectionRows.Count(row => row.IsSelected) == 1 &&
                    collectionRows.All(row => row.Bounds.Height == gameRow.Bounds.Height) &&
                    LibraryGames.TranslatePoint(new Point(0, LibraryGames.Bounds.Height), SidebarLists)!.Value.Y <
                    LibraryCollections.TranslatePoint(default, SidebarLists)!.Value.Y,
                    "Collections did not follow the Library and match its row height.");
                Capture(Path.Combine(previews, $"sidebar-collections-{size.Width}.png"));
                foreach (var row in collectionRows)
                {
                    var item = (SidebarCollection)row.Content!;
                    var icon = row.GetVisualDescendants().OfType<SymbolIcon>().Single();
                    var expected = item.Collection.IsDynamic
                        ? LucideIconCatalog.Find("flag-triangle-right")?.Geometry
                        : this.FindResource("LG.Icon.Library");
                    Require(Math.Abs(icon.Bounds.Width - 14) <= 1 && Math.Abs(icon.Bounds.Height - 14) <= 1 &&
                        Equals(icon.Data, expected), $"The {item.Title} sidebar icon did not match its collection type " +
                        $"(size {icon.Bounds.Size}, matching symbol {Equals(icon.Data, expected)}).");
                }
                var picker = SortPicker();
                var menu = PreferenceMenu(picker);
                picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(menu.IsOpen && menu.RevealProgress < 1, "The collection sort menu did not start its reveal.");
                await WaitFor(() => menu.RevealProgress == 1, "The collection sort menu did not finish revealing.");
                VerifyMenuTheme(menu, Path.Combine(previews, $"collection-sort-menu-{size.Width}.png"));
                menu.Close();
                await WaitFor(() => !menu.IsOpen, "The collection sort menu did not collapse.");
                await VerifySortMotion(previews, size.Width);
            }
        }
        finally
        {
            _library.Games.Remove(played);
            _sort = sortMode;
            _motion = motionEnabled;
            Classes.Set("lg-motion", motion);
            (Width, Height) = windowSize;
            Navigate(Page.Collections, dynamicCollection);
        }

        async Task VerifySortMotion(string previews, double width)
        {
            var beforeSort = MemberCards();
            var originalPosition = Position(beforeSort.Single(card => card.Title == played.Title));
            var firstSlot = Position(beforeSort[0]);
            var remainingTravel = new List<double>();
            var sortPicker = SortPicker();
            PreferenceMenu(sortPicker).Closed += (_, _) =>
            {
                if (_cardGrid is not { Children.Count: > 0 } grid ||
                    grid.Children[0].RenderTransform is not TranslateTransform translation) return;

                RecordTravel();
                translation.PropertyChanged += (_, _) => RecordTravel();

                void RecordTravel() => remainingTravel.Add(new Vector(translation.X, translation.Y).Length);
            };
            await SelectPreferenceOption(sortPicker, "Most played");
            await WaitFor(() => _sort == 2, "Selecting a sort option did not update the collection.");
            var mostPlayed = MemberCards()[0];
            Require(AutomationProperties.GetHelpText(SortPicker()) == "Current value Most played" &&
                mostPlayed.Title == played.Title,
                "The shared sort picker did not apply or display the chosen collection order.");
            await WaitFor(() => PositionsBetween(Position(mostPlayed), originalPosition, firstSlot),
                "The sorted collection tile did not move between its previous and new slots.");
            Capture(Path.Combine(previews, $"collection-sort-moving-{width}.png"));
            await WaitFor(() => _cardGrid?.Children[0].RenderTransform == null && (Position(mostPlayed) - firstSlot).Length < 4,
                "The sorted collection tile did not settle in its new slot.");
            var recordedTravel = string.Join(", ", remainingTravel.Select(distance => distance.ToString("0.##", CultureInfo.InvariantCulture)));
            Require(remainingTravel.Count >= 2 &&
                remainingTravel.Zip(remainingTravel.Skip(1)).All(pair => pair.Second <= pair.First + .01),
                $"The sorted tile jumped away from its destination while finishing at width {width} ({recordedTravel}).");
            Capture(Path.Combine(previews, $"collection-sort-settled-{width}.png"));

            _motion = false;
            Classes.Remove("lg-motion");
            await SelectPreferenceOption(SortPicker(), "Title A–Z");
            await WaitFor(() => _sort == 0, "The reduced-motion sort did not apply.");
            var restoredCard = MemberCards().Single(card => card.Title == played.Title);
            Require(_cardGrid?.Children[^1].RenderTransform == null && (Position(restoredCard) - originalPosition).Length < 4,
                "With motion disabled, the sorted card did not immediately reach its destination.");
            Capture(Path.Combine(previews, $"collection-sort-reduced-motion-{width}.png"));
        }

        Button SortPicker() => PageBody.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Sort collection");

        GameCard[] MemberCards()
        {
            UpdateLayout();
            return PageBody.Children.OfType<UniformGrid>().Single().GetVisualDescendants().OfType<GameCard>().ToArray();
        }

        Vector Position(GameCard card) => card.TranslatePoint(default, PageBody)!.Value;

        static bool PositionsBetween(Vector current, Vector start, Vector end) =>
            (current - start).Length > 4 && (current - end).Length > 4;
    }

    private void ReleaseHistoryButton(bool forward)
    {
        var pointer = new Pointer(0, PointerType.Mouse, true);
        var properties = new PointerPointProperties(RawInputModifiers.None,
            forward ? PointerUpdateKind.XButton2Released : PointerUpdateKind.XButton1Released);
        var released = new PointerReleasedEventArgs(MainArea, pointer, this, default, 0, properties, KeyModifiers.None,
            forward ? MouseButton.XButton2 : MouseButton.XButton1);
        MainArea.RaiseEvent(released);
        Require(released.Handled, "The launcher did not handle a mouse navigation button.");
    }

    private async Task VerifySearchMotion()
    {
        SearchBox.Text = "legacy";
        ShowSearchResults();
        UpdateLayout();
        var one = SearchDropdown.Bounds.Height;
        SearchBox.Text = "genre:";
        ShowSearchResults();
        UpdateLayout();
        var two = SearchDropdown.Bounds.Height;
        Require(one > 0 && two > one, "The dropdown did not size immediately with motion disabled.");
        CloseSearch();

        _motion = true;
        Classes.Set("lg-motion", true);
        try
        {
            SearchBox.Text = "legacy";
            ShowSearchResults();
            UpdateLayout();
            Require(SearchDropdown.Bounds.Height < one, "The dropdown opened at full height with motion enabled.");
            var top = SearchDropdown.TranslatePoint(default, SearchBox)?.Y;
            await WaitFor(() => SearchDropdown.Bounds.Height == one, "The opening reveal did not settle at one result.");
            Require(SearchDropdown.TranslatePoint(default, SearchBox)?.Y == top, "The opening reveal moved the top edge.");

            SearchBox.Text = "genre:";
            ShowSearchResults();
            await WaitFor(() => SearchDropdown.Bounds.Height > one && SearchDropdown.Bounds.Height < two,
                "Adding a result did not animate the dropdown height.");
            var expanding = SearchDropdown.Bounds.Height;
            SearchBox.Text = "legacy";
            ShowSearchResults();
            UpdateLayout();
            Require(SearchDropdown.Bounds.Height == expanding, "Changing results during an animation caused a jump.");
            await WaitFor(() => SearchDropdown.Bounds.Height == one, "An interrupted resize did not settle at the latest result count.");
        }
        finally
        {
            CloseSearch();
            _motion = false;
            Classes.Set("lg-motion", false);
        }
    }

    private async Task VerifyRenderingPreference(string preview)
    {
        var section = PageBody.GetVisualDescendants().OfType<HeaderedContentControl>()
            .Single(control => Equals(control.Header, "Performance"));
        var toggle = section.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "Hardware rendering");
        var priority = section.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "Set process priority");
        Require(priority.IsChecked == false, "Set process priority did not default to off under Performance.");
        Require(toggle.IsChecked == true && section.GetVisualDescendants().Contains(toggle),
            "Hardware rendering did not default to on under Performance.");
        var notice = section.GetVisualDescendants().OfType<TextBlock>()
            .Single(label => label.Text?.Contains("GPU resources", StringComparison.Ordinal) == true);
        Require(notice.Text is { } text && text.Contains("game performance on lower-end devices", StringComparison.Ordinal) &&
            text.Contains("Quit and reopen", StringComparison.Ordinal), "The rendering preference needs performance and restart help.");
        var reports = Path.GetDirectoryName(preview)!;
        var previousSize = new Size(Width, Height);
        try
        {
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(85);
                UpdateLayout();
                if (size.Width == 730) section.BringIntoView();
                Capture(Path.Combine(reports, $"app-behaviour-{size.Width}.png"));
                CaptureControl(section, Path.Combine(reports, $"app-behaviour-performance-{size.Width}.png"));
                if (size.Width == 1230)
                {
                    priority.IsChecked = true;
                    Require(_store.Load().Preferences.SetProcessPriority && !DialogHost.HasModal,
                        "Turning on Set process priority did not save immediately without prompting for a restart.");
                    CaptureControl(section, Path.Combine(reports, "app-behaviour-performance-enabled.png"));
                    priority.IsChecked = false;
                    Require(!_store.Load().Preferences.SetProcessPriority, "Set process priority could not be turned off.");
                }
                toggle.IsChecked = false;
                Require(_store.Load().Preferences.DisableHardwareRendering, "Turning off hardware rendering was not saved.");
                var prompt = RenderingRestartPrompt();
                var later = RestartButton(prompt, "Later");
                await WaitFor(() => later.IsFocused, "The restart prompt did not focus Later.");
                Capture(Path.Combine(reports, $"hardware-rendering-restart-{size.Width}.png"));
                if (size.Width == 1230)
                {
                    var game = Guid.NewGuid().ToString();
                    _playing.Add(game);
                    try
                    {
                        RestartButton(prompt, "Restart now").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(DialogHost.HasModal && prompt.Body.Children.OfType<TextBlock>().Any(label => label.IsVisible &&
                            label.Text?.Contains("running games", StringComparison.Ordinal) == true),
                            "Restart did not wait for the running game.");
                    }
                    finally { _playing.Remove(game); }
                }
                later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!DialogHost.HasModal && _store.Load().Preferences.DisableHardwareRendering,
                    "Deferring restart did not keep the saved rendering change.");
                toggle.IsChecked = true;
                Require(!_store.Load().Preferences.DisableHardwareRendering, "Re-enabling hardware rendering was not saved.");
                RenderingRestartPrompt();
                DialogHost.DismissTop();
            }
            toggle.IsChecked = false;
            RenderingRestartPrompt();
            DialogHost.DismissTop();
        }
        finally
        {
            Width = previousSize.Width;
            Height = previousSize.Height;
            UpdateLayout();
        }

        ModalDialog RenderingRestartPrompt()
        {
            UpdateLayout();
            var prompt = DialogHost.GetVisualDescendants().OfType<ModalDialog>()
                .Single(dialog => AutomationProperties.GetName(dialog) == "Restart LG Launcher?");
            Require(RestartButton(prompt, "Restart now").IsEnabled, "The restart action was missing.");
            return prompt;
        }

        static Button RestartButton(ModalDialog dialog, string label) =>
            dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, label));
    }

    private async Task VerifySetupUpdates(string preview)
    {
        var perUser = UpdateInstaller.IsUserSetupInstallation;
        Require(UpdateInstaller.IsSetupInstallation && !UpdateInstaller.IsSupported &&
            UpdateInstaller.SupportsAutomaticUpdates == perUser && _store.Load().Preferences.AutomaticUpdates,
            "Windows Setup did not respect its installation scope and keep the saved preference.");
        var release = new Version(99, 1, 0);
        var page = UpdateFeed.ReleasePage(release);
        _updateResult = new("An official update is available.", page, true,
            new(new Uri("https://github.com/H4RL33/lg_launcher/releases/download/v99.1.0/LG-Launcher.exe"), new string('0', 64), 1, release));
        Navigate(Page.About);
        UpdateLayout();
        Require(_updateReleaseAction is { IsEffectivelyVisible: true } && _settingsInstallAction is { IsVisible: false } &&
            !UpdateAction.IsVisible, "Windows Setup offered a portable executable in place of an installer.");

        const string setupAsset = """
            {"tag_name":"v99.1.0","assets":[{"name":"LG-Launcher-Setup-99.1.0.exe","state":"uploaded","size":123,
            "digest":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "browser_download_url":"https://github.com/H4RL33/lg_launcher/releases/download/v99.1.0/LG-Launcher-Setup-99.1.0.exe"}]}
            """;
        _updateResult = UpdateFeed.Read(setupAsset, UpdateFeed.CurrentVersion);
        var folder = Path.GetDirectoryName(preview)!;
        foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
        {
            Width = size.Width;
            Height = size.Height;
            Navigate(Page.Behaviour);
            await Task.Delay(100);
            UpdateLayout();
            var automatic = PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(toggle => AutomationProperties.GetName(toggle) == "Automatic updates");
            Require(automatic.IsEffectivelyEnabled == perUser && automatic.IsChecked == perUser,
                "App Behaviour did not offer automatic updates according to the Windows Setup scope.");
            Capture(Path.Combine(folder, $"setup-behaviour-{size.Width}.png"));

            Navigate(Page.About);
            await Task.Delay(100);
            UpdateLayout();
            Require(_updateReleaseAction?.IsEffectivelyVisible == !perUser &&
                _settingsInstallAction?.IsEffectivelyVisible == perUser && UpdateAction.IsVisible == perUser &&
                Equals(_settingsInstallAction?.Content, "Install update"),
                "The official Windows Setup package did not offer the right update action for this installation.");
            Capture(Path.Combine(folder, $"setup-about-{size.Width}.png"));
        }

        _updateResult = UpdateFeed.Read(setupAsset.Replace("sha256:", "md5:"), UpdateFeed.CurrentVersion);
        RefreshUpdateActions();
        Require(_updateReleaseAction is { IsEffectivelyVisible: true } && _settingsInstallAction is { IsVisible: false } &&
            !UpdateAction.IsVisible, "Windows Setup offered an installer without a verified digest.");
    }

    private async Task VerifyFirstUsageReset(ToggleSwitch automatic, string preview)
    {
        var section = PageBody.GetVisualDescendants().OfType<HeaderedContentControl>()
            .Single(control => Equals(control.Header, "Miscellaneous"));
        var reset = section.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Reset") && AutomationProperties.GetName(button) == "Reset first-usage prompts");
        var row = reset.GetVisualAncestors().OfType<Grid>().First(grid => grid.Classes.Contains("lg-preference"));
        var label = section.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Reset first-usage prompts");
        Require(label.TranslatePoint(default, section) is { } labelOrigin &&
            reset.TranslatePoint(default, section) is { } resetOrigin && labelOrigin.X < resetOrigin.X,
            "The reset button was not on the right of its label.");
        automatic.IsChecked = true;
        var previous = _store.Load();
        Require(previous.UsageNoticeAccepted && previous.Preferences.AutomaticUpdates, "The reset fixture did not have both choices set.");
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var saved = _store.Load();
        Require(!DialogHost.HasModal && automatic.IsChecked == false && !saved.UsageNoticeAccepted &&
            !saved.Preferences.AutomaticUpdates && saved.Preferences.DisableHardwareRendering == previous.Preferences.DisableHardwareRendering,
            "Reset did not clear the two first-usage choices or changed an unrelated setting.");
        Require(!row.IsEnabled && !reset.IsEffectivelyEnabled && ToolTip.GetTip(row) is string tooltip &&
            tooltip.Contains("next time you open", StringComparison.Ordinal), "Reset remained available while both prompts were pending.");
        await WaitFor(() => Math.Abs(row.Opacity - .58) < .001, "The disabled reset row did not use the shared fade.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "first-usage-reset-disabled.png"));
        automatic.IsChecked = true;
        Require(reset.IsEffectivelyEnabled, "Re-enabling automatic updates left the reset row disabled.");
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var prompts = ShowFirstUsagePrompts();
        UpdateLayout();
        var accept = DialogHost.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "I understand"));
        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (UpdateInstaller.SupportsAutomaticUpdates)
        {
            await WaitFor(() => DialogHost.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Keep manual updates")),
                "Accepting the reset usage disclosure did not reopen the automatic update question.");
            DialogHost.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Keep manual updates"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Require(await prompts && _store.Load().UsageNoticeAccepted && !_store.Load().Preferences.AutomaticUpdates,
            "The first-usage prompts did not save the new choices.");
    }

    private static async Task WaitFor(Func<bool> condition, string error)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
        Require(condition(), error);
    }

    private void Capture(string path)
    {
        CaptureControl(this, path);
    }

    private void VerifySplash(StartupWindow splash, string preview)
    {
        splash.UpdateLayout();
        Require(!IsVisible && splash.IsVisible && splash.Bounds.Width is > 0 and <= 500 && splash.Bounds.Height is > 0 and <= 230,
            "The compact splash did not appear in its own window before the launcher.");
        if (OperatingSystem.IsWindows())
            Require(WindowsDesktop.HasNativeCaption(splash) && !splash.ShowInTaskbar &&
                splash.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Text == splash.Title && text.IsVisible) == 1,
                "The splash lost its native caption style or drew extra title bar content.");
        Require(splash.FindControl<Image>("SplashArtwork")?.Source != null &&
            splash.FindControl<TextBlock>("SplashOperation")?.Text?.Contains("Rescanning", StringComparison.Ordinal) == true,
            "The splash did not show its artwork and current operation.");
        CaptureControl(splash, preview);
    }

    private static void CaptureControl(Control view, string path)
    {
        view.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(view.Bounds.Size, 1));
        bitmap.Render(view);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ReleaseFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/H4RL33/lg_launcher/releases/latest",
                "The update check requested the wrong feed.");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v99.2.0","html_url":"https://example.com/release","draft":false,"prerelease":false}""")
            });
        }
    }
}
