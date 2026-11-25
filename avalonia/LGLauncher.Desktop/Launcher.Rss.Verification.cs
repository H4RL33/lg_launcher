using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private const string RssFixture = """
        <rss version="2.0"><channel><title>Developer news</title>
        <item><title>New build available</title><link>https://example.org/articles/build</link>
        <pubDate>Thu, 19 Nov 2026 12:00:00 GMT</pubDate>
        <description><![CDATA[<p>Release notes &amp; <strong>highlights</strong>.</p>
        <script>hidden text</script><p><em>Second paragraph.</em></p>
        <p>Downloads are available as a <a href="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"><b>fixture</b> magnet</a>
        or a <a href="../files/fixture.torrent">torrent descriptor</a>.</p>
        <template><template><img src="hidden-inner.png"></template><script>const label = "</template>";</script>
        <img src="hidden-outer.png"></template>
        <img src="cover.png" alt="Release cover"><img src="file:///blocked.png">
        <img src="data:image/png;base64,blocked">]]></description>
        <enclosure url="https://example.org/files/extra.torrent" type="application/x-bittorrent"/>
        <enclosure url="https://example.org/files/extra.torrent#duplicate" type="application/x-bittorrent"/>
        </item>
        <item><title>Older update</title><link>javascript:alert(1)</link></item>
        </channel></rss>
        """;

    private async Task VerifyRss(string preview)
    {
        VerifyRssMarkupLimits();
        VerifyRssTransferMarkup();
        await VerifyRssTorrentDescriptors();
        RssNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(_page == Page.Rss && RssNav.Classes.Contains("selected"), "The sidebar RSS button did not open the reader.");
        Navigate(Page.Home);
        UpdateLayout();
        var shelfSettings = VerifyHomeRssHeader("No feeds found");
        Require(!_homeRssRefresh!.IsEnabled, "The Home RSS refresh was enabled without any feeds.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "home-rss-no-feeds.png"));
        shelfSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(_page == Page.RssSettings, "The Home RSS settings icon did not open feed settings.");
        UpdateLayout();
        var feedUrl = PageBody.GetVisualDescendants().OfType<TextBox>()
            .Single(box => Avalonia.Automation.AutomationProperties.GetName(box) == "Feed URL");
        Require(feedUrl.VerticalContentAlignment == VerticalAlignment.Center, "The feed URL was not vertically centered.");
        RssNav.Focus();
        Classes.Add("lg-motion");
        try { await VerifyTextFieldHover(feedUrl, Path.Combine(Path.GetDirectoryName(preview)!, "rss-url-hover.png")); }
        finally { Classes.Remove("lg-motion"); }
        var previousFont = _library.Preferences.InterfaceFont;
        try
        {
            _library.Preferences.InterfaceFont = "Consolas";
            ApplyFont();
            VerifyFeedUrlMenu(feedUrl, Path.Combine(Path.GetDirectoryName(preview)!, "rss-url-menu.png"));
        }
        finally
        {
            _library.Preferences.InterfaceFont = previousFont;
            ApplyFont();
        }
        var interval = PageBody.GetVisualDescendants().OfType<NumericUpDown>().Single();
        Require(interval.Value == 30 && RssRefreshMinutes == 30, "Existing profiles did not default to a 30-minute RSS interval.");
        var intervalLabel = PageBody.GetVisualDescendants().OfType<TextBlock>()
            .Single(label => label.Text == "Refresh interval (minutes)");
        var intervalPosition = interval.TranslatePoint(interval.Bounds.Center - interval.Bounds.Position, this);
        var labelPosition = intervalLabel.TranslatePoint(intervalLabel.Bounds.Center - intervalLabel.Bounds.Position, this);
        Require(intervalPosition is { } controlCenter && labelPosition is { } labelCenter &&
            controlCenter.X > labelCenter.X && Math.Abs(controlCenter.Y - labelCenter.Y) <= 1,
            "The interval label and controls were not in the same horizontal settings row.");
        var steps = interval.GetVisualDescendants().OfType<RepeatButton>().ToArray();
        Require(interval.Classes.Contains("lg-number") && steps.Length == 2 &&
            steps.All(button => button.Content is LGLauncher.Components.SymbolIcon),
            "The RSS interval did not use the shared number field and step buttons.");
        await VerifyNumberSpinnerInsets(interval, preview);
        steps.Single(button => button.Name == "PART_IncreaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(interval.Value == 31 && _store.Load().Preferences.RssRefreshMinutes == 31 &&
            _rssTimer.Interval == TimeSpan.FromMinutes(31), "The themed number field did not save after incrementing.");
        steps.Single(button => button.Name == "PART_DecreaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(interval.Value == 30 && _store.Load().Preferences.RssRefreshMinutes == 30 &&
            _rssTimer.Interval == TimeSpan.FromMinutes(30), "The themed number field did not save after decrementing.");
        var intervalText = interval.GetVisualDescendants().OfType<TextBox>().Single();
        intervalText.Focus();
        intervalText.Text = "1";
        Require(_store.Load().Preferences.RssRefreshMinutes == 30, "A partial edit changed the saved RSS interval.");
        intervalText.Text = "12";
        intervalText.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
        Require(_store.Load().Preferences.RssRefreshMinutes == 12 && _rssTimer.Interval == TimeSpan.FromMinutes(12),
            "Submitting a typed interval did not save and update the timer.");
        intervalText.Text = "60";
        feedUrl.Focus();
        Require(_store.Load().Preferences.RssRefreshMinutes == 60 && _rssTimer.Interval == TimeSpan.FromMinutes(60),
            "Leaving the interval field did not save and update the timer.");
        intervalText.Focus();
        intervalText.Text = "";
        feedUrl.Focus();
        Require(interval.Value == 60 && _store.Load().Preferences.RssRefreshMinutes == 60 &&
            _rssTimer.Interval == TimeSpan.FromMinutes(60), "Leaving an empty interval changed the saved setting.");
        Require(!PageBody.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Save interval")),
            "RSS settings still showed a separate interval save action.");

        var internalBrowser = RssBrowserPreference();
        Require(internalBrowser.IsChecked == false,
            "RSS links did not default to the system browser.");
        internalBrowser.IsChecked = true;
        var internalTransfers = RssTransferPreference();
        Require(internalTransfers.IsChecked == false, "RSS transfers did not default to the system handler.");
        internalTransfers.IsChecked = true;
        Require(_store.Load().Preferences.OpenRssTransfersInternally,
            "The internal transfer preference did not persist.");

        var path = Path.Combine(Program.DataDirectory, "rss-fixture.xml");
        File.WriteAllText(path, RssFixture);
        var list = Path.Combine(Program.DataDirectory, "subscriptions.opml");
        File.WriteAllText(list, """
            <opml version="2.0"><body><outline text="News">
            <outline text="Remote news" xmlUrl="https://example.org/atom"/>
            <outline xmlUrl="https://example.org/atom#duplicate"/><outline xmlUrl="file:///blocked.xml"/>
            </outline></body></opml>
            """);
        Require(AddRssSources(RssReader.Import(path)) == "Added 1 feed." &&
            AddRssSources(RssReader.Import(list)) == "Added 1 feed." && RssSubscriptions.Count == 2,
            "RSS/OPML import did not add the file and deduplicate the nested subscription list.");
        var fileFeed = RssSubscriptions.Single(feed => feed.IsFile);
        var remote = RssSubscriptions.Single(feed => !feed.IsFile);
        UpdateLayout();
        var remoteToggle = _rssFeedRows!.GetVisualDescendants().OfType<ToggleSwitch>().Last();
        var switchThumb = remoteToggle.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "PART_MovingKnobs");
        var thumbTravel = remoteToggle.GetVisualDescendants().OfType<Canvas>()
            .Single(canvas => canvas.Name == "PART_SwitchKnob").Bounds.Width;
        Classes.Add("lg-motion");
        remoteToggle.IsChecked = false;
        Require(!_store.Load().Preferences.RssFeeds.Single(feed => feed.Id == remote.Id).Enabled &&
            _rssFeedRows.GetVisualDescendants().Contains(remoteToggle),
            "Disabling the remote feed did not save the setting.");
        try
        {
            await WaitFor(() => Canvas.GetLeft(switchThumb) > 0 && Canvas.GetLeft(switchThumb) < thumbTravel,
                "The feed switch did not animate toward its off position.");
            await WaitFor(() => Canvas.GetLeft(switchThumb) == 0, "The feed switch did not settle at its off position.");
        }
        finally { Classes.Remove("lg-motion"); }
        remoteToggle.IsChecked = true;
        Require(Canvas.GetLeft(switchThumb) == thumbTravel, "The feed switch animated with reduced motion enabled.");
        remoteToggle.IsChecked = false;

        var settingsWidth = Width;
        try
        {
            foreach (var width in new[] { MinWidth, 1230, 1600 })
            {
                Width = width;
                UpdateLayout();
                Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"rss-settings-{width}.png"));
            }
        }
        finally
        {
            Width = settingsWidth;
            UpdateLayout();
        }
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "rss-settings.png"));
        Navigate(Page.Home);
        var shelfHost = _homeRssHost;
        Require(shelfHost != null && PageBody.Children[0] == shelfHost,
            "Recent RSS articles were not enabled by default at the top of Home.");
        UpdateLayout();
        VerifyHomeRssHeader("No articles found");
        Require(_homeRssRefresh!.IsEnabled, "The Home RSS refresh was disabled with an enabled feed.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "home-rss-no-articles.png"));
        _rssRefreshPending = false;
        _homeRssRefresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(_rssRefreshPending, "The Home RSS refresh icon did not request an update.");
        await RefreshRss();
        Require(_rssSnapshots[fileFeed.Id].Articles.Length == 2 && !_rssSnapshots.ContainsKey(remote.Id),
            "Refresh did not respect enabled feeds or load all articles.");
        Require(_rssSnapshots[fileFeed.Id].Articles[1].Link == null, "An unsafe article link was accepted.");
        UpdateLayout();
        var shelf = PageBody.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "HomeRssRow");
        Require(shelfHost == _homeRssHost, "Refreshing articles replaced the Home view.");
        Require(shelf.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden, "The Home RSS shelf showed a horizontal scrollbar.");
        var originalWidth = Width;
        try
        {
            foreach (var width in new[] { MinWidth, 1230, 1600 })
            {
                Width = width;
                await Task.Delay(40);
                UpdateLayout();
                VerifyHomeRssHeader(null);
                Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"home-rss-{width}.png"));
            }
            Width = MinWidth;
            var next = shelfHost!.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "HomeRssRowNext");
            await WaitFor(() => next.IsEffectivelyVisible && next.IsEnabled, "The Home RSS shelf did not offer scrolling when it overflowed.");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => shelf.Offset.X > 0, "The Home RSS arrow did not scroll the shelf with its scrollbar hidden.");
        }
        finally
        {
            Width = originalWidth;
            UpdateLayout();
        }
        var firstArticleCard = shelf.GetVisualDescendants().OfType<Button>().First(button =>
            Avalonia.Automation.AutomationProperties.GetName(button) == "New build available");
        var cardText = firstArticleCard.GetVisualDescendants().OfType<TextBlock>().ToArray();
        Require(cardText.Single(label => label.Classes.Contains("lg-muted")).FontWeight == FontWeight.Normal &&
            cardText.Single(label => label.Text == "New build available").FontWeight == FontWeight.SemiBold,
            "The RSS card caption inherited the heavier action weight, or its title lost emphasis.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "home-rss.png"));
        firstArticleCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(_page == Page.RssArticle && PageTitle.Text == "New build available", "The Home article card did not open the reader.");
        Navigate(Page.Rss);
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "rss.png"));
        var articleButton = PageBody.Children.OfType<Button>().First();
        Require(Avalonia.Automation.AutomationProperties.GetName(articleButton) == "New build available",
            "The RSS view did not show articles newest first.");
        articleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UpdateLayout();
        var articleText = PageBody.GetVisualDescendants().OfType<SelectableTextBlock>()
            .SelectMany(block => block.Inlines!.OfType<Run>()).ToArray();
        Require(_page == Page.RssArticle &&
            articleText.Any(run => run.Text == "highlights" && run.FontWeight == FontWeight.Bold) &&
            articleText.Any(run => run.Text == "Second paragraph." && run.FontStyle == FontStyle.Italic) &&
            articleText.All(run => run.Text?.Contains("hidden text") != true),
            "The article reader did not preserve safe rich text.");
        var transferLinks = PageBody.GetVisualDescendants().OfType<Button>().Where(button =>
            AutomationProperties.GetName(button) is "fixture magnet" or "torrent descriptor").ToArray();
        Require(transferLinks.Length == 2 && transferLinks.All(button =>
                button.Focusable && button.Bounds.Width > 0 && button.Bounds.Height > 0),
            "Inline RSS transfers did not render as two keyboard-accessible links.");
        Require(PageBody.GetVisualDescendants().OfType<Button>().Count(button => Equals(button.Content, "Open .torrent file")) == 1 &&
            PageBody.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Open original")),
            "An RSS enclosure was duplicated or replaced the ordinary article action.");
        foreach (var link in transferLinks)
        {
            var paragraph = link.FindAncestorOfType<SelectableTextBlock>()!;
            var label = (TextBlock)link.Content!;
            var labelOrigin = label.TranslatePoint(default, paragraph);
            Require(labelOrigin is { } position &&
                Math.Abs(position.Y + label.TextLayout.TextLines[0].Baseline - paragraph.TextLayout.TextLines[0].Baseline) < 1.1,
                "The inline transfer labels and surrounding text did not share a baseline.");
        }
        await VerifyRssTransferPicker(transferLinks[0]);
        var picture = PageBody.GetVisualDescendants().OfType<RssArticleImage>().Single();
        Require(Avalonia.Automation.AutomationProperties.GetName(picture) == "Release cover",
            "The article reader did not show only the safe image and its alternative text.");
        ReleaseHistoryButton(forward: false);
        Require(_page == Page.Rss, "Mouse Back did not return from the article to the RSS reader.");
        ReleaseHistoryButton(forward: true);
        Require(_page == Page.RssArticle && PageTitle.Text == "New build available",
            "Mouse Forward did not restore the RSS article.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "rss-article.png"));
        await VerifyRssImageViewport(preview);

        File.WriteAllText(path, RssFixture.Replace("New build available", "Refreshed article"));
        await RefreshRss();
        Require(_rssSnapshots[fileFeed.Id].Articles[0].Title == "Refreshed article", "A local feed did not refresh from its file.");
        File.Delete(path);
        var offline = await new RssReader(Program.DataDirectory).Refresh(fileFeed, CancellationToken.None);
        Require(offline.Snapshot?.Articles[0] is { Title: "Refreshed article", Blocks: { } savedBlocks } &&
            savedBlocks.SelectMany(block => block.Runs ?? []).Any(run => run.Transfer == RssFixtureMagnet) &&
            offline.Error != null, "An unreadable feed lost the persisted offline article or its transfer links.");
        var legacy = new RssSubscription { Id = Guid.NewGuid(), IsFile = true, Location = path };
        var legacyCache = Path.Combine(Program.DataDirectory, "rss", legacy.Id.ToString("N") + ".json");
        File.WriteAllText(legacyCache, """{"Title":"Old feed","Articles":[{"Title":"Cached article","Text":"Old plain text"}]}""");
        Require((await _rssReader.Refresh(legacy, CancellationToken.None)).Snapshot?.Articles.Single().Text == "Old plain text",
            "The reader lost the plain-text cache from a previous version.");
        _rssReader.RemoveCache(legacy.Id);

        using var client = new HttpClient(new AtomFixture());
        var reader = new RssReader(Program.DataDirectory, client);
        var remoteCache = Path.Combine(Program.DataDirectory, "rss", remote.Id.ToString("N") + ".json");
        File.WriteAllText(remoteCache, """
            {"Title":"Old Atom feed","Articles":[{"Title":"Cached article","Text":"Old rich text","Blocks":[]}],"ETag":"\"article-feed\""}
            """);
        var first = await reader.Refresh(remote, CancellationToken.None);
        var second = await reader.Refresh(remote, CancellationToken.None);
        var atomArticle = first.Snapshot?.Articles.Single();
        Require(atomArticle?.Link == "https://example.org/news/story" &&
            atomArticle.Blocks?.Single(block => block.Image != null).Image == "https://example.org/news/media/photo.png" &&
            second.Snapshot?.Articles.Single().Title == "Atom story" && second.Error == null,
            "Atom parsing or conditional refresh failed.");
        var hostile = Path.Combine(Program.DataDirectory, "forbidden.xml");
        File.WriteAllText(hostile, """<!DOCTYPE rss [<!ENTITY test "malformed">]><rss><channel><title>&test;</title></channel></rss>""");
        try
        {
            RssReader.Import(hostile);
            Require(false, "The feed parser accepted a DTD.");
        }
        catch (XmlException) { }

        var fixtureAddress = new Uri("http://127.0.0.1:1/articles/build");
        var fixtureArticle = _rssSnapshots[fileFeed.Id].Articles[0];
        foreach (var transfer in new[] { RssFixtureMagnet, "http://example.org/files/primary.torrent" })
        {
            Navigate(new ViewLocation(Page.RssArticle, Article: new ArticleView(fileFeed.Id, "Developer news",
                fixtureArticle with { Link = transfer })));
            UpdateLayout();
            var actions = PageBody.GetVisualDescendants().OfType<Button>().ToArray();
            Require(!actions.Any(button => Equals(button.Content, "Open original")),
                "A primary RSS transfer retained the browser action.");
            await VerifyRssTransferPicker(actions.Single(button => Equals(button.Content, "Open transfer")));
        }
        Navigate(new ViewLocation(Page.RssArticle, Article: new ArticleView(fileFeed.Id, "Developer news",
            fixtureArticle with { Link = fixtureAddress.AbsoluteUri })));
        UpdateLayout();
        PageBody.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Open original"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(_page == Page.Web && _webView == WebView.Browser && _webAddress?.Text == fixtureAddress.AbsoluteUri,
            "Open original did not send the RSS article to the launcher's Browser.");

        Navigate(Page.RssSettings);
        UpdateLayout();
        var savedInternalBrowser = RssBrowserPreference();
        Require(savedInternalBrowser.IsChecked == true &&
            _store.Load().Preferences.OpenRssLinksInInternalBrowser,
            "The internal browser preference was not restored in RSS settings.");
        savedInternalBrowser.IsChecked = false;
        Require(!_store.Load().Preferences.OpenRssLinksInInternalBrowser,
            "Turning off the RSS browser preference did not persist.");
        var savedInternalTransfers = RssTransferPreference();
        Require(savedInternalTransfers.IsChecked == true && _store.Load().Preferences.OpenRssTransfersInternally,
            "The internal transfer preference was not restored in RSS settings.");
        savedInternalTransfers.IsChecked = false;
        Require(!_store.Load().Preferences.OpenRssTransfersInternally,
            "Turning off the RSS transfer preference did not persist.");
        RemoveRssFeed(remote);
        RemoveRssFeed(fileFeed);
        Require(_store.Load().Preferences.RssFeeds.Count == 0, "Removing feeds did not persist.");
    }

    private ToggleSwitch RssBrowserPreference() => PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
        .Single(toggle => AutomationProperties.GetName(toggle) == "Open links in internal browser");

    private Button VerifyHomeRssHeader(string? emptyText)
    {
        var headerChildren = _homeRssHost!.Children[0].GetVisualDescendants().ToArray();
        var title = headerChildren.OfType<TextBlock>().Single(label => label.Text == "Recent RSS articles");
        var settings = headerChildren.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "RSS feed settings");
        var refresh = _homeRssRefresh!;
        Require(headerChildren.Contains(refresh) && new[] { settings, refresh }.All(button =>
                button.Content is SymbolIcon && button.Background is ISolidColorBrush { Color.A: 0 } &&
                button.BorderBrush is ISolidColorBrush { Color.A: 0 } && ToolTip.GetTip(button) is string),
            "The Home RSS header did not show transparent symbolic actions with tooltips.");
        var titleEnd = title.TranslatePoint(new Point(title.Bounds.Width, title.Bounds.Height / 2), this);
        var settingsStart = settings.TranslatePoint(new Point(0, settings.Bounds.Height / 2), this);
        var refreshStart = refresh.TranslatePoint(new Point(0, refresh.Bounds.Height / 2), this);
        Require(titleEnd is { } titlePosition && settingsStart is { } settingsPosition && refreshStart is { } refreshPosition &&
            titlePosition.X <= settingsPosition.X && settingsPosition.X < refreshPosition.X &&
            Math.Abs(titlePosition.Y - settingsPosition.Y) <= 1 && Math.Abs(settingsPosition.Y - refreshPosition.Y) <= 1,
            "The Home RSS header actions did not align alongside the title.");
        Require(_homeRssHost!.Children.OfType<TextBlock>().SingleOrDefault()?.Text == emptyText &&
            !_homeRssHost.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Open RSS")),
            "The Home RSS shelf did not show the expected empty state.");
        return settings;
    }

    private async Task VerifyNumberSpinnerInsets(NumericUpDown interval, string preview)
    {
        var reports = Path.GetDirectoryName(preview)!;
        var initialSize = new Size(Width, Height);
        var initialLocation = interval.ButtonSpinnerLocation;
        try
        {
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(85);
                foreach (var location in new[] { Location.Right, Location.Left })
                {
                    var hover = (IPseudoClasses)CheckInsets(location).Classes;
                    hover.Add(":pointerover");
                    try { CaptureControl(interval, Path.Combine(reports, $"rss-spinner-{location.ToString().ToLowerInvariant()}-{size.Width}.png")); }
                    finally { hover.Remove(":pointerover"); }
                }
            }

            interval.Height = 48;
            CheckInsets(Location.Right);
            CheckInsets(Location.Left);
        }
        finally
        {
            interval.ClearValue(HeightProperty);
            interval.ButtonSpinnerLocation = initialLocation;
            Width = initialSize.Width;
            Height = initialSize.Height;
            UpdateLayout();
        }

        RepeatButton CheckInsets(Location location)
        {
            interval.ButtonSpinnerLocation = location;
            UpdateLayout();
            var frame = interval.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NumberFrame");
            var steps = frame.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "StepButtons");
            var buttons = steps.Children.OfType<RepeatButton>().ToArray();
            var edgeButton = location == Location.Right ? buttons[^1] : buttons[0];
            var origin = edgeButton.TranslatePoint(default, frame) ?? throw new InvalidOperationException("The spinner buttons were not in the number field.");
            var side = location == Location.Right ? frame.Bounds.Width - origin.X - edgeButton.Bounds.Width : origin.X;
            var bottom = frame.Bounds.Height - origin.Y - edgeButton.Bounds.Height;
            Require(Math.Abs(side - origin.Y) < 1 && Math.Abs(side - bottom) < 1,
                $"The {location} spinner insets differed. Side {side}, top {origin.Y}, bottom {bottom}.");
            return edgeButton;
        }
    }

    private void VerifyFeedUrlMenu(TextBox field, string preview)
    {
        field.Focus();
        field.RaiseEvent(new ContextRequestedEventArgs());
        var menu = field.ContextMenu;
        Require(field.ContextFlyout == null && menu is FrostedContextMenu { IsOpen: true },
            "The feed URL field did not open the shared frosted menu.");
        var actions = menu!.Items.OfType<MenuItem>().ToArray();
        Require(actions.Select(item => item.Header).SequenceEqual(["Cut", "Copy", "Paste"]) &&
            actions.All(item => item.Icon is SymbolIcon) &&
            actions.Select(item => item.InputGesture).SequenceEqual([TextBox.CutGesture, TextBox.CopyGesture, TextBox.PasteGesture]) &&
            !actions[0].IsEnabled && !actions[1].IsEnabled && actions[2].IsEnabled,
            "The feed URL menu lost its editing icons, shortcuts or command states.");
        menu.Close();
        field.Text = "https://example.org/feed.xml";
        field.SelectAll();
        field.RaiseEvent(new ContextRequestedEventArgs());
        Require(actions.All(item => item.IsEnabled), "The feed URL menu did not enable Cut and Copy for selected text.");
        VerifyMenuTheme(menu, preview);

        var commands = new[] { TextBox.CuttingToClipboardEvent, TextBox.CopyingToClipboardEvent, TextBox.PastingFromClipboardEvent };
        foreach (var (action, command) in actions.Zip(commands))
        {
            var invoked = false;
            void Intercept(object? sender, RoutedEventArgs args) { invoked = true; args.Handled = true; }
            field.AddHandler(command, Intercept);
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            field.RemoveHandler(command, Intercept);
            Require(invoked, $"The feed URL menu did not invoke {action.Header}.");
        }
        menu.Close();
        field.Clear();
    }

    private static void VerifyRssMarkupLimits()
    {
        var markup = new XElement("description", String.Concat(Enumerable.Repeat("<strong>b</strong><i>i</i>", 5000)));
        var runs = RssMarkup.Read(markup, html: true, origin: null).SelectMany(block => block.Runs ?? []).Count();
        Require(runs is > 0 and <= 4096, "An article with excessive inline formatting created too many native text runs.");
        markup.Value = "<ul><li><p>One</p></li><li><p>Two</p></li></ul>";
        Require(RssMarkup.PlainText(RssMarkup.Read(markup, html: true, origin: null)) == "• One\n• Two",
            "Paragraphs inside list items separated list markers from their text.");
    }

    private ToggleSwitch RssTransferPreference() => PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
        .Single(toggle => AutomationProperties.GetName(toggle) == "Open transfers in internal download manager");

    private async Task VerifyRssTransferPicker(Button link)
    {
        var before = _webTransfers?.Items.Count ?? 0;
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => DialogHost.Children.OfType<ModalDialog>().Any(dialog =>
            AutomationProperties.GetName(dialog) == "Choose download folder"),
            "The RSS transfer did not use the existing destination picker.");
        DialogHost.DismissTop();
        await WaitFor(() => !_rssOpeningTransfer, "The RSS transfer picker did not dismiss.");
        Require(_page == Page.RssArticle && (_webTransfers?.Items.Count ?? 0) == before,
            "Cancelling an RSS transfer changed the view or queued a torrent.");
    }

    private const string RssFixtureMagnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

    private static void VerifyRssTransferMarkup()
    {
        var origin = new Uri("https://example.org/articles/story");
        var markup = new XElement("description", $"""
            <p>Download <a href="{RssFixtureMagnet}&amp;dn=Fixture"><strong>Magnet</strong> link</a>,
            <a href="../files/Game.TORRENT?key=7">torrent file</a>, <a href='magnet:?xt=invalid'>invalid</a>,
            <a href='file:///game.torrent'>local</a>, <a href='https://name:pass@example.org/game.torrent'>credentials</a>
            or <a href='https://example.org/story?name=game.torrent'>web page</a>.</p>
            """);
        var runs = RssMarkup.Read(markup, html: true, origin).SelectMany(block => block.Runs ?? []).ToArray();
        Require(runs.Single(run => run.Text == "Magnet") is { Style: RssTextStyle.Bold, Transfer: RssFixtureMagnet + "&dn=Fixture" } &&
            runs.Single(run => run.Text == "link").Transfer == RssFixtureMagnet + "&dn=Fixture" &&
            runs.Single(run => run.Text == "torrent file").Transfer == "https://example.org/files/Game.TORRENT?key=7" &&
            runs.Where(run => run.Transfer != null).All(run => run.Text is "Magnet" or " " or "link" or "torrent file"),
            "RSS HTML transfer links were stripped or an unsupported link became clickable.");

        var xhtml = XElement.Parse("""
            <div xmlns="http://www.w3.org/1999/xhtml" xml:base="../files/">
                <p><a xml:base="daily/" href="patch.torrent">daily patch</a></p>
            </div>
            """);
        var xhtmlRuns = RssMarkup.Read(xhtml, html: false, new Uri("https://example.org/files/"))
            .SelectMany(block => block.Runs ?? []);
        Require(xhtmlRuns.Single(run => run.Text == "daily patch").Transfer == "https://example.org/files/daily/patch.torrent",
            "Atom XHTML lost a relative torrent link.");
    }

    private static async Task VerifyRssTorrentDescriptors()
    {
        var handler = new RssTorrentFixture();
        using var client = new HttpClient(handler);
        var url = new Uri("http://example.org/redirect.torrent");
        var descriptor = await WebTransfers.DownloadTorrentDescriptorAsync(url, CancellationToken.None, client);
        Require(descriptor.Torrent.Name == "test" && descriptor.Bytes.Length > 0 && handler.Requests == 2,
            "The torrent descriptor did not follow a safe HTTP-to-HTTPS redirect or load fixture metadata.");

        await VerifyRejected(new Uri("https://example.org/unsafe.torrent"));
        Require(handler.Requests == 3, "A forbidden redirect target was contacted.");
        await VerifyRejected(new Uri("https://example.org/oversized.torrent"));
        await VerifyRejected(new Uri("https://example.org/invalid.torrent"));

        string? handedOff = null;
        try
        {
            await OpenRssTorrentFile(descriptor.Bytes, CancellationToken.None, path =>
            {
                handedOff = path;
                Require(Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                    Path.GetFileName(path).StartsWith("LGLauncher-RSS-", StringComparison.Ordinal) &&
                    Path.GetExtension(path) == ".torrent" && File.ReadAllBytes(path).SequenceEqual(descriptor.Bytes),
                    "The associated app did not receive an independent temporary .torrent file.");
            });
            Require(File.Exists(handedOff), "The external handoff removed the file before the associated app could read it.");
        }
        finally
        {
            if (handedOff != null) File.Delete(handedOff);
        }

        string? rejectedPath = null;
        try
        {
            await OpenRssTorrentFile(descriptor.Bytes, CancellationToken.None, path =>
            {
                rejectedPath = path;
                throw new System.ComponentModel.Win32Exception(1155);
            });
            Require(false, "A missing system torrent handler was reported as successful.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Require(rejectedPath != null && rejectedPath != handedOff && !File.Exists(rejectedPath),
                "An unsuccessful external handoff left a temporary file.");
        }

        async Task VerifyRejected(Uri uri)
        {
            try
            {
                await WebTransfers.DownloadTorrentDescriptorAsync(uri, CancellationToken.None, client);
                Require(false, "An unsafe, oversized or invalid descriptor response was accepted.");
            }
            catch (Exception error) when (error is IOException or ArgumentException) { }
        }
    }

    private sealed class RssTorrentFixture : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/redirect.torrent" => new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://example.org/fixture") }
                },
                "/unsafe.torrent" => new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("http://example.org/fixture") }
                },
                "/oversized.torrent" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("") { Headers = { ContentLength = 9 * 1024 * 1024 } }
                },
                "/invalid.torrent" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>Missing.</html>")
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "d4:infod6:lengthi1e4:name4:test12:piece lengthi16384e6:pieces20:01234567890123456789ee")
                }
            });
        }
    }

    private async Task VerifyRssImageViewport(string preview)
    {
        using var fixture = new RssImageFixture();
        using var client = new HttpClient(fixture);
        var image = new RssArticleImage(new Uri("https://example.org/cover"), "RSS test cover", load: true,
            CancellationToken.None, client);
        var separator = new Border { Height = 1600 };
        PageBody.Children.Add(separator);
        PageBody.Children.Add(image);
        UpdateLayout();
        await Task.Delay(60);
        Require(!fixture.Requested, "The reader downloaded an image well outside the viewport.");
        image.BringIntoView();
        await WaitFor(() => image.Child is Image { Source: Bitmap bitmap } &&
            bitmap.PixelSize.Width is > 0 and <= 1280 && bitmap.PixelSize.Height is > 0 and <= 1280,
            "The reader could not download, decode and present a visible raster image.");
        PageBody.Children.Remove(separator);
        var articleTitle = PageTitle.Text;
        SelectGame(_library.Games.First());
        Require(_page == Page.Game && !PageBody.Children.Contains(image) && image.Child is TextBlock,
            "Opening game details left a decoded RSS image attached to the hidden article.");
        ReleaseHistoryButton(forward: false);
        Require(_page == Page.RssArticle && PageTitle.Text == articleTitle,
            "Mouse Back did not restore the article after releasing its decoded image.");
        var originalWidth = Width;
        try
        {
            foreach (var width in new[] { MinWidth, 1230, 1600 })
            {
                Width = width;
                UpdateLayout();
                Capture(Path.Combine(Path.GetDirectoryName(preview)!, $"rss-article-return-{width}.png"));
            }
        }
        finally { Width = originalWidth; }
    }

    private sealed class RssImageFixture : HttpMessageHandler
    {
        private int _requests;
        internal bool Requested => Volatile.Read(ref _requests) > 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (request.RequestUri?.AbsolutePath == "/cover")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("/cover.jpg", UriKind.Relative) }
                });
            var raster = typeof(Launcher).Assembly.GetManifestResourceStream("LGLauncher.Welcome.jpg")!;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(raster) });
        }
    }

    private sealed class AtomFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.IfNoneMatch.Any(value => value.Tag == "\"article-feed\""))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var atom = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <feed xmlns="http://www.w3.org/2005/Atom" xml:base="/news/"><title>Atom News</title>
                    <entry><title>Atom story</title><link href="story"/><content type="xhtml">
                    <div xmlns="http://www.w3.org/1999/xhtml" xml:base="media/"><p>News summary</p>
                    <img src="photo.png" alt="A photo" /></div></content></entry></feed>
                    """),
                RequestMessage = request
            };
            atom.Headers.ETag = new EntityTagHeaderValue("\"article-feed\"");
            return Task.FromResult(atom);
        }
    }
}
