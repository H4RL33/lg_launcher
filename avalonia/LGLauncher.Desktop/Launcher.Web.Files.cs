using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LGLauncher.Components;
using Microsoft.Win32.SafeHandles;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly HashSet<string> WebShellProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ahk", ".appinstaller", ".application", ".appref-ms", ".appx", ".appxbundle", ".cmd", ".com", ".cpl",
        ".hta", ".inf", ".ins", ".isp", ".jar", ".jnlp", ".js", ".jse", ".lnk", ".msc", ".msi", ".msix",
        ".msixbundle", ".msp", ".mst", ".pif", ".pl", ".ps1", ".py", ".pyw", ".rb", ".reg", ".scf", ".scr",
        ".sct", ".sh", ".shb", ".shs", ".url", ".vb", ".vbe", ".vbs", ".website", ".ws", ".wsc", ".wsf", ".wsh"
    };

    private sealed record WebFileEntry(string FullPath, string Name, bool IsFolder, long? Size = null, DateTime? ModifiedUtc = null,
        bool IsHidden = false)
    {
        public string Extension => Path.GetExtension(Name.TrimEnd(' ', '.'));
        public bool IsBatchFile => !IsFolder && Extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        public bool IsExecutable => IsBatchFile || (!IsFolder && Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record WebFileListing(string? Folder, WebFileEntry[] Entries, string? SelectedFile, string Message, bool Readable = false);
    private sealed record WebFilesView(Grid Surface, Grid Listing, TextBox Path, ListBox Entries, TextBlock Message,
        TextBlock SelectionMessage, Button Up, Button Open);
    private sealed class WebFileInProgressException : IOException;

    private sealed class WebFileList : ListBox
    {
        protected override Type StyleKeyOverride => typeof(ListBox);

        protected override bool ShouldTriggerSelection(Visual selectable, PointerEventArgs args) =>
            args.Properties.PointerUpdateKind is not (PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased) &&
            base.ShouldTriggerSelection(selectable, args);
    }

    private WebFilesView? _webFilesView;
    private CancellationTokenSource? _webFilesLoading;
    private string? _webFilesLocation;
    private string? _webFilesRequestedLocation;
    private string? _webFilesDirectory;
    // A null entry represents Downloads, the source of a transfer-to-Files jump.
    private readonly List<string?> _webFilesHistory = [];
    private int _webFilesHistoryIndex = -1;
    private bool WebFileHistoryAtDownloads => _webFilesHistoryIndex >= 0 && _webFilesHistory[_webFilesHistoryIndex] == null;

    private Control BuildWebFiles() => (_webFilesView ??= CreateWebFilesView()).Surface;

    private void ClearWebFileSelection()
    {
        if (_webFilesView is { } view) view.Entries.SelectedItem = null;
    }

    private WebFilesView CreateWebFilesView(Action<string>? pickFolder = null, Func<string?>? pickerDirectory = null,
        Action? refreshPicker = null)
    {
        var picking = pickFolder != null;
        Action<string> navigate = pickFolder ?? (location => SetWebFileLocation(location));
        var pathDescription = picking ? "Folder path" : "Folder or file path";
        var path = TextField(null, pathDescription);
        path.Name = "WebFilesPath";
        path.PlaceholderText = pathDescription;
        path.UseTextEditingMenu();

        var up = WebIconButton("Parent folder", "LG.Icon.ChevronUp", () =>
        {
            var folder = picking ? pickerDirectory?.Invoke() : _webFilesDirectory;
            if (folder != null && Path.GetDirectoryName(folder) is { } parent)
            {
                if (picking) navigate(parent);
                else SetWebFileLocation(parent, direction: -1);
            }
        }, "arrow-up");
        up.Name = "WebFilesUp";
        var go = WebIconButton("Go to path", "LG.Icon.Chevron", () => navigate(path.Text ?? ""), "arrow-right");
        go.Name = "WebFilesGo";
        path.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None) return;
            args.Handled = true;
            navigate(path.Text ?? "");
        };
        var refresh = WebIconButton("Refresh folder", "LG.Icon.Refresh", refreshPicker ?? RefreshWebFiles);
        refresh.Name = "WebFilesRefresh";
        var open = WebIconButton("Open file", "LG.Icon.ExternalLink", () =>
        {
            if (_webFilesView is { } view) _ = OpenWebFile(view);
        });
        open.Name = "WebFilesOpen";
        open.IsEnabled = false;
        var address = WebTableAddressBar("WebFilesAddressBar", path, [up, refresh], picking ? [go] : [go, open]);
        var selectionMessage = Label("This download is still in progress.", muted: true);
        selectionMessage.IsVisible = false;
        selectionMessage.Margin = new Thickness(10, 6);
        Grid.SetRow(selectionMessage, 0);
        var columns = new WebTableColumns(88, 68, 58, 106);
        var entries = new WebFileList
        {
            Name = "WebFilesEntries",
            Classes = { "lg", "web-file-list" },
            AutoScrollToSelectedItem = true,
            ItemTemplate = new FuncDataTemplate<WebFileEntry>((entry, _) => entry == null ? null : BuildWebFileRow(entry, columns, picking))
        };
        var headers = WebTableHeaders("WebFilesHeaders", columns, ["Name", "Type", "Size", "Modified"],
            column => entries.Items.OfType<WebFileEntry>().Select(entry => column switch
            {
                0 => WebFileDisplayName(entry, picking),
                1 => DescribeWebFileType(entry),
                2 => DescribeWebFileSize(entry),
                _ => FormatWebFileDate(entry.ModifiedUtc?.ToLocalTime()),
            }));
        var headerViewport = new Canvas { Height = WebTableHeaderHeight, ClipToBounds = true, Children = { headers } };
        headerViewport.Bind(BackgroundProperty, this.GetResourceObservable("LG.Surface"));
        Grid.SetRow(headerViewport, 1);

        AutomationProperties.SetName(entries, "Files and folders");
        ScrollViewer.SetHorizontalScrollBarVisibility(entries, ScrollBarVisibility.Auto);
        // Keep the horizontal extent available in an empty folder as well as during row virtualization.
        entries.ItemsPanel = new FuncTemplate<Panel>(() =>
        {
            var itemsPanel = new VirtualizingStackPanel();
            itemsPanel.Bind(MinWidthProperty, headers.GetObservable(BoundsProperty, bounds => bounds.Width));
            return itemsPanel;
        });
        entries.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) =>
        {
            if (entries.Scroll is not ScrollViewer scroll) return;
            columns.SetViewportWidth(scroll.Viewport.Width);
            Canvas.SetLeft(headers, -scroll.Offset.X);
        });
        entries.ContainerPrepared += (_, args) =>
        {
            if (args.Container is not ListBoxItem row) return;
            row.ContextMenu = !picking && row.Content is WebFileEntry entry ? WebFileRowMenu(row, entry) : null;
        };
        entries.ContainerClearing += (_, args) =>
        {
            if (args.Container is not ListBoxItem row) return;
            WebFileRowVisual(row)?.Classes.Remove("web-menu-open");
            // The row may be recycled before a frosted menu finishes its closing animation.
            row.ContextMenu?.Close();
            row.ContextMenu = null;
        };
        var message = Label("", muted: true);
        message.Name = "WebFilesMessage";
        message.Margin = new Thickness(10, 8);
        message.VerticalAlignment = VerticalAlignment.Top;
        var listing = new Grid { Name = "WebFilesListingContent", Children = { entries, message } };
        var listingViewport = new Grid
        {
            Name = "WebFilesListing",
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Children = { listing }
        };
        if (!picking)
        {
            listingViewport.ContextMenu = WebFileListingMenu();
            listingViewport.AddHandler(PointerPressedEvent, (_, args) =>
            {
                if (args.Properties.PointerUpdateKind is not (PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed) ||
                    args.Source is not Visual source || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null ||
                    source.FindAncestorOfType<ScrollBar>(includeSelf: true) != null) return;
                entries.SelectedItem = null;
            }, RoutingStrategies.Tunnel);
        }
        Grid.SetRow(listingViewport, 2);

        var table = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children = { selectionMessage, headerViewport, listingViewport }
        };
        Grid.SetColumn(table, 1);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("112,*"),
            Children = { BuildWebQuickAccess(pickFolder), table }
        };
        Grid.SetRow(body, 1);
        var surface = new Grid
        {
            Name = "WebFilesSurface",
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { address, body }
        };
        var filesView = new WebFilesView(surface, listing, path, entries, message, selectionMessage, up, open);
        if (!picking)
        {
            entries.SelectionChanged += (_, _) => UpdateWebFileAction(filesView);
            surface.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None || entries.SelectedItem == null) return;
                entries.SelectedItem = null;
                args.Handled = true;
            };
        }
        entries.DoubleTapped += (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Content is not WebFileEntry entry)
            {
                return;
            }
            if (entry.IsFolder)
            {
                args.Handled = true;
                if (!WebTransfers.IsIncompletePath(entry.FullPath)) navigate(entry.FullPath);
            }
            else if (!picking && entry.IsExecutable)
            {
                args.Handled = true;
                entries.SelectedItem = entry;
                _ = OpenWebFile(filesView);
            }
        };
        entries.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None ||
                entries.SelectedItem is not WebFileEntry { IsFolder: true } entry) return;
            if (WebTransfers.IsIncompletePath(entry.FullPath)) return;
            args.Handled = true;
            entries.Focus();
            navigate(entry.FullPath);
        }, RoutingStrategies.Tunnel);
        return filesView;
    }

    private Control BuildWebFileRow(WebFileEntry entry, WebTableColumns columns, bool picking)
    {
        var iconKey = entry switch
        {
            { IsFolder: true } => "LG.Icon.FolderOpen",
            { IsBatchFile: true } => "LG.Icon.Settings",
            _ => "LG.Icon.Play"
        };
        var icon = Symbol(iconKey, 16);
        if (!entry.IsFolder && !entry.IsExecutable)
        {
            icon.Data = LucideIconCatalog.Find("file-text")?.Geometry;
        }

        var name = WebTableCell(WebFileDisplayName(entry, picking), muted: false);
        ToolTip.SetTip(name, entry.FullPath);
        var type = DescribeWebFileType(entry);
        var kind = WebTableCell(type);
        ToolTip.SetTip(kind, type);
        var size = WebTableCell(DescribeWebFileSize(entry));
        var modified = entry.ModifiedUtc?.ToLocalTime();
        var date = WebTableCell(FormatWebFileDate(modified));
        ToolTip.SetTip(date, modified?.ToString("F", CultureInfo.CurrentCulture));
        var row = WebTableRow(columns, WebTableName(icon, name), kind, size, date);
        row.Tag = entry;
        if (entry.IsFolder) icon.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("LG.FolderIcon"));
        ApplyWebFileRowTint(row, entry, picking);
        return row;
    }

    private void ApplyWebFileRowTint(Border row, WebFileEntry entry, bool picking = false)
    {
        var cut = !picking && _webFileClipboard is { Cut: true } clipboard &&
            WebQuickAccessPathComparer.Equals(clipboard.Entry.FullPath, entry.FullPath);
        row.Classes.Set("web-file-muted", entry.IsHidden || cut);
        if (row.Child is Grid content) content.Opacity = entry.IsHidden || cut ? .55 : 1;
    }

    private string WebFileDisplayName(WebFileEntry entry, bool picking)
    {
        if (picking || entry.IsFolder || !_library.Preferences.HideWebFileExtensions) return entry.Name;
        var extension = Path.GetExtension(entry.Name);
        return extension.Length > 0 && extension.Length < entry.Name.Length ? entry.Name[..^extension.Length] : entry.Name;
    }

    private static Border? WebFileRowVisual(ListBoxItem row) =>
        row.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "WebTableRow");

    private static string DescribeWebFileType(WebFileEntry entry) => entry switch
    {
        { IsFolder: true } => "Folder",
        { IsExecutable: true, IsBatchFile: false } => "Application",
        { Extension.Length: > 1 } => entry.Extension[1..].ToUpperInvariant() + " file",
        _ => "File",
    };

    private static string DescribeWebFileSize(WebFileEntry entry) =>
        entry.Size is { } bytes ? FormatWebTransferSize(bytes) : "—";

    private static string FormatWebFileDate(DateTime? modified) =>
        modified?.ToString("g", CultureInfo.CurrentCulture) ?? "—";

    private void SetWebFileLocation(string? path, bool rememberLocation = true, int direction = 1)
    {
        if (_webFilesView is { } view)
        {
            _ = ReadWebFileLocation(view, path ?? WebDownloadDirectory, rememberLocation: rememberLocation, direction: direction);
        }
    }

    private bool MoveInWebFileHistory(int direction)
    {
        if (_webView == WebView.Downloads && !WebFileHistoryAtDownloads) return false;
        var index = _webFilesHistoryIndex + direction;
        if (index < 0 || index >= _webFilesHistory.Count) return false;

        _webFilesHistoryIndex = index;
        if (_webFilesHistory[index] is { } path)
        {
            SelectWebView(WebView.Files);
            SetWebFileLocation(path, rememberLocation: false, direction: direction);
        }
        else
        {
            _webFilesLoading?.Cancel();
            SelectWebView(WebView.Downloads);
        }
        return true;
    }

    private void RememberWebFileLocation(string? location)
    {
        if (_webFilesHistoryIndex >= 0 &&
            String.Equals(location, _webFilesHistory[_webFilesHistoryIndex], StringComparison.OrdinalIgnoreCase)) return;

        _webFilesHistory.RemoveRange(_webFilesHistoryIndex + 1, _webFilesHistory.Count - _webFilesHistoryIndex - 1);
        _webFilesHistory.Add(location);
        if (_webFilesHistory.Count > MaximumHistoryEntries) _webFilesHistory.RemoveAt(0);
        _webFilesHistoryIndex = _webFilesHistory.Count - 1;
    }

    private void RefreshWebFiles()
    {
        if (_webFilesView is not { } view || _webFilesLoading != null) return;
        var selected = (view.Entries.SelectedItem as WebFileEntry)?.FullPath;
        _ = ReadWebFileLocation(view, _webFilesDirectory ?? _webFilesLocation ?? WebDownloadDirectory, selected);
    }

    private async Task ReadWebFileLocation(WebFilesView view, string location, string? selectedFile = null,
        bool rememberLocation = false, int direction = 0)
    {
        var downloads = WebDownloadDirectory;
        var currentFolder = _webFilesDirectory ?? downloads;
        _webFilesLoading?.Cancel();
        using var loading = new CancellationTokenSource();
        _webFilesLoading = loading;
        _webFilesRequestedLocation = selectedFile ?? location;

        CancelMotion(view.Listing);
        view.Path.Text = location;
        view.Up.IsEnabled = false;
        view.Entries.ItemsSource = null;
        view.Message.Text = "Reading folder…";
        view.Message.IsVisible = true;
        try
        {
            var listing = await Task.Run(() => ListWebFiles(location, currentFolder, downloads, selectedFile, loading.Token), loading.Token);
            if (_closed || loading.IsCancellationRequested) return;

            var changedFolder = _webFilesDirectory != null && listing.Readable &&
                !WebQuickAccessPathComparer.Equals(_webFilesDirectory, listing.Folder);
            _webFilesDirectory = listing.Folder;
            _webFilesDirectoryReadable = listing.Readable;
            UpdateWebQuickAccessSelection();
            _webFilesLocation = listing.SelectedFile ?? listing.Folder ?? location;
            if (rememberLocation && listing.Folder != null) RememberWebFileLocation(_webFilesLocation);
            view.Path.Text = listing.Folder ?? location;
            ToolTip.SetTip(view.Path, view.Path.Text);
            view.Up.IsEnabled = listing.Folder != null && Path.GetDirectoryName(listing.Folder) != null;
            var entries = _library.Preferences.HideWebHiddenFiles
                ? listing.Entries.Where(entry => !entry.IsHidden).ToArray()
                : listing.Entries;
            view.Entries.ItemsSource = entries;
            view.Message.Text = entries.Length == 0 && listing.Readable ? "This folder is empty." : listing.Message;
            view.Message.IsVisible = view.Message.Text.Length > 0;
            if (listing.SelectedFile != null && _page == Page.Web && _webView == WebView.Files)
            {
                view.Entries.SelectedItem = Array.Find(entries, entry =>
                    String.Equals(entry.FullPath, listing.SelectedFile, StringComparison.OrdinalIgnoreCase));
            }
            UpdateWebFileAction(view);
            if (changedFolder && direction != 0)
            {
                AnimateMotion(view.Listing, token => ViewMotion.Move(view.Listing, new Vector(direction * 42, 0), token));
            }
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_webFilesLoading, loading))
            {
                _webFilesLoading = null;
                _webFilesRequestedLocation = null;
            }
        }
    }

    private static WebFileListing ListWebFiles(string location, string currentFolder, string downloads, string? selectedFile,
        CancellationToken cancellation, bool foldersOnly = false)
    {
        var enteredPath = location.Trim();
        if (enteredPath.Length >= 2 && enteredPath[0] == '"' && enteredPath[^1] == '"') enteredPath = enteredPath[1..^1];
        if (enteredPath.Length == 0) return new(null, [], null, foldersOnly ? "Enter a folder path." : "Enter a folder or file path.");

        string? folder = null;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NormalizeWebFilePathInput(enteredPath), currentFolder));
            folder = ResolveWebFilePath(folder, downloads);

            if (!File.GetAttributes(folder).HasFlag(FileAttributes.Directory))
            {
                if (foldersOnly) return new(null, [], null, "Choose a folder.");
                selectedFile = folder;
                folder = Path.GetDirectoryName(folder);
                if (folder == null) return new(null, [], null, "The file’s containing folder could not be found.");
            }

            var entries = new List<WebFileEntry>();
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                if (IsWebFileStagingPath(item.FullName) ||
                    foldersOnly && (item is not DirectoryInfo || WebTransfers.IsIncompletePath(item.FullName))) continue;
                long? size = null;
                DateTime? modifiedUtc = null;
                var isHidden = false;
                try
                {
                    var attributes = item.Attributes;
                    isHidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                    if (item is FileInfo file) size = file.Length;
                    var modified = item.LastWriteTimeUtc;
                    if (modified > DateTime.FromFileTimeUtc(0)) modifiedUtc = modified;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Keep entries visible when their metadata becomes unavailable during enumeration.
                }
                entries.Add(new WebFileEntry(item.FullName, item.Name, item is DirectoryInfo, size, modifiedUtc, isHidden));
            }
            var ordered = entries.OrderByDescending(entry => entry.IsFolder)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var empty = foldersOnly ? "No subfolders." : "This folder is empty.";
            return new(folder, ordered, selectedFile, ordered.Length == 0 ? empty : "", Readable: true);
        }
        catch (WebFileInProgressException)
        {
            return new(null, [], null, "This download is still in progress.");
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException ||
            error is Win32Exception { NativeErrorCode: 2 or 3 })
        {
            var message = String.Equals(folder, downloads, StringComparison.OrdinalIgnoreCase)
                ? "Downloads will appear here after the first file is saved."
                : "The folder or file could not be found.";
            return new(folder, [], null, message);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException ||
            error is Win32Exception { NativeErrorCode: 5 })
        {
            return new(folder, [], null, "Access to this folder or file was denied.");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return new(null, [], null, "Enter a valid folder or file path.");
        }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            return new(folder, [], null, "The folder or file could not be read.");
        }
    }

    private static bool IsWebFileStagingPath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(WithoutWebFileDevicePrefix(path));
        if (WebTransfers.IsStagingDestination(normalized)) return true;
        if (!normalized.Contains(WebFileCopyStagingPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        for (string? segmentPath = normalized; segmentPath != null; segmentPath = Path.GetDirectoryName(segmentPath))
        {
            var name = Path.GetFileName(segmentPath);
            if (name.StartsWith(WebFileCopyStagingPrefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(name.AsSpan(WebFileCopyStagingPrefix.Length), "N", out _)) return true;
        }
        return false;
    }

    private static string NormalizeWebFilePathInput(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var normalized = WithoutWebFileDevicePrefix(path);
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\??\", StringComparison.Ordinal) ||
            normalized.Contains('?'))
        {
            throw new ArgumentException("Choose a standard drive or network file path.", nameof(path));
        }
        return normalized;
    }

    private static string WithoutWebFileDevicePrefix(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + normalized[8..];
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) && normalized.Length >= 7 &&
            Char.IsAsciiLetter(normalized[4]) && normalized[5] == ':' && normalized[6] == '\\')
        {
            return normalized[4..];
        }
        return normalized;
    }

    private static string ResolveWebFilePath(string path, string downloads)
    {
        if (IsWebFileStagingPath(path) || WebTransfers.IsIncompletePath(path)) throw new WebFileInProgressException();
        if (!OperatingSystem.IsWindows()) return path;

        var localPath = NormalizeWebFilePathInput(path);
        ValidateWindowsFilePath(localPath);
        var staging = Path.Combine(WithoutWebFileDevicePrefix(downloads), WebTransfers.StagingDirectoryName);
        using var target = OpenWebFilePath(localPath);
        const uint ntVolumeName = 2;
        var finalTarget = Path.TrimEndingDirectorySeparator(WindowsFinalPath(target, ntVolumeName));
        if (IsWebFileStagingPath(finalTarget)) throw new WebFileInProgressException();

        using var stagingHandle = OpenWindowsFileHandle(staging);
        var stagingError = Marshal.GetLastPInvokeError();
        if (!stagingHandle.IsInvalid)
        {
            var finalStage = Path.TrimEndingDirectorySeparator(WindowsFinalPath(stagingHandle, ntVolumeName));
            if (finalTarget.Equals(finalStage, StringComparison.OrdinalIgnoreCase) ||
                finalTarget.StartsWith(finalStage + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new WebFileInProgressException();
            }
        }
        else if (stagingError is not (2 or 3))
        {
            throw new Win32Exception(stagingError);
        }

        // Browse and launch through the resolved target so an alias cannot be redirected after validation.
        var resolved = NormalizeWebFilePathInput(WindowsFinalPath(target, 0));
        ValidateWindowsFilePath(resolved);
        if (WebTransfers.IsIncompletePath(resolved)) throw new WebFileInProgressException();
        return resolved;
    }

    private static void ValidateWindowsFilePath(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var network = path.StartsWith(@"\\", StringComparison.Ordinal);
        var volumeLength = network ? 2 : 1;
        if (parts.Length < volumeLength || (network && (parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase) ||
            parts[1].Equals("mailslot", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("IPC$", StringComparison.OrdinalIgnoreCase))) ||
            (!network && (parts[0].Length != 2 || !Char.IsAsciiLetter(parts[0][0]) || parts[0][1] != ':')) ||
            path.AsSpan(network ? 0 : 2).Contains(':') || parts.Skip(volumeLength).Any(IsWindowsDeviceName))
        {
            throw new ArgumentException("Choose a standard drive or network file path.", nameof(path));
        }
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var extension = segment.IndexOf('.');
        var name = (extension < 0 ? segment : segment[..extension]).TrimEnd(' ').ToUpperInvariant();
        if (name is "CON" or "NUL" or "AUX" or "PRN" or "CONIN$" or "CONOUT$") return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(name[3]);
    }

    private static SafeFileHandle OpenWebFilePath(string path)
    {
        var handle = OpenWindowsFileHandle(path);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static SafeFileHandle OpenWindowsFileHandle(string path)
    {
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        var extended = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        return CreateFileW(extended, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            openExisting, backupSemantics, IntPtr.Zero);
    }

    private static string WindowsFinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
            if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (length < buffer.Length) return new string(buffer, 0, (int)length);
            buffer = new char[length + 1];
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint capacity, uint flags);

    private void UpdateWebFileAction(WebFilesView view)
    {
        var entry = view.Entries.SelectedItem as WebFileEntry;
        var incomplete = entry != null && (entry.IsFolder
            ? WebTransfers.IsIncompletePath(entry.FullPath)
            : IsWebDownloadIncomplete(entry.FullPath));
        var (action, icon) = entry switch
        {
            { IsFolder: true } => ("Open folder", "LG.Icon.FolderOpen"),
            { IsExecutable: true } => ("Run application", "LG.Icon.Play"),
            _ => ("Open file", "LG.Icon.ExternalLink"),
        };
        view.Open.Content = Symbol(icon, 16);
        AutomationProperties.SetName(view.Open, action);
        ToolTip.SetTip(view.Open, action);
        view.Open.IsEnabled = entry != null && !incomplete;
        view.SelectionMessage.IsVisible = incomplete;
    }

    private async Task OpenWebFile(WebFilesView view, WebFileEntry? entry = null)
    {
        var fromSelection = entry == null;
        entry ??= view.Entries.SelectedItem as WebFileEntry;
        if (entry == null) return;
        if (entry.IsFolder ? WebTransfers.IsIncompletePath(entry.FullPath) : IsWebDownloadIncomplete(entry.FullPath))
        {
            UpdateWebFileAction(view);
            return;
        }
        if (entry.IsFolder)
        {
            SetWebFileLocation(entry.FullPath);
            return;
        }

        if (entry.IsExecutable)
        {
            if (!await Confirm("Run application?", $"Run “{entry.Name}”?\n\n{entry.FullPath}", "Run")) return;
        }
        else if (WebShellProgramExtensions.Contains(entry.Extension) &&
            !await Confirm("Open file?", $"Opening “{entry.Name}” may run software on this device.\n\n{entry.FullPath}", "Open"))
        {
            return;
        }

        if (_closed || _page != Page.Web || _webView != WebView.Files || (fromSelection && !Equals(view.Entries.SelectedItem, entry)) ||
            !WebQuickAccessPathComparer.Equals(_webFilesDirectory, Path.GetDirectoryName(entry.FullPath))) return;
        if (IsWebDownloadIncomplete(entry.FullPath))
        {
            UpdateWebFileAction(view);
            return;
        }

        try
        {
            var resolved = ResolveWebFilePath(entry.FullPath, WebDownloadDirectory);
            var resolvedEntry = new WebFileEntry(resolved, Path.GetFileName(resolved), false);
            if (!entry.Extension.Equals(resolvedEntry.Extension, StringComparison.OrdinalIgnoreCase))
            {
                await Notice("Could not open file", "Open this file directly from its containing folder.");
                return;
            }
            var attributes = File.GetAttributes(entry.FullPath);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                (entry.IsExecutable && attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                await Notice("Could not open file", "Choose a regular file.");
                return;
            }

            if (IsWebDownloadIncomplete(resolved))
            {
                await Notice("Could not open file", "This file is an incomplete download.");
                return;
            }
            using var process = Process.Start(WebFileStartInfo(resolved, entry.IsBatchFile));
        }
        catch (WebFileInProgressException)
        {
            await Notice("Could not open file", "This file is an incomplete download.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or SecurityException or ArgumentException)
        {
            await Notice("Could not open file", error.Message);
        }
    }

    private static ProcessStartInfo WebFileStartInfo(string path, bool isBatchFile)
    {
        var workingDirectory = Path.GetDirectoryName(path);
        if (!isBatchFile || !OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo(path)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
        }

        if (path.Contains('"') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.Split(Path.DirectorySeparatorChar).Any(segment => segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new ArgumentException("Choose a standard Windows file path.", nameof(path));
        }

        // Cmd expands this quoted variable once, so any percent signs in the path stay literal.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/d /v:off /s /c \"\"%LG_LAUNCHER_BATCH_PATH%\"\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        start.Environment["LG_LAUNCHER_BATCH_PATH"] = path;
        return start;
    }
}
