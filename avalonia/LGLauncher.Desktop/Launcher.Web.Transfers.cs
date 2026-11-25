using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private WebTransfers? _webTransfers;
    private DispatcherTimer? _webTransferTimer;
    private Action? _refreshWebTransferPage;
    private readonly HashSet<WebTransfer> _webTransferActions = [];

    private WebTransfers WebTransferQueue
    {
        get
        {
            if (_webTransfers != null) return _webTransfers;

            var transfers = new WebTransfers(DefaultWebDownloadDirectory, WebDataDirectory, () =>
            {
                var preferences = _library.Preferences;
                return (!preferences.DisableWebDht, preferences.WebTorrentBindAddress, preferences.WebTorrentProtocol);
            });
            transfers.SetStopRatio(_library.Preferences.WebTorrentStopRatio ?? WebTransfers.DefaultStopRatio);
            string? lastFilesDirectory = null;
            var lastFilesPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool RefreshTransferFiles()
            {
                if (_page != Page.Web || _webView != WebView.Files || _webFilesDirectory is not { } directory)
                {
                    lastFilesDirectory = null;
                    lastFilesPaths.Clear();
                    return true;
                }
                if (_webFilesLoading != null) return false;

                var paths = transfers.Items.Where(transfer => StringComparer.OrdinalIgnoreCase.Equals(transfer.TargetDirectory, directory))
                    .Select(transfer => transfer.Snapshot.OutputPath ?? transfer.Stage).OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!StringComparer.OrdinalIgnoreCase.Equals(lastFilesDirectory, directory)) lastFilesPaths.Clear();
                if (!lastFilesPaths.SetEquals(paths)) RefreshWebFiles();
                lastFilesDirectory = directory;
                lastFilesPaths = paths;
                return true;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) =>
            {
                transfers.PollBrowserDownloads();
                _refreshWebTransferPage?.Invoke();
                if (RefreshTransferFiles() && !transfers.Items.Any(item => item.Snapshot.Progress.State == WebDownloadState.Active))
                    timer.Stop();
            };
            transfers.Added += () =>
            {
                _refreshWebTransferPage?.Invoke();
                timer.Start();
            };
            _webTransfers = transfers;
            _webTransferTimer = timer;
            return transfers;
        }
    }

    private Control BuildWebTransfers()
    {
        var transfers = WebTransferQueue;
        var link = new TextBox
        {
            Name = "WebMagnetInput",
            Classes = { "lg" },
            PlaceholderText = "Paste a magnet link or .torrent URL",
            MaxLength = 8192
        };
        AutomationProperties.SetName(link, "Magnet link or .torrent URL");
        var warningText = Label("");
        warningText.Name = "WebMagnetNotice";
        warningText.TextWrapping = TextWrapping.Wrap;
        warningText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("LG.Warning"));
        AutomationProperties.SetLiveSetting(warningText, AutomationLiveSetting.Polite);

        var warningIcon = Symbol("LG.Icon.Warning");
        warningIcon.VerticalAlignment = VerticalAlignment.Top;
        warningIcon.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("LG.Warning"));
        var warningContents = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            Children = { warningIcon, warningText },
        };
        Grid.SetColumn(warningText, 1);
        var warningPanel = new Border
        {
            Child = warningContents,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10, 6),
            Padding = new Thickness(10, 7),
        };
        warningPanel.Bind(Border.BackgroundProperty, this.GetResourceObservable("LG.WarningFill"));
        warningPanel.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LG.Warning"));
        warningPanel.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("LG.PanelRadius"));
        var notice = new Border
        {
            Name = "WebTransferWarning",
            ClipToBounds = true,
            Child = warningPanel,
            IsVisible = false,
        };
        var warningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        warningTimer.Tick += (_, _) => HideWarning();

        void HideWarning()
        {
            warningTimer.Stop();
            if (!notice.IsVisible) return;

            var height = notice.Bounds.Height;
            if (!CanAnimate(notice) || height <= 0)
            {
                CancelMotion(notice);
                notice.IsVisible = false;
                return;
            }
            AnimateMotion(notice, async token =>
            {
                await ViewMotion.RevealFromTop(notice, MaxHeightProperty, height, 0, token);
                if (!token.IsCancellationRequested) notice.IsVisible = false;
            });
        }

        void ShowWarning(string message)
        {
            warningTimer.Stop();
            CancelMotion(notice);
            var previousHeight = notice.IsVisible ? notice.Bounds.Height : 0;
            warningText.Text = message;
            notice.ClearValue(MaxHeightProperty);
            notice.IsVisible = true;
            if (CanAnimate(notice))
            {
                var width = (notice.Parent as Control)?.Bounds.Width ?? Bounds.Width;
                notice.Measure(new Size(width, double.PositiveInfinity));
                var targetHeight = notice.DesiredSize.Height;
                notice.MaxHeight = previousHeight;
                AnimateMotion(notice, async token =>
                {
                    await ViewMotion.RevealFromTop(notice, MaxHeightProperty, previousHeight, targetHeight, token);
                    if (!token.IsCancellationRequested) notice.ClearValue(MaxHeightProperty);
                });
            }
            warningTimer.Start();
        }

        var adding = false;
        async void AddTransfer()
        {
            if (adding) return;
            adding = true;
            try
            {
                var address = (link.Text ?? string.Empty).Trim();
                WebTransfers.ValidateLink(address);
                var target = await PickWebDownloadFolder();
                if (target == null || _closed) return;

                await transfers.AddLinkAsync(address, target);
                if (_closed) return;

                link.Clear();
                HideWarning();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or
                UnauthorizedAccessException or TimeoutException or FormatException or MonoTorrent.TorrentException)
            {
                if (!_closed) ShowWarning(error.Message);
            }
            finally
            {
                adding = false;
            }
        }

        var add = WebIconButton("Add transfer", "LG.Icon.Plus", AddTransfer);
        add.Name = "WebAddMagnet";
        link.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            AddTransfer();
            args.Handled = true;
        };
        var input = WebTableAddressBar("WebTransfersAddressBar", link, [], [add]);
        var empty = Label("No downloads this session.", muted: true);
        empty.Margin = new Thickness(10, 8);
        var list = new StackPanel { Name = "WebTransferList", Children = { empty } };
        var rows = new Dictionary<WebTransfer, WebTransferRow>();
        var columns = new WebTableColumns(125, 73, 102, 76, 79, 87, 78, 70, 56);
        var headers = WebTableHeaders("WebTransferHeaders", columns,
            ["Name", "Source", "Status", "Progress", "Size", "Down Speed", "Up Speed", "ETA", "Ratio"],
            column => rows.Values.Select(row => row.ColumnText(column)));

        void Refresh()
        {
            empty.IsVisible = transfers.Items.Count == 0;
            foreach (var removed in rows.Keys.Except(transfers.Items).ToArray())
            {
                list.Children.Remove(rows[removed].Panel);
                rows.Remove(removed);
            }
            foreach (var transfer in transfers.Items)
            {
                if (!rows.TryGetValue(transfer, out var row))
                {
                    row = BuildWebTransferRow(transfer, columns);
                    rows.Add(transfer, row);
                    list.Children.Insert(0, row.Panel);
                }
                var snapshot = transfer.Snapshot;
                var progress = snapshot.Progress;
                var percent = progress.State == WebDownloadState.Completed ? 100d : progress.Total is > 0
                    ? Math.Clamp(100d * progress.Received / progress.Total.Value, 0d, 100d)
                    : (double?)null;
                row.Name.Text = snapshot.Name;
                ToolTip.SetTip(row.Name, snapshot.Name);
                row.Status.Text = DescribeWebTransferState(progress);
                ToolTip.SetTip(row.Status, progress.Detail ?? row.Status.Text);
                AutomationProperties.SetHelpText(row.Status, progress.Detail ?? "");
                row.Percent.Text = percent is { } knownPercent ? $"{knownPercent:0}%" : "—";
                var displayedSize = progress.Total ?? (progress.State == WebDownloadState.Completed ? progress.Received : null);
                row.Size.Text = displayedSize is { } total ? FormatWebTransferSize(total) : "—";
                ToolTip.SetTip(row.Size, progress.Total is { } size
                    ? $"{FormatWebTransferSize(progress.Received)} of {FormatWebTransferSize(size)} received"
                    : $"{FormatWebTransferSize(progress.Received)} received");
                var active = progress.State == WebDownloadState.Active;
                row.Down.Text = active ? FormatWebTransferRate(progress.DownloadRate) : "—";
                row.Up.Text = active ? FormatWebTransferRate(progress.UploadRate) : "—";
                row.Eta.Text = FormatWebTransferEta(progress);
                row.Ratio.Text = progress.Ratio?.ToString("0.00") ?? "—";
            }
        }

        var table = new StackPanel { Children = { headers, list } };
        var scroll = new ScrollViewer
        {
            Name = "WebTransferScroll",
            Content = table,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        scroll.ScrollChanged += (_, _) => columns.SetViewportWidth(scroll.Viewport.Width);
        var panel = new Grid
        {
            Name = "WebDownloadsPage",
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children = { input, notice, scroll },
        };
        Grid.SetRow(notice, 1);
        Grid.SetRow(scroll, 2);
        panel.AttachedToVisualTree += (_, _) =>
        {
            _refreshWebTransferPage = Refresh;
            Refresh();
        };
        panel.DetachedFromVisualTree += (_, _) =>
        {
            if (_refreshWebTransferPage == Refresh) _refreshWebTransferPage = null;
            warningTimer.Stop();
            CancelMotion(notice);
            notice.IsVisible = false;
        };
        Refresh();
        return panel;
    }

    private WebTransferRow BuildWebTransferRow(WebTransfer transfer, WebTableColumns columns)
    {
        var name = WebTableCell("", muted: false);
        var torrent = transfer.TorrentHash != null;
        var icon = Symbol("LG.Icon.Download", 16);
        if (torrent) icon.Data = LucideIconCatalog.Find("magnet")?.Geometry ?? icon.Data;
        var source = WebTableCell(torrent ? "BitTorrent" : "Browser");
        var status = WebTableCell("");
        var percent = WebTableCell("");
        var size = WebTableCell("");
        var down = WebTableCell("");
        var up = WebTableCell("");
        var eta = WebTableCell("");
        var ratio = WebTableCell("");
        var panel = WebTableRow(columns, WebTableName(icon, name), source, status, percent, size, down, up, eta, ratio);
        var menu = BuildWebTransferMenu(transfer);
        menu.Opened += (_, _) => panel.Classes.Add("web-menu-open");
        menu.Closed += (_, _) => panel.Classes.Remove("web-menu-open");
        panel.ContextMenu = menu;
        return new(panel, name, status, percent, size, down, up, eta, ratio);
    }

    private ContextMenu BuildWebTransferMenu(WebTransfer transfer)
    {
        var menu = LauncherMenus.Create();
        var start = menu.AddAction("Start", () => _ = ChangeWebTransfer(transfer, transfer.RequestStartAsync), "LG.Icon.Play");
        var stop = menu.AddAction("Stop", () => _ = ChangeWebTransfer(transfer, transfer.RequestStopAsync));
        if (LucideIconCatalog.Find("square") is { } square)
            stop.Icon = new SymbolIcon { Data = square.Geometry, Width = 16, Height = 16 };
        var remove = menu.AddAction("Remove", () => _ = ChangeWebTransfer(transfer, () => ConfirmWebTransferRemoval(transfer)),
            "LG.Icon.Delete");
        menu.AddSeparator();
        menu.AddAction("Open target folder", () =>
            ShowWebFiles(transfer.TargetDirectory, fromTransfer: true), "LG.Icon.FolderOpen");
        var open = menu.AddAction("Open in Files", () =>
        {
            if (transfer.Snapshot.OutputPath is { } path) ShowWebFiles(path, fromTransfer: true);
        }, "LG.Icon.FolderOpen");
        menu.Opening += (_, _) =>
        {
            var busy = _webTransferActions.Contains(transfer);
            start.IsEnabled = !busy && transfer.CanStart;
            stop.IsEnabled = !busy && transfer.CanStop;
            remove.IsEnabled = !busy;
            open.IsVisible = transfer.Snapshot.OutputPath != null;
        };
        return menu;
    }

    private async Task ConfirmWebTransferRemoval(WebTransfer transfer)
    {
        var deleteFiles = false;
        var dialog = new ModalDialog("Remove transfer?", 510);
        var snapshot = transfer.Snapshot;
        dialog.Body.Children.Add(Label($"Remove “{snapshot.Name}” from Downloads?", muted: true));
        if (snapshot.OutputPath == null)
        {
            var retained = Path.Combine(transfer.TargetDirectory, WebTransfers.StagingDirectoryName, "retained");
            dialog.Body.Children.Add(Label($"Remove keeps partial data in {retained}.", muted: true));
        }
        _ = DialogActions(dialog, "Remove", () => dialog.Close(true), defaultAction: false);
        dialog.Footer.Children.Add(ActionButton("Remove and delete files", () =>
        {
            deleteFiles = true;
            dialog.Close(true);
        }, "destructive"));
        if (await ShowModal(dialog) && !_closed)
            await WebTransferQueue.RemoveAsync(transfer, deleteFiles);
    }

    private async Task ChangeWebTransfer(WebTransfer transfer, Func<Task> action)
    {
        if (!_webTransferActions.Add(transfer)) return;
        _refreshWebTransferPage?.Invoke();
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            COMException or TimeoutException or MonoTorrent.TorrentException)
        {
            if (!_closed) await Notice("Could not change transfer", error.Message);
        }
        finally
        {
            _webTransferActions.Remove(transfer);
            if (!_closed)
            {
                WebTransferQueue.PollBrowserDownloads();
                _refreshWebTransferPage?.Invoke();
                if (WebTransferQueue.Items.Any(item => item.Snapshot.Progress.State == WebDownloadState.Active))
                    _webTransferTimer?.Start();
            }
        }
    }

    private static string DescribeWebTransferState(WebDownloadProgress progress) => progress.State switch
    {
        WebDownloadState.Completed => "Completed",
        WebDownloadState.Cancelled => "Cancelled",
        WebDownloadState.Failed => "Failed",
        WebDownloadState.Stopped => "Stopped",
        _ when progress is { Total: null, Detail: not null } => "Finding metadata",
        _ => progress.Detail ?? "Downloading",
    };

    private static string FormatWebTransferRate(long? rate) => rate is { } bytes ? $"{FormatWebTransferSize(bytes)}/s" : "—";

    private static string FormatWebTransferEta(WebDownloadProgress progress)
    {
        if (progress.State != WebDownloadState.Active || progress.DownloadRate is not > 0 ||
            progress.Total is not { } total || total <= progress.Received) return "—";

        var seconds = Math.Ceiling((total - progress.Received) / (double)progress.DownloadRate.Value);
        return seconds switch
        {
            >= 86400 => $"{Math.Floor(seconds / 86400):0} d",
            >= 3600 => $"{Math.Floor(seconds / 3600):0} h {Math.Floor(seconds % 3600 / 60):0} m",
            >= 60 => $"{Math.Floor(seconds / 60):0} m {seconds % 60:0} s",
            _ => $"{seconds:0} s",
        };
    }

    private static string FormatWebTransferSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} TiB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KiB",
        _ => $"{bytes} B"
    };

    private async Task StopWebTransfersForCloseAsync()
    {
        _webTransferTimer?.Stop();
        _refreshWebTransferPage = null;
        try
        {
            if (_webTransfers is { } transfers) await transfers.StopAsync();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Could not stop web transfers: {0}", error.Message);
        }
    }

    private void DisposeWebTransfers() => _ = StopWebTransfersForCloseAsync();

    private sealed record WebTransferRow(Border Panel, TextBlock Name, TextBlock Status, TextBlock Percent, TextBlock Size,
        TextBlock Down, TextBlock Up, TextBlock Eta, TextBlock Ratio)
    {
        public string? ColumnText(int column) => column == 0 ? Name.Text :
            (Panel.Child as Grid)?.Children.OfType<TextBlock>().FirstOrDefault(cell => Grid.GetColumn(cell) == column)?.Text;
    }
}
