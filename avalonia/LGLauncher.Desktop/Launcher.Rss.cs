using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Xml;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly FilePickerFileType RssFiles = new("RSS, Atom or OPML files")
    {
        Patterns = ["*.rss", "*.atom", "*.xml", "*.rdf", "*.opml"]
    };

    private readonly RssReader _rssReader;
    private readonly DispatcherTimer _rssTimer = new();
    private readonly Dictionary<Guid, RssSnapshot> _rssSnapshots = [];
    private readonly Dictionary<Guid, string> _rssErrors = [];
    private readonly Dictionary<Guid, RssFeedRow> _rssFeedControls = [];
    private readonly HashSet<Guid> _rssCacheToRemove = [];
    private bool _rssRefreshing;
    private bool _rssRefreshPending;
    private DateTimeOffset? _rssLastChecked;
    private StackPanel? _rssFeedRows;
    private StackPanel? _homeRssHost;
    private Button? _homeRssRefresh;
    private TextBlock? _homeRssEmpty;
    private ArticleView[] _homeRssArticles = [];
    private ArticleView? _rssOpenArticle;

    private List<RssSubscription> RssSubscriptions => _library.Preferences.RssFeeds ??= [];
    private int RssRefreshMinutes => _library.Preferences.RssRefreshMinutes is > 0 and <= RssReader.MaximumMinutes and var minutes
        ? minutes : RssReader.DefaultMinutes;

    private void RestartRssTimer()
    {
        _rssTimer.Stop();
        _rssTimer.Interval = TimeSpan.FromMinutes(RssRefreshMinutes);
        if (_ready && !_closed && !_verification && RssSubscriptions.Any(feed => feed.Enabled)) _rssTimer.Start();
    }

    private void RequestRssRefresh()
    {
        _rssRefreshPending = RssSubscriptions.Any(feed => feed.Enabled);
        if (CanFetch && !_updateHandoff && !_rssRefreshing && _rssRefreshPending) _ = RefreshRss();
        else if (_page == Page.Rss && _playing.Count > 0) Render();
    }

    private async Task RefreshRss()
    {
        var sources = RssSubscriptions.Where(feed => feed.Enabled).ToArray();
        if (_rssRefreshing || sources.Length == 0) return;
        var token = _background.Token;
        _rssRefreshPending = false;
        _rssRefreshing = true;
        RefreshRssView();
        try
        {
            var results = new (RssSnapshot? Snapshot, string? Error)[sources.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, sources.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
                async (index, cancel) => results[index] = await _rssReader.Refresh(sources[index], cancel));

            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                if (!source.Enabled || !RssSubscriptions.Contains(source)) continue;
                var (snapshot, error) = results[index];
                if (snapshot != null) _rssSnapshots[source.Id] = snapshot;
                if (error == null) _rssErrors.Remove(source.Id);
                else _rssErrors[source.Id] = error;
            }
            _rssLastChecked = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _rssRefreshPending = !_closed; }
        finally
        {
            _rssRefreshing = false;
            foreach (var id in _rssCacheToRemove) _rssReader.RemoveCache(id);
            _rssCacheToRemove.Clear();
            if (!_closed)
            {
                RefreshRssView();
                if (_rssRefreshPending) RequestRssRefresh();
            }
        }
    }

    private void RefreshRssView()
    {
        if (_page == Page.Rss) Render();
        if (_page == Page.RssSettings) RenderRssSubscriptions();
        RefreshHomeRss();
    }

    private Control BuildRssSettings()
    {
        var url = TextField(null, "Feed URL");
        url.UseTextEditingMenu();
        url.PlaceholderText = "https://example.com/feed.xml";
        url.MaxLength = 2048;
        url.VerticalContentAlignment = VerticalAlignment.Center;
        url.Padding = new Thickness(10, 0);
        var message = Label("", muted: true);
        void AddUrl()
        {
            if (!RssReader.TryWebAddress(url.Text, out var address))
            {
                message.Text = "Enter an HTTP or HTTPS feed URL.";
                return;
            }
            message.Text = AddRssSources([new RssSource(address.AbsoluteUri)]);
            url.Clear();
        }
        url.KeyDown += (_, args) => { if (args.Key == Key.Enter) { AddUrl(); args.Handled = true; } };
        var add = ActionButton("Add feed", AddUrl, "primary");
        var import = IconButton("Import file", "LG.Icon.Folder", async () =>
        {
            try
            {
                var path = await PickFile(this, "Import a feed or an OPML subscription list", RssFiles, null);
                if (path == null) return;
                message.Text = "Reading file…";
                var sources = await Task.Run(() => RssReader.Import(path));
                if (!_closed) message.Text = AddRssSources(sources);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or ArgumentException)
            {
                message.Text = "Could not import the file. Choose a valid RSS, Atom or OPML file.";
            }
        });
        var interval = new NumericUpDown
        {
            Value = RssRefreshMinutes, Minimum = 1, Maximum = RssReader.MaximumMinutes,
            Increment = 1, FormatString = "0", Width = 118, Classes = { "lg-number" }
        };
        AutomationProperties.SetName(interval, "Refresh interval in minutes");
        var intervalNotice = Label("", muted: true);
        intervalNotice.IsVisible = false;
        var saveInterval = ActionButton("Save interval", () =>
        {
            intervalNotice.IsVisible = true;
            if (interval.Value is not { } value || value != Decimal.Truncate(value) ||
                value < 1 || value > RssReader.MaximumMinutes)
            {
                intervalNotice.Text = "Enter a whole number from 1 to 1,440 minutes.";
                return;
            }
            intervalNotice.Text = SetRssInterval((int)value) ? "Refresh interval saved." : "Could not save the refresh interval.";
        });
        var intervalLabel = Label("Refresh interval (minutes)");
        intervalLabel.VerticalAlignment = VerticalAlignment.Center;
        intervalLabel.FontWeight = FontWeight.SemiBold;
        intervalLabel.FontSize = 17;
        var intervalActions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { interval, saveInterval }
        };
        Grid.SetColumn(intervalActions, 1);
        var intervalRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 12,
            Children = { intervalLabel, intervalActions }
        };
        _rssFeedRows = new StackPanel { Spacing = 10 };
        _rssFeedControls.Clear();
        RenderRssSubscriptions();
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("Add a feed", new StackPanel
                {
                    Spacing = 8,
                    Children = { url, Actions(add, import), Label("Import an OPML subscription list or a local RSS or Atom file.", muted: true), message }
                }),
                new Border
                {
                    Classes = { "lg-panel" }, Padding = new Thickness(18),
                    Child = new StackPanel { Spacing = 8, Children = { intervalRow, intervalNotice } }
                },
                Section("Feeds", _rssFeedRows)
            }
        };
    }

    private void RenderRssSubscriptions()
    {
        if (_rssFeedRows == null) return;
        var active = RssSubscriptions.Select(feed => feed.Id).ToHashSet();
        foreach (var id in _rssFeedControls.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _rssFeedRows.Children.Remove(_rssFeedControls[id].Container);
            _rssFeedControls.Remove(id);
        }
        if (_rssFeedControls.Count == 0) _rssFeedRows.Children.Clear();
        if (active.Count == 0)
        {
            _rssFeedRows.Children.Add(Label("No feeds added.", muted: true));
            return;
        }
        foreach (var feed in RssSubscriptions)
        {
            var name = RssName(feed);
            if (!_rssFeedControls.TryGetValue(feed.Id, out var row))
            {
                row = BuildRssFeedRow(feed);
                _rssFeedControls.Add(feed.Id, row);
                _rssFeedRows.Children.Add(row.Container);
            }
            row.Name.Text = name;
            AutomationProperties.SetName(row.Toggle, "Enable " + name);
            AutomationProperties.SetName(row.Remove, "Remove " + name);
            row.Error.Text = feed.Enabled ? _rssErrors.GetValueOrDefault(feed.Id) : null;
            row.Error.IsVisible = row.Error.Text != null;
        }
    }

    private sealed record RssFeedRow(Grid Container, TextBlock Name, TextBlock Error, ToggleSwitch Toggle, Button Remove);

    private RssFeedRow BuildRssFeedRow(RssSubscription feed)
    {
        var caption = feed.IsFile ? "Local file · " + feed.Location : feed.Location;
        var name = Label("");
        var error = Label("", muted: true);
        var info = new StackPanel { Spacing = 3, Children = { name, Label(caption, muted: true), error } };
        var toggle = new ToggleSwitch { IsChecked = feed.Enabled, OnContent = "", OffContent = "" };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!SetRssEnabled(feed, toggle.IsChecked == true)) toggle.IsChecked = feed.Enabled;
        };
        var remove = IconButton("Remove", "LG.Icon.Delete", () => RemoveRssFeed(feed), "quiet");
        var tools = Actions(toggle, remove);
        tools.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 10, Children = { info, tools } };
        Grid.SetColumn(tools, 1);
        return new RssFeedRow(row, name, error, toggle, remove);
    }

    private string RssName(RssSubscription feed)
    {
        if (_rssSnapshots.TryGetValue(feed.Id, out var snapshot) && !String.IsNullOrWhiteSpace(snapshot.Title)) return snapshot.Title;
        if (!String.IsNullOrWhiteSpace(feed.Name)) return feed.Name;
        if (feed.IsFile) return Path.GetFileName(feed.Location);
        return RssReader.TryWebAddress(feed.Location, out var uri) ? uri.Host : "Feed";
    }

    private string AddRssSources(RssSource[] sources)
    {
        if (sources.Length == 0) return "No supported feed URLs were found.";
        var added = new List<RssSubscription>();
        foreach (var source in sources)
        {
            var location = source.IsFile ? Path.GetFullPath(source.Location) : new UriBuilder(source.Location) { Fragment = "" }.Uri.AbsoluteUri;
            var comparer = source.IsFile && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (RssSubscriptions.Any(feed => feed.IsFile == source.IsFile && String.Equals(feed.Location, location, comparer))) continue;
            var feed = new RssSubscription { Id = Guid.NewGuid(), Location = location, Name = source.Name, IsFile = source.IsFile, Enabled = true };
            RssSubscriptions.Add(feed);
            added.Add(feed);
        }
        if (added.Count == 0) return "These feeds are already added.";
        if (!SaveRss(() => RssSubscriptions.RemoveAll(added.Contains))) return "Could not save the feeds.";
        RenderRssSubscriptions();
        RestartRssTimer();
        RequestRssRefresh();
        return added.Count == 1 ? "Added 1 feed." : $"Added {added.Count} feeds.";
    }

    private bool SetRssInterval(int minutes)
    {
        if (minutes is < 1 or > RssReader.MaximumMinutes) return false;
        var previous = _library.Preferences.RssRefreshMinutes;
        _library.Preferences.RssRefreshMinutes = minutes;
        if (!SaveRss(() => _library.Preferences.RssRefreshMinutes = previous)) return false;
        RestartRssTimer();
        return true;
    }

    private bool SetRssEnabled(RssSubscription feed, bool enabled)
    {
        if (feed.Enabled == enabled) return true;
        feed.Enabled = enabled;
        if (!SaveRss(() => feed.Enabled = !enabled)) return false;
        RestartRssTimer();
        RenderRssSubscriptions();
        RefreshHomeRss();
        if (enabled) RequestRssRefresh();
        return true;
    }

    private void RemoveRssFeed(RssSubscription feed)
    {
        var index = RssSubscriptions.IndexOf(feed);
        if (index < 0) return;
        RssSubscriptions.RemoveAt(index);
        if (!SaveRss(() => RssSubscriptions.Insert(index, feed))) return;
        _rssSnapshots.Remove(feed.Id);
        _rssErrors.Remove(feed.Id);
        if (_rssRefreshing) _rssCacheToRemove.Add(feed.Id);
        else _rssReader.RemoveCache(feed.Id);
        RestartRssTimer();
        RenderRssSubscriptions();
        RefreshHomeRss();
    }

    private bool SaveRss(Action rollback)
    {
        var previousFailure = _saveFailed;
        if (Save()) return true;
        rollback();
        _saveFailed = previousFailure;
        return false;
    }

    private void RenderRss()
    {
        PageTitle.Text = "RSS";
        var feeds = RssSubscriptions.Where(feed => feed.Enabled).ToArray();
        var refresh = IconButton("Refresh", "LG.Icon.Refresh", RequestRssRefresh);
        refresh.IsEnabled = feeds.Length > 0 && !_rssRefreshing;
        PageBody.Children.Add(Actions(refresh, ActionButton("Manage feeds", () => Navigate(Page.RssSettings), "quiet")));
        var message = _rssRefreshing ? "Refreshing feeds…" : _rssRefreshPending && _playing.Count > 0
            ? "Refresh will resume after your games close." : _rssLastChecked is { } time
                ? $"Last checked {time.ToString("g", CultureInfo.CurrentCulture)}" : "";
        var failed = feeds.Count(feed => _rssErrors.ContainsKey(feed.Id));
        if (failed > 0)
        {
            var failure = failed == 1 ? "1 feed needs attention in RSS settings." : $"{failed} feeds need attention in RSS settings.";
            message = message.Length == 0 ? failure : $"{message} · {failure}";
        }
        if (message.Length != 0) PageBody.Children.Add(Label(message, muted: true));
        var articles = RecentRssArticles().ToArray();
        if (articles.Length == 0)
        {
            if (!_rssRefreshing) PageBody.Children.Add(Section("No articles yet", Label(feeds.Length == 0
                ? "Add or enable a feed in RSS settings." : "Use Refresh to check your enabled feeds.", muted: true)));
            return;
        }

        const int pageSize = 20;
        _offset = Math.Min(_offset, (articles.Length - 1) / pageSize * pageSize);
        foreach (var item in articles.Skip(_offset).Take(pageSize))
        {
            var row = ActionButton(item.Article.Title, () => Navigate(new ViewLocation(Page.RssArticle, Article: item)));
            row.Classes.Add("settings-row");
            row.Content = new StackPanel
            {
                Spacing = 3,
                Children = { Label(item.Article.Title), Label(RssArticleCaption(item.FeedTitle, item.Article), muted: true) }
            };
            AutomationProperties.SetName(row, item.Article.Title);
            PageBody.Children.Add(row);
        }
        AddPagination(articles.Length, pageSize);
    }

    private void RenderRssArticle()
    {
        if (_rssOpenArticle is not { } entry) return;
        PageTitle.Text = entry.Article.Title;
        var back = IconButton("RSS", "LG.Icon.Back", () => Navigate(Page.Rss), "quiet");
        var actions = Actions(back);
        if (RssReader.TryWebAddress(entry.Article.Link, out var original))
        {
            actions.Children.Add(ActionButton("Open original", () =>
            {
                try { Process.Start(new ProcessStartInfo(original.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                { Status("Could not open the article in your browser."); }
            }));
        }
        PageBody.Children.Add(actions);
        PageBody.Children.Add(Label(RssArticleCaption(entry.FeedTitle, entry.Article), muted: true));
        var body = new StackPanel { Spacing = 12, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var block in entry.Article.Blocks ?? [])
        {
            if (RssReader.TryWebAddress(block.Image, out var image))
                body.Children.Add(new RssArticleImage(image, block.Alt, CanFetch, _background.Token));
            else if (block.Runs is { Length: > 0 })
                body.Children.Add(BuildRssParagraph(block));
        }
        if (body.Children.Count == 0)
        {
            if (!String.IsNullOrWhiteSpace(entry.Article.Text))
            {
                var plainText = ArticleText();
                plainText.Text = entry.Article.Text;
                body.Children.Add(plainText);
            }
            else body.Children.Add(Label("This feed did not include article text.", muted: true));
        }
        PageBody.Children.Add(body);
    }

    private static readonly FontFamily RssCodeFont = new("Consolas");

    private SelectableTextBlock ArticleText()
    {
        var difference = FontSize - _defaultFontSize;
        return new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22 + difference, FontSize = 15 + difference };
    }

    private Control BuildRssParagraph(RssBlock block)
    {
        var paragraph = ArticleText();
        if (block.Style == RssBlockStyle.Heading)
        {
            paragraph.FontSize += 5;
            paragraph.LineHeight += 6;
            paragraph.FontWeight = FontWeight.SemiBold;
        }
        if (block.Style == RssBlockStyle.Code) paragraph.FontFamily = RssCodeFont;
        foreach (var part in block.Runs!)
        {
            var run = new Run(part.Text);
            if ((part.Style & RssTextStyle.Bold) != 0) run.FontWeight = FontWeight.Bold;
            if ((part.Style & RssTextStyle.Italic) != 0) run.FontStyle = FontStyle.Italic;
            if ((part.Style & RssTextStyle.Code) != 0) run.FontFamily = RssCodeFont;
            var decorations = part.Style & (RssTextStyle.Underline | RssTextStyle.Strike);
            if (decorations != RssTextStyle.Normal)
            {
                run.TextDecorations = new TextDecorationCollection();
                if ((decorations & RssTextStyle.Underline) != 0) run.TextDecorations.Add(TextDecorations.Underline[0]);
                if ((decorations & RssTextStyle.Strike) != 0) run.TextDecorations.Add(TextDecorations.Strikethrough[0]);
            }
            paragraph.Inlines!.Add(run);
        }
        return block.Style == RssBlockStyle.Quote
            ? new Border
            {
                Child = paragraph, BorderBrush = this.FindResource("LG.Accent") as IBrush,
                BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2)
            }
            : paragraph;
    }

    private IEnumerable<ArticleView> RecentRssArticles() => RssSubscriptions
        .Where(feed => feed.Enabled && _rssSnapshots.ContainsKey(feed.Id))
        .SelectMany(feed =>
        {
            var name = RssName(feed);
            return _rssSnapshots[feed.Id].Articles.Select(article => new ArticleView(feed.Id, name, article));
        })
        .OrderByDescending(item => item.Article.Published);

    private void RenderHomeRss()
    {
        _homeRssHost = null;
        _homeRssRefresh = null;
        _homeRssEmpty = null;
        _homeRssArticles = [];
        if (!_library.Preferences.ShowRecentRss) return;
        _homeRssHost = new StackPanel { Spacing = 12 };
        PageBody.Children.Add(_homeRssHost);
        RefreshHomeRss();
    }

    private void RefreshHomeRss()
    {
        if (_page != Page.Home || _homeRssHost == null || !PageBody.Children.Contains(_homeRssHost)) return;
        const string title = "Recent RSS articles";
        var emptyText = RssSubscriptions.Count == 0 ? "No feeds found" : "No articles found";
        var canRefresh = !_rssRefreshing && RssSubscriptions.Any(feed => feed.Enabled);
        if (_homeRssRefresh != null) _homeRssRefresh.IsEnabled = canRefresh;
        if (_homeRssEmpty != null) _homeRssEmpty.Text = emptyText;
        var latest = RecentRssArticles().Take(6).ToArray();
        static (Guid, string, string, DateTimeOffset?) CardText(ArticleView entry) =>
            (entry.FeedId, entry.FeedTitle, entry.Article.Title, entry.Article.Published);
        var sameCards = _homeRssHost.Children.Count > 0 &&
            _homeRssArticles.Select(CardText).SequenceEqual(latest.Select(CardText));
        _homeRssArticles = latest;
        if (sameCards) return;

        _homeRssHost.Children.Clear();
        _homeRssEmpty = null;
        var label = Heading(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        var settings = HeaderButton("RSS feed settings", "LG.Icon.Settings", () => Navigate(Page.RssSettings));
        _homeRssRefresh = HeaderButton("Refresh RSS feeds", "LG.Icon.Refresh", RequestRssRefresh);
        _homeRssRefresh.IsEnabled = canRefresh;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { settings, _homeRssRefresh } };
        var heading = new WrapPanel { ItemSpacing = 8, Children = { label, buttons } };
        if (latest.Length == 0)
        {
            _homeRssEmpty = Label(emptyText, muted: true);
            _homeRssHost.Children.Add(heading);
            _homeRssHost.Children.Add(_homeRssEmpty);
            return;
        }
        AddHomeShelf(title, "HomeRssRow", latest.Select((entry, index) => BuildRssCard(entry, index)),
            cardWidth: 240, host: _homeRssHost, scrollbar: ScrollBarVisibility.Hidden, headerContent: heading);

        Button HeaderButton(string name, string icon, Action action)
        {
            var button = ActionButton(name, action, "quiet");
            button.Content = Symbol(icon, 17);
            button.MinHeight = 32;
            button.Width = 32;
            button.Padding = new Thickness(0);
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
            return button;
        }
    }

    private Button BuildRssCard(ArticleView entry, int index)
    {
        var title = Label(entry.Article.Title);
        title.FontWeight = FontWeight.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxLines = 2;
        var caption = Label(RssArticleCaption(entry.FeedTitle, entry.Article), muted: true);
        caption.TextWrapping = TextWrapping.NoWrap;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var card = ActionButton(entry.Article.Title,
            () => Navigate(new ViewLocation(Page.RssArticle, Article: _homeRssArticles[index])));
        card.Height = 85;
        card.Padding = new Thickness(14, 10);
        card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        card.Content = new StackPanel { Spacing = 4, Children = { title, caption } };
        AutomationProperties.SetName(card, entry.Article.Title);
        ToolTip.SetTip(card, entry.Article.Title);
        return card;
    }

    private static string RssArticleCaption(string feed, RssArticle article) => article.Published is { } date
        ? $"{feed} · {date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}" : feed;
}
