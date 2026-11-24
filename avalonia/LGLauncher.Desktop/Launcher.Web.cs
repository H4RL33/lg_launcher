using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private enum WebView { Browser, Downloads, Files }

    private static string WebDataDirectory => Path.Combine(Program.DataDirectory, "web");
    private string WebDownloadDirectory => Path.Combine(WebDataDirectory, "Downloads");
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

    private void SelectWebView(WebView view)
    {
        if (_webContent == null) return;

        _webView = view;
        for (var index = 0; index < _webSections.Length; index++)
        {
            _webSections[index].Classes.Set("selected", index == (int)view);
        }

        var panel = view switch
        {
            WebView.Browser => _webBrowser ??= BuildWebBrowser(),
            WebView.Downloads => _webDownloads ??= new Border { Child = BuildWebTransfers(), Padding = new Thickness(16) },
            _ => _webFiles ??= new Border { Child = BuildWebFiles(), Padding = new Thickness(16) },
        };
        // Keep the browser attached so background tabs and in-progress downloads retain their controller.
        if (_webContent.Content is not Grid panels)
        {
            panels = new Grid();
            _webContent.Content = panels;
        }
        if (!panels.Children.Contains(panel))
        {
            panels.Children.Add(panel);
        }
        foreach (var child in panels.Children)
        {
            child.IsVisible = child == panel;
        }
        UpdateWebBrowserVisibility();
    }

    private void ShowWebFiles(string? path = null)
    {
        if (_closed) return;
        SelectWebView(WebView.Files);
        if (path != null) SetWebFileLocation(path);
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
        return IsWebFileStagingPath(path, WebDownloadDirectory) ||
            path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".!qb", StringComparison.OrdinalIgnoreCase);
    }
}
