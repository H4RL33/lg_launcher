using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LGLauncher.Components;
using Microsoft.Web.WebView2.Core;

namespace LGLauncher;

internal sealed partial class Launcher
{
    internal async Task VerifyWeb(string preview)
    {
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
            _webBack!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => first.Core?.DocumentTitle == "Web fixture one" && _webForward?.IsEnabled == true,
                "The browser did not go back to the previous page.");

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
            Capture(Path.Combine(directory, "web-magnet-confirmation.png"));
            DialogHost.DismissTop();
            await WaitFor(() => !_confirmingWebMagnet, "The Magnet confirmation did not dismiss.");
            Require(_webTransfers == null, "Cancelling the Magnet confirmation created a transfer.");

            ToggleWebBookmarks();
            await ClickMagnetLink(first.Core!);
            await WaitFor(() => DialogHost.HasModal, "Clicking a Magnet link in a web page did not open the launcher confirmation.");
            DialogHost.DismissTop();
            await WaitFor(() => !_confirmingWebMagnet, "The browser Magnet confirmation did not dismiss.");
            GoTo(root + "/file");
            await WaitFor(() => _webTransfers?.Items.Any(item => item.Snapshot.OutputPath != null) == true,
                "The WebView2 download was not published by the Downloads manager.");
            var downloaded = WebTransferQueue.Items.Single().Snapshot.OutputPath!;
            Require(await File.ReadAllTextAsync(downloaded) == "LG Launcher web download fixture",
                "The completed download contents did not match the server response.");
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
                Capture(Path.Combine(directory, $"web-downloads-{width}.png"));
                var open = _webDownloads!.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.IsVisible && Equals(button.Content, "Open in Files"));
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ListBox? files = null;
                await WaitFor(() =>
                {
                    files = _webFiles?.GetVisualDescendants().OfType<ListBox>()
                        .SingleOrDefault(control => control.Name == "WebFilesEntries");
                    return files?.SelectedItem != null;
                }, "Open in Files did not select the completed download.");
                Require(_webView == WebView.Files && files!.Bounds.Width > 400,
                    "The built-in Files view did not fill the workspace.");
                Capture(Path.Combine(directory, $"web-files-{width}.png"));
            }

            SelectWebView(WebView.Browser);
            Width = 730;
            Height = 510;
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
                string? header;
                do
                {
                    header = await request.ReadLineAsync(token).ConfigureAwait(false);
                }
                while (!string.IsNullOrEmpty(header));

                var target = line.Split(' ').ElementAtOrDefault(1);
                if (target == "/redirect")
                {
                    await stream.WriteAsync("HTTP/1.1 302 Found\r\nLocation: /one\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token)
                        .ConfigureAwait(false);
                    continue;
                }
                var download = target == "/file";
                var title = target == "/two" ? "Web fixture two" : "Web fixture one";
                var content = download
                    ? "LG Launcher web download fixture"
                    : $"<!doctype html><title>{title}</title><style>body{{background:#151929;color:#f1f2fc;font:16px Segoe UI;padding:32px}} a{{color:#bfb0ff}}</style><h1>{title}</h1><p><a href=/two>Open page two</a></p><p><a href=/file>Download the fixture</a></p><p><a id=magnet href='magnet:?xt=urn:btih:0123456789012345678901234567890123456789'>Open Magnet link</a></p>";
                var payload = Encoding.UTF8.GetBytes(content);
                var type = download ? "application/octet-stream" : "text/html; charset=utf-8";
                var disposition = download ? "Content-Disposition: attachment; filename=web-fixture.txt\r\n" : "";
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
