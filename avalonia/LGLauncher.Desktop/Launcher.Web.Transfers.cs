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

namespace LGLauncher;

internal sealed partial class Launcher
{
    private WebTransfers? _webTransfers;
    private DispatcherTimer? _webTransferTimer;
    private Action? _refreshWebTransferPage;

    private WebTransfers WebTransferQueue
    {
        get
        {
            if (_webTransfers != null) return _webTransfers;

            var transfers = new WebTransfers(WebDownloadDirectory);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) =>
            {
                transfers.PollBrowserDownloads();
                _refreshWebTransferPage?.Invoke();
                if (!transfers.Items.Any(item => item.Snapshot.Progress.State == WebDownloadState.Active)) timer.Stop();
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
            PlaceholderText = "Paste a magnet link",
            MaxLength = 8192
        };
        AutomationProperties.SetName(link, "Magnet link");
        var notice = Label("", muted: true);
        notice.Name = "WebMagnetNotice";
        notice.IsVisible = false;
        AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Polite);

        async void AddMagnet()
        {
            try
            {
                await transfers.AddMagnetAsync(link.Text ?? string.Empty);
                link.Clear();
                notice.IsVisible = false;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                notice.Text = error.Message;
                notice.IsVisible = true;
            }
        }

        var add = ActionButton("Add magnet", AddMagnet, "primary");
        add.Name = "WebAddMagnet";
        link.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            AddMagnet();
            args.Handled = true;
        };
        var input = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
            Children = { link, add }
        };
        Grid.SetColumn(add, 1);
        add.VerticalAlignment = VerticalAlignment.Center;

        var empty = Label("No downloads this session.", muted: true);
        var list = new StackPanel { Name = "WebTransferList", Spacing = 10, Children = { empty } };
        var rows = new Dictionary<WebTransfer, WebTransferRow>();

        void Refresh()
        {
            empty.IsVisible = transfers.Items.Count == 0;
            foreach (var transfer in transfers.Items)
            {
                if (!rows.TryGetValue(transfer, out var row))
                {
                    row = BuildWebTransferRow(transfer);
                    rows.Add(transfer, row);
                    list.Children.Insert(0, row.Panel);
                }
                var snapshot = transfer.Snapshot;
                var progress = snapshot.Progress;
                row.Name.Text = snapshot.Name;
                row.Status.Text = DescribeWebProgress(progress, transfer.CanCancel);
                row.Progress.IsVisible = progress.State == WebDownloadState.Active;
                row.Progress.IsIndeterminate = row.Progress.IsVisible && progress.Total is not > 0 && _motion;
                row.Progress.Value = progress.Total is > 0
                    ? Math.Clamp(100d * progress.Received / progress.Total.Value, 0d, 100d)
                    : 0;
                row.Cancel.IsVisible = progress.State == WebDownloadState.Active;
                row.Cancel.IsEnabled = transfer.CanCancel;
                row.Open.IsVisible = snapshot.OutputPath != null;
            }
        }

        var panel = new StackPanel
        {
            Name = "WebDownloadsPage",
            Spacing = 14,
            Children =
            {
                Section("Magnet link", new StackPanel { Spacing = 7, Children = { input, notice } }),
                Section("Transfers", list)
            }
        };
        panel.AttachedToVisualTree += (_, _) =>
        {
            _refreshWebTransferPage = Refresh;
            Refresh();
        };
        panel.DetachedFromVisualTree += (_, _) =>
        {
            if (_refreshWebTransferPage == Refresh) _refreshWebTransferPage = null;
        };
        Refresh();
        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private WebTransferRow BuildWebTransferRow(WebTransfer transfer)
    {
        var name = Label("");
        name.FontWeight = FontWeight.SemiBold;
        var status = Label("", muted: true);
        var progress = new ProgressBar { Classes = { "lg-progress" } };
        AutomationProperties.SetName(progress, "Download progress");
        var cancel = ActionButton("Cancel", () =>
        {
            try
            {
                transfer.RequestCancel();
                _refreshWebTransferPage?.Invoke();
            }
            catch (Exception error) when (error is InvalidOperationException or COMException)
            {
                SetWebBrowserStatus(error.Message);
            }
        }, "quiet");
        var open = ActionButton("Open in Files", () =>
        {
            if (transfer.Snapshot.OutputPath is { } path)
                ShowWebFiles(path);
        });
        var actions = Actions(cancel, open);
        Grid.SetColumn(actions, 1);
        var labels = new StackPanel { Spacing = 4, Children = { name, status } };
        var details = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 10,
            Children = { labels, actions }
        };
        var panel = new Border
        {
            Classes = { "lg-panel" },
            Padding = new Thickness(12),
            Child = new StackPanel { Spacing = 9, Children = { details, progress } }
        };
        return new(panel, name, status, progress, cancel, open);
    }

    private static string DescribeWebProgress(WebDownloadProgress progress, bool canCancel)
    {
        var phase = progress.State switch
        {
            WebDownloadState.Completed => "Completed",
            WebDownloadState.Cancelled => "Cancelled",
            WebDownloadState.Failed => "Failed",
            _ when !canCancel => "Cancelling…",
            _ when progress.Total == null && progress.Received == 0 && !string.IsNullOrWhiteSpace(progress.Detail) => progress.Detail,
            _ => "Downloading"
        };
        if (progress.State is WebDownloadState.Active or WebDownloadState.Completed)
        {
            if (progress.Total is > 0 && progress.State == WebDownloadState.Active)
                phase += $" · {FormatWebTransferSize(progress.Received)} of {FormatWebTransferSize(progress.Total.Value)}";
            else if (progress.Received > 0 || progress.State == WebDownloadState.Completed)
                phase += $" · {FormatWebTransferSize(progress.Received)}";
        }

        return (canCancel || progress.State == WebDownloadState.Failed) && !string.IsNullOrWhiteSpace(progress.Detail) && phase != progress.Detail
            ? $"{phase} · {progress.Detail}"
            : phase;
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

    private sealed record WebTransferRow(Border Panel, TextBlock Name, TextBlock Status, ProgressBar Progress, Button Cancel, Button Open);
}
