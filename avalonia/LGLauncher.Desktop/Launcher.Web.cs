using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private enum WebView { Browser, Downloads, Files }

    private static string WebDataDirectory => Path.Combine(Program.DataDirectory, "web");
    private static string DefaultWebDownloadDirectory => Path.Combine(WebDataDirectory, "Downloads");
    private string WebDownloadDirectory => _library.Preferences.WebDownloadsDirectory is { } path && Path.IsPathFullyQualified(path)
        ? path : DefaultWebDownloadDirectory;
    private ContentControl? _webContent;
    private Control? _webBrowser;
    private Control? _webDownloads;
    private Control? _webFiles;
    private Button[] _webSections = [];
    private WebView _webView;
    private bool _stoppingWebTransfers;
    private bool _webTransfersStopped;

    private void RenderWebWorkspace()
    {
        if (_webContent != null) return;

        _webSections =
        [
            WebIconButton("Browser", "LG.Icon.ExternalLink", () => SelectWebView(WebView.Browser), "globe"),
            WebIconButton("Downloads", "LG.Icon.Download", () => SelectWebView(WebView.Downloads)),
            WebIconButton("Files", "LG.Icon.FolderOpen", () => ShowWebFiles()),
        ];
        var tools = new StackPanel { Spacing = 3, Margin = new Thickness(5, 5, 5, 0) };
        foreach (var button in _webSections)
        {
            tools.Children.Add(button);
        }
        var rail = WebChromePanel(tools, "LG.Sidebar", new Thickness(1, 0, 0, 0));
        rail.Name = "WebToolsRail";

        _webContent = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var workspace = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Children = { _webContent, rail },
        };
        Grid.SetColumn(rail, 1);
        WebWorkspaceHost.Content = workspace;
        SelectWebView(WebView.Browser);
    }

    private Border WebChromePanel(Control content, string background, Thickness borderThickness)
    {
        var panel = new Border { Child = content, BorderThickness = borderThickness };
        panel.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LG.Line"));
        panel.Bind(Border.BackgroundProperty, this.GetResourceObservable(background));
        return panel;
    }

    private Border WebTableAddressBar(string name, TextBox address, Button[] leading, Button[] trailing)
    {
        address.CornerRadius = default;
        address.BorderThickness = new Thickness(leading.Length == 0 ? 0 : 1, 0, 1, 0);
        address.Padding = new Thickness(10, 0);
        address.VerticalAlignment = VerticalAlignment.Stretch;
        address.VerticalContentAlignment = VerticalAlignment.Center;

        var bar = new Grid { ColumnSpacing = 3, Margin = new Thickness(leading.Length == 0 ? 0 : 5, 0, 5, 0) };
        Control[] controls = [.. leading, address, .. trailing];
        for (var column = 0; column < controls.Length; column++)
        {
            var control = controls[column];
            bar.ColumnDefinitions.Add(new ColumnDefinition(control == address ? GridLength.Star : GridLength.Auto));
            Grid.SetColumn(control, column);
            bar.Children.Add(control);
        }

        var chrome = WebChromePanel(bar, "LG.Sidebar", new Thickness(0, 0, 0, 1));
        chrome.Name = name;
        return chrome;
    }

    private const int WebTableHeaderHeight = 32;
    private const int WebTableSidePadding = 10;
    private const int WebTableTitleInset = 24;

    private Border WebTableHeaders(string name, WebTableColumns columns, string[] titles,
        Func<int, IEnumerable<string?>> values)
    {
        var labels = titles.Select(title =>
        {
            var label = WebTableCell(title);
            label.FontWeight = FontWeight.SemiBold;
            return label;
        }).ToArray();
        labels[0].Margin = new Thickness(WebTableTitleInset, 0, 0, 0);

        var cells = WebTableCells(columns, labels);
        for (var column = 0; column < labels.Length; column++)
        {
            var index = column;
            var label = labels[column];
            var handle = new Border
            {
                Name = "WebTableResizeHandle",
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
            };
            Grid.SetColumn(handle, column);
            AutomationProperties.SetName(handle, $"Resize {label.Text} column");
            AttachWebTableResize(handle, () => cells.ColumnDefinitions[index].ActualWidth, Resize, Fit);
            cells.Children.Add(handle);

            void Resize(double width) => columns.SetWidth(index, Math.Max(32, width));

            double Fit() => FitWebTableColumn(label, values(index), index == 0 ? FontSize : label.FontSize);
        }

        var header = WebChromePanel(cells, "LG.Surface", new Thickness(0, 0, 0, 1));
        header.Name = name;
        header.MinHeight = WebTableHeaderHeight;
        return header;
    }

    private void AttachWebTableResize(Border handle, Func<double> currentWidth, Action<double> resize, Func<double> fit)
    {
        IPointer? pointer = null;
        double startX = 0;
        double startWidth = 0;
        var dragging = false;
        handle.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            args.Handled = true;
            if (args.ClickCount == 2)
            {
                resize(fit());
                return;
            }

            pointer = args.Pointer;
            startX = args.GetPosition(ShellSurface).X;
            startWidth = currentWidth();
            dragging = false;
            args.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, args) =>
        {
            if (args.Pointer != pointer) return;
            var delta = args.GetPosition(ShellSurface).X - startX;
            if (!dragging && Math.Abs(delta) < 1) return;
            dragging = true;
            resize(startWidth + delta);
            args.Handled = true;
        };
        handle.PointerReleased += (_, args) =>
        {
            if (args.Pointer != pointer || args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            args.Pointer.Capture(null);
            args.Handled = true;
        };
        handle.PointerCaptureLost += (_, _) => pointer = null;
    }

    private static double FitWebTableColumn(TextBlock header, IEnumerable<string?> values, double cellFontSize)
    {
        var culture = CultureInfo.CurrentCulture;
        var titleTypeface = new Typeface(header.FontFamily, header.FontStyle, header.FontWeight, header.FontStretch);
        var cellTypeface = new Typeface(header.FontFamily, header.FontStyle, FontWeight.Normal, header.FontStretch);
        var width = Measure(header.Text, titleTypeface, header.FontSize);
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            width = Math.Max(width, Measure(value, cellTypeface, cellFontSize));
        }
        return Math.Ceiling(width + header.Margin.Left + header.Margin.Right + 1);

        double Measure(string? text, Typeface typeface, double fontSize) => string.IsNullOrEmpty(text) ? 0 :
            new FormattedText(text, culture, header.FlowDirection, typeface, fontSize, Brushes.Black)
                .WidthIncludingTrailingWhitespace;
    }

    private Border WebTableRow(WebTableColumns columns, params Control[] cells)
    {
        var row = new Border
        {
            Name = "WebTableRow",
            Classes = { "web-table-row" },
            BorderThickness = new Thickness(0, 0, 0, 1),
            MinHeight = 36,
            Child = WebTableCells(columns, cells),
        };
        row.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LG.Line"));
        return row;
    }

    private static Grid WebTableCells(WebTableColumns columns, Control[] cells)
    {
        var row = new Grid
        {
            ColumnDefinitions = columns.CreateDefinitions(),
            Margin = new Thickness(WebTableSidePadding, 0),
        };
        for (var column = 0; column < cells.Length; column++)
        {
            var cell = cells[column];
            var margin = cell.Margin;
            cell.Margin = new Thickness(margin.Left + (column == 0 ? 0 : 4), margin.Top, margin.Right + 4, margin.Bottom);
            Grid.SetColumn(cell, column);
            row.Children.Add(cell);
        }
        return row;
    }

    private sealed class WebTableColumns(params double[] widths)
    {
        private readonly ColumnDefinition[] _widths = widths.Select(width => new ColumnDefinition(new GridLength(width))).ToArray();
        private readonly double _minimumNameWidth = widths[0];
        private bool _stretchName = true;
        private double _viewportWidth;

        public ColumnDefinitions CreateDefinitions()
        {
            var definitions = new ColumnDefinitions();
            foreach (var width in _widths)
            {
                var definition = new ColumnDefinition();
                definition.Bind(ColumnDefinition.WidthProperty, width.GetObservable(ColumnDefinition.WidthProperty));
                definitions.Add(definition);
            }
            return definitions;
        }

        public void SetWidth(int column, double width)
        {
            if (column == 0) _stretchName = false;
            _widths[column].Width = new GridLength(width);
        }

        public void SetViewportWidth(double width)
        {
            width = Math.Floor(width);
            if (!_stretchName || _viewportWidth == width) return;
            _viewportWidth = width;
            var detailsWidth = _widths.Skip(1).Sum(column => column.Width.Value);
            _widths[0].Width = new GridLength(Math.Max(_minimumNameWidth, width - detailsWidth - 2 * WebTableSidePadding));
        }
    }

    private static TextBlock WebTableCell(string text, bool muted = true)
    {
        var cell = Label(text, muted);
        cell.TextAlignment = TextAlignment.Left;
        cell.TextWrapping = TextWrapping.NoWrap;
        cell.TextTrimming = TextTrimming.CharacterEllipsis;
        cell.VerticalAlignment = VerticalAlignment.Center;
        return cell;
    }

    private static Grid WebTableName(Control icon, TextBlock name)
    {
        var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*"), ColumnSpacing = WebTableTitleInset - 16, Children = { icon, name } };
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        return cell;
    }

    private void SelectWebView(WebView view)
    {
        if (_webContent == null) return;

        if (_webView == WebView.Files && view != WebView.Files) ClearWebFileSelection();
        _webView = view;
        for (var index = 0; index < _webSections.Length; index++)
        {
            _webSections[index].Classes.Set("selected", index == (int)view);
        }

        var panel = view switch
        {
            WebView.Browser => _webBrowser ??= BuildWebBrowser(),
            WebView.Downloads => _webDownloads ??= BuildWebTransfers(),
            _ => _webFiles ??= BuildWebFiles(),
        };
        // Keep the browser attached so background tabs and in-progress downloads retain their controller.
        if (_webContent.Content is not Grid panels)
        {
            panels = new Grid { ClipToBounds = true };
            _webContent.Content = panels;
        }
        var changed = !panels.Children.Contains(panel) || !panel.IsVisible;
        if (!panels.Children.Contains(panel))
        {
            panels.Children.Add(panel);
        }
        foreach (var child in panels.Children)
        {
            if (child != panel && child.IsVisible) CancelMotion(WebAnimationTarget(child));
            child.IsVisible = child == panel;
        }
        UpdateWebBrowserVisibility();
        if (changed) AnimateView(WebAnimationTarget(panel));
    }

    private Control WebAnimationTarget(Control panel) => panel == _webBrowser ? _webBrowserChrome ?? panel : panel;

    private void AnimateWebWorkspace()
    {
        if (_webContent?.Content is Grid panels && panels.Children.FirstOrDefault(child => child.IsVisible) is { } panel)
            AnimateView(WebAnimationTarget(panel));
    }

    private void ShowWebFiles(string? path = null, bool fromTransfer = false)
    {
        if (_closed) return;
        if (fromTransfer && _webView == WebView.Downloads && path != null)
        {
            RememberWebFileLocation(null);
            RememberWebFileLocation(path);
            SelectWebView(WebView.Files);
            SetWebFileLocation(path, rememberLocation: false);
            return;
        }

        SelectWebView(WebView.Files);
        if (path != null) SetWebFileLocation(path);
        else if (WebFileHistoryAtDownloads || _webFilesLocation == null && _webFilesLoading == null)
            SetWebFileLocation(_webFilesLocation);
        else RefreshWebFiles();
    }

    private bool HandleWebKey(KeyEventArgs args)
    {
        if (_page != Page.Web || _webView != WebView.Browser || args.KeyModifiers != KeyModifiers.Control)
        {
            return false;
        }

        switch (args.Key)
        {
            case Key.L:
                FocusWebAddress();
                return true;
            case Key.T:
                AddWebTab();
                return true;
            case Key.W when _activeWebTab != null:
                CloseWebTab(_activeWebTab);
                return true;
            default:
                return false;
        }
    }

    private bool DeferCloseForWebTransfers(WindowClosingEventArgs args)
    {
        if (_webTransfers == null || _webTransfersStopped) return false;

        args.Cancel = true;
        if (!_stoppingWebTransfers)
        {
            _stoppingWebTransfers = true;
            ShellSurface.IsEnabled = false;
            UpdateWebBrowserVisibility();
            _ = StopWebTransfersAndClose();
        }
        return true;
    }

    private async Task StopWebTransfersAndClose()
    {
        await StopWebTransfersForCloseAsync();
        _webTransfersStopped = true;
        if (!_closed) Dispatcher.UIThread.Post(Close);
    }

    private bool IsWebDownloadIncomplete(string path)
    {
        return IsWebFileStagingPath(path) || WebTransfers.IsIncompletePath(path) ||
            path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".!qb", StringComparison.OrdinalIgnoreCase);
    }
}
