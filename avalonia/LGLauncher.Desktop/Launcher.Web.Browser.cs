using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using LGLauncher.Components;
using Microsoft.Web.WebView2.Core;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private const int MaximumWebTabs = 12;
    private sealed class BrowserTab(Button select, Grid header)
    {
        public Button Select { get; } = select;
        public Grid Header { get; } = header;
        public NativeWebView? Browser { get; set; }
        public CoreWebView2? Core { get; set; }
        public Action? Unsubscribe { get; set; }
        public Uri? PendingAddress { get; set; }
        public string Status { get; set; } = "";
        public int ActiveDownloads { get; set; }
        public bool Closed { get; set; }
    }

    private readonly List<BrowserTab> _webTabs = [];
    private readonly HashSet<BrowserTab> _closedWebTabs = [];
    private List<WebBookmark> _webBookmarks = [];
    private BrowserTab? _activeWebTab;
    private Grid? _webNativeHost;
    private UniformGrid? _webTabStrip;
    private ScrollViewer? _webTabScroll;
    private StackPanel? _webBookmarkRows;
    private ScrollViewer? _webBookmarkPanel;
    private TextBox? _webAddress;
    private string _shownWebAddress = "";
    private TextBlock? _webStatus;
    private Button? _webBack;
    private Button? _webForward;
    private Button? _webReload;
    private Button? _webSaveBookmark;
    private Button? _webShowBookmarks;
    private bool _showWebBookmarks;
    private bool _confirmingWebMagnet;

    private Control BuildWebBrowser()
    {
        _webAddress = TextField(null, "Web address");
        _webAddress.Name = "WebAddress";
        _webAddress.PlaceholderText = "Enter a URL or Magnet link";
        _webAddress.CornerRadius = default;
        _webAddress.BorderThickness = new Thickness(1, 0);
        _webAddress.VerticalAlignment = VerticalAlignment.Stretch;
        _webAddress.VerticalContentAlignment = VerticalAlignment.Center;
        _webAddress.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            SubmitWebAddress();
        };

        _webBack = WebIconButton("Back", "LG.Icon.ChevronLeft", () => _activeWebTab?.Browser?.GoBack());
        _webForward = WebIconButton("Forward", "LG.Icon.Chevron", () => _activeWebTab?.Browser?.GoForward());
        _webReload = WebIconButton("Reload", "LG.Icon.Refresh", () => _activeWebTab?.Browser?.Refresh());
        _webShowBookmarks = WebIconButton("Bookmarks", "LG.Icon.Library", ToggleWebBookmarks, "bookmark");
        _webSaveBookmark = ActionButton("Add current page", AddWebBookmark);
        var go = WebIconButton("Go to address", "LG.Icon.Chevron", SubmitWebAddress, "arrow-right");
        var newTab = WebIconButton("New tab", "LG.Icon.Plus", () => AddWebTab());
        newTab.Name = "WebNewTab";
        var toolbar = new Grid
        {
            Name = "WebAddressBar",
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,Auto,*,Auto,Auto,Auto"),
            ColumnSpacing = 3,
            Margin = new Thickness(5, 0),
        };
        Control[] toolbarControls = [_webBack, _webForward, _webReload, _webAddress, go, _webShowBookmarks, newTab];
        for (var index = 0; index < toolbarControls.Length; index++)
        {
            var child = toolbarControls[index];
            Grid.SetColumn(child, index);
            toolbar.Children.Add(child);
        }

        _webTabStrip = new UniformGrid { Name = "WebTabStrip", Rows = 1 };
        _webTabScroll = new ScrollViewer
        {
            Name = "WebTabScroll",
            Content = _webTabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _webTabScroll.ScrollChanged += (_, _) => SizeWebTabs();
        var tabs = WebChromePanel(_webTabScroll, "LG.Surface", new Thickness(0, 1, 0, 0));
        var chrome = WebChromePanel(new StackPanel { Children = { toolbar, tabs } }, "LG.Sidebar", new Thickness(0, 0, 0, 1));

        _webStatus = Label("", muted: true);
        _webStatus.Name = "WebBrowserStatus";
        _webStatus.Margin = new Thickness(10, 4);
        _webStatus.IsVisible = false;
        AutomationProperties.SetLiveSetting(_webStatus, AutomationLiveSetting.Polite);
        _webNativeHost = new Grid { Name = "WebNativeHost", ClipToBounds = true };
        _webBookmarkRows = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
        _webBookmarkPanel = new ScrollViewer
        {
            Name = "WebBookmarkList",
            Content = _webBookmarkRows,
            IsVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var viewport = WebChromePanel(new Grid { Children = { _webNativeHost, _webBookmarkPanel } }, "LG.Canvas", default);
        var panel = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            Children = { chrome, _webStatus, viewport },
        };
        Grid.SetRow(_webStatus, 1);
        Grid.SetRow(viewport, 2);
        AddWebTab(focusAddress: false);
        try
        {
            _webBookmarks = WebBookmarks.Read(WebDataDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            SetWebBrowserStatus("The saved bookmarks could not be read.");
        }
        UpdateWebToolbar();
        return panel;
    }

    private Button WebIconButton(string name, string icon, Action action, string? lucide = null)
    {
        var button = ActionButton(name, action, "quiet");
        var symbol = Symbol(icon, 16);
        if (lucide != null) symbol.Data = LucideIconCatalog.Find(lucide)?.Geometry ?? symbol.Data;
        button.Content = symbol;
        button.MinWidth = 32;
        button.MinHeight = 32;
        button.Padding = new Thickness(6, 4);
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }

    private BrowserTab? AddWebTab(Uri? address = null, bool focusAddress = true)
    {
        if (_webTabStrip == null) return null;
        if (_webTabs.Count >= MaximumWebTabs)
        {
            SetWebBrowserStatus($"Up to {MaximumWebTabs} browser tabs can be open at once.");
            return null;
        }

        var title = Label("New tab");
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var select = new Button
        {
            Content = title,
            Classes = { "lg", "quiet" },
            MinHeight = 32,
            Padding = new Thickness(8, 0, 32, 0),
            CornerRadius = default,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var close = WebIconButton("Close tab", "LG.Icon.Close", () => { });
        close.MinWidth = 28;
        close.Padding = new Thickness(5, 4);
        close.CornerRadius = default;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Stretch;
        var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
        divider.Bind(Border.BackgroundProperty, this.GetResourceObservable("LG.Line"));
        var header = new Grid { Children = { select, close, divider } };
        var tab = new BrowserTab(select, header);
        Click(select, () => SelectWebTab(tab));
        Click(close, () => CloseWebTab(tab));
        _webTabs.Add(tab);
        _webTabStrip.Children.Add(header);
        SizeWebTabs();
        SelectWebTab(tab);
        if (address != null) NavigateWebAddress(tab, address);
        else if (focusAddress) FocusWebAddress();
        return tab;
    }

    private void SizeWebTabs()
    {
        if (_webTabStrip == null || _webTabScroll == null) return;

        const double minimumTabWidth = 116;
        _webTabStrip.Width = Math.Max(_webTabScroll.Viewport.Width, _webTabs.Count * minimumTabWidth);
    }

    private void SelectWebTab(BrowserTab tab)
    {
        if (tab.Closed || _webAddress == null) return;

        _activeWebTab = tab;
        _showWebBookmarks = false;
        foreach (var candidate in _webTabs)
        {
            candidate.Select.Classes.Set("selected", candidate == tab);
        }
        SetWebAddressText(tab.PendingAddress?.AbsoluteUri ?? PageAddress(tab) ?? "");
        UpdateWebBrowserVisibility();
        UpdateWebToolbar();
        Dispatcher.UIThread.Post(() => tab.Header.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void CloseWebTab(BrowserTab tab)
    {
        var index = _webTabs.IndexOf(tab);
        if (index < 0) return;

        _webTabs.RemoveAt(index);
        _webTabStrip?.Children.Remove(tab.Header);
        SizeWebTabs();
        tab.Closed = true;
        if (tab.ActiveDownloads > 0) _closedWebTabs.Add(tab);
        else RemoveWebTabBrowser(tab);

        if (_webTabs.Count == 0) AddWebTab();
        else if (_activeWebTab == tab) SelectWebTab(_webTabs[Math.Min(index, _webTabs.Count - 1)]);
        else UpdateWebBrowserVisibility();
    }

    private void RemoveWebTabBrowser(BrowserTab tab)
    {
        tab.Unsubscribe?.Invoke();
        tab.Unsubscribe = null;
        tab.Core = null;
        if (tab.Browser != null) _webNativeHost?.Children.Remove(tab.Browser);
        tab.Browser = null;
        _closedWebTabs.Remove(tab);
    }

    private void DisposeWebBrowser()
    {
        foreach (var tab in _webTabs.Concat(_closedWebTabs).ToArray())
        {
            RemoveWebTabBrowser(tab);
        }
    }

    private void FocusWebAddress()
    {
        _webAddress?.Focus();
        _webAddress?.SelectAll();
    }

    private void SubmitWebAddress()
    {
        if (_activeWebTab == null) return;
        if (WebAddress.Parse(_webAddress?.Text) is not { } address)
        {
            SetWebBrowserStatus("Enter a valid HTTP or HTTPS address, or a Magnet link.");
            return;
        }
        if (address.Scheme == "magnet")
        {
            _ = ConfirmWebMagnet(address.AbsoluteUri);
            return;
        }
        NavigateWebAddress(_activeWebTab, address);
    }

    private void NavigateWebAddress(BrowserTab tab, Uri address)
    {
        _showWebBookmarks = false;
        tab.PendingAddress = address;
        if (_activeWebTab == tab) SetWebAddressText(address.AbsoluteUri);
        if (!OperatingSystem.IsWindows())
        {
            SetWebTabStatus(tab, "The built-in browser requires Windows and the Microsoft Edge WebView2 Runtime.");
            return;
        }

        SetWebTabStatus(tab, "Loading…");
        if (tab.Core is { } core)
        {
            tab.PendingAddress = null;
            core.Navigate(address.AbsoluteUri);
        }
        else if (tab.Browser == null)
        {
            CreateWebTabBrowser(tab);
        }
        UpdateWebBrowserVisibility();
    }

    private void CreateWebTabBrowser(BrowserTab tab)
    {
        if (_webNativeHost == null) return;

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or DllNotFoundException or COMException)
        {
            SetWebTabStatus(tab, "Install the Microsoft Edge WebView2 Runtime to use the browser.");
            return;
        }

        var view = new NativeWebView { AdapterPreference = [WebViewAdapterType.WebView2], IsVisible = _activeWebTab == tab };
        AutomationProperties.SetName(view, "Web page");
        view.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.UserDataFolder = Path.Combine(WebDataDirectory, "Runtime");
                windows.EnableDevTools = false;
            }
        };
        view.AdapterCreated += (_, _) =>
        {
            if (_closed || tab.Closed) return;
            if (view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle)
            {
                BrowserStartFailed();
                return;
            }

            try
            {
                var core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
                ConnectWebTab(tab, core);
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                BrowserStartFailed();
            }
        };
        tab.Browser = view;
        _webNativeHost.Children.Add(view);

        void BrowserStartFailed()
        {
            SetWebTabStatus(tab, "The Microsoft Edge WebView2 Runtime could not start.");
            Dispatcher.UIThread.Post(() =>
            {
                if (tab.Browser == view) RemoveWebTabBrowser(tab);
            });
        }
    }

    private void ConnectWebTab(BrowserTab tab, CoreWebView2 core)
    {
        tab.Core = core;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;

        core.NavigationStarting += Navigating;
        core.NavigationCompleted += Navigated;
        core.SourceChanged += Changed;
        core.DocumentTitleChanged += Changed;
        core.HistoryChanged += Changed;
        core.NewWindowRequested += NewWindow;
        core.LaunchingExternalUriScheme += ExternalLink;
        core.DownloadStarting += Download;
        tab.Unsubscribe = () =>
        {
            core.NavigationStarting -= Navigating;
            core.NavigationCompleted -= Navigated;
            core.SourceChanged -= Changed;
            core.DocumentTitleChanged -= Changed;
            core.HistoryChanged -= Changed;
            core.NewWindowRequested -= NewWindow;
            core.LaunchingExternalUriScheme -= ExternalLink;
            core.DownloadStarting -= Download;
        };
        if (tab.PendingAddress is { } pending)
        {
            tab.PendingAddress = null;
            core.Navigate(pending.AbsoluteUri);
        }

        void Navigating(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (WebAddress.IsPage(args.Uri))
            {
                SetWebTabStatus(tab, "Loading…");
                return;
            }
            if (args.Uri == "about:blank") return;
            args.Cancel = true;
            if (args.IsUserInitiated && WebAddress.IsMagnet(args.Uri)) QueueMagnetPrompt(args.Uri);
        }

        void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess || args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                SetWebTabStatus(tab, "");
            }
            else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted)
            {
                SetWebTabStatus(tab, $"The page could not load ({args.WebErrorStatus}).");
            }
            RefreshWebTab(tab);
        }

        void Changed(object? sender, object args) => RefreshWebTab(tab);

        void NewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            if (!args.IsUserInitiated || tab != _activeWebTab || _page != Page.Web) return;
            if (WebAddress.IsMagnet(args.Uri)) QueueMagnetPrompt(args.Uri);
            else if (WebAddress.IsPage(args.Uri)) AddWebTab(new Uri(args.Uri));
        }

        void ExternalLink(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        {
            args.Cancel = true;
            if (args.IsUserInitiated && WebAddress.IsMagnet(args.Uri)) QueueMagnetPrompt(args.Uri);
        }

        void Download(object? sender, CoreWebView2DownloadStartingEventArgs args) => StartWebDownload(tab, args);
    }

    private void RefreshWebTab(BrowserTab tab)
    {
        if (tab.Closed) return;

        var title = tab.Core?.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title)) title = PageAddress(tab) ?? "New tab";
        if (tab.Select.Content is TextBlock label) label.Text = title;
        AutomationProperties.SetName(tab.Select, title);
        ToolTip.SetTip(tab.Select, title);
        if (_activeWebTab == tab)
        {
            if (PageAddress(tab) is { } address && _webAddress != null &&
                (!_webAddress.IsFocused || _webAddress.Text == _shownWebAddress))
            {
                SetWebAddressText(address);
            }
            UpdateWebToolbar();
        }
    }

    private void SetWebAddressText(string address)
    {
        if (_webAddress == null) return;
        _shownWebAddress = address;
        _webAddress.Text = address;
    }

    private static string? PageAddress(BrowserTab? tab)
    {
        var source = tab?.Core?.Source;
        return WebAddress.IsPage(source) ? source : null;
    }

    private void UpdateWebBrowserVisibility()
    {
        if (_webNativeHost == null) return;

        _webNativeHost.IsVisible = !_showWebBookmarks && !DialogHost.IsVisible && !_stoppingWebTransfers;
        if (_webBookmarkPanel != null) _webBookmarkPanel.IsVisible = _showWebBookmarks;
        foreach (var tab in _webTabs.Concat(_closedWebTabs))
        {
            if (tab.Browser != null) tab.Browser.IsVisible = tab == _activeWebTab && !tab.Closed;
        }
    }

    private void UpdateWebToolbar()
    {
        if (_webStatus == null || _webBack == null || _webForward == null || _webReload == null ||
            _webSaveBookmark == null || _webShowBookmarks == null) return;

        var core = _activeWebTab?.Core;
        var address = PageAddress(_activeWebTab);
        _webBack.IsEnabled = core?.CanGoBack == true;
        _webForward.IsEnabled = core?.CanGoForward == true;
        _webReload.IsEnabled = address != null;
        _webSaveBookmark.IsEnabled = address != null && !_webBookmarks.Any(bookmark => bookmark.Url == address);
        _webShowBookmarks.Classes.Set("selected", _showWebBookmarks);
        _webStatus.Text = _activeWebTab?.Status;
        _webStatus.IsVisible = !string.IsNullOrEmpty(_webStatus.Text);
    }

    private void SetWebBrowserStatus(string message)
    {
        if (_activeWebTab != null && _page == Page.Web && _webView == WebView.Browser) SetWebTabStatus(_activeWebTab, message);
        else Status(message);
    }

    private void SetWebTabStatus(BrowserTab tab, string message)
    {
        tab.Status = message;
        if (tab == _activeWebTab) UpdateWebToolbar();
    }

    private void AddWebBookmark()
    {
        if (PageAddress(_activeWebTab) is not { } address || _webBookmarks.Any(bookmark => bookmark.Url == address)) return;

        var title = _activeWebTab?.Core?.DocumentTitle;
        SaveWebBookmarks([.. _webBookmarks, new WebBookmark(string.IsNullOrWhiteSpace(title) ? address : title, address)]);
    }

    private void ToggleWebBookmarks()
    {
        _showWebBookmarks = !_showWebBookmarks;
        if (_showWebBookmarks) RefreshWebBookmarks();
        UpdateWebBrowserVisibility();
        UpdateWebToolbar();
    }

    private void RefreshWebBookmarks()
    {
        if (_webBookmarkRows == null) return;

        if (_webSaveBookmark?.Parent is Panel previous) previous.Children.Remove(_webSaveBookmark);
        _webBookmarkRows.Children.Clear();
        var heading = Heading("Bookmarks");
        heading.VerticalAlignment = VerticalAlignment.Center;
        var titleRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Children = { heading } };
        if (_webSaveBookmark != null)
        {
            Grid.SetColumn(_webSaveBookmark, 1);
            titleRow.Children.Add(_webSaveBookmark);
        }
        _webBookmarkRows.Children.Add(titleRow);
        if (_webBookmarks.Count == 0) _webBookmarkRows.Children.Add(Label("No bookmarks yet.", muted: true));
        foreach (var bookmark in _webBookmarks)
        {
            var open = ActionButton(bookmark.Title, () =>
            {
                if (_activeWebTab != null) NavigateWebAddress(_activeWebTab, new Uri(bookmark.Url));
            }, "quiet");
            var title = Label(string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title);
            title.TextWrapping = TextWrapping.NoWrap;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            open.HorizontalContentAlignment = HorizontalAlignment.Left;
            open.Content = title;
            ToolTip.SetTip(open, bookmark.Url);
            var remove = WebIconButton("Remove bookmark", "LG.Icon.Close",
                () => SaveWebBookmarks(_webBookmarks.Where(item => item != bookmark).ToList()));
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Children = { open, remove } };
            Grid.SetColumn(remove, 1);
            _webBookmarkRows.Children.Add(row);
        }
    }

    private void SaveWebBookmarks(List<WebBookmark> bookmarks)
    {
        try
        {
            WebBookmarks.Save(WebDataDirectory, bookmarks);
            _webBookmarks = bookmarks;
            if (_showWebBookmarks) RefreshWebBookmarks();
            UpdateWebToolbar();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetWebBrowserStatus("The bookmark change could not be saved.");
        }
    }

    private void QueueMagnetPrompt(string address) =>
        Dispatcher.UIThread.Post(() => _ = ConfirmWebMagnet(address));

    private async Task ConfirmWebMagnet(string address)
    {
        if (_closed || _confirmingWebMagnet || _page != Page.Web || _webView != WebView.Browser || DialogHost.HasModal) return;
        _confirmingWebMagnet = true;
        try
        {
            if (!await Confirm("Open Magnet link", "Add this Magnet link to Downloads? BitTorrent shares pieces with other peers while downloading.", "Add download") || _closed)
            {
                return;
            }
            await WebTransferQueue.AddMagnetAsync(address);
            if (!_closed && _page == Page.Web) SelectWebView(WebView.Downloads);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_closed) SetWebBrowserStatus(error.Message);
        }
        finally
        {
            _confirmingWebMagnet = false;
        }
    }

    private void StartWebDownload(BrowserTab tab, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Handled = true;
        if (tab.Closed || _closed || _stoppingWebTransfers)
        {
            args.Cancel = true;
            return;
        }

        string? path = null;
        try
        {
            var operation = args.DownloadOperation;
            path = WebTransferQueue.ReserveBrowserPath(Path.GetFileName(args.ResultFilePath));
            args.ResultFilePath = path;
            WebTransferQueue.TrackBrowserDownload(path, Snapshot, operation.Cancel);
            tab.ActiveDownloads++;
            operation.StateChanged += DownloadStateChanged;
            SetWebTabStatus(tab, "Download started.");

            WebDownloadProgress Snapshot()
            {
                var state = operation.State switch
                {
                    CoreWebView2DownloadState.Completed => WebDownloadState.Completed,
                    CoreWebView2DownloadState.InProgress => WebDownloadState.Active,
                    _ when operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled => WebDownloadState.Cancelled,
                    _ => WebDownloadState.Failed,
                };
                var total = operation.TotalBytesToReceive;
                return new WebDownloadProgress(operation.BytesReceived, total is <= long.MaxValue ? (long?)total : null, state,
                    state == WebDownloadState.Failed ? operation.InterruptReason.ToString() : null);
            }

            void DownloadStateChanged(object? sender, object eventArgs)
            {
                if (operation.State == CoreWebView2DownloadState.InProgress) return;

                operation.StateChanged -= DownloadStateChanged;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_closed) return;
                    WebTransferQueue.PollBrowserDownloads();
                    tab.ActiveDownloads--;
                    if (tab.Closed && tab.ActiveDownloads == 0) RemoveWebTabBrowser(tab);
                });
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            args.Cancel = true;
            if (path != null) WebTransferQueue.ReleaseBrowserPath(path);
            SetWebTabStatus(tab, "The download could not be started.");
        }
    }
}
