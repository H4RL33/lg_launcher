using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LGLauncher.Components;
using Microsoft.Web.WebView2.Core;
using SkiaSharp;

namespace LGLauncher;

internal sealed partial class Launcher
{
    internal async Task VerifyWeb(string preview)
    {
        var captures = Path.GetDirectoryName(preview)!;
        VerifyModalCorners(captures);
        await VerifyWebSettings(captures);
        var web = LibrarySurface.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(button => AutomationProperties.GetName(button) == "Web");
        Require(web != null, "The Web destination was missing from the sidebar.");
        web!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(WebWorkspaceHost.IsVisible && !ContentScroll.IsVisible && _activeWebTab?.Browser == null,
            "The Web workspace did not open with a lazy browser tab.");
        HomeNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(ContentScroll.IsVisible && PageBody.Children.OfType<Grid>().Any(header =>
            header.Children.OfType<TextBlock>().Any(heading => heading.Text == "Library")),
            "Returning to Home did not show the Library section.");
        web.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        using var lifetime = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = ServeWebFixture(listener, lifetime.Token);
        var root = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        try
        {
            var first = _activeWebTab!;
            FocusWebAddress();
            GoTo(root + "/redirect");
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture one",
                $"The native browser did not open the loopback page ({first.Status}).");
            Require(_webAddress!.Text == root + "/one", "The address bar did not retain the opened URL.");

            _webShowBookmarks!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            _webSaveBookmark!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            _webShowBookmarks.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(WebBookmarks.Read(WebDataDirectory).Single() == new WebBookmark("Web fixture one", root + "/one"),
                "Adding a bookmark did not persist its URL and title.");
            GoTo(root + "/two");
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture two" && _webBack?.IsEnabled == true,
                "Navigating in the same tab did not enable Back.");
            ReleaseHistoryButton(forward: false);
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture one" && _webForward?.IsEnabled == true && _page == Page.Web,
                "The mouse Back button did not stay in the browser and return to the previous page.");
            ReleaseHistoryButton(forward: true);
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture two", "The mouse Forward button did not restore the next browser page.");
            _webBack!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture one", "The browser toolbar did not use the same tab history.");
            await VerifyWebAdBlocking(first.Core!);
            SendWebRendererHistoryButton(forward: true);
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture two" && _page == Page.Web,
                "The mouse Forward button did not work over the native WebView2 renderer.");
            SendWebRendererHistoryButton(forward: false);
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture one", "The mouse Back button did not work over the native WebView2 renderer.");
            await VerifyWebBrowserOptions(root, first, captures);

            AddWebTab();
            var second = _activeWebTab!;
            Require(_webTabs.Count == 2 && second != first && second.Browser == null,
                "Adding a new tab did not preserve the first tab.");
            GoTo(root + "/two");
            await WaitFor(() => second.Core?.DocumentTitle == "Web fixture two", "The second browser tab did not load independently.");
            first.Select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(first.Core?.DocumentTitle == "Web fixture one" && _webAddress.Text == root + "/one",
                "Selecting the first tab lost its page or address.");

            HomeNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            web.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_activeWebTab == first && first.Core?.DocumentTitle == "Web fixture one",
                "The browser lost its page after navigating through the launcher.");
            var directory = Path.GetDirectoryName(preview)!;
            ToggleWebBookmarks();
            Require(_webBookmarkPanel?.IsVisible == true && _webNativeHost?.IsVisible == false,
                "The bookmark list did not replace the native page surface.");
            Capture(Path.Combine(directory, "web-bookmarks.png"));
            CloseWebTab(second);

            _webAddress.Text = "magnet:?xt=urn:btih:0123456789012345678901234567890123456789";
            SubmitWebAddress();
            Require(DialogHost.HasModal && _webNativeHost?.IsVisible == false, "The Magnet link did not open a native launcher confirmation.");
            var magnetDialog = DialogHost.Children.OfType<ModalDialog>().Single();
            var alwaysOpen = magnetDialog.Body.Children.OfType<CheckBox>()
                .SingleOrDefault(check => Equals(check.Content, "Always open"));
            Require(alwaysOpen?.IsChecked == false, "The Magnet confirmation did not offer an unchecked Always open option.");
            Capture(Path.Combine(directory, "web-magnet-confirmation.png"));
            alwaysOpen!.IsChecked = true;
            DialogHost.DismissTop();
            await WaitFor(() => !_confirmingWebMagnet, "The Magnet confirmation did not dismiss.");
            Require(_webTransfers == null && !_store.Load().Preferences.AlwaysOpenMagnetLinks,
                "Cancelling the Magnet confirmation queued a transfer or remembered Always open.");

            ToggleWebBookmarks();
            await ClickMagnetLink(first.Core!);
            await WaitFor(() => DialogHost.HasModal, "Clicking a Magnet link in a web page did not open the launcher confirmation.");
            DialogHost.DismissTop();
            await WaitFor(() => !_confirmingWebMagnet, "The browser Magnet confirmation did not dismiss.");
            await VerifyAlwaysOpenMagnet(first.Core!, directory);
            GoTo(root + "/file");
            await WaitFor(() => _webTransfers?.Items.Any(item => item.Snapshot.OutputPath != null) == true,
                "The WebView2 download was not published by the Downloads manager.");
            var downloaded = WebTransferQueue.Items.Single().Snapshot.OutputPath!;
            Require(await File.ReadAllTextAsync(downloaded) == "LG Launcher web download fixture",
                "The completed download contents did not match the server response.");
            GoTo(root + "/notes");
            await WaitFor(() => WebTransferQueue.Items.Count(item => item.Snapshot.OutputPath != null) == 2,
                "The second download did not reach the transfer table.");
            var patches = Directory.CreateDirectory(Path.Combine(WebDownloadDirectory, "Patches")).FullName;
            for (var index = 1; index <= 36; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(patches, $"patch-{index:00}.txt"), "File table scroll fixture");
            }
            ShowWebFiles(WebDownloadDirectory);
            await WaitFor(() => _webFilesDirectory == WebDownloadDirectory && _webFilesLoading == null, "Files did not open Downloads.");
            SetWebFileLocation(patches);
            await WaitFor(() => _webFilesDirectory == patches && _webFilesLoading == null, "Files did not open a subfolder.");
            ReleaseHistoryButton(forward: false);
            await WaitFor(() => _webFilesDirectory == WebDownloadDirectory && _webFilesLoading == null && _page == Page.Web,
                "The mouse Back button did not return to the previous Files folder.");
            ReleaseHistoryButton(forward: true);
            await WaitFor(() => _webFilesDirectory == patches && _webFilesLoading == null, "The mouse Forward button did not restore the Files folder.");
            ReleaseHistoryButton(forward: false);
            await WaitFor(() => _webFilesDirectory == WebDownloadDirectory && _webFilesLoading == null, "Files did not reopen Downloads.");
            ShowWebFiles(WebDataDirectory);
            await WaitFor(() => _webFilesDirectory == WebDataDirectory && _webFilesLoading == null, "Files did not navigate after using history.");
            ReleaseHistoryButton(forward: true);
            Require(_webFilesLoading == null && _webFilesDirectory == WebDataDirectory,
                "Opening a different Files folder did not discard the old forward history.");
            ShowWebFiles(WebDownloadDirectory);
            await WaitFor(() => _webFilesDirectory == WebDownloadDirectory && _webFilesLoading == null, "Files did not reopen Downloads.");
            SelectWebView(WebView.Browser);
            GoTo(root + "/one");
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture one", "The browser could not return after a download.");

            foreach (var (width, height) in new[] { (730, 510), (1230, 805), (1600, 900) })
            {
                Width = width;
                Height = height;
                SelectWebView(WebView.Browser);
                await Task.Delay(160);
                UpdateLayout();
                Require(_webAddress.Bounds.Width > 110 && _webNativeHost!.Bounds.Width > 400,
                    $"The {width}-dip browser toolbar or page viewport did not fit.");
                var rail = WebWorkspaceHost.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "WebToolsRail");
                var tools = ((StackPanel)rail.Child!).Children.OfType<Button>().ToArray();
                Require(tools.Select(AutomationProperties.GetName).SequenceEqual(["Browser", "Downloads", "Files"]) &&
                    tools.All(button => button.Content is SymbolIcon) && tools[0].Bounds.Y < tools[2].Bounds.Y &&
                    rail.Bounds.X > _webNativeHost!.Bounds.Width - 1, "The Web tools did not form an icon rail on the right.");
                Require(_webAddress.TranslatePoint(default, WebWorkspaceHost)!.Value.Y <
                    _webTabScroll!.TranslatePoint(default, WebWorkspaceHost)!.Value.Y &&
                    Math.Abs(_webTabStrip!.Bounds.Width - _webTabScroll.Viewport.Width) < 1,
                    "The address field did not sit above a full-width tab strip.");
                var addressBar = _webBrowser!.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "WebAddressBar");
                var bookmarkBounds = _webShowBookmarks!.Bounds;
                var gapBelowBookmark = addressBar.Bounds.Height - bookmarkBounds.Bottom;
                Require(bookmarkBounds.Top >= 2 && Math.Abs(bookmarkBounds.Top - gapBelowBookmark) < 1,
                    "The Bookmarks button did not have matching gaps above and below.");
                var newTab = addressBar.Children.OfType<Button>().Single(button => button.Name == "WebNewTab");
                var closeTab = first.Header.Children.OfType<Button>().Single(button => button != first.Select);
                Require(_webAddress.CornerRadius == default && first.Select.CornerRadius == default &&
                    Math.Abs(_webAddress.Bounds.Height - addressBar.Bounds.Height) < 1 &&
                    Math.Abs(first.Select.Bounds.Height - first.Header.Bounds.Height) < 1,
                    "The browser address field and tabs did not fill their bars with square edges.");
                Require(newTab.Content is SymbolIcon && closeTab.Bounds.X > first.Select.Bounds.X &&
                    Math.Abs(closeTab.Bounds.Right - first.Select.Bounds.Right) < 1 &&
                    Math.Abs(closeTab.Bounds.Height - first.Select.Bounds.Height) < 1,
                    "The tab actions were not inside the active tab and address bar.");
                Capture(Path.Combine(directory, $"web-browser-{width}.png"));
                using (var page = File.Create(Path.Combine(directory, $"web-page-{width}.png")))
                {
                    await first.Core!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, page);
                }

                SelectWebView(WebView.Downloads);
                UpdateLayout();
                var transferRow = VerifyWebTable(_webDownloads!, "WebTransfersAddressBar", "WebTransferHeaders",
                    ["Name", "Source", "Status", "Progress", "Size", "Down Speed", "Up Speed", "ETA", "Ratio"], Path.GetFileName(downloaded));
                var transferValues = transferRow.GetVisualDescendants().OfType<TextBlock>().Select(label => label.Text).ToArray();
                Require(transferValues.Contains("Browser") && transferValues.Contains("Completed") &&
                    transferValues.Contains("100%") && transferValues.Contains("32 B"), "The transfer row did not show completed download details.");
                var transferScroll = _webDownloads!.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(control => control.Name == "WebTransferScroll");
                Require((transferScroll.Extent.Width > transferScroll.Viewport.Width + 1) == (width == 730),
                    "The transfer table did not scroll horizontally only at the narrow window size.");
                Capture(Path.Combine(directory, $"web-downloads-{width}.png"));
                if (width == 730)
                {
                    transferScroll.Offset = new Vector(transferScroll.Extent.Width, 0);
                    UpdateLayout();
                    Capture(Path.Combine(directory, "web-downloads-730-scrolled.png"));
                    transferScroll.Offset = default;
                }
                Require(transferRow.ColumnDefinitions.Count == 9 && !transferRow.GetVisualDescendants().OfType<Button>().Any(),
                    "The transfer table retained a trailing action column.");
                var completedRow = FindWebTableRow(_webDownloads, Path.GetFileName(downloaded));
                InvokeWebFixtureMenu(await OpenWebFixtureMenu(completedRow), "Open in Files");
                ListBox? files = null;
                await WaitFor(() =>
                {
                    files = _webFiles?.GetVisualDescendants().OfType<ListBox>()
                        .SingleOrDefault(control => control.Name == "WebFilesEntries");
                    return files?.SelectedItem != null;
                }, "Open in Files did not select the completed download.");
                var shortcuts = _webFiles!.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "WebFilesQuickAccess");
                Require(_webView == WebView.Files && files!.Bounds.Width > 320 && shortcuts.Bounds.Width >= 100,
                    "Files did not fit the Quick Access sidebar and table in the workspace.");
                UpdateLayout();
                var fileRow = VerifyWebTable(_webFiles!, "WebFilesAddressBar", "WebFilesHeaders",
                    ["Name", "Type", "Size", "Modified"], Path.GetFileName(downloaded));
                var fileValues = fileRow.Children.OfType<TextBlock>().Select(label => label.Text).ToArray();
                Require(fileValues.Length == 3 && fileValues[0] == "TXT file" && fileValues[1] == "32 B" &&
                    !string.IsNullOrWhiteSpace(fileValues[2]) && fileValues[2] != "—",
                    "The file row did not show file type, byte count, and modification time.");
                Capture(Path.Combine(directory, $"web-files-{width}.png"));
            }

            await VerifyWebQuickAccess(patches, directory);
            await VerifyWebFileActions(directory);

            Width = 730;
            Height = 510;
            ShowWebFiles(Path.Combine(patches, "patch-24.txt"));
            await WaitFor(() => _webFilesView?.Entries.SelectedItem is WebFileEntry { Name: "patch-24.txt" } &&
                _webFiles!.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "patch-24.txt"),
                "Files did not scroll to the selected entry in a longer folder.");
            UpdateLayout();
            var fileScroll = _webFilesView!.Entries.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Require(fileScroll.Extent.Height > fileScroll.Viewport.Height && fileScroll.Offset.Y > 0,
                "The file table did not scroll to reveal the selected entry.");
            VerifyWebTable(_webFiles!, "WebFilesAddressBar", "WebFilesHeaders", ["Name", "Type", "Size", "Modified"], "patch-24.txt");
            Capture(Path.Combine(directory, "web-files-scrolling.png"));
            await VerifyWebTransferActions(downloaded, directory);
            await VerifyWebNativePause(root);
            SelectWebView(WebView.Browser);
            var overflowTabs = Enumerable.Range(0, 6).Select(_ => AddWebTab(focusAddress: false)!).ToArray();
            await Task.Delay(100);
            UpdateLayout();
            var tabScroll = _webBrowser!.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.Name == "WebTabScroll");
            Require(tabScroll.Extent.Width > tabScroll.Viewport.Width, "Extra tabs did not overflow into the scrollable strip.");
            Capture(Path.Combine(directory, "web-tab-overflow.png"));
            foreach (var tab in overflowTabs) CloseWebTab(tab);
            ToggleWebBookmarks();
            var removeBookmark = _webBookmarkRows!.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Remove bookmark");
            removeBookmark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(WebBookmarks.Read(WebDataDirectory).Count == 0, "Removing a bookmark did not persist the change.");
            Capture(preview);

            Width = 1230;
            Height = 805;
            await WaitFor(() => ClientSize.Width >= 1200, "The window did not resize for the column controls fixture.");
            SelectWebView(WebView.Downloads);
            VerifyWebColumnSizing(_webDownloads!, "WebTransferHeaders", WebFixtureSlowName, column: 4);
            var longName = $"zz-{new string('W', 38)}.txt";
            await File.WriteAllTextAsync(Path.Combine(patches, longName), "Auto-fit must include virtualized rows.");
            ShowWebFiles(patches);
            await WaitFor(() => _webFilesDirectory == patches && _webFilesLoading == null, "Files could not reopen the fixture for column resizing.");
            Require(!_webFiles!.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == longName),
                "The long filename was visible in the Files auto-fit fixture.");
            VerifyWebColumnSizing(_webFiles, "WebFilesHeaders", "patch-01.txt", column: 2);
            VerifyWebColumnSizing(_webFiles, "WebFilesHeaders", "patch-01.txt", column: 0, minimumFit: 350);
            await VerifyWebTransferWarning(directory);
            await VerifyWebDownloadDirectory(root, directory);
        }
        finally
        {
            await StopWebTransfersForCloseAsync();
            await lifetime.CancelAsync();
            await serve;
        }

        void GoTo(string address)
        {
            _webAddress!.Text = address;
            SubmitWebAddress();
        }
    }

    private async Task VerifyWebDownloadDirectory(string root, string captures)
    {
        var original = WebDownloadDirectory;
        var folder = Directory.CreateDirectory(Path.Combine(WebDataDirectory, "Custom downloads fixture")).FullName;
        await ChooseFolder(folder);
        Require(_store.Load().Preferences.WebDownloadsDirectory == folder, "The new download directory was not saved.");

        Navigate(Page.Web);
        SelectWebView(WebView.Files);
        UpdateLayout();
        var downloads = _webFiles!.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "WebFilesQuickAccessDownloads");
        downloads.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => _webFilesDirectory == folder && _webFilesLoading == null, "The Downloads shortcut did not use the new location.");
        Capture(Path.Combine(captures, "web-files-custom-downloads.png"));

        SelectWebView(WebView.Browser);
        NavigateWebAddress(_activeWebTab!, new Uri(root + "/file"));
        await WaitFor(() => WebTransferQueue.Items.Any(item => item.TargetDirectory == folder &&
            item.Snapshot.OutputPath == Path.Combine(folder, "web-fixture.txt")), "A browser download did not use the new directory.");
        Require(await File.ReadAllTextAsync(Path.Combine(folder, "web-fixture.txt")) == "LG Launcher web download fixture",
            "The browser did not publish the downloaded file into the configured directory.");
        await ChooseFolder(original);
        Require(WebDownloadDirectory == original && _store.Load().Preferences.WebDownloadsDirectory == null,
            "Choosing the original download folder did not restore the default.");

        async Task ChooseFolder(string path)
        {
            Navigate(Page.FileViewerSettings);
            UpdateLayout();
            var trigger = PageBody.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Launcher downloads directory");
            trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => DialogHost.HasModal, "The download directory setting did not open the folder picker.");
            var picker = DialogHost.Children.OfType<ModalDialog>().Single();
            UpdateLayout();
            var address = picker.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "WebFilesPath");
            var selectedPath = picker.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "WebDownloadFolderPath");
            address.Text = path;
            address.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
            var choose = picker.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Use this folder"));
            await WaitFor(() => selectedPath.Text == path && choose.IsEnabled, "The settings folder picker did not reach the requested directory.");
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => WebDownloadDirectory == path, "The download directory setting did not take effect.");
        }
    }

    private async Task VerifyWebTransferWarning(string captures)
    {
        SelectWebView(WebView.Downloads);
        UpdateLayout();
        var input = _webDownloads!.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "WebMagnetInput");
        input.Text = "invalid download address";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
        var warning = _webDownloads.GetVisualDescendants().OfType<Border>().Single(panel => panel.Name == "WebTransferWarning");
        await WaitFor(() => warning.IsVisible && warning.Bounds.Height > 0, "An invalid link did not show a transfer warning.");
        var panel = (Border)warning.Child!;
        Require(Equals(panel.Background, this.FindResource("LG.WarningFill")) &&
            Equals(panel.BorderBrush, this.FindResource("LG.Warning")) &&
            panel.GetVisualDescendants().OfType<SymbolIcon>().Any() &&
            panel.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text?.Contains("magnet", StringComparison.OrdinalIgnoreCase) == true),
            "The transfer warning did not use the common warning colours, icon, and message.");
        await Task.Delay(200);
        Capture(Path.Combine(captures, "web-transfer-warning.png"));
        await WaitFor(() => !warning.IsVisible, "The transfer warning did not dismiss automatically.");
        input.Clear();
    }

    private const int WebFixtureSlowBytes = 512 * 1024;
    private const string WebFixtureSlowName = "slow-fixture.bin";

    private async Task VerifyWebSettings(string captures)
    {
        await VerifySection("Browser", "Privacy & Security", "Search & Home",
            "Enable uBO Lite", "Enable pop-up blocking", "Show single tab");
        await VerifySection("Transfer Manager", "Torrent", null, "Enable DHT");
        await VerifySection("File Viewer", "View", null, "Show hidden files", "Show file extensions");
        Navigate(Page.Home);

        async Task VerifySection(string name, string firstHeading, string? secondHeading, params string[] toggles)
        {
            Navigate(Page.Settings);
            UpdateLayout();
            var entry = PageBody.Children.OfType<Button>().SingleOrDefault(button => AutomationProperties.GetName(button) == name);
            Require(entry != null, $"Settings did not contain {name}.");
            if (name == "Browser")
            {
                var globe = (SymbolIcon)WebNavIcon.Content!;
                var sectionIcon = entry!.GetVisualDescendants().OfType<SymbolIcon>().First();
                Require(sectionIcon.Data == globe.Data, "Browser settings did not use the launcher globe.");
            }
            entry!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UpdateLayout();
            Require(PageTitle.Text == name, $"Settings did not open {name}.");
            var sections = PageBody.GetVisualDescendants().OfType<HeaderedContentControl>().Select(panel => panel.Header).ToArray();
            Require(sections.Contains(firstHeading) && (secondHeading == null || sections.Contains(secondHeading)),
                $"The {name} settings panels were missing.");
            foreach (var label in toggles)
            {
                var toggle = PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
                    .SingleOrDefault(control => AutomationProperties.GetName(control) == label);
                Require(toggle?.IsChecked == true, $"{label} was not on by default.");
            }
            var inputs = PageBody.GetVisualDescendants().OfType<TextBox>().ToArray();
            Require(inputs.All(field => field.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center),
                $"The text in {name} settings fields was not vertically centered.");
            if (name == "Browser")
            {
                var homepage = inputs.Single(field => AutomationProperties.GetName(field) == "Default homepage");
                var custom = inputs.Single(field => AutomationProperties.GetName(field) == "Custom search URL");
                Require(custom.IsEffectivelyVisible && !custom.IsEffectivelyEnabled,
                    "The custom URL field was hidden or enabled by default.");
                homepage.Text = "example.test";
                homepage.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
                Require(_store.Load().Preferences.WebHomepage == "example.test", "The inline homepage did not save.");
                homepage.Clear();
                homepage.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
                var engines = PageBody.GetVisualDescendants().OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "Default search engine");
                Require(engines.ContextMenu!.Items.OfType<MenuItem>().All(item => item.Icon is SymbolIcon { Data: not null }),
                    "Search-engine choices did not show icons.");
                await ChoosePreference(engines, "Custom");
                Require(custom.IsEffectivelyEnabled, "Choosing Custom did not enable the inline search URL.");
                custom.Text = "https://example.test/?q=%s";
                custom.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
                Require(_store.Load().Preferences.WebSearchTemplate == custom.Text, "The custom search URL did not save.");
                custom.Clear();
                custom.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
                await ChoosePreference(engines, "None");
                Require(custom.IsEffectivelyVisible && !custom.IsEffectivelyEnabled, "Choosing None hid or enabled the custom URL.");
            }
            else if (name == "Transfer Manager")
            {
                var bind = PageBody.GetVisualDescendants().OfType<TextBox>()
                    .Single(field => AutomationProperties.GetName(field) == "Bind address");
                bind.Text = "127.0.0.1";
                bind.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
                Require(_store.Load().Preferences.WebTorrentBindAddress == "127.0.0.1", "The inline bind address did not save.");
                bind.Clear();
                bind.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
                var ratio = PageBody.GetVisualDescendants().OfType<NumericUpDown>().Single();
                Require(ratio.Value == 1, "The transfer stop ratio did not default to 1.0 in a number spinner.");
                ratio.Value = 1.5m;
                Require(_store.Load().Preferences.WebTorrentStopRatio == 1.5, "Changing the stop ratio did not autosave.");
                ratio.Value = 1;
            }
            foreach (var (width, height) in new[] { (730, 510), (1600, 900), (1230, 805) })
            {
                Width = width;
                Height = height;
                await WaitFor(() => Math.Abs(ClientSize.Width - width) < 2, "The Web settings window did not resize.");
                UpdateLayout();
                Capture(Path.Combine(captures, $"web-settings-{name.Replace(' ', '-').ToLowerInvariant()}-{width}.png"));
            }
        }
    }

    private void VerifyModalCorners(string captures)
    {
        var preferences = _library.Preferences;
        var originalRadius = preferences.WidgetRadius;
        var content = new Border { Background = Brushes.White };
        var footer = new Border { Background = Brushes.White };
        var dialog = new ModalDialog("", 120, content: content, footerPanel: footer) { Height = 100, BoxShadow = default };
        _ = DialogHost.Show(dialog);
        try
        {
            foreach (int? radius in new int?[] { null, 0, 4, 8, 12, 16, 20 })
            {
                preferences.WidgetRadius = radius;
                ApplyWidgetTheme();
                var setting = radius?.ToString() ?? "default";
                Require(dialog.CornerRadius == new CornerRadius(radius ?? 12), $"The dialog did not use radius {setting}.");
                var path = Path.Combine(captures, $"modal-corners-{setting}.png");
                content.Opacity = footer.Opacity = 0;
                CaptureControl(dialog, path);
                using var frame = SKBitmap.Decode(path);
                content.Opacity = footer.Opacity = 1;
                CaptureControl(dialog, path);
                using var filled = SKBitmap.Decode(path);
                for (var y = 0; y < frame.Height; y++)
                {
                    for (var x = 0; x < frame.Width; x++)
                    {
                        var reference = frame.GetPixel(x, y);
                        var rendered = filled.GetPixel(x, y);
                        var frameEdge = x == 0 || y == 0 || x == frame.Width - 1 || y == frame.Height - 1;
                        var preservesStroke = !frameEdge || reference.Alpha < 224 || rendered.Red <= reference.Red + 16;
                        Require((reference.Alpha != 0 || rendered.Alpha <= 16) && preservesStroke,
                            $"Modal content crossed the frame at radius {setting} ({x}, {y}).");
                    }
                }
            }
        }
        finally
        {
            dialog.Close();
            preferences.WidgetRadius = originalRadius;
            ApplyWidgetTheme();
        }
    }

    private async Task VerifyWebNativePause(string root)
    {
        SelectWebView(WebView.Browser);
        SetWebAddressText(root + "/slow");
        SubmitWebAddress();
        var queue = WebTransferQueue;
        WebTransfer? slow = null;
        await WaitFor(() =>
        {
            slow = queue.Items.SingleOrDefault(transfer => transfer.Snapshot.Name == WebFixtureSlowName);
            return slow?.Snapshot.Progress is { State: WebDownloadState.Active, Received: > 0 };
        }, "The live WebView2 fixture did not begin downloading.");
        SelectWebView(WebView.Downloads);
        UpdateLayout();
        var row = FindWebTableRow(_webDownloads!, WebFixtureSlowName);
        InvokeWebFixtureMenu(await OpenWebFixtureMenu(row), "Stop");
        await WaitFor(() => slow!.Snapshot.Progress.State == WebDownloadState.Stopped && !_webTransferActions.Contains(slow),
            "WebView2 did not pause the running download.");
        InvokeWebFixtureMenu(await OpenWebFixtureMenu(row, new Point(row.Bounds.Width - 3, row.Bounds.Height / 2)), "Start");
        await WaitFor(() => slow!.Snapshot.Progress.State == WebDownloadState.Completed, "WebView2 did not resume and complete the download.");
        var saved = await File.ReadAllBytesAsync(slow!.Snapshot.OutputPath!);
        Require(saved.Length == WebFixtureSlowBytes && saved.All(value => value == 65),
            "The resumed WebView2 download did not contain the expected bytes.");
    }

    private async Task VerifyWebFileActions(string captures)
    {
        var folder = Directory.CreateDirectory(Path.Combine(WebDataDirectory, "File actions fixture")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(folder, "Target")).FullName;
        var original = Path.Combine(folder, "sample.txt");
        var hidden = Path.Combine(folder, "hidden.txt");
        await File.WriteAllTextAsync(original, "File actions fixture");
        await File.WriteAllTextAsync(hidden, "Hidden file fixture");
        await File.WriteAllTextAsync(Path.Combine(folder, "example.exe"), "Executable confirmation fixture");
        await File.WriteAllTextAsync(Path.Combine(folder, "example.bat"), "@echo off");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        await OpenFolder(folder);
        Require(FileRow("hidden.txt").Classes.Contains("web-file-muted"),
            "A hidden file was missing its dim treatment.");
        Require(_webNativeHost?.IsVisible == false && _activeWebTab?.Core?.Settings.IsStatusBarEnabled == false,
            "The native browser or its status popup could still appear over Files.");

        var sample = FileRow("sample.txt");
        var files = _webFilesView!;
        var selected = files.Entries.Items.OfType<WebFileEntry>().Single(entry => entry.FullPath == original);
        files.Entries.SelectedItem = selected;
        UpdateLayout();
        Require(Equals(sample.Background, this.FindResource("LG.WebRowContextTint")),
            "Files did not use the Transfers tint for a selected row.");
        Capture(Path.Combine(captures, "web-files-selection.png"));

        var listing = _webFiles!.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "WebFilesListing");
        Require(PostWebFixtureClick(listing, new Point(32, listing.Bounds.Height - 18), rightClick: false),
            "Could not click empty space in Files.");
        await WaitFor(() => files.Entries.SelectedItem == null && !files.Open.IsEnabled,
            "Clicking empty space did not clear the file selection.");

        files.Entries.SelectedItem = selected;
        files.Entries.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        Require(files.Entries.SelectedItem == null && !files.Open.IsEnabled, "Escape did not clear the file selection.");

        files.Entries.SelectedItem = selected;
        SelectWebView(WebView.Browser);
        SelectWebView(WebView.Files);
        Require(files.Entries.SelectedItem == null, "The selection persisted after leaving Files.");

        files.Entries.SelectedItem = selected;
        Navigate(Page.Home);
        Navigate(Page.Web);
        Require(files.Entries.SelectedItem == null, "The selection persisted after leaving Web.");

        files.Entries.SelectedItem = files.Entries.Items.OfType<WebFileEntry>().Single(entry => entry.FullPath == hidden);
        var rowMenu = await OpenFileMenu(sample, rightClick: true);
        Require(files.Entries.SelectedItem == null, "Right-clicking a row set a persistent Files selection.");
        Require(sample.Classes.Contains("web-menu-open") && Equals(sample.Background, this.FindResource("LG.WebRowContextTint")),
            "The active Files menu did not use the Transfers tint.");
        Capture(Path.Combine(captures, "web-files-item-menu.png"));
        CaptureControl(rowMenu, Path.Combine(captures, "web-files-context-menu.png"));
        InvokeWebFixtureMenu(rowMenu, "Copy");
        await WaitFor(() => !sample.Classes.Contains("web-menu-open"), "The Files row retained its menu tint after dismissal.");
        Require(files.Entries.SelectedItem == null, "A context action left its file selected.");

        await OpenFolder(target);
        InvokeWebFixtureMenu(await OpenListingMenu(rightClick: true), "Paste");
        var copied = Path.Combine(target, "sample.txt");
        await WaitFor(() => File.Exists(copied) && FileOperationSettled(), "Pasting a copy did not create a file.");
        Require(File.Exists(original) && await File.ReadAllTextAsync(copied) == "File actions fixture", "Copy changed the source or its bytes.");

        InvokeWebFixtureMenu(await OpenFileMenu(FileRow("sample.txt")), "Rename");
        await ApplyFileDialog("renamed.txt", "Rename");
        var renamed = Path.Combine(target, "renamed.txt");
        await WaitFor(() => File.Exists(renamed) && FileOperationSettled(), "Rename did not save the new name.");
        var renamedRow = FileRow("renamed.txt");
        InvokeWebFixtureMenu(await OpenFileMenu(renamedRow), "Cut");
        Require(renamedRow.Classes.Contains("web-file-muted"), "A cut file was missing its dim treatment.");

        var listingMenu = await OpenListingMenu();
        CaptureControl(listingMenu, Path.Combine(captures, "web-files-empty-menu.png"));
        InvokeWebFixtureMenu(listingMenu, "Go Back");
        await WaitFor(() => _webFilesDirectory == folder && _webFilesLoading == null, "The background menu did not navigate back.");
        InvokeWebFixtureMenu(await OpenListingMenu(), "Paste");
        var moved = Path.Combine(folder, "renamed.txt");
        await WaitFor(() => File.Exists(moved) && !File.Exists(renamed) && FileOperationSettled(), "Pasting a cut file did not move it.");

        InvokeWebFixtureMenu(await OpenListingMenu(), "New Directory");
        await ApplyFileDialog("Created", "Create");
        await WaitFor(() => Directory.Exists(Path.Combine(folder, "Created")) && FileOperationSettled(),
            "New Directory did not create a folder.");

        var exeMenu = await OpenFileMenu(FileRow("example.exe"));
        InvokeWebFixtureMenu(exeMenu, "Run");
        await WaitFor(() => DialogHost.HasModal, "Run did not request confirmation.");
        Require(DialogHost.Children.OfType<ModalDialog>().Single().Body.Children.OfType<TextBlock>()
            .Any(label => label.Text?.Contains("example.exe", StringComparison.Ordinal) == true),
            "Run did not identify the selected executable.");
        DialogHost.DismissTop();
        Require(files.Entries.SelectedItem == null, "Cancelling a context-menu Run left its file selected.");

        var executable = FileRow("example.exe");
        var pointer = new Pointer(0, PointerType.Mouse, true);
        var position = executable.TranslatePoint(new Point(12, 12), this) ?? default;
        var released = new PointerReleasedEventArgs(executable, pointer, this, position, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left);
        executable.RaiseEvent(new TappedEventArgs(DoubleTappedEvent, released));
        await WaitFor(() => DialogHost.HasModal, "Double-clicking an executable did not request confirmation.");
        DialogHost.DismissTop();

        var batch = FileRow("example.bat");
        Require(batch.GetVisualDescendants().OfType<SymbolIcon>().Any(icon => Equals(icon.Data, this.FindResource("LG.Icon.Settings"))),
            "Batch scripts did not have the cog icon.");
        InvokeWebFixtureMenu(await OpenFileMenu(batch), "Run");
        await WaitFor(() => DialogHost.HasModal, "Run did not request confirmation for a batch script.");
        DialogHost.DismissTop();

        var deletion = await OpenFileMenu(FileRow("renamed.txt"));
        var delete = deletion.Items.OfType<MenuItem>().Single(item => item.Name == "WebFileDelete");
        Require(Equals(delete.Header, "Delete") && !delete.Classes.Contains("destructive"), "Ordinary Delete was styled as destructive.");
        deletion.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.LeftShift, KeyModifiers = KeyModifiers.Shift });
        Require(Equals(delete.Header, "Destroy") && delete.Classes.Contains("destructive"), "Shift did not switch Delete to Destroy.");
        CaptureControl(deletion, Path.Combine(captures, "web-files-destroy-menu.png"));
        InvokeWebFixtureMenu(deletion, "Destroy");
        await WaitFor(() => DialogHost.HasModal, "Destroy did not request confirmation.");
        Require(File.Exists(moved), "Destroy deleted the file before confirmation.");
        ClickWebDialogAction("Destroy");
        await WaitFor(() => !File.Exists(moved) && FileOperationSettled(), "Confirmed Destroy did not delete the fixture file.");

        Navigate(Page.FileViewerSettings);
        UpdateLayout();
        var showHidden = GetPreferenceToggle("Show hidden files");
        var showExtensions = GetPreferenceToggle("Show file extensions");
        showHidden.IsChecked = showExtensions.IsChecked = false;
        var saved = _store.Load().Preferences;
        Require(saved.HideWebHiddenFiles && saved.HideWebFileExtensions, "The Files visibility settings did not persist.");
        Navigate(Page.Web);
        await OpenFolder(folder);
        Require(!_webFilesView!.Entries.Items.OfType<WebFileEntry>().Any(entry => entry.IsHidden),
            "Turning off Show hidden files left hidden entries in the table.");
        _ = FileRow("sample");
        Navigate(Page.FileViewerSettings);
        UpdateLayout();
        GetPreferenceToggle("Show hidden files").IsChecked = GetPreferenceToggle("Show file extensions").IsChecked = true;
        Navigate(Page.Web);
        await OpenFolder(WebDownloadDirectory);

        bool FileOperationSettled() => !_webFileOperationPending && _webFilesLoading == null;

        ToggleSwitch GetPreferenceToggle(string name) => PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(toggle => AutomationProperties.GetName(toggle) == name);

        Border FileRow(string name)
        {
            UpdateLayout();
            return FindWebTableRow(_webFiles!, name);
        }

        async Task OpenFolder(string path)
        {
            ShowWebFiles(path);
            await WaitFor(() => _webFilesDirectory == path && _webFilesLoading == null, "Files did not navigate to " + path);
            UpdateLayout();
        }

        Task<FrostedContextMenu> OpenFileMenu(Border row, bool rightClick = false)
        {
            var owner = row.GetVisualAncestors().OfType<ListBoxItem>().First();
            return OpenWebFixtureMenu(owner, rightClick ? new Point(32, owner.Bounds.Height / 2) : null);
        }

        Task<FrostedContextMenu> OpenListingMenu(bool rightClick = false)
        {
            var listing = _webFiles!.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "WebFilesListing");
            return OpenWebFixtureMenu(listing, rightClick ? new Point(32, listing.Bounds.Height - 18) : null);
        }

        async Task ApplyFileDialog(string name, string action)
        {
            await WaitFor(() => DialogHost.HasModal, $"The {action} action did not prompt for a name.");
            var dialog = DialogHost.Children.OfType<ModalDialog>().Single();
            dialog.Body.GetVisualDescendants().OfType<TextBox>().Single().Text = name;
            ClickWebDialogAction(action);
        }
    }

    private async Task VerifyWebQuickAccess(string patches, string captures)
    {
        var row = FindWebTableRow(_webFiles!, "Patches");
        var target = row.GetVisualAncestors().Prepend(row).OfType<Control>().First(control => control.ContextMenu != null);
        var folderMenu = await OpenWebFixtureMenu(target);
        CaptureControl(folderMenu, Path.Combine(captures, "web-folder-menu.png"));
        InvokeWebFixtureMenu(folderMenu, "Add to Quick Access");

        Button? pin = null;
        await WaitFor(() =>
        {
            pin = _webFiles!.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(button => button.Name == "WebFilesQuickAccessPin");
            return pin != null;
        }, "Pinning the folder did not add it to Quick Access.");
        Require(ReadWebQuickAccessPaths().SequenceEqual([patches], StringComparer.OrdinalIgnoreCase),
            "The Quick Access folder was not saved.");
        Require(!_webFiles!.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "WebFilesDownloads"),
            "Files still had a Downloads toolbar button.");
        pin!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => _webFilesDirectory == patches, "The pin did not open the folder.");
        Capture(Path.Combine(captures, "web-quick-access.png"));

        var pinMenu = await OpenWebFixtureMenu(pin);
        InvokeWebFixtureMenu(pinMenu, "Remove from Quick Access");
        Require(ReadWebQuickAccessPaths().Length == 0, "Removing a Quick Access pin did not persist.");
        var downloads = _webFiles.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "WebFilesQuickAccessDownloads");
        downloads.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => _webFilesDirectory == WebDownloadDirectory, "The default Downloads pin did not open its folder.");
    }

    private async Task VerifyAlwaysOpenMagnet(CoreWebView2 browser, string captures)
    {
        await ClickMagnetLink(browser);
        await WaitFor(() => DialogHost.HasModal, "The Magnet link did not prompt before Always open was enabled.");
        var dialog = DialogHost.Children.OfType<ModalDialog>().Single();
        dialog.Body.Children.OfType<CheckBox>().Single().IsChecked = true;
        dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Add download"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var destination = Directory.CreateDirectory(Path.Combine(WebDataDirectory, "Magnet fixture folder")).FullName;
        var childFolder = Directory.CreateDirectory(Path.Combine(destination, "Subfolder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(destination, "file.txt"), "The picker only shows folders.");
        await VerifyWebFolderPicker(destination, childFolder, Path.Combine(captures, "web-download-folder-picker.png"));
        await WaitFor(() => _webView == WebView.Downloads && _webTransfers?.Items.Count == 1,
            "Confirming the Magnet link and its folder did not queue it.");
        Require(_store.Load().Preferences.AlwaysOpenMagnetLinks, "Confirming Always open did not save the choice to the profile.");
        var transfer = WebTransferQueue.Items.Single();
        Require(transfer.TargetDirectory == childFolder, "The transfer lost its picked destination.");
        await WaitFor(() => transfer.Stage is { } stage && Directory.Exists(stage),
            "The pending torrent did not create a visible partial directory.");
        var partial = transfer.Stage!;
        Require(Path.GetDirectoryName(partial) == childFolder && WebTransfers.IsIncompletePath(partial),
            "The pending torrent did not use a protected partial directory in the chosen destination.");
        var transferRow = FindWebTableRow(_webDownloads!, transfer.Snapshot.Name);
        InvokeWebFixtureMenu(await OpenWebFixtureMenu(transferRow), "Open target folder");
        await WaitFor(() => _webView == WebView.Files && _webFilesDirectory == childFolder, "The transfer menu ignored the picked folder.");
        await WaitFor(() => _webFilesView!.Entries.Items.OfType<WebFileEntry>().Any(entry => entry.FullPath == partial),
            "Files did not show the pending torrent's directory.");
        _webFilesView!.Entries.SelectedItem = _webFilesView.Entries.Items.OfType<WebFileEntry>().Single(entry => entry.FullPath == partial);
        Require(!_webFilesView.Open.IsEnabled, "Files could open a pending torrent's protected directory.");
        Navigate(Page.Home);
        Navigate(Page.Web);
        SelectWebView(WebView.Browser);
        SelectWebView(WebView.Files);
        Require(WebTransferQueue.Items.Contains(transfer) && transfer.Snapshot.Progress.State == WebDownloadState.Active &&
            Directory.Exists(partial), "Moving between launcher views interrupted the pending torrent.");
        ReleaseHistoryButton(forward: false);
        Require(_webView == WebView.Downloads, "The selected torrent folder did not keep Downloads in history.");
        await WebTransferQueue.RemoveAsync(transfer, deleteFiles: false);

        SelectWebView(WebView.Browser);
        await ClickMagnetLink(browser);
        await WaitFor(() => DialogHost.Children.OfType<ModalDialog>().Any(dialog =>
            AutomationProperties.GetName(dialog) == "Choose download folder"), "Always open did not go straight to the folder picker.");
        DialogHost.DismissTop();
        await WaitFor(() => !_confirmingWebMagnet, "The folder picker did not dismiss.");
        Require(WebTransferQueue.Items.Count == 0, "Cancelling the folder picker still queued the torrent.");

        SelectWebView(WebView.Downloads);
        var input = _webDownloads!.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WebMagnetInput");
        input.Text = "magnet:?xt=urn:btih:0123456789012345678901234567890123456789";
        var add = _webDownloads.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WebAddMagnet");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await VerifyWebFolderPicker(destination, childFolder);
        await WaitFor(() => WebTransferQueue.Items.Count == 1, "Adding a Magnet from Downloads did not use the folder picker.");
        Require(WebTransferQueue.Items.Single().TargetDirectory == childFolder, "Downloads ignored the selected folder.");
        await WebTransferQueue.RemoveAsync(WebTransferQueue.Items.Single(), deleteFiles: false);

        Navigate(Page.Behaviour);
        UpdateLayout();
        var preference = PageBody.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(toggle => AutomationProperties.GetName(toggle) == "Always open Magnet links");
        Require(preference.IsChecked == true, "App Behaviour did not reflect the saved Always open choice.");
        preference.IsChecked = false;
        Require(!_store.Load().Preferences.AlwaysOpenMagnetLinks, "The App Behaviour setting did not restore confirmation.");

        Navigate(Page.Web);
        SelectWebView(WebView.Browser);
        await ClickMagnetLink(browser);
        await WaitFor(() => DialogHost.HasModal, "Turning off Always open did not restore the Magnet confirmation.");
        DialogHost.DismissTop();
        await WaitFor(() => !_confirmingWebMagnet, "The restored Magnet confirmation did not dismiss.");
    }

    private async Task VerifyWebFolderPicker(string destination, string childFolder, string? preview = null)
    {
        ModalDialog? picker = null;
        await WaitFor(() =>
        {
            picker = DialogHost.Children.OfType<ModalDialog>().SingleOrDefault(dialog =>
                AutomationProperties.GetName(dialog) == "Choose download folder");
            return picker != null;
        }, "The folder picker did not open.");
        UpdateLayout();
        Require(!picker!.GetVisualDescendants().OfType<TextBlock>().Any(label =>
            label.Text == "Choose download folder" && label.IsEffectivelyVisible), "The folder picker still showed its title.");
        var path = picker.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WebFilesPath");
        var footer = picker.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "WebDownloadFolderFooter");
        var selectedPath = footer.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "WebDownloadFolderPath");
        var cancel = footer.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"));
        var choose = footer.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Use this folder"));
        Require(selectedPath.IsReadOnly && selectedPath.TranslatePoint(default, footer) is { } pathPosition &&
            cancel.TranslatePoint(default, footer) is { } cancelPosition && choose.TranslatePoint(default, footer) is { } choosePosition &&
            pathPosition.X < cancelPosition.X && cancelPosition.X < choosePosition.X, "The picker footer did not place the path before its actions.");
        var height = picker.Bounds.Height;
        Require(height > 0 && Math.Abs(height - picker.Height) < 1, "The folder picker did not have a fixed height.");
        path.Text = destination;
        picker.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WebFilesGo")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var entries = picker.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "WebFilesEntries");
        await WaitFor(() => entries.ItemsSource is WebFileEntry[] { Length: 1 } items &&
            items[0].FullPath == childFolder && items[0].IsFolder, "The picker did not list only subfolders.");
        Require(selectedPath.Text == destination, "The footer did not show the current folder.");
        if (preview != null) Capture(preview);
        entries.SelectedIndex = 0;
        entries.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await WaitFor(() => path.Text == childFolder && selectedPath.Text == childFolder && choose.IsEnabled,
            "The picker footer did not update after navigating into the subfolder.");
        UpdateLayout();
        Require(Math.Abs(picker.Bounds.Height - height) < 1, "The folder picker resized during navigation.");
        choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private async Task VerifyWebTransferActions(string downloaded, string captures)
    {
        const string name = "pending-fixture.zip";
        var queue = WebTransferQueue;
        var path = queue.ReserveBrowserPath(name);
        var partial = path + ".crdownload";
        await File.WriteAllTextAsync(partial, "Partial transfer fixture");
        var state = WebDownloadState.Active;
        var knownSize = true;
        queue.TrackBrowserDownload(path, () => new WebDownloadProgress(256, knownSize ? 1024 : null, state, DownloadRate: 128),
            () => state = WebDownloadState.Cancelled, () => state = WebDownloadState.Stopped, () => state = WebDownloadState.Active);
        var pending = queue.Items.Single(transfer => transfer.Snapshot.Name == name);
        queue.PollBrowserDownloads();

        SelectWebView(WebView.Downloads);
        UpdateLayout();
        var row = FindWebTableRow(_webDownloads!, name);
        Require(!row.GetVisualDescendants().OfType<ProgressBar>().Any(), "The transfer row still showed a progress bar.");
        var headings = _webDownloads!.GetVisualDescendants().OfType<TextBlock>().Select(label => label.Text);
        Require(new[] { "Down Speed", "Up Speed", "ETA", "Ratio" }.All(headings.Contains),
            "The transfer table did not show all four detail columns.");
        Require(!row.GetVisualDescendants().OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Stop transfer"),
            "The transfer row still rendered an inline Stop action.");
        var cells = (Grid)row.Child!;
        var progress = cells.Children.OfType<TextBlock>().Single(cell => Grid.GetColumn(cell) == 3);
        await WaitFor(() => progress.Text == "25%" && cells.Children.OfType<TextBlock>().Any(cell => cell.Text == "128 B/s") &&
            cells.Children.OfType<TextBlock>().Any(cell => cell.Text == "6 s"), "Transfer percentage, speed, and ETA were not shown.");
        Capture(Path.Combine(captures, "web-transfer-progress.png"));
        knownSize = false;
        await WaitFor(() => progress.Text == "—",
            "A transfer with unknown size did not show a dash in the Progress cell.");
        Capture(Path.Combine(captures, "web-transfer-metadata-progress.png"));
        knownSize = true;
        var menu = await OpenWebFixtureMenu(row, new Point(3, row.Bounds.Height / 2));
        Require(menu.Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => item.Header as string)
            .SequenceEqual(["Start", "Stop", "Remove", "Open target folder"]), "The transfer menu did not show all four actions.");
        Require(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Start")).IsEnabled == false,
            "Start was enabled for a running transfer.");
        Require(row.Classes.Contains("web-menu-open") &&
            !menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Remove")).Classes.Contains("destructive"),
            "The transfer row or Remove action used the wrong active styling.");
        CaptureControl(menu, Path.Combine(captures, "web-transfer-menu.png"));
        Capture(Path.Combine(captures, "web-transfer-menu-selection.png"));
        InvokeWebFixtureMenu(menu, "Stop");
        await WaitFor(() => pending.Snapshot.Progress.State == WebDownloadState.Stopped && !_webTransferActions.Contains(pending),
            "Stopping the transfer did not update the queue.");
        Require(File.Exists(partial), "Stopping the transfer discarded its partial file.");
        var scroll = _webDownloads!.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "WebTransferScroll");
        scroll.Offset = new Vector(scroll.Extent.Width, 0);
        InvokeWebFixtureMenu(await OpenWebFixtureMenu(row, new Point(row.Bounds.Width - 3, row.Bounds.Height / 2)), "Start");
        await WaitFor(() => pending.Snapshot.Progress.State == WebDownloadState.Active && !_webTransferActions.Contains(pending),
            "Starting the transfer did not resume it.");

        var metric = cells.Children.OfType<TextBlock>().Single(cell => cell.Text == "128 B/s");
        var metricPoint = metric.TranslatePoint(new Point(metric.Bounds.Width / 2, metric.Bounds.Height / 2), row);
        Require(metricPoint != null, "The download speed cell was outside the row.");
        InvokeWebFixtureMenu(await OpenWebFixtureMenu(row, metricPoint), "Open target folder");
        await WaitFor(() => _webView == WebView.Files && _webFilesDirectory == WebDownloadDirectory,
            "Open target folder did not open the transfer destination in Files.");

        ReleaseHistoryButton(forward: false);
        Require(_webView == WebView.Downloads, "Mouse Back did not return to Downloads after Open target folder.");
        ReleaseHistoryButton(forward: true);
        await WaitFor(() => _webView == WebView.Files && _webFilesDirectory == WebDownloadDirectory && _webFilesLoading == null,
            "Mouse Forward did not reopen the target folder.");
        ReleaseHistoryButton(forward: false);

        InvokeWebFixtureMenu(await OpenWebFixtureMenu(row), "Remove");
        Require(DialogHost.HasModal, "Removing a transfer did not show the confirmation.");
        Capture(Path.Combine(captures, "web-transfer-remove.png"));
        DialogHost.DismissTop();
        await WaitFor(() => !_webTransferActions.Contains(pending), "Cancelling removal did not dismiss.");
        Require(queue.Items.Contains(pending) && File.Exists(partial), "Cancelling removal changed the transfer or its partial file.");
        InvokeWebFixtureMenu(await OpenWebFixtureMenu(row), "Remove");
        ClickWebDialogAction("Remove and delete files");
        await WaitFor(() => !queue.Items.Contains(pending), "The confirmed removal did not clear the transfer.");
        Require(!File.Exists(partial), "The explicit delete action left the fixture partial file.");

        var completed = FindWebTableRow(_webDownloads!, Path.GetFileName(downloaded));
        InvokeWebFixtureMenu(await OpenWebFixtureMenu(completed), "Remove");
        ClickWebDialogAction("Remove");
        await WaitFor(() => !queue.Items.Any(transfer => transfer.Snapshot.OutputPath == downloaded),
            "Removing the completed transfer did not clear its row.");
        Require(File.Exists(downloaded), "Removing the completed transfer without deletion removed its file.");

        const string closingName = "finished-during-remove.txt";
        var closingPath = queue.ReserveBrowserPath(closingName);
        var closingPartial = closingPath + ".crdownload";
        await File.WriteAllTextAsync(closingPartial, "Finished while removing");
        var closingState = WebDownloadState.Active;
        queue.TrackBrowserDownload(closingPath, () => new WebDownloadProgress(23, 23, closingState),
            () => closingState = WebDownloadState.Cancelled, () =>
            {
                File.Move(closingPartial, closingPath);
                closingState = WebDownloadState.Completed;
            });
        var finishing = queue.Items.Single(transfer => transfer.Snapshot.Name == closingName);
        await queue.RemoveAsync(finishing, deleteFiles: false);
        Require(!queue.Items.Contains(finishing) &&
            await File.ReadAllTextAsync(Path.Combine(WebDownloadDirectory, closingName)) == "Finished while removing",
            "A download that finished during Remove was not kept in Downloads.");
    }

    private void ClickWebDialogAction(string action)
    {
        var dialog = DialogHost.GetVisualDescendants().OfType<ModalDialog>().Single();
        dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, action))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private async Task<FrostedContextMenu> OpenWebFixtureMenu(Control target, Point? point = null)
    {
        UpdateLayout();
        Require(target.ContextMenu is FrostedContextMenu, "The web row had no styled context menu.");
        var menu = (FrostedContextMenu)target.ContextMenu!;
        await WaitFor(() => !menu.IsOpen, "The previous web menu did not finish closing.");
        if (point is { } position)
        {
            Require(target.GetVisualAt(position) != null, "The transfer row did not accept a context click in its empty space.");
            Require(PostWebFixtureClick(target, position, rightClick: true), "Could not send a right-click to the Web view.");
        }
        else
        {
            target.RaiseEvent(new ContextRequestedEventArgs());
        }
        await WaitFor(() => menu.IsOpen, "The web row did not open a styled context menu.");
        await WaitFor(() => menu.RevealProgress == 1, "The web row menu did not finish opening.");
        return menu;
    }

    private bool PostWebFixtureClick(Control target, Point position, bool rightClick)
    {
        var handle = TryGetPlatformHandle()!.Handle;
        var screen = target.PointToScreen(position) - this.PointToScreen(default);
        var coordinates = new IntPtr((screen.Y << 16) | (screen.X & 0xffff));
        return PostMessage(handle, 0x0200, IntPtr.Zero, coordinates) &&
            PostMessage(handle, rightClick ? 0x0204u : 0x0201u, new IntPtr(rightClick ? 2 : 1), coordinates) &&
            PostMessage(handle, rightClick ? 0x0205u : 0x0202u, IntPtr.Zero, coordinates);
    }

    private static void InvokeWebFixtureMenu(ContextMenu menu, string action)
    {
        var item = menu.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Header, action));
        Require(item.IsEnabled, $"The {action} action was disabled.");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
    }

    private static Border FindWebTableRow(Control view, string name) =>
        view.GetVisualDescendants().OfType<Border>().Single(panel => panel.Name == "WebTableRow" &&
            panel.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == name));

    private static Grid VerifyWebTable(Control view, string barName, string headerName, string[] titles, string rowName)
    {
        var panels = view.GetVisualDescendants().OfType<Border>().ToArray();
        var bar = panels.SingleOrDefault(panel => panel.Name == barName);
        Require(bar?.Child is Grid && Math.Abs(bar.Bounds.Width - view.Bounds.Width) < 1 &&
            bar.TranslatePoint(default, view) is { Y: 0 },
            $"The {barName} did not sit flush at the top of its view.");
        var addressBar = (Grid)bar!.Child!;
        var address = addressBar.Children.OfType<TextBox>().Single();
        if (barName == "WebTransfersAddressBar")
        {
            Require(address.TranslatePoint(default, bar) is { X: 0 }, "The Downloads field did not meet the left edge.");
        }
        Require(address.CornerRadius == default && Math.Abs(address.Bounds.Height - addressBar.Bounds.Height) < 1 &&
            addressBar.Children.OfType<Button>().All(button => button.Content is SymbolIcon),
            "The table address bar did not use a flush field and symbolic actions.");

        var header = panels.Single(panel => panel.Name == headerName);
        var headerCells = (Grid)header.Child!;
        Require(headerCells.Children.OfType<TextBlock>().Select(label => label.Text).SequenceEqual(titles),
            $"The {headerName} did not show the expected columns.");
        Require(headerCells.Children.OfType<TextBlock>().All(label => label.TextAlignment == Avalonia.Media.TextAlignment.Left),
            $"The {headerName} labels were not left-aligned.");
        var row = FindWebTableRow(view, rowName);
        var cells = (Grid)row.Child!;
        Require(cells.Children.OfType<TextBlock>().All(label => label.TextAlignment == Avalonia.Media.TextAlignment.Left),
            $"The {rowName} cells were not left-aligned.");
        Require(row.BorderThickness.Bottom == 1 &&
            headerCells.ColumnDefinitions.Zip(cells.ColumnDefinitions)
                .All(pair => Math.Abs(pair.First.ActualWidth - pair.Second.ActualWidth) < 1),
            $"The {rowName} cells did not line up with the column headers and row divider.");
        return cells;
    }

    private void VerifyWebColumnSizing(Control view, string headerName, string rowName, int column, double minimumFit = 0)
    {
        UpdateLayout();
        var header = (Grid)view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == headerName).Child!;
        var handle = header.Children.OfType<Border>().Single(border =>
            border.Name == "WebTableResizeHandle" && Grid.GetColumn(border) == column);
        var initialWidth = header.ColumnDefinitions[column].ActualWidth;
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), this)
            ?? throw new InvalidOperationException("The column handle was outside the table.");
        var end = new Point(start.X + 42, start.Y);
        var pointer = new Pointer(0, PointerType.Mouse, true);
        var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        handle.RaiseEvent(new PointerPressedEventArgs(handle, pointer, this, start, 0, pressed, KeyModifiers.None, 1));
        handle.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, handle, pointer, this, end, 0, pressed, KeyModifiers.None));
        handle.RaiseEvent(new PointerReleasedEventArgs(handle, pointer, this, end, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        UpdateLayout();
        var draggedWidth = header.ColumnDefinitions[column].ActualWidth;
        Require(Math.Abs(draggedWidth - initialWidth - 42) < 1, $"The {headerName} divider did not resize when dragged.");
        VerifyRowWidth(draggedWidth);

        handle.RaiseEvent(new PointerPressedEventArgs(handle, pointer, this, end, 0, pressed, KeyModifiers.None, 2));
        UpdateLayout();
        var fittedWidth = header.ColumnDefinitions[column].ActualWidth;
        Require(fittedWidth > minimumFit && fittedWidth < draggedWidth - 1, $"The {headerName} edge did not autofit on double-click.");
        VerifyRowWidth(fittedWidth);

        void VerifyRowWidth(double expected)
        {
            var row = (Grid)FindWebTableRow(view, rowName).Child!;
            Require(!row.Children.OfType<Border>().Any(border => border.Name == "WebTableColumnDivider"),
                $"The {rowName} row had a visible column separator.");
            Require(Math.Abs(row.ColumnDefinitions[column].ActualWidth - expected) < 1,
                $"The {rowName} row did not stay aligned with the resized header.");
        }
    }

    private void SendWebRendererHistoryButton(bool forward)
    {
        const uint extraButtonDown = 0x020B, extraButtonUp = 0x020C;
        var host = IntPtr.Zero;
        EnumChildWindows(TryGetPlatformHandle()!.Handle, (window, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != "Chrome_RenderWidgetHostHWND") return true;

            host = window;
            return false;
        }, IntPtr.Zero);
        Require(host != IntPtr.Zero, "The fixture could not find the native WebView2 renderer window.");
        var button = (forward ? 2 : 1) << 16;
        var keyState = forward ? 0x0040 : 0x0020;
        var point = (32 << 16) | 32;
        Require(PostMessage(host, extraButtonDown, new IntPtr(button | keyState), new IntPtr(point)) &&
            PostMessage(host, extraButtonUp, new IntPtr(button), new IntPtr(point)), "Could not send the mouse button to the renderer.");
    }

    private delegate bool ChildWindowCallback(IntPtr window, IntPtr state);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, ChildWindowCallback callback, IntPtr state);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private async Task VerifyWebBrowserOptions(string root, BrowserTab first, string captures)
    {
        var preferences = _library.Preferences;
        var browser = first.Core!;
        preferences.HideWebSingleTab = true;
        UpdateWebTabStripVisibility();
        Require(_webTabBar?.IsVisible == false, "The tab strip did not hide with one tab.");
        var additional = AddWebTab(focusAddress: false, useHomepage: false)!;
        Require(_webTabBar?.IsVisible == true, "The tab strip did not reappear with two tabs.");
        CloseWebTab(additional);
        Require(_webTabBar?.IsVisible == false, "Closing the extra tab did not hide the strip again.");
        preferences.HideWebSingleTab = false;
        UpdateWebTabStripVisibility();
        Require(_webTabBar?.IsVisible == true, "The tab strip did not show with the preference enabled.");
        Require(!browser.Settings.IsStatusBarEnabled, "The native browser hover overlay was not disabled.");
        Require(WebAddress.ParseInput("two words", "Google", null)?.Query == "?q=two%20words" &&
            WebAddress.ParseInput("two words", "DDG-NOAI", null)?.Host == "noai.duckduckgo.com" &&
            WebAddress.ParseInput("two words", null, null) == null &&
            WebAddress.ParseInput("example.org", "DDG", null)?.Host == "example.org" &&
            !WebAddress.IsSearchTemplate("file:///local/%s"), "The browser did not distinguish search from navigation.");

        try
        {
            preferences.WebHomepage = root + "/two";
            var home = AddWebTab(focusAddress: false)!;
            await WaitFor(() => home.Core?.DocumentTitle == "Web fixture two", "A new tab did not open the default homepage.");
            preferences.WebSearchEngine = "Custom";
            preferences.WebSearchTemplate = root + "/one?q=%s";
            ApplyWebBrowserPreferences();
            _webAddress!.Text = "space & punctuation?";
            SubmitWebAddress();
            await WaitFor(() => home.Core?.Source == root + "/one?q=space%20%26%20punctuation%3F",
                "Custom search did not navigate with an encoded query.");
            CloseWebTab(home);
            first.Select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            preferences.DisableWebAdBlocker = true;
            ApplyWebBrowserPreferences();
            await WaitFor(() => _webAdBlockEnabled == false, "The running browser did not disable uBO Lite.");
            preferences.DisableWebAdBlocker = false;
            ApplyWebBrowserPreferences();
            await WaitFor(() => _webAdBlockEnabled == true, "The running browser did not restore uBO Lite.");

            var count = _webTabs.Count;
            browser.Navigate(root + "/auto-popup?blocked");
            await WaitFor(() => browser.DocumentTitle == "Popup attempted", "The fixture did not try to open a pop-up.");
            Require(_webTabs.Count == count, "An automatic pop-up opened with pop-up blocking enabled.");
            preferences.DisableWebPopupBlocking = true;
            browser.Navigate(root + "/auto-popup?allowed");
            await WaitFor(() => _webTabs.Count == count + 1 && _activeWebTab?.Core?.DocumentTitle == "Web fixture two",
                "Disabling pop-up blocking did not open the pop-up in a launcher tab.");
            CloseWebTab(_activeWebTab!);
            first.Select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            browser.Navigate(root + "/one");
            await WaitFor(() => browser.DocumentTitle == "Web fixture one", "The browser did not return from the pop-up fixture.");
            UpdateLayout();
            var host = _webNativeHost!;
            var rendererSize = host.Bounds.Size;
            var bottom = new Point(0, rendererSize.Height);
            Require(host.TranslatePoint(bottom, _webBrowser!) is { Y: var edge } &&
                Math.Abs(edge - _webBrowser!.Bounds.Height) < 1, "The renderer left a gap at the bottom of Browser.");
            var position = await browser.ExecuteScriptAsync(
                "(() => { const r = document.querySelector('a').getBoundingClientRect(); return [r.x + 8, r.y + 6]; })()");
            var point = JsonSerializer.Deserialize<double[]>(position)!;
            await browser.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type = "mouseMoved", x = point[0], y = point[1] }));
            await WaitFor(() => _webLinkPreview?.Text?.EndsWith("/two", StringComparison.Ordinal) == true,
                "The link preview did not show the hovered link in Browser.");
            var popup = _webLinkPreviewPopup!;
            await WaitFor(() => popup.IsOpen && popup.Child?.Bounds.Height > 0, "The link preview overlay did not open.");
            UpdateLayout();
            var preview = popup.Child!;
            var previewBottom = preview.PointToScreen(new Point(0, preview.Bounds.Height));
            var hostBottom = host.PointToScreen(bottom);
            Require(!popup.IsUsingOverlayLayer && !popup.IsHitTestVisible && host.Bounds.Size == rendererSize &&
                previewBottom.X == hostBottom.X && Math.Abs(previewBottom.Y - hostBottom.Y) < 4,
                "The link preview was not a click-through overlay at the renderer's bottom edge.");
            CaptureControl(preview, Path.Combine(captures, "web-link-preview.png"));
            SelectWebView(WebView.Files);
            Require(_webLinkPreviewPopup?.IsOpen == false && _webLinkPreview?.Text == null,
                "The link preview remained visible when switching to Files.");
            SelectWebView(WebView.Browser);
        }
        finally
        {
            preferences.WebHomepage = preferences.WebSearchEngine = preferences.WebSearchTemplate = null;
            preferences.DisableWebAdBlocker = preferences.DisableWebPopupBlocking = false;
            ApplyWebBrowserPreferences();
        }
    }

    private static async Task VerifyWebAdBlocking(CoreWebView2 browser)
    {
        const string redirectedAd = "https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?launcher-fixture";
        const string blockedAd = "https://ad.doubleclick.net/ddm/clk/launcher-fixture";
        await browser.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        await SetOffline(true);
        var requests = browser.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        var failures = browser.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        string? redirectedRequest = null;
        string? blockedRequest = null;
        string? failure = null;
        var redirectedToExtension = false;
        requests.DevToolsProtocolEventReceived += Request;
        failures.DevToolsProtocolEventReceived += Failed;
        try
        {
            await browser.ExecuteScriptAsync("for (const url of " + JsonSerializer.Serialize(new[] { redirectedAd, blockedAd }) +
                ") document.head.appendChild(Object.assign(document.createElement('script'), { src: url }));");
            await WaitFor(() => redirectedToExtension && failure != null, "The browser did not report uBO Lite handling both ad requests.");
            Require(failure == "net::ERR_BLOCKED_BY_CLIENT", "uBO Lite did not block the ad request: " + failure);
        }
        finally
        {
            requests.DevToolsProtocolEventReceived -= Request;
            failures.DevToolsProtocolEventReceived -= Failed;
            await SetOffline(false);
            await browser.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
        }

        Task<string> SetOffline(bool offline) => browser.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions",
            JsonSerializer.Serialize(new { offline, latency = 0, downloadThroughput = -1, uploadThroughput = -1 }));

        void Request(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            using var payload = JsonDocument.Parse(args.ParameterObjectAsJson);
            var value = payload.RootElement;
            var request = value.GetProperty("request").GetProperty("url").GetString();
            var requestId = value.GetProperty("requestId").GetString();
            if (request == redirectedAd) redirectedRequest = requestId;
            else if (request == blockedAd) blockedRequest = requestId;
            else if (redirectedRequest != null && requestId == redirectedRequest &&
                Uri.TryCreate(request, UriKind.Absolute, out var redirected) && redirected.Scheme == "chrome-extension" &&
                redirected.AbsolutePath == "/web_accessible_resources/googlesyndication_adsbygoogle.js")
            {
                redirectedToExtension = true;
            }
        }

        void Failed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            using var payload = JsonDocument.Parse(args.ParameterObjectAsJson);
            var value = payload.RootElement;
            if (blockedRequest != null && value.GetProperty("requestId").GetString() == blockedRequest)
            {
                failure = value.GetProperty("errorText").GetString();
            }
        }
    }

    private static async Task ClickMagnetLink(CoreWebView2 browser)
    {
        var result = await browser.ExecuteScriptAsync(
            "(() => { const r = document.getElementById('magnet').getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2]; })()");
        var point = JsonSerializer.Deserialize<double[]>(result)!;
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
        {
            var input = JsonSerializer.Serialize(new { type, x = point[0], y = point[1], button = "left", clickCount = 1 });
            await browser.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", input);
        }
    }

    private static async Task ServeSlowWebFixture(Stream stream, int rangeStart, CancellationToken token)
    {
        rangeStart = Math.Clamp(rangeStart, 0, WebFixtureSlowBytes - 1);
        var status = rangeStart == 0 ? "200 OK" : "206 Partial Content";
        var contentRange = rangeStart == 0 ? "" : $"Content-Range: bytes {rangeStart}-{WebFixtureSlowBytes - 1}/{WebFixtureSlowBytes}\r\n";
        var remaining = WebFixtureSlowBytes - rangeStart;
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nAccept-Ranges: bytes\r\nETag: \"lg-fixture\"\r\n" +
            $"Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename={WebFixtureSlowName}\r\n" +
            $"{contentRange}Content-Length: {remaining}\r\nConnection: close\r\n\r\n");
        var block = new byte[8192];
        Array.Fill(block, (byte)65);
        try
        {
            await stream.WriteAsync(headers, token).ConfigureAwait(false);
            while (remaining > 0)
            {
                var count = Math.Min(remaining, block.Length);
                await stream.WriteAsync(block.AsMemory(0, count), token).ConfigureAwait(false);
                remaining -= count;
                await Task.Delay(45, token).ConfigureAwait(false);
            }
        }
        catch (IOException) when (!token.IsCancellationRequested)
        {
            // Pausing may close the original HTTP response before the range request arrives.
        }
    }

    private static async Task ServeWebFixture(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                using var stream = client.GetStream();
                using var request = new StreamReader(stream, leaveOpen: true);
                var line = await request.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null) continue;
                var rangeStart = 0;
                string? header;
                do
                {
                    header = await request.ReadLineAsync(token).ConfigureAwait(false);
                    const string rangePrefix = "Range: bytes=";
                    if (header?.StartsWith(rangePrefix, StringComparison.OrdinalIgnoreCase) == true)
                        int.TryParse(header[rangePrefix.Length..].Split('-', 2)[0], out rangeStart);
                }
                while (!string.IsNullOrEmpty(header));

                var target = line.Split(' ').ElementAtOrDefault(1);
                if (target == "/slow")
                {
                    await ServeSlowWebFixture(stream, rangeStart, token).ConfigureAwait(false);
                    continue;
                }
                if (target == "/redirect")
                {
                    await stream.WriteAsync("HTTP/1.1 302 Found\r\nLocation: /one\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token)
                        .ConfigureAwait(false);
                    continue;
                }
                var downloadName = target switch { "/file" => "web-fixture.txt", "/notes" => "release-notes.md", _ => null };
                var download = downloadName != null;
                var title = target == "/two" ? "Web fixture two" : "Web fixture one";
                var content = download
                    ? "LG Launcher web download fixture"
                    : $"<!doctype html><title>{title}</title><style>body{{background:#151929;color:#f1f2fc;font:16px Segoe UI;padding:32px}} a{{color:#bfb0ff}}</style><h1>{title}</h1><p><a href=/two>Open page two</a></p><p><a href=/file>Download the fixture</a></p><p><a id=magnet href='magnet:?xt=urn:btih:0123456789012345678901234567890123456789'>Open Magnet link</a></p>";
                if (target?.StartsWith("/auto-popup?", StringComparison.Ordinal) == true)
                    content = "<!doctype html><title>Automatic pop-up</title><script>setTimeout(() => { window.open('/two'); document.title = 'Popup attempted'; }, 0);</script>";
                var payload = Encoding.UTF8.GetBytes(content);
                var type = download ? "application/octet-stream" : "text/html; charset=utf-8";
                var disposition = download ? $"Content-Disposition: attachment; filename={downloadName}\r\n" : "";
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\n{disposition}Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                try
                {
                    await stream.WriteAsync(headers, token).ConfigureAwait(false);
                    await stream.WriteAsync(payload, token).ConfigureAwait(false);
                }
                catch (IOException) when (!token.IsCancellationRequested)
                {
                    // WebView2 may abandon a favicon or a superseded navigation.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
