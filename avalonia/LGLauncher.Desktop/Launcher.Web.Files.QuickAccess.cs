using System.ComponentModel;
using System.Security;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private const string WebQuickAccessFileName = "quick-access.json";
    private static readonly StringComparer WebQuickAccessPathComparer = StringComparer.OrdinalIgnoreCase;

    private string[]? _webQuickAccessPaths;
    private StackPanel? _webQuickAccessRows;

    private Border BuildWebQuickAccess(Action<string>? pickFolder = null)
    {
        try
        {
            _webQuickAccessPaths = ReadWebQuickAccessPaths();
        }
        catch (Exception error) when (IsWebQuickAccessFailure(error))
        {
            Status($"Could not load Quick Access. {error.Message}");
        }

        var heading = WebTableCell("Quick Access");
        heading.Classes.Add("sidebar-heading");
        heading.Margin = new Thickness(4, 0, 4, 0);
        var rows = new StackPanel { Spacing = 2 };
        if (pickFolder == null)
        {
            _webQuickAccessRows = rows;
            RefreshWebQuickAccessRows();
        }
        else
        {
            PopulateWebQuickAccess(rows, pickFolder, editable: false);
            foreach (var drive in DriveInfo.GetDrives())
            {
                rows.Children.Add(WebQuickAccessButton(drive.Name, drive.Name, pickFolder, isDefault: false, editable: false));
            }
        }
        var shortcuts = new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(shortcuts, 1);
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("24,*"),
            Margin = new Thickness(6, 4),
            Children = { heading, shortcuts }
        };
        var panel = WebChromePanel(content, "LG.Surface", new Thickness(0, 0, 1, 0));
        panel.Name = "WebFilesQuickAccess";
        return panel;
    }

    private void ApplyWebDownloadDirectory(string previous)
    {
        if (WebQuickAccessPathComparer.Equals(previous, WebDownloadDirectory)) return;

        RefreshWebQuickAccessRows();
        if (WebQuickAccessPathComparer.Equals(previous, _webFilesRequestedLocation ?? _webFilesLocation))
        {
            SetWebFileLocation(null);
        }
    }

    private void RefreshWebQuickAccessRows()
    {
        if (_webQuickAccessRows is not { } rows) return;
        PopulateWebQuickAccess(rows, path => SetWebFileLocation(path), editable: true);
        UpdateWebQuickAccessSelection();
    }

    private void PopulateWebQuickAccess(StackPanel rows, Action<string> navigate, bool editable)
    {
        rows.Children.Clear();
        var downloads = WebDownloadDirectory;
        rows.Children.Add(WebQuickAccessButton(downloads, "Downloads", navigate, isDefault: true, editable));
        foreach (var path in _webQuickAccessPaths ?? [])
        {
            if (WebQuickAccessPathComparer.Equals(path, downloads)) continue;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            rows.Children.Add(WebQuickAccessButton(path, String.IsNullOrEmpty(name) ? path : name, navigate, isDefault: false, editable));
        }
    }

    private Button WebQuickAccessButton(string path, string label, Action<string> navigate, bool isDefault, bool editable)
    {
        var button = ActionButton(label, () => navigate(path), "quiet");
        button.Name = isDefault ? "WebFilesQuickAccessDownloads" : "WebFilesQuickAccessPin";
        button.Tag = path;
        button.Classes.Add("nav");
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(3, 0);
        button.MinHeight = 32;
        var content = WebTableName(Symbol(isDefault ? "LG.Icon.Download" : "LG.Icon.FolderOpen", 16), WebTableCell(label, muted: false));
        content.ColumnSpacing = 4;
        button.Content = content;
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, path);
        if (editable && !isDefault)
        {
            var menu = LauncherMenus.Create();
            menu.AddAction("Remove from Quick Access", () => RemoveWebQuickAccessPath(path), "LG.Icon.Close");
            button.ContextMenu = menu;
        }
        return button;
    }

    private void UpdateWebQuickAccessSelection()
    {
        if (_webQuickAccessRows is not { } rows) return;
        foreach (var button in rows.Children.OfType<Button>())
        {
            button.Classes.Set("selected", WebQuickAccessPathComparer.Equals(button.Tag as string, _webFilesDirectory));
        }
    }

    private void AddWebFolderQuickAccessAction(ContextMenu menu, string path)
    {
        var add = menu.AddAction("Add to Quick Access", () => _ = AddWebQuickAccessPath(path), "LG.Icon.Plus");
        menu.Opening += (_, _) =>
        {
            add.IsEnabled = !WebTransfers.IsIncompletePath(path) &&
                !WebQuickAccessPathComparer.Equals(path, WebDownloadDirectory) &&
                !(_webQuickAccessPaths ?? []).Contains(path, WebQuickAccessPathComparer);
        };
    }

    private async Task AddWebQuickAccessPath(string path)
    {
        var downloads = WebDownloadDirectory;
        try
        {
            var folder = await Task.Run(() =>
            {
                var resolved = ResolveWebFilePath(path, downloads);
                if (!File.GetAttributes(resolved).HasFlag(FileAttributes.Directory))
                {
                    throw new DirectoryNotFoundException("Choose an available folder.");
                }
                var defaultFolder = Directory.Exists(downloads) ? ResolveWebFilePath(downloads, downloads) : downloads;
                return WebQuickAccessPathComparer.Equals(resolved, defaultFolder) ? null : resolved;
            });
            if (_closed || folder == null || WebQuickAccessPathComparer.Equals(folder, WebDownloadDirectory)) return;

            var pins = _webQuickAccessPaths ?? ReadWebQuickAccessPaths();
            if (pins.Contains(folder, WebQuickAccessPathComparer)) return;
            SaveWebQuickAccessPaths([.. pins, folder]);
        }
        catch (Exception error) when (IsWebQuickAccessFailure(error))
        {
            if (!_closed) await Notice("Could not add to Quick Access", error is WebFileInProgressException
                ? "This download is still in progress."
                : error.Message);
        }
    }

    private void RemoveWebQuickAccessPath(string path)
    {
        try
        {
            if (_webQuickAccessPaths is not { } pins) return;
            SaveWebQuickAccessPaths(pins.Where(pin => !WebQuickAccessPathComparer.Equals(pin, path)).ToArray());
        }
        catch (Exception error) when (IsWebQuickAccessFailure(error))
        {
            _ = Notice("Could not remove from Quick Access", error.Message);
        }
    }

    private string[] ReadWebQuickAccessPaths()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(WebDataDirectory, WebQuickAccessFileName));
            return (JsonSerializer.Deserialize<string?[]>(json) ?? [])
                .OfType<string>()
                .Where(path => Path.IsPathFullyQualified(path) && !IsWebFileStagingPath(path) &&
                    !WebTransfers.IsIncompletePath(path))
                .Distinct(WebQuickAccessPathComparer)
                .ToArray();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private void SaveWebQuickAccessPaths(string[] pins)
    {
        Directory.CreateDirectory(WebDataDirectory);
        var destination = Path.Combine(WebDataDirectory, WebQuickAccessFileName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(pins));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        _webQuickAccessPaths = pins;
        RefreshWebQuickAccessRows();
    }

    private static bool IsWebQuickAccessFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            JsonException or Win32Exception or SecurityException;
}
