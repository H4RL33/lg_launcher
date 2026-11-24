using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LGLauncher.Components;
using SkiaSharp;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private async Task VerifyPreferences(Game game, string preview)
    {
        await VerifyArtworkAccent(preview);
        await VerifyColourwaysAndWidgets(preview);
        var font = Setting<Button>("Base interface font size");
        var previousFontSize = EffectiveFontSize;
        var inheritedHeadingSize = PageTitle.FontSize;
        await ChoosePreference(font, "16");
        Require(_store.Load().Preferences.InterfaceFontSize == 16 && FontSize == 16 &&
            PageTitle.FontSize == inheritedHeadingSize + 16 - previousFontSize, "The base font size did not update the interface typography or persist.");
        await ChoosePreference(font, previousFontSize.ToString());

        var windowSize = new Size(Width, Height);
        _library.Preferences.InterfaceScalePercent = 120;
        ApplyInterfaceScale();
        Navigate(Page.Settings);
        Navigate(Page.Appearance);
        UpdateLayout();
        var scale = Setting<Button>("Base interface scale");
        font = Setting<Button>("Base interface font size");
        Require(scale.Classes.Contains("choice-picker") &&
            scale.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(["75%", "100%", "125%", "150%", "175%", "200%", "Custom…"]),
            "The scale dropdown did not offer the presets and Custom.");
        Require(AutomationProperties.GetHelpText(scale) == "Current value 120%" && InterfaceRoot.LayoutTransform?.Value.M11 == 1.2,
            "The launcher did not preserve an in-range scale value between presets.");
        Require(scale.TranslatePoint(new Point(scale.Bounds.Width, 0), this) is { } scaleEdge &&
            font.TranslatePoint(new Point(font.Bounds.Width, 0), this) is { } settingEdge &&
            Math.Abs(scaleEdge.X - settingEdge.X) < 1,
            "The scale dropdown did not align with the right edge of the other appearance settings.");
        await ChoosePreference(scale, "200%");
        Require(InterfaceRoot.LayoutTransform?.Value.M11 == 2 && _store.Load().Preferences.InterfaceScalePercent == 200,
            "Selecting 200% did not apply and save the scale.");
        await ChoosePreference(scale, "75%");
        Require(InterfaceRoot.LayoutTransform?.Value.M11 == .75 && _store.Load().Preferences.InterfaceScalePercent == 75,
            "Selecting 75% did not apply and save the scale.");
        await ChoosePreference(scale, "125%");
        Require(_store.Load().Preferences.InterfaceScalePercent == 125 &&
            InterfaceRoot.LayoutTransform?.Value is { M11: 1.25, M22: 1.25 }, "Selecting 125% did not apply and save the scale.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "appearance-scaled.png"));
        await ChoosePreference(scale, "100%");
        await VerifyCustomInterfaceScale(scale, preview);
        await ChoosePreference(scale, "100%");
        Width = windowSize.Width;
        Height = windowSize.Height;
        await VerifyPreferencePickers(_fontPicker!, font, scale, preview);

        _motion = true;
        Classes.Add("lg-motion");
        var reduceMotion = Setting<ToggleSwitch>("Reduce Motion");
        var artworkShift = Setting<ToggleSwitch>("Interactive Artwork Shift");
        var artworkShiftRow = (Grid)artworkShift.Parent!;
        var artworkShiftTrack = artworkShift.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SwitchKnobBounds");
        var enabledTrack = artworkShiftTrack.Background;
        try
        {
            artworkShiftRow.IsEnabled = false;
            Require(!artworkShift.IsEffectivelyEnabled, "The setting remained interactive during its disabled fade.");
            await WaitFor(() => artworkShiftRow.Opacity is > .58 and < 1, "The setting did not fade to its disabled appearance.");
            await WaitFor(() => Math.Abs(artworkShiftRow.Opacity - .58) < .001, "The disabled appearance did not settle.");
            artworkShiftRow.IsEnabled = true;
            await WaitFor(() => artworkShiftRow.Opacity is > .58 and < 1, "The setting did not fade back to its enabled appearance.");
            await WaitFor(() => artworkShiftRow.Opacity == 1, "The enabled appearance did not settle.");
        }
        finally { artworkShiftRow.IsEnabled = true; }
        reduceMotion.IsChecked = true;
        Require(_store.Load().Preferences.ReduceMotion && !_motion && !Classes.Contains("lg-motion"),
            "Reduce Motion did not disable launcher animations and persist.");
        Require(!artworkShift.IsEffectivelyEnabled && Math.Abs(artworkShiftRow.Opacity - .58) < .001 &&
            !Equals(artworkShiftTrack.Background, enabledTrack) &&
            AutomationProperties.GetHelpText(artworkShift) == "Overridden by Reduce Motion.",
            "Reduce Motion did not dim and disable the Interactive Artwork Shift setting.");
        artworkShift.IsChecked = false;
        Require(artworkShift.IsChecked == true && !_store.Load().Preferences.DisableArtworkShift,
            "An overridden toggle changed its stored preference.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "appearance-reduced-motion.png"));
        reduceMotion.IsChecked = false;
        Require(artworkShift.IsEffectivelyEnabled && artworkShiftRow.Opacity == 1 && artworkShift.IsChecked == true,
            "Turning off Reduce Motion did not restore the saved artwork shift setting.");
        Setting<ToggleSwitch>("Translucency").IsChecked = false;
        artworkShift.IsChecked = false;
        SelectGame(game);
        await WaitFor(() => _detailsRequest == null, "Game artwork did not load while translucent backgrounds were disabled.");
        Require(_artwork.Picture != null && !_chromeArtwork.IsVisible && Classes.Contains("lg-opaque"),
            "Translucency did not switch game chrome to the standard theme.");
        Require(SteamAppId.IsVisible, "Turning off translucency hid the Steam App ID stat.");
        _motion = true;
        MoveArtworkParallax(new Point(0, 0));
        Require(ArtworkHost.RenderTransform?.Value.IsIdentity == true, "Artwork still shifted with that setting disabled.");
        _motion = false;
        GameMenu(GameOptions, game);
        var options = GameOptions.ContextMenu!;
        Require(options.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "FrostedBackdrop").Background == null,
            "A context menu still sampled a translucent backdrop.");
        options.Close();
        Navigate(Page.Appearance);
        UpdateLayout();
        Setting<ToggleSwitch>("Translucency").IsChecked = true;
        Setting<ToggleSwitch>("Interactive Artwork Shift").IsChecked = true;

        Navigate(Page.LibrarySettings);
        UpdateLayout();
        var useLogos = Setting<ToggleSwitch>("Use Game Logos");
        var playtime = Setting<ToggleSwitch>("Track playtime");
        var themeMusic = Setting<ToggleSwitch>("Theme music");
        var storeTags = Setting<ToggleSwitch>("Show Store Tags");
        var rssArticles = Setting<ToggleSwitch>("Display most-recent RSS articles on Home");
        Require(playtime.IsChecked == true && useLogos.IsChecked == true && themeMusic.IsChecked == false && storeTags.IsChecked == true && rssArticles.IsChecked == true &&
            !PageBody.GetVisualDescendants().OfType<ToggleSwitch>().Any(toggle => AutomationProperties.GetName(toggle) == "Unlimited Genres"),
            "An existing profile did not receive the expected library defaults.");
        useLogos.IsChecked = false;
        themeMusic.IsChecked = true;
        storeTags.IsChecked = false;
        rssArticles.IsChecked = false;
        var prefs = _store.Load().Preferences;
        Require(!prefs.UseGameLogos && prefs.PlayThemeMusic && !prefs.ShowStoreTags && !prefs.ShowRecentRss,
            "The library and Home preferences did not round-trip.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "library-settings.png"));
        var librarySize = (Width, Height);
        var tileCaption = Setting<ToggleSwitch>("Show playtime caption");
        var clearButton = Setting<Button>("Clear playtime data");
        var dateFormatPicker = Setting<Button>("Date format");
        foreach (var size in new[] { (730, 510), (1230, 805), (1600, 900) })
        {
            (Width, Height) = size;
            await Task.Delay(85);
            Require(clearButton.Parent is Grid row && clearButton.Content is SymbolIcon icon &&
                Math.Abs(clearButton.Bounds.Width - (icon.Bounds.Width + clearButton.Padding.Left + clearButton.Padding.Right +
                    clearButton.BorderThickness.Left + clearButton.BorderThickness.Right)) <= 1 &&
                Math.Abs(clearButton.Bounds.Right - row.Bounds.Width) <= 1,
                "Clear playtime did not use the compact, right-aligned symbolic delete button.");
            var deleteEdge = clearButton.TranslatePoint(new Point(clearButton.Bounds.Width, 0), PageBody)!.Value.X;
            var tracks = playtime.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Name is "OuterBorder" or "SwitchKnobBounds").ToArray();
            Require(tracks.Length == 2 && tracks.All(track =>
                Math.Abs(track.TranslatePoint(new Point(track.Bounds.Width, 0), PageBody)!.Value.X - deleteEdge) <= 1),
                $"The playtime toggle tracks did not align with the delete button at {size.Item1}px.");
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"library-theme-music-{size.Item1}.png"));
            tileCaption.BringIntoView();
            UpdateLayout();
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"library-game-tiles-{size.Item1}.png"));
            Require(dateFormatPicker.Parent is Grid dateRow && dateFormatPicker.Bounds.Width is >= 112 and <= 136 &&
                Math.Abs(dateFormatPicker.Bounds.Right - dateRow.Bounds.Width) <= 1,
                "The Date format picker was not compact and right-aligned.");
            var dateMenu = PreferenceMenu(dateFormatPicker);
            dateFormatPicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => dateMenu.IsOpen && dateMenu.RevealProgress == 1, "The Date format menu did not open.");
            dateMenu.UpdateLayout();
            CaptureControl(dateMenu, Path.Combine(Path.GetDirectoryName(preview)!, $"library-date-format-menu-{size.Item1}.png"));
            var dateMenuWidth = dateMenu.Bounds.Width / EffectiveInterfaceScale;
            Require(dateMenuWidth >= dateFormatPicker.Bounds.Width - 1 && dateMenuWidth <= 160 &&
                dateMenu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(["Numeric", "Friendly"]),
                $"The Date format menu did not fit both choices at a compact width ({dateMenuWidth:0} DIP).");
            dateMenu.Close();
            await WaitFor(() => !dateMenu.IsOpen, "The Date format menu did not close.");
            ContentScroll.Offset = default;
        }
        (Width, Height) = librarySize;
        useLogos.IsChecked = true;
        themeMusic.IsChecked = false;
        storeTags.IsChecked = true;
        rssArticles.IsChecked = true;
        Require(!_store.Load().Preferences.PlayThemeMusic, "Disabling theme music did not persist.");
        playtime.IsChecked = false;
        Require(_store.Load().Preferences.DisablePlaytimeTracking && !GameSortChoices().Contains(2), "Disabling playtime did not persist or hide its sort.");
        SelectGame(game);
        Require(!PlaytimeDetail.IsVisible, "Game details displayed playtime with tracking disabled.");
        Navigate(Page.LibrarySettings);
        UpdateLayout();
        Setting<ToggleSwitch>("Track playtime").IsChecked = true;
        var modIndicator = Setting<ToggleSwitch>("Show mod indicator");
        var tilePlaytime = Setting<ToggleSwitch>("Show playtime caption");
        var tileFixture = new Game { Title = "Modded portrait", IsModded = true, PlaySeconds = 120 };
        Require(BuildGameCard(tileFixture) is { ShowPlaytime: true, IsModded: true }, "Tile preferences did not default on.");
        modIndicator.IsChecked = tilePlaytime.IsChecked = false;
        var tilePreferences = _store.Load().Preferences;
        Require(tilePreferences.HideTileModIndicator && tilePreferences.HideTilePlaytime &&
            BuildGameCard(tileFixture) is { ShowPlaytime: false, IsModded: false }, "The tile controls did not apply and save.");
        modIndicator.IsChecked = tilePlaytime.IsChecked = true;

        await ChoosePreference(Setting<Button>("Date format"), "Friendly");
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
            var date = new DateTime(DateTime.Now.Year, 11, 22, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            Require(_store.Load().Preferences.FriendlyLaunchDates && FormatLastLaunched(date) == "Nov. 22" &&
                FormatLastLaunched(date.AddYears(-1)) == $"Nov. 22, {DateTime.Now.Year - 1}" && FormatLastLaunched(null) == "Never",
                "The friendly date format did not preserve the month, date, and older year.");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
        await ChoosePreference(Setting<Button>("Date format"), "Numeric");
        var previousTimes = _library.Games.Select(entry => (Game: entry, entry.PlaySeconds)).ToArray();
        Setting<Button>("Clear playtime data").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(ActiveModDialog is { } clear && AutomationProperties.GetName(clear) == "Clear playtime data?",
            "Clearing playtime did not require confirmation.");
        DialogHost.DismissTop();
        Require(previousTimes.All(entry => entry.Game.PlaySeconds == entry.PlaySeconds), "Cancelling clear playtime changed history.");
        Setting<Button>("Clear playtime data").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ActiveModDialog!.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Clear playtime"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => _store.Load().Games.All(entry => entry.PlaySeconds == 0), "Confirmed playtime clearing did not save.");
        foreach (var entry in previousTimes) entry.Game.PlaySeconds = entry.PlaySeconds;
        Require(Save(), "The verification fixture could not restore its history.");

        var version = UpdateFeed.CurrentVersion;
        var releaseTag = UpdateFeed.ReleaseTag(version);
        using var missingRelease = new HttpClient(new InstalledReleaseFixture(releaseTag, HttpStatusCode.NotFound));
        _releaseNotesRequest = LoadReleaseNotes(CancellationToken.None, missingRelease);
        Navigate(Page.About);
        await WaitFor(() => AboutText("Release notes are unavailable for this version."),
            "A missing release did not show the release-notes fallback.");
        var reports = Path.GetDirectoryName(preview)!;
        Capture(Path.Combine(reports, "about-release-unavailable.png"));

        Navigate(Page.Settings);
        using var mismatchedRelease = new HttpClient(new InstalledReleaseFixture("v99.2.0"));
        Require(await UpdateFeed.GetReleaseNotes(CancellationToken.None, mismatchedRelease) == null,
            "GitHub notes for another version were accepted as the installed release.");
        using var installedRelease = new HttpClient(new InstalledReleaseFixture(releaseTag));
        var pendingNotes = new TaskCompletionSource<RssBlock[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseNotesRequest = pendingNotes.Task;
        Navigate(Page.About);
        await WaitFor(() => AboutText("Loading release notes…"), "About did not show that release notes were loading.");
        pendingNotes.SetResult(await LoadReleaseNotes(CancellationToken.None, installedRelease));
        await WaitFor(() => AboutRuns().Any(run => run.Text == "Library and game details"),
            "The installed release notes did not render in About.");
        UpdateLayout();
        var about = PageBody.GetVisualDescendants().OfType<Control>().ToArray();
        Require(about.OfType<Button>().Any(button => Equals(button.Content, "Check for new updates")) &&
            about.OfType<TextBlock>().Any(text => text.Text == $"Version {UpdateFeed.DisplayVersion(version)} · Build {version}") &&
            about.OfType<TextBlock>().Any(text => text.Text == "Support on Ko-fi") &&
            about.OfType<TextBlock>().Any(text => text.Text == UsageDisclosure && Equals(text.Foreground, this.FindResource("LG.Neutral")) &&
                text.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Center && text.TextAlignment == TextAlignment.Center &&
                !text.GetVisualAncestors().OfType<HeaderedContentControl>().Any()) &&
            Grid.GetColumn(SupportButton) > Grid.GetColumn(GameCount), "About or the status line was missing a requested detail.");
        var aboutSections = about.OfType<HeaderedContentControl>().ToArray();
        Require(aboutSections.Length == 2 && Equals(aboutSections[0].Header, "Version & Updates") &&
            aboutSections[1].Header is Grid && AboutText($"Release notes · {releaseTag}") && !about.OfType<TabControl>().Any(),
            "About did not show the installed version's release notes beneath the update controls.");
        Require(HasReleaseLink(releaseTag) &&
            UpdateFeed.ReleasePage(version).AbsoluteUri == $"https://github.com/H4RL33/lg_launcher/releases/tag/{releaseTag}" &&
            AboutRuns().Any(run => run.FontWeight == FontWeight.Bold && run.Text == "Refresh from Steam") &&
            AboutRuns().Any(run => run.Text?.StartsWith("• ", StringComparison.Ordinal) == true),
            "About did not render the release notes as native rich text or link to the installed release.");
        VerifyReleaseTypography();
        Capture(Path.Combine(reports, "about.png"));
        var channel = Setting<Button>("Release channel");
        Require(AutomationProperties.GetHelpText(channel) == "Current value Stable" && !_store.Load().Preferences.UsePreviewUpdates,
            "About did not use the Stable channel for the existing profile.");
        await ChoosePreference(channel, "Next preview");
        Require(_store.Load().Preferences.UsePreviewUpdates, "Choosing Next did not save the channel.");
        foreach (var size in new[] { (730, 510), (1230, 805), (1600, 900) })
        {
            (Width, Height) = size;
            await Task.Delay(85);
            Capture(Path.Combine(reports, $"about-release-channel-{size.Item1}.png"));
            var channelMenu = PreferenceMenu(channel);
            channel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => channelMenu.IsOpen && channelMenu.RevealProgress == 1, "The channel picker did not open.");
            VerifyMenuTheme(channelMenu, Path.Combine(reports, $"about-release-channel-menu-{size.Item1}.png"));
            channelMenu.Close();
            await WaitFor(() => !channelMenu.IsOpen, "The channel picker did not close.");
        }
        await ChoosePreference(channel, "Stable");
        Require(!_store.Load().Preferences.UsePreviewUpdates, "Returning to Stable did not clear the preview opt-in.");
        var newerVersion = new Version(version.Major, version.Minor, version.Build + 1);
        var newerTag = $"v{newerVersion}";
        var earlierResult = _updateResult;
        _updateResult = UpdateFeed.Read(JsonSerializer.Serialize(new { tag_name = newerTag, body = "### New in this release\n\n- **Managed mods** help set up OptiScaler and its dependencies on supported games." }), version);
        RefreshReleaseNotes();
        UpdateLayout();
        var tabs = PageBody.GetVisualDescendants().OfType<TabControl>().Single();
        Require(tabs.Items.OfType<TabItem>().Select(tab => tab.Header).SequenceEqual(["Current release", "Available release"]) &&
            tabs.SelectedIndex == 0 && HasReleaseLink(releaseTag), "About did not default to the installed release when a newer release was found.");
        VerifyReleaseTypography();
        var underlines = tabs.Items.OfType<TabItem>().Select(tab => tab.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "SelectionUnderline")).ToArray();
        Require(underlines[0].Opacity == 1 && underlines[1].Opacity == 0 &&
            Equals(underlines[0].Background, this.FindResource("LG.Accent")), "Only the selected release tab should have an accent underline.");
        var motionWasEnabled = Classes.Contains("lg-motion");
        try
        {
            Classes.Add("lg-motion");
            Require(underlines.All(line => line.Transitions?.Count == 1), "The tab underline did not receive its motion transition.");
            tabs.SelectedIndex = 1;
            await WaitFor(() => underlines[1].Opacity is > .01 and < .99, "The selected tab underline did not animate.");
            await WaitFor(() => underlines[0].Opacity == 0 && underlines[1].Opacity == 1, "The underline transition did not settle.");
            Classes.Remove("lg-motion");
            tabs.SelectedIndex = 0;
            Require(underlines[0].Opacity == 1 && underlines[1].Opacity == 0, "The underline did not change immediately with motion disabled.");
        }
        finally { Classes.Set("lg-motion", motionWasEnabled); }
        foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
        {
            Width = size.Width;
            Height = size.Height;
            await Task.Delay(100);
            tabs.SelectedIndex = size.Width == 730 ? 0 : 1;
            UpdateLayout();
            Require(tabs.SelectedIndex == 0 ? HasReleaseLink(releaseTag) && AboutRuns().Any(run => run.Text == "Library and game details") :
                HasReleaseLink(newerTag) && AboutRuns().Any(run => run.Text == "Managed mods"),
                "The selected release tab showed notes or a GitHub link for the wrong version.");
            Capture(Path.Combine(reports, $"about-release-notes-{size.Width}.png"));
        }
        _updateResult = earlierResult;
        RefreshReleaseNotes();
        Width = windowSize.Width;
        Height = windowSize.Height;
        Navigate(Page.Appearance);
        UpdateLayout();

        T Setting<T>(string name) where T : Control => PageBody.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name);
        bool AboutText(string value) => PageBody.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == value);
        bool HasReleaseLink(string tag) => PageBody.GetVisualDescendants().OfType<HeaderedContentControl>()
            .Select(section => section.Header).OfType<Grid>().SelectMany(header => header.Children.OfType<Button>()).Any(button =>
                button.IsEffectivelyVisible && Grid.GetColumn(button) == 1 && button.Content is SymbolIcon icon &&
                Equals(icon.Data, this.FindResource("LG.Icon.ExternalLink")) &&
                AutomationProperties.GetName(button) == $"View {tag} on GitHub" &&
                Equals(ToolTip.GetTip(button), $"View {tag} on GitHub"));
        IEnumerable<Run> AboutRuns() => PageBody.GetVisualDescendants().OfType<SelectableTextBlock>()
            .SelectMany(paragraph => paragraph.Inlines!.OfType<Run>());
        void VerifyReleaseTypography()
        {
            var body = AboutRuns().Single(run => run.Text?.Contains("retries metadata and artwork", StringComparison.Ordinal) == true);
            var heading = AboutRuns().Single(run => run.Text == "Library and game details");
            Require(body.FontWeight == FontWeight.Normal && body.FontFamily == FontFamily && heading.FontWeight == FontWeight.SemiBold,
                $"Release note typography was {body.FontFamily.Name} {body.FontWeight} for body text and {heading.FontWeight} for the heading.");
        }
    }

    private async Task VerifyGameTileLayout(Game game, string preview)
    {
        var librarySize = (Width, Height);
        Navigate(Page.LibrarySettings);
        UpdateLayout();
        var layoutPicker = Picker("Minimum width");
        foreach (var size in new[] { (730, 510), (1230, 805), (1600, 900) })
        {
            (Width, Height) = size;
            await Task.Delay(40);
            layoutPicker.BringIntoView();
            UpdateLayout();
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"library-game-tile-layout-{size.Item1}.png"));
        }
        (Width, Height) = librarySize;

        var columns = Picker("Columns");
        columns.BringIntoView();
        UpdateLayout();
        var minimumWidth = Picker("Minimum width");
        Require(AutomationProperties.GetHelpText(columns) == "Current value 6" && GameTileColumns == 6,
            "Existing profiles did not default to six game tile columns.");
        Require(AutomationProperties.GetHelpText(minimumWidth) == "Current value 120 px" && GameTileMinimumWidth == 120,
            "Existing profiles did not default to a 120 px minimum tile width.");
        await ChoosePreference(columns, "10");
        minimumWidth.BringIntoView();
        UpdateLayout();
        await ChoosePreference(minimumWidth, "32 px");
        Require(_store.Load().Preferences is { TileColumns: 10, TileMinimumWidth: 32 },
            "Changing the game tile layout did not save both preferences.");
        (Width, Height) = (730, 510);
        Navigate(Page.Library);
        UpdateLayout();
        var allGames = _cardGrid;
        Require(allGames is { Columns: 10 } && Math.Abs(allGames.Bounds.Width - PageBody.Bounds.Width) <= 1,
            "The full Library did not fill the available space with ten columns.");
        var playedTile = allGames!.Children.OfType<Border>().Select(host => host.Child).OfType<GameCard>().First(card => card.ShowPlaytime);
        var caption = playedTile.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PlaytimePill");
        Require(caption.Bounds.Width > 0 && caption.Bounds.Width <= playedTile.Bounds.Width,
            "A dense game tile's playtime caption overflowed the tile.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "library-game-columns-10-730.png"));
        Navigate(Page.Collections, _library.Collections.First(collection => collection.Includes(game)));
        UpdateLayout();
        Require(_cardGrid is { Columns: 10 } members && Math.Abs(members.Bounds.Width - PageBody.Bounds.Width) <= 1,
            "Collection members did not use the saved game column count.");
        Navigate(Page.Home);
        UpdateLayout();
        var homeLibrary = PageBody.Children.OfType<ItemsControl>().Single(view => view.Name == "HomeLibraryGrid");
        homeLibrary.BringIntoView();
        await WaitFor(() => homeLibrary.GetVisualDescendants().OfType<UniformGrid>().Any(grid => grid.Columns == 10),
            "The Home Library did not use the saved game column count.");
        (Width, Height) = librarySize;
        Navigate(Page.LibrarySettings);
        UpdateLayout();
        minimumWidth = Picker("Minimum width");
        minimumWidth.BringIntoView();
        UpdateLayout();
        await ChoosePreference(minimumWidth, "160 px");
        for (var count = 2; count <= 10; count++)
        {
            var threshold = count * 160 + (count - 1) * GameTileColumnSpacing;
            Require(FittingGameTileColumns(threshold - 1) == count - 1 && FittingGameTileColumns(threshold) == count,
                $"Tile sizing skipped the {count}-column threshold.");
        }
        (Width, Height) = (730, 510);
        Navigate(Page.Library);
        await WaitFor(() => _cardGrid?.Columns == 2, "The narrow Library did not reduce columns at the higher minimum tile width.");
        (Width, Height) = librarySize;
        await WaitFor(() => _cardGrid?.Columns > 2, "The Library did not restore columns at the wider size.");
        Navigate(Page.LibrarySettings);
        UpdateLayout();
        await ChoosePreference(Picker("Columns"), "6");
        await ChoosePreference(Picker("Minimum width"), "120 px");
        Require(_store.Load().Preferences is { TileColumns: null, TileMinimumWidth: null }, "Restoring default tile settings did not persist.");
        Navigate(Page.Home);

        Button Picker(string name) => PageBody.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == name);
    }

    private async Task VerifyColourwaysAndWidgets(string preview)
    {
        var folder = Path.GetDirectoryName(preview)!;
        var previousSize = (Width, Height);
        Navigate(Page.Appearance);
        UpdateLayout();
        var picker = Setting<Button>("Colourway");
        var accent = ((ISolidColorBrush)this.FindResource("LG.AccentFill")!).Color;
        foreach (var palette in Colourways.Skip(1))
        {
            await ChoosePreference(picker, palette.Name);
            Require(Background is ISolidColorBrush background && background.Color == Color.Parse(palette.Canvas) &&
                MainArea.Background is ISolidColorBrush canvas && canvas.Color == background.Color &&
                this.FindResource("LG.ArtworkFallback") is ISolidColorBrush &&
                ((ISolidColorBrush)this.FindResource("LG.AccentFill")!).Color == accent &&
                _store.Load().Preferences.Colourway == palette.Name, $"Changing to {palette.Name} did not apply and save its surface palette.");
            Capture(Path.Combine(folder, $"colourway-{palette.Name.Replace(' ', '-').ToLowerInvariant()}.png"));
        }
        Navigate(Page.Widgets);
        UpdateLayout();
        await ChoosePreference(Setting<Button>("Corner radius"), "4 px");
        await ChoosePreference(Setting<Button>("Shadow strength"), "Off");
        Require(this.FindResource("LG.PanelRadius") is CornerRadius radius && radius == new CornerRadius(4) &&
            this.FindResource("LG.ModalShadow") is BoxShadows shadow && shadow.Equals(default(BoxShadows)) &&
            _store.Load().Preferences.WidgetRadius == 4 && _store.Load().Preferences.WidgetShadowStrength == 0,
            "Widget preferences did not apply to shared resources or persist.");
        foreach (var size in new[] { (730, 510), (1230, 805), (1600, 900) })
        {
            (Width, Height) = size;
            await Task.Delay(85);
            Capture(Path.Combine(folder, $"widget-customisation-{size.Item1}.png"));
        }
        (Width, Height) = previousSize;
        await ChoosePreference(Setting<Button>("Corner radius"), "Default");
        await ChoosePreference(Setting<Button>("Shadow strength"), "100%");
        Navigate(Page.Appearance);
        UpdateLayout();
        await ChoosePreference(Setting<Button>("Colourway"), "Classic");
        Require(MainArea.Background is RadialGradientBrush &&
            this.FindResource("LG.ArtworkFallback") is LinearGradientBrush, "Classic did not restore its directional lighting.");
        Capture(Path.Combine(folder, "colourway-classic.png"));

        T Setting<T>(string name) where T : Control => PageBody.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name);
    }

    private async Task VerifyArtworkAccent(string preview)
    {
        var folder = Path.GetDirectoryName(preview)!;
        var cool = SampleGame("Teal artwork", new SKColor(16, 136, 119));
        var warm = SampleGame("Gold artwork", new SKColor(199, 118, 23));
        var dark = SampleGame("Blue artwork", new SKColor(32, 70, 161));
        var red = SampleGame("Red artwork", new SKColor(191, 38, 31), colouredDetail: false);
        var neutral = SampleGame("Greyscale artwork", new SKColor(100, 100, 100), colouredDetail: false);
        var missing = new Game { Title = "No artwork", Path = Path.Combine(Program.DataDirectory, "missing-artwork.exe") };
        var fixtures = new[] { cool, warm, dark, red, neutral, missing };
        _library.Games.AddRange(fixtures);
        var windowSize = (Width, Height);
        try
        {
            var purple = Accent();
            Require(ReadToggle().IsChecked == true, "A profile without the artwork-accent setting did not default to enabled.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(40);
                Capture(Path.Combine(folder, $"appearance-artwork-accent-{size.Width}.png"));
            }

            var teal = await Open(cool);
            Require(teal != purple && teal.G > teal.R && teal.B > teal.R &&
                PlayGame.Background is ISolidColorBrush fill && fill.Color == teal,
                "The primary button did not use the game's dominant artwork colour.");
            CheckTextContrast(teal, Colors.Black);
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(40);
                Capture(Path.Combine(folder, $"game-artwork-accent-{size.Width}.png"));
            }

            var gold = await Open(warm);
            Require(gold != teal && gold.R > gold.G && gold.G > gold.B, "A second game's artwork did not replace the previous accent.");
            Require(ArtworkAccent.Contrast(SidebarSelection(), ((ISolidColorBrush)this.FindResource("LG.Text")!).Color) >= 4.5,
                "The selected library row lost text contrast on warm artwork.");
            var blue = await Open(dark);
            Require(blue.B > blue.R && blue.B > blue.G, "Dark blue artwork did not supply a blue accent.");
            CheckTextContrast(blue, Colors.White);
            Capture(Path.Combine(folder, "game-artwork-accent-light-text.png"));
            await VerifyAccentTransition(teal, blue);
            SelectGame(red);
            Require(Accent() != purple && (_detailsRequest == null || Accent() == blue),
                "The temporary artwork gap flashed the default accent while changing games.");
            await WaitFor(() => _detailsRequest == null, "The red artwork fixture did not finish loading.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(40);
                CheckRedSidebar();
                Capture(Path.Combine(folder, $"sidebar-red-accent-{size.Width}.png"));
            }
            var previousTranslucency = _library.Preferences.DisableTranslucency;
            try
            {
                _library.Preferences.DisableTranslucency = true;
                ApplyTranslucency();
                CheckRedSidebar();
                Capture(Path.Combine(folder, "sidebar-red-accent-opaque.png"));
            }
            finally
            {
                _library.Preferences.DisableTranslucency = previousTranslucency;
                ApplyTranslucency();
            }
            Require(await Open(neutral) == purple && await Open(missing) == purple, "Neutral or missing artwork did not restore the default accent.");
            await Open(warm);
            SelectGame(red);
            Navigate(Page.Home);
            Require(Accent() == purple, "Leaving game details did not restore the default accent immediately.");
            await WaitFor(() => _detailsRequest == null, "The abandoned artwork request did not finish cancelling.");
            Require(Accent() == purple, "Navigating away allowed an artwork request to tint the Home view.");

            Navigate(Page.Appearance);
            ReadToggle().IsChecked = false;
            Require(_store.Load().Preferences.DisableArtworkAccent && await Open(cool) == purple,
                "Disabling the artwork accent did not persist and restore purple on game details.");
            Navigate(Page.Appearance);
            Require(Accent() == purple && ReadToggle().IsChecked == false, "Appearance did not restore the default accent and saved toggle.");
            ReadToggle().IsChecked = true;
            Require(!_store.Load().Preferences.DisableArtworkAccent && await Open(cool) == teal, "Re-enabling the artwork accent did not restore the game colour.");
            await VerifyDefaultAccentColour(purple, teal);
        }
        finally
        {
            foreach (var fixture in fixtures) _library.Games.Remove(fixture);
            (Width, Height) = windowSize;
            Navigate(Page.Appearance);
            Require(Save(), "Could not remove the artwork accent fixtures from the test profile.");
        }

        Color Accent() => ((ISolidColorBrush)this.FindResource("LG.AccentFill")!).Color;

        async Task VerifyDefaultAccentColour(Color purple, Color teal)
        {
            Navigate(Page.Appearance);
            var defaultForeground = ((ISolidColorBrush)this.FindResource("LG.OnAccent")!).Color;
            var defaultSelection = ((ISolidColorBrush)this.FindResource("LG.Selection")!).Color;
            var defaultSwitchTrack = ((ISolidColorBrush)ArtworkSwitchTrack().Background!).Color;
            var picker = OpenAccentPicker();
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(40);
                var purplePreset = picker.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "Default");
                var swatch = picker.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "HexColourPreview");
                Require(purplePreset.Content is Border && swatch.Bounds.Height > 0 &&
                    Math.Abs(swatch.Bounds.Width - picker.HexField.Bounds.Height) < 1 &&
                    Math.Abs(swatch.Bounds.Height - picker.HexField.Bounds.Height) < 1,
                    "The default preset did not match other swatches or the hex preview did not fit the field.");
                Capture(Path.Combine(folder, $"default-accent-picker-{size.Width}.png"));
            }

            picker.HexField.Text = "#123";
            PressDialogButton("Apply");
            Require(DialogHost.HasModal && Accent() == purple && _store.Load().Preferences.DefaultAccentColour == null &&
                !picker.GetVisualDescendants().OfType<Button>().Any(button => button.Classes.Contains("selected")),
                "Invalid hex input kept a selected preset, changed the default accent, or dismissed the picker.");
            Capture(Path.Combine(folder, "default-accent-picker-invalid.png"));
            picker.HexField.Text = "#123456";
            PressDialogButton("Cancel");
            Require(!DialogHost.HasModal && Accent() == purple && _store.Load().Preferences.DefaultAccentColour == null,
                "Cancelling the colour picker changed the default accent.");

            picker = OpenAccentPicker();
            picker.HexField.Text = "31415d";
            PressDialogButton("Apply");
            var custom = Color.FromRgb(49, 65, 93);
            Require(!DialogHost.HasModal && Accent() == custom && _store.Load().Preferences.DefaultAccentColour == "#31415D",
                "Applying a custom colour did not update and persist the palette.");
            var track = ((ISolidColorBrush)ArtworkSwitchTrack().Background!).Color;
            var knob = (ISolidColorBrush)ReadToggle().GetVisualDescendants().OfType<Ellipse>().Single(part => part.Name == "SwitchKnobOn").Fill!;
            Require(track != defaultSwitchTrack && track.B > track.R && ArtworkAccent.Contrast(track, knob.Color) >= 4.5,
                "Enabled switches did not adopt a legible custom accent.");
            Capture(Path.Combine(folder, "appearance-custom-accent.png"));
            var splash = CreateSplash();
            try
            {
                splash.Show();
                splash.ShowProgress(60, "Loading library");
                Require(splash.FindControl<ProgressBar>("SplashProgress")?.Foreground is ISolidColorBrush indicator &&
                    indicator.Color == ArtworkAccent.ForDarkSurface(custom), "The splash did not use the custom accent.");
                CaptureControl(splash, Path.Combine(folder, "splash-custom-accent.png"));
            }
            finally
            {
                splash.Close();
            }
            Navigate(Page.Home);
            Require(Accent() == custom,
                "The default accent was not applied outside Appearance.");
            Require(await Open(cool) == teal && await Open(neutral) == custom && await Open(missing) == custom,
                "Artwork colours did not override the custom default only when artwork supplied a usable colour.");
            CheckTextContrast(custom, Colors.White);
            await Open(cool);
            Navigate(Page.Home);
            Require(Accent() == custom, "Leaving game details did not immediately restore the custom default colour.");

            Navigate(Page.Appearance);
            ReadToggle().IsChecked = false;
            Require(await Open(cool) == custom, "A disabled artwork accent overrode the custom default colour.");
            Navigate(Page.Appearance);
            ReadToggle().IsChecked = true;
            picker = OpenAccentPicker();
            Require(picker.HexField.Text == "#31415D" && picker.HexField.SelectionBrush is ISolidColorBrush selectedText &&
                selectedText.Color == ((ISolidColorBrush)this.FindResource("LG.Selection")!).Color,
                "Reopening the colour picker did not restore the selected colour and tint text selection.");
            Capture(Path.Combine(folder, "default-accent-picker-custom.png"));
            PressDialogButton("Default");
            Require(picker.HexField.Text == "#BFB0FF", "The default preset did not select the built-in purple.");
            PressDialogButton("Apply");
            Require(Accent() == purple && _store.Load().Preferences.DefaultAccentColour == null &&
                ((ISolidColorBrush)this.FindResource("LG.OnAccent")!).Color == defaultForeground &&
                ((ISolidColorBrush)this.FindResource("LG.Selection")!).Color == defaultSelection &&
                ((ISolidColorBrush)ArtworkSwitchTrack().Background!).Color == defaultSwitchTrack,
                "Resetting the accent did not restore the exact built-in palette.");

            Border ArtworkSwitchTrack() => ReadToggle().GetVisualDescendants().OfType<Border>()
                .Single(part => part.Name == "SwitchKnobBounds");

            ColourPicker OpenAccentPicker()
            {
                UpdateLayout();
                var trigger = PageBody.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "Default accent colour");
                trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                UpdateLayout();
                return DialogHost.GetVisualDescendants().OfType<ColourPicker>().Single();
            }

            void PressDialogButton(string name)
            {
                var button = DialogHost.GetVisualDescendants().OfType<Button>()
                    .Single(item => AutomationProperties.GetName(item) == name || Equals(item.Content, name));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                UpdateLayout();
            }
        }

        async Task VerifyAccentTransition(Color start, Color end)
        {
            var previousMotion = _motion;
            try
            {
                foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
                {
                    Width = size.Width;
                    Height = size.Height;
                    _motion = false;
                    Require(await Open(cool) == start, "The starting palette did not apply immediately with motion disabled.");
                    var brush = (SolidColorBrush)PlayGame.Background!;
                    var samples = new List<(Color Fill, Color Text)>();
                    var captured = false;
                    brush.PropertyChanged += Observe;
                    try
                    {
                        _motion = true;
                        SelectGame(dark);
                        await WaitFor(() => _detailsRequest == null && Accent() == end, "The artwork accent transition did not settle.");
                    }
                    finally { brush.PropertyChanged -= Observe; }

                    Require(captured && samples[^1].Fill == end && samples.All(sample =>
                        sample.Text == ArtworkAccent.TextOn(sample.Fill) && ArtworkAccent.Contrast(sample.Fill, sample.Text) >= 4.5),
                        "The accent fill and foreground did not animate together with readable text.");

                    void Observe(object? sender, AvaloniaPropertyChangedEventArgs args)
                    {
                        if (args.Property != SolidColorBrush.ColorProperty) return;
                        samples.Add((brush.Color, ((ISolidColorBrush)PlayGame.Foreground!).Color));
                        if (captured || brush.Color == start || brush.Color == end) return;
                        captured = true;
                        Capture(Path.Combine(folder, $"artwork-accent-transition-{size.Width}.png"));
                    }
                }
            }
            finally { _motion = previousMotion; }
        }

        Color SidebarSelection()
        {
            UpdateLayout();
            var item = LibraryGames.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.IsSelected);
            var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().Single(part => part.Name == "PART_ContentPresenter");
            Require(presenter.Background is ISolidColorBrush { Opacity: 1, Color.A: 255 }, "The selected library row lost its solid tint.");
            return ((ISolidColorBrush)presenter.Background!).Color;
        }

        void CheckRedSidebar()
        {
            var selection = SidebarSelection();
            var text = ((ISolidColorBrush)this.FindResource("LG.Text")!).Color;
            Require(selection.R >= selection.G * 1.7 && selection.R >= selection.B * 1.7 &&
                ArtworkAccent.Contrast(selection, text) >= 4.5,
                "The selected library row lost the red artwork hue or text contrast.");
        }

        void CheckTextContrast(Color fill, Color expected)
        {
            Require(PlayGame.Foreground is ISolidColorBrush text && text.Color == expected &&
                ArtworkAccent.Contrast(fill, text.Color) >= 4.5,
                "The Play button did not pick a readable foreground for the artwork accent.");
        }
        ToggleSwitch ReadToggle()
        {
            UpdateLayout();
            return PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(toggle => AutomationProperties.GetName(toggle) == "Artwork accent colour");
        }

        async Task<Color> Open(Game game)
        {
            SelectGame(game);
            await WaitFor(() => _detailsRequest == null, "The accent fixture artwork did not finish loading.");
            UpdateLayout();
            return Accent();
        }

        Game SampleGame(string title, SKColor colour, bool colouredDetail = true)
        {
            var game = new Game { Title = title, Path = Path.Combine(Program.DataDirectory, $"{title}.exe"), CustomArtworkId = Guid.NewGuid().ToString("N") };
            var path = _store.CustomArtworkPath(game.CustomArtworkId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var bitmap = new SKBitmap(480, 270);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(colour);
            if (colouredDetail)
            {
                using var detail = new SKPaint { Color = new SKColor(238, 36, 79) };
                canvas.DrawRect(new SKRect(310, 35, 450, 110), detail);
            }
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, png.ToArray());
            return game;
        }
    }

    private static FrostedContextMenu PreferenceMenu(Button picker) => picker.ContextMenu as FrostedContextMenu
        ?? throw new InvalidOperationException("The preference picker did not use the shared frosted context menu.");

    private static async Task ChoosePreference(Button picker, string label)
    {
        await SelectPreferenceOption(picker, label);
        await WaitFor(() => picker.ContextMenu is { IsOpen: false } && AutomationProperties.GetHelpText(picker) == $"Current value {label}",
            "The preference selection did not apply when the menu closed.");
    }

    private static async Task SelectPreferenceOption(Button picker, string label)
    {
        var menu = PreferenceMenu(picker);
        picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => menu.IsOpen && menu.RevealProgress == 1, "The preference menu did not open.");
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private async Task VerifyCustomInterfaceScale(Button picker, string preview)
    {
        await SelectPreferenceOption(picker, "Custom…");
        await WaitFor(() => DialogHost.HasModal, "Choosing Custom did not open the interface scale editor.");
        UpdateLayout();
        var dialog = DialogHost.GetVisualDescendants().OfType<ModalDialog>().Single();
        var field = ScaleField(dialog);
        var apply = dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Apply"));
        Require(field.Text == "100", "The scale editor did not start with the current percentage.");
        foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
        {
            Width = size.Width;
            Height = size.Height;
            await Task.Delay(85);
            UpdateLayout();
            Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"appearance-custom-scale-{size.Width}.png"));
        }
        foreach (var invalid in new[] { "74", "201", "137.5" })
        {
            field.Text = invalid;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(DialogHost.HasModal && _store.Load().Preferences.InterfaceScalePercent == 100,
                $"The scale editor accepted an invalid percentage ({invalid}).");
        }
        Require(dialog.GetVisualDescendants().OfType<TextBlock>().Any(label => label.IsVisible && label.Text == "Enter a whole number from 75 to 200."),
            "The scale editor did not explain how to correct an invalid percentage.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "appearance-custom-scale-invalid.png"));
        field.Text = "137";
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => !DialogHost.HasModal && AutomationProperties.GetHelpText(picker) == "Current value 137%",
            "The custom scale did not update the picker.");
        Require(_store.Load().Preferences.InterfaceScalePercent == 137 &&
            InterfaceRoot.LayoutTransform?.Value is { M11: 1.37, M22: 1.37 } &&
            PreferenceMenu(picker).Items.OfType<MenuItem>().Single(item => item.IsChecked).Header is "Custom…",
            "The custom scale did not apply, persist, and select Custom in the menu.");
        await SelectPreferenceOption(picker, "Custom…");
        await WaitFor(() => DialogHost.HasModal, "The custom scale editor could not be reopened.");
        UpdateLayout();
        field = ScaleField(DialogHost.GetVisualDescendants().OfType<ModalDialog>().Single());
        Require(field.Text == "137", "The scale editor did not reopen with the saved custom percentage.");
        field.Text = "160";
        DialogHost.DismissTop();
        Require(_store.Load().Preferences.InterfaceScalePercent == 137, "Cancelling the editor changed the custom scale.");

        static TextBox ScaleField(ModalDialog dialog) => dialog.GetVisualDescendants().OfType<TextBox>()
            .Single(field => AutomationProperties.GetName(field) == "Interface scale percentage");
    }

    private async Task VerifyPreferencePickers(Button family, Button fontSize, Button scale, string preview)
    {
        var windowSize = new Size(Width, Height);
        var reports = Path.GetDirectoryName(preview)!;
        try
        {
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(85);
                Capture(Path.Combine(reports, $"appearance-pickers-{size.Width}.png"));
                foreach (var (picker, name) in new[] { (family, "font-family"), (fontSize, "font-size"), (scale, "interface-scale") })
                {
                    var menu = PreferenceMenu(picker);
                    picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitFor(() => menu.IsOpen && menu.RevealProgress == 1, "The shared preference menu did not open.");
                    var backdrop = menu.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "FrostedBackdrop");
                    var scroll = menu.GetVisualDescendants().OfType<ScrollViewer>().Single();
                    Require(backdrop.Background is ImageBrush && menu.Padding == new Thickness(4) && scroll.Margin == menu.Padding &&
                        menu.Items.OfType<MenuItem>().Count(item => item.IsChecked) == 1,
                        "The preference menu lacked the context menu's frosted backdrop, uniform padding, or checked option.");
                    if (picker == scale)
                    {
                        Require(menu.Items.OfType<MenuItem>().Single(item => item.IsChecked).Header is "100%" &&
                            AutomationProperties.GetHelpText(scale) == "Current value 100%",
                            "The picker reverted its label or checked option after cancelling Custom and selecting 100%.");
                    }
                    CaptureControl(menu, Path.Combine(reports, $"picker-{name}-{size.Width}.png"));
                    menu.Close();
                    await WaitFor(() => !menu.IsOpen, "The preference menu remained open.");
                }
            }

            var fontMenu = PreferenceMenu(family);
            var fontLeft = family.TranslatePoint(default, this) ?? throw new InvalidOperationException("The font picker was not in the window.");
            fontMenu.Width = ClientSize.Width - fontLeft.X + 24;
            family.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Down });
            await WaitFor(() => fontMenu.IsOpen && fontMenu.RevealProgress == 1, "Down Arrow did not open the font picker.");
            var menuRight = fontMenu.PointToScreen(new Point(fontMenu.Bounds.Width, 0)).X;
            var pickerRight = family.PointToScreen(new Point(family.Bounds.Width, 0)).X;
            Require(Math.Abs(menuRight - pickerRight) <= 1,
                $"The font menu extended past the launcher instead of aligning to the right edge of its button ({menuRight} vs {pickerRight}).");
            fontMenu.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = "System default" });
            Require(fontMenu.SelectedItem is MenuItem { Header: "System default" } && _library.Preferences.InterfaceFont == "Segoe UI",
                "Typing in the font menu did not move to the matching option without committing it.");
            fontMenu.Close();
            await WaitFor(() => !fontMenu.IsOpen, "The font picker remained open.");
            fontMenu.ClearValue(WidthProperty);

            var scaleMenu = PreferenceMenu(scale);
            Classes.Add("lg-motion");
            scale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => scaleMenu.RevealProgress is > 0 and < 1, "The preference menu did not animate open.");
            await WaitFor(() => scaleMenu.RevealProgress == 1, "The preference menu reveal did not finish.");
            scaleMenu.Close();
            await WaitFor(() => scaleMenu.IsOpen && scaleMenu.RevealProgress is > 0 and < 1, "The preference menu did not animate closed.");
            await WaitFor(() => !scaleMenu.IsOpen, "The preference menu dismissal did not finish.");

            Classes.Remove("lg-motion");
            SettingTranslucency().IsChecked = false;
            scale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(scaleMenu.IsOpen && scaleMenu.RevealProgress == 1 &&
                scaleMenu.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "FrostedBackdrop").Background == null,
                "The opaque preference menu still sampled the window or animated with motion disabled.");
            CaptureControl(scaleMenu, Path.Combine(reports, "picker-opaque.png"));
            scaleMenu.Close();
            Require(!scaleMenu.IsOpen, "The preference menu did not dismiss immediately with motion disabled.");
        }
        finally
        {
            Classes.Remove("lg-motion");
            SettingTranslucency().IsChecked = true;
            Width = windowSize.Width;
            Height = windowSize.Height;
            UpdateLayout();
        }

        ToggleSwitch SettingTranslucency() => PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(toggle => AutomationProperties.GetName(toggle) == "Translucency");
    }

    private sealed class InstalledReleaseFixture(string tag, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var currentTag = UpdateFeed.ReleaseTag(UpdateFeed.CurrentVersion);
            Require(request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsoluteUri == $"https://api.github.com/repos/H4RL33/lg_launcher/releases/tags/{currentTag}",
                "About requested notes for a different version or repository.");
            const string markdown = """
                ### Library and game details

                - **Refresh from Steam** retries metadata and artwork while preserving custom artwork and saved framing.
                - Review available genres and tags from a game's details.

                ### Windows download

                Run the portable executable directly or use Windows Setup to add a Start menu shortcut. Windows x64 requires the .NET 10 Runtime.
                """;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = tag,
                    prerelease = tag.Contains("-next.", StringComparison.Ordinal),
                    body = markdown
                }))
            });
        }
    }
}
