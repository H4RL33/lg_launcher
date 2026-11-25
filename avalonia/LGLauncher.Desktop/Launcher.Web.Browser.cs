using System.Diagnostics;
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
    private const string WebStartupError = "The Microsoft Edge WebView2 Runtime could not start. Reload to try again.";
    private sealed class BrowserTab(Button select, Grid header)
    {
        public Button Select { get; } = select;
        public Grid Header { get; } = header;
        public NativeWebView? Browser { get; set; }
        public CoreWebView2? Core { get; set; }
        public Action? Unsubscribe { get; set; }
        public Action<CoreWebView2?>? CompletePopup { get; set; }
        public bool IsPopup { get; set; }
        public Uri? PendingAddress { get; set; }
        public Uri? LastAddress { get; set; }
        public string Status { get; set; } = "";
        public int ActiveDownloads { get; set; }
        public bool Closed { get; set; }
        public bool Starting { get; set; }
    }

    private readonly List<BrowserTab> _webTabs = [];
    private readonly HashSet<BrowserTab> _closedWebTabs = [];
    private List<WebBookmark> _webBookmarks = [];
    private BrowserTab? _activeWebTab;
    private Grid? _webNativeHost;
    private Control? _webBrowserChrome;
    private UniformGrid? _webTabStrip;
    private ScrollViewer? _webTabScroll;
    private Border? _webTabBar;
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
    private Task<WebContentBlocking.EnvironmentHandle?>? _webEnvironment;
    private readonly SemaphoreSlim _webAdBlockUpdates = new(1);
    private bool? _webAdBlockEnabled;
    private string? _webAdBlockError;

    private Control BuildWebBrowser()
    {
        _webAddress = TextField(null, "Web address");
        _webAddress.Name = "WebAddress";
        UpdateWebAddressPrompt();
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

        _webBack = WebIconButton("Back", "LG.Icon.ChevronLeft", () => MoveInWebHistory(-1));
        _webForward = WebIconButton("Forward", "LG.Icon.Chevron", () => MoveInWebHistory(1));
        _webReload = WebIconButton("Reload", "LG.Icon.Refresh", ReloadWebTab);
        _webShowBookmarks = WebIconButton("Bookmarks", "LG.Icon.Library", ToggleWebBookmarks, "bookmark");
        _webShowBookmarks.MinHeight = 0;
        _webShowBookmarks.Margin = new Thickness(0, 3);
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
        _webTabBar = WebChromePanel(_webTabScroll, "LG.Surface", new Thickness(0, 1, 0, 0));
        var chrome = WebChromePanel(new StackPanel { Children = { toolbar, _webTabBar } }, "LG.Sidebar", new Thickness(0, 0, 0, 1));

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
        var viewport = WebChromePanel(new Grid { Children = { _webNativeHost, _webBookmarkPanel } },
            "LG.Canvas", default);
        _webBrowserChrome = chrome;

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

    private BrowserTab? AddWebTab(Uri? address = null, bool focusAddress = true, bool useHomepage = true)
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
        UpdateWebTabStripVisibility();
        SelectWebTab(tab);
        if (address == null && useHomepage && WebAddress.Parse(_library.Preferences.WebHomepage) is { Scheme: "http" or "https" } homepage)
        {
            address = homepage;
        }
        if (address != null) NavigateWebAddress(tab, address);
        if (focusAddress && address == null) FocusWebAddress();
        return tab;
    }

    private void UpdateWebTabStripVisibility()
    {
        if (_webTabBar != null) _webTabBar.IsVisible = !_library.Preferences.HideWebSingleTab || _webTabs.Count > 1;
        SizeWebTabs();
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
        UpdateWebTabStripVisibility();
        tab.Closed = true;
        if (tab.ActiveDownloads > 0) _closedWebTabs.Add(tab);
        else RemoveWebTabBrowser(tab);

        if (_webTabs.Count == 0) AddWebTab();
        else if (_activeWebTab == tab) SelectWebTab(_webTabs[Math.Min(index, _webTabs.Count - 1)]);
        else UpdateWebBrowserVisibility();
    }

    private void RemoveWebTabBrowser(BrowserTab tab)
    {
        CompleteWebPopup(tab, null);
        try
        {
            tab.Unsubscribe?.Invoke();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            // WebView2 may have already closed the controller and released its handlers.
            Trace.TraceInformation("WebView2 handlers could not be detached: {0}", error.Message);
        }
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
        if (OperatingSystem.IsWindows() && _webEnvironment is { IsCompletedSuccessfully: true } environment)
        {
            environment.Result?.Dispose();
        }
    }

    private void FocusWebAddress()
    {
        _webAddress?.Focus();
        _webAddress?.SelectAll();
    }

    private void UpdateWebAddressPrompt()
    {
        if (_webAddress == null) return;

        var preferences = _library.Preferences;
        _webAddress.PlaceholderText = WebAddress.SearchTemplate(preferences.WebSearchEngine, preferences.WebSearchTemplate) != null
            ? "Search or enter an address"
            : "Enter a URL or Magnet link";
    }

    private void SubmitWebAddress()
    {
        if (_activeWebTab == null) return;

        var preferences = _library.Preferences;
        if (WebAddress.ParseInput(_webAddress?.Text, preferences.WebSearchEngine, preferences.WebSearchTemplate) is not { } address)
        {
            SetWebBrowserStatus(WebAddress.SearchTemplate(preferences.WebSearchEngine, preferences.WebSearchTemplate) != null
                ? "Enter a search term, a valid web address, or a Magnet link."
                : "Enter a valid HTTP or HTTPS address, or a Magnet link.");
            return;
        }
        if (address.Scheme == "magnet")
        {
            _ = ConfirmWebMagnet(address.AbsoluteUri);
            return;
        }
        NavigateWebAddress(_activeWebTab, address);
    }

    private void OpenRssLinkInWebBrowser(Uri address)
    {
        if (_closed || !WebAddress.IsPage(address.AbsoluteUri)) return;
        Navigate(Page.Web);
        SelectWebView(WebView.Browser);
        if (_activeWebTab is { } tab && (_webTabs.Count >= MaximumWebTabs || tab.Core == null && tab.PendingAddress == null))
            NavigateWebAddress(tab, address);
        else AddWebTab(address, focusAddress: false);
    }

    private void NavigateWebAddress(BrowserTab tab, Uri address)
    {
        CompleteWebPopup(tab, null);
        _showWebBookmarks = false;
        tab.PendingAddress = address;
        tab.LastAddress = address;
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
        else if (tab.Browser == null && !tab.Starting)
        {
            _ = CreateWebTabBrowser(tab);
        }
        UpdateWebBrowserVisibility();
    }

    private void ReloadWebTab()
    {
        if (_activeWebTab is not { } tab) return;
        if (tab.Core != null) tab.Browser?.Refresh();
        else if (CanRetryWebTab(tab) && tab.PendingAddress is { } address) NavigateWebAddress(tab, address);
    }

    private static bool CanRetryWebTab(BrowserTab tab) =>
        !tab.Closed && !tab.Starting && tab.Browser == null && tab.PendingAddress != null;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task CreateWebTabBrowser(BrowserTab tab)
    {
        if (_webNativeHost == null) return;

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or DllNotFoundException or COMException)
        {
            CompleteWebPopup(tab, null);
            SetWebTabStatus(tab, "Install the Microsoft Edge WebView2 Runtime to use the browser.");
            return;
        }

        var runtimeDirectory = Path.Combine(WebDataDirectory, "Runtime");
        try
        {
            Directory.CreateDirectory(runtimeDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("WebView2 profile directory is unavailable: {0}", error.Message);
            CompleteWebPopup(tab, null);
            SetWebTabStatus(tab, WebStartupError);
            return;
        }

        tab.Starting = true;
        var environmentTask = _webEnvironment ??= CreateExtensionEnvironment(runtimeDirectory);
        var environment = await environmentTask;
        tab.Starting = false;
        if (_closed)
        {
            environment?.Dispose();
            return;
        }
        if (tab.Closed) return;

        var view = new NativeWebView { AdapterPreference = [WebViewAdapterType.WebView2], IsVisible = _activeWebTab == tab };
        var failureQueued = false;
        // The native host can fail before the adapter publishes any error or creation event.
        var startupTimeout = DispatcherTimer.RunOnce(() => BrowserStartFailed("The controller did not initialize in time."),
            TimeSpan.FromSeconds(15), DispatcherPriority.Background);
        view.DetachedFromVisualTree += (_, _) => startupTimeout.Dispose();
        AutomationProperties.SetName(view, "Web page");
        view.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.UserDataFolder = runtimeDirectory;
                if (environment != null) windows.ExplicitEnvironment = environment.DangerousGetHandle();
                windows.EnableDevTools = false;
            }
        };
        view.PropertyChanged += (_, args) =>
        {
            if (args.Property == NativeWebView.AdapterInfoProperty &&
                view.AdapterInfo is DetailedWebViewAdapterInfo { UnavailableReason: { Length: > 0 } reason })
            {
                BrowserStartFailed(reason);
            }
        };
        view.AdapterCreated += async (_, _) =>
        {
            if (_closed || tab.Closed || failureQueued || tab.Browser != view) return;
            startupTimeout.Dispose();
            if (view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle)
            {
                BrowserStartFailed("Avalonia did not return a WebView2 platform handle.");
                return;
            }

            try
            {
                var core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
                if (environment != null) await ApplyWebAdBlocking(core);
                if (!_closed && !tab.Closed && !failureQueued && tab.Browser == view) ConnectWebTab(tab, core);
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                BrowserStartFailed(error.Message);
            }
        };
        tab.Browser = view;
        _webNativeHost.Children.Add(view);

        void BrowserStartFailed(string reason)
        {
            if (_closed || tab.Closed || failureQueued || tab.Browser != view) return;
            failureQueued = true;
            Trace.TraceWarning("WebView2 controller initialization failed: {0}", reason);
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || tab.Browser != view) return;
                // Re-attempt the extension environment on retry if even the fallback failed to start.
                if (environment == null && _webEnvironment == environmentTask) _webEnvironment = null;
                tab.PendingAddress ??= tab.LastAddress;
                RemoveWebTabBrowser(tab);
                SetWebTabStatus(tab, WebStartupError);
            });
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<WebContentBlocking.EnvironmentHandle?> CreateExtensionEnvironment(string runtimeDirectory)
    {
        try
        {
            return await WebContentBlocking.CreateEnvironment(runtimeDirectory);
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException or
            InvalidOperationException or ArgumentException or NotImplementedException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not start an extension-enabled WebView2 environment: {0}", error);
            if (!_library.Preferences.DisableWebAdBlocker) _webAdBlockError = "uBO Lite could not start.";
            if (!_closed) UpdateWebToolbar();
            return null;
        }
    }

    private void ApplyWebBrowserPreferences()
    {
        UpdateWebAddressPrompt();
        if (!OperatingSystem.IsWindows() || _closed) return;

        if (_webEnvironment is { IsCompletedSuccessfully: true, Result: null })
        {
            _webAdBlockError = _library.Preferences.DisableWebAdBlocker ? null : "uBO Lite could not start.";
            UpdateWebToolbar();
        }
        else if (_webTabs.Concat(_closedWebTabs).FirstOrDefault(tab => tab.Core != null)?.Core is { } core)
        {
            _ = ApplyWebAdBlocking(core);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task ApplyWebAdBlocking(CoreWebView2 core)
    {
        await _webAdBlockUpdates.WaitAsync();
        try
        {
            while (!_closed)
            {
                var enabled = !_library.Preferences.DisableWebAdBlocker;
                if (_webAdBlockEnabled == enabled) return;

                try
                {
                    await WebContentBlocking.SetEnabled(core.Profile, WebDataDirectory, enabled);
                    _webAdBlockEnabled = enabled;
                    _webAdBlockError = null;
                }
                catch (Exception error) when (error is COMException or IOException or InvalidOperationException or UnauthorizedAccessException or NotImplementedException)
                {
                    Trace.TraceWarning("Could not change uBO Lite state: {0}", error);
                    _webAdBlockEnabled = null;
                    _webAdBlockError = enabled ? "uBO Lite could not start." : "uBO Lite could not be disabled.";
                }

                if (_library.Preferences.DisableWebAdBlocker != enabled) break;
            }
            if (!_closed) UpdateWebToolbar();
        }
        finally
        {
            _webAdBlockUpdates.Release();
        }
    }

    private void ConnectWebTab(BrowserTab tab, CoreWebView2 core)
    {
        tab.Core = core;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        // Keep hovered URLs out of the embedded browser.
        core.Settings.IsStatusBarEnabled = false;

        core.NavigationStarting += Navigating;
        core.NavigationCompleted += Navigated;
        core.SourceChanged += Changed;
        core.DocumentTitleChanged += Changed;
        core.HistoryChanged += Changed;
        core.ProcessFailed += ProcessFailed;
        core.NewWindowRequested += NewWindow;
        core.WindowCloseRequested += WindowClose;
        core.LaunchingExternalUriScheme += ExternalLink;
        core.DownloadStarting += Download;
        tab.Unsubscribe = () =>
        {
            core.NavigationStarting -= Navigating;
            core.NavigationCompleted -= Navigated;
            core.SourceChanged -= Changed;
            core.DocumentTitleChanged -= Changed;
            core.HistoryChanged -= Changed;
            core.ProcessFailed -= ProcessFailed;
            core.NewWindowRequested -= NewWindow;
            core.WindowCloseRequested -= WindowClose;
            core.LaunchingExternalUriScheme -= ExternalLink;
            core.DownloadStarting -= Download;
        };
        CompleteWebPopup(tab, core);
        if (tab == _activeWebTab) UpdateWebBrowserVisibility();
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

        void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (_closed || tab.Core != core) return;
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                SetWebTabStatus(tab, "The page stopped. Reload to try again.");
                return;
            }
            if (args.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;

            var failedView = tab.Browser;
            tab.Core = null;
            tab.PendingAddress ??= tab.LastAddress;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed) return;
                if (tab.Browser == failedView)
                {
                    RemoveWebTabBrowser(tab);
                    if (!tab.Closed) SetWebTabStatus(tab, "The browser stopped. Reload to try again.");
                }
                _webTransfers?.PollBrowserDownloads();
                _refreshWebTransferPage?.Invoke();
            });
        }

        void NewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            if (tab != _activeWebTab || !IsWebBrowserDisplayed() || _closed) return;

            var userInitiated = args.IsUserInitiated;
            var allowAutomatic = _library.Preferences.DisableWebPopupBlocking;
            if (!userInitiated && !allowAutomatic) return;

            var address = args.Uri;
            if (userInitiated && WebAddress.IsMagnet(address))
            {
                QueueMagnetPrompt(address);
                return;
            }
            if (!WebAddress.IsPage(address) && address != "about:blank") return;

            if (OperatingSystem.IsWindows()) OpenWebPopup(tab, args);
        }

        void WindowClose(object? sender, object args)
        {
            if (tab.IsPopup) Dispatcher.UIThread.Post(() => CloseWebTab(tab));
        }

        void ExternalLink(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        {
            args.Cancel = true;
            if (args.IsUserInitiated && WebAddress.IsMagnet(args.Uri)) QueueMagnetPrompt(args.Uri);
        }

        void Download(object? sender, CoreWebView2DownloadStartingEventArgs args) => StartWebDownload(tab, core, args);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OpenWebPopup(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs args)
    {
        var popup = AddWebTab(focusAddress: false, useHomepage: false);
        if (popup == null) return;

        // WebView2 needs a fresh controller in the same profile to preserve the window opener.
        var deferral = args.GetDeferral();
        popup.CompletePopup = core =>
        {
            try
            {
                using (deferral)
                {
                    if (core == null || _closed || opener.Closed) return;

                    args.NewWindow = core;
                    popup.IsPopup = true;
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                if (!_closed && !popup.Closed) SetWebTabStatus(popup, "The pop-up could not open.");
            }
        };
        _ = CreateWebTabBrowser(popup);
    }

    private static void CompleteWebPopup(BrowserTab tab, CoreWebView2? core)
    {
        var completion = tab.CompletePopup;
        tab.CompletePopup = null;
        completion?.Invoke(core);
    }

    private void MoveInWebHistory(int direction)
    {
        if (_activeWebTab?.Core is not { } core) return;
        if (direction < 0 && core.CanGoBack) core.GoBack();
        if (direction > 0 && core.CanGoForward) core.GoForward();
    }

    private void RefreshWebTab(BrowserTab tab)
    {
        if (tab.Closed) return;

        var address = PageAddress(tab);
        if (address != null) tab.LastAddress = new Uri(address);
        var title = tab.Core?.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title)) title = address ?? "New tab";
        if (tab.Select.Content is TextBlock label) label.Text = title;
        AutomationProperties.SetName(tab.Select, title);
        ToolTip.SetTip(tab.Select, title);
        if (_activeWebTab == tab)
        {
            if (address != null && _webAddress != null &&
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

    private bool IsWebBrowserDisplayed() =>
        _page == Page.Web && _webView == WebView.Browser && !_showWebBookmarks && !DialogHost.IsVisible && !_stoppingWebTransfers;

    private void UpdateWebBrowserVisibility()
    {
        if (_webNativeHost == null) return;

        _webNativeHost.IsVisible = IsWebBrowserDisplayed();
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
        _webReload.IsEnabled = address != null || _activeWebTab is { } tab && CanRetryWebTab(tab);
        _webSaveBookmark.IsEnabled = address != null && !_webBookmarks.Any(bookmark => bookmark.Url == address);
        _webShowBookmarks.Classes.Set("selected", _showWebBookmarks);
        _webStatus.Text = _activeWebTab?.Status is { Length: > 0 } status ? status : _webAdBlockError;
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
            var preferences = _library.Preferences;
            var rememberChoice = false;
            if (!preferences.AlwaysOpenMagnetLinks)
            {
                var dialog = new ModalDialog("Open Magnet link", 453);
                dialog.Body.Children.Add(Label("Add this Magnet link to Downloads? BitTorrent shares pieces with other peers while downloading.", muted: true));
                var alwaysOpen = new CheckBox { Content = "Always open", IsChecked = false };
                dialog.Body.Children.Add(alwaysOpen);
                _ = DialogActions(dialog, "Add download", () => dialog.Close(true), defaultAction: false);
                if (!await ShowModal(dialog) || _closed) return;

                rememberChoice = alwaysOpen.IsChecked == true;
            }

            var destination = await PickWebDownloadFolder();
            if (destination == null || _closed) return;
            await WebTransferQueue.AddMagnetAsync(address, destination);
            if (_closed) return;
            if (rememberChoice)
            {
                SavePreference(preferences.AlwaysOpenMagnetLinks, true, value => preferences.AlwaysOpenMagnetLinks = value, apply: null);
            }
            if (_page == Page.Web) SelectWebView(WebView.Downloads);
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

    private static WebDownloadState BrowserDownloadState(CoreWebView2DownloadState nativeState,
        CoreWebView2DownloadInterruptReason reason, bool canResume, bool paused, bool cancelRequested) => nativeState switch
        {
            CoreWebView2DownloadState.Completed => WebDownloadState.Completed,
            CoreWebView2DownloadState.InProgress => WebDownloadState.Active,
            _ when cancelRequested => WebDownloadState.Cancelled,
            _ when paused || canResume => WebDownloadState.Stopped,
            _ when reason == CoreWebView2DownloadInterruptReason.UserCanceled => WebDownloadState.Cancelled,
            _ => WebDownloadState.Failed,
        };

    private void StartWebDownload(BrowserTab tab, CoreWebView2 core, CoreWebView2DownloadStartingEventArgs args)
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
            path = WebTransferQueue.ReserveBrowserPath(Path.GetFileName(args.ResultFilePath), WebDownloadDirectory);
            args.ResultFilePath = path;
            var paused = false;
            var resuming = false;
            var cancelRequested = false;
            var sampleTimestamp = Stopwatch.GetTimestamp();
            var sampledBytes = operation.BytesReceived;
            long? downloadRate = null;
            var progress = new WebDownloadProgress(sampledBytes, null, WebDownloadState.Active);
            WebTransferQueue.TrackBrowserDownload(path, Snapshot, CancelDownload, PauseDownload, ResumeDownload, ReleaseDownload);
            tab.ActiveDownloads++;
            operation.StateChanged += DownloadStateChanged;
            SetWebTabStatus(tab, "Download started.");

            WebDownloadProgress Snapshot()
            {
                if (tab.Core != core)
                {
                    return progress with { State = WebDownloadState.Failed, Detail = "The browser stopped.", DownloadRate = null };
                }

                var nativeState = operation.State;
                if (nativeState == CoreWebView2DownloadState.InProgress && resuming)
                {
                    paused = false;
                    resuming = false;
                }
                var interrupted = nativeState == CoreWebView2DownloadState.Interrupted;
                var reason = interrupted ? operation.InterruptReason : CoreWebView2DownloadInterruptReason.None;
                var canResume = interrupted && !paused && !cancelRequested && operation.CanResume;
                var state = BrowserDownloadState(nativeState, reason, canResume, paused, cancelRequested);
                var total = operation.TotalBytesToReceive;
                var received = operation.BytesReceived;
                var timestamp = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(sampleTimestamp, timestamp);
                if (state != WebDownloadState.Active)
                {
                    downloadRate = null;
                    sampleTimestamp = timestamp;
                    sampledBytes = received;
                }
                else if (elapsed >= TimeSpan.FromMilliseconds(250))
                {
                    downloadRate = (long)(Math.Max(0, received - sampledBytes) / elapsed.TotalSeconds);
                    sampleTimestamp = timestamp;
                    sampledBytes = received;
                }

                var detail = state == WebDownloadState.Failed || state == WebDownloadState.Stopped && !paused ? reason.ToString() : null;
                progress = new WebDownloadProgress(received, total is <= long.MaxValue ? (long?)total : null, state,
                    detail, DownloadRate: downloadRate);
                return progress;
            }

            void PauseDownload()
            {
                operation.Pause();
                paused = true;
                resuming = false;
            }

            void ResumeDownload()
            {
                if (!operation.CanResume) throw new InvalidOperationException("The site no longer allows this download to resume.");
                operation.Resume();
                resuming = true;
            }

            void CancelDownload()
            {
                operation.Cancel();
                cancelRequested = true;
                paused = false;
                resuming = false;
            }

            void DownloadStateChanged(object? sender, object eventArgs) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (_closed) return;
                    WebTransferQueue.PollBrowserDownloads();
                    _refreshWebTransferPage?.Invoke();
                });

            void ReleaseDownload()
            {
                try
                {
                    operation.StateChanged -= DownloadStateChanged;
                }
                catch (Exception error) when (error is COMException or InvalidOperationException)
                {
                    // The renderer may have already released its native operation.
                }
                tab.ActiveDownloads--;
                if (!_closed && tab.Closed && tab.ActiveDownloads == 0)
                {
                    Dispatcher.UIThread.Post(() => RemoveWebTabBrowser(tab));
                }
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
