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
        <template><template><img src="hidden-inner.png"></template><script>const label = "</template>";</script>
        <img src="hidden-outer.png"></template>
        <img src="cover.png" alt="Release cover"><img src="file:///blocked.png">
        <img src="data:image/png;base64,blocked">]]></description></item>
        <item><title>Older update</title><link>javascript:alert(1)</link></item>
        </channel></rss>
        """;

    private async Task VerifyRss(string preview)
    {
        VerifyRssMarkupLimits();
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
        Require(interval.Value == 31, "The themed number field did not increment.");
        steps.Single(button => button.Name == "PART_DecreaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(interval.Value == 30, "The themed number field did not decrement.");
        interval.Value = 12;
        PageBody.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Save interval")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(_store.Load().Preferences.RssRefreshMinutes == 12 && _rssTimer.Interval == TimeSpan.FromMinutes(12),
            "Changing the interval did not save and update the timer.");

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
        var picture = PageBody.GetVisualDescendants().OfType<RssArticleImage>().Single();
        Require(Avalonia.Automation.AutomationProperties.GetName(picture) == "Release cover",
            "The article reader did not show only the safe image and its alternative text.");
        ReleaseHistoryButton(forward: false);
        Require(_page == Page.Rss, "Mouse Back did not return from the article to the RSS reader.");
        ReleaseHistoryButton(forward: true);
        Require(_page == Page.RssArticle && PageTitle.Text == "New build available",
            "Mouse Forward did not restore the RSS article.");
        Capture(Path.Combine(Path.GetDirectoryName(preview)!, "rss-article.png"));
        await VerifyRssImageViewport();

        File.WriteAllText(path, RssFixture.Replace("New build available", "Refreshed article"));
        await RefreshRss();
        Require(_rssSnapshots[fileFeed.Id].Articles[0].Title == "Refreshed article", "A local feed did not refresh from its file.");
        File.Delete(path);
        var offline = await new RssReader(Program.DataDirectory).Refresh(fileFeed, CancellationToken.None);
        Require(offline.Snapshot?.Articles[0].Title == "Refreshed article" && offline.Error != null,
            "An unreadable feed lost the persisted offline copy.");
        var legacy = new RssSubscription { Id = Guid.NewGuid(), IsFile = true, Location = path };
        var legacyCache = Path.Combine(Program.DataDirectory, "rss", legacy.Id.ToString("N") + ".json");
        File.WriteAllText(legacyCache, """{"Title":"Old feed","Articles":[{"Title":"Cached article","Text":"Old plain text"}]}""");
        Require((await _rssReader.Refresh(legacy, CancellationToken.None)).Snapshot?.Articles.Single().Text == "Old plain text",
            "The reader lost the plain-text cache from a previous version.");
        _rssReader.RemoveCache(legacy.Id);

        using var client = new HttpClient(new AtomFixture());
        var reader = new RssReader(Program.DataDirectory, client);
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

        Navigate(Page.RssSettings);
        RemoveRssFeed(remote);
        RemoveRssFeed(fileFeed);
        Require(_store.Load().Preferences.RssFeeds.Count == 0, "Removing feeds did not persist.");
    }

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

    private async Task VerifyRssImageViewport()
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
        PageBody.Children.Remove(image);
        PageBody.Children.Remove(separator);
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
