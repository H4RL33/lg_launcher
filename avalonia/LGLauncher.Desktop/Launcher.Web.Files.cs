using System.ComponentModel;
using System.Diagnostics;
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
        ".ahk", ".appinstaller", ".application", ".appref-ms", ".appx", ".appxbundle", ".bat", ".cmd", ".com", ".cpl",
        ".hta", ".inf", ".ins", ".isp", ".jar", ".jnlp", ".js", ".jse", ".lnk", ".msc", ".msi", ".msix",
        ".msixbundle", ".msp", ".mst", ".pif", ".pl", ".ps1", ".py", ".pyw", ".rb", ".reg", ".scf", ".scr",
        ".sct", ".sh", ".shb", ".shs", ".url", ".vb", ".vbe", ".vbs", ".website", ".ws", ".wsc", ".wsf", ".wsh"
    };

    private sealed record WebFileEntry(string FullPath, string Name, bool IsFolder)
    {
        public string Extension => Path.GetExtension(Name.TrimEnd(' ', '.'));
        public bool IsExecutable => !IsFolder && Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WebFileListing(string? Folder, WebFileEntry[] Entries, string? SelectedFile, string Message);
    private sealed record WebFilesView(Grid Surface, TextBox Path, ListBox Entries, TextBlock Message,
        TextBlock SelectionMessage, Button Up, Button Open);
    private sealed class WebFileInProgressException : IOException;

    private WebFilesView? _webFilesView;
    private CancellationTokenSource? _webFilesLoading;
    private string? _webFilesLocation;
    private string? _webFilesDirectory;

    private Control BuildWebFiles()
    {
        _webFilesView ??= CreateWebFilesView();
        SetWebFileLocation(_webFilesLocation);
        return _webFilesView.Surface;
    }

    private WebFilesView CreateWebFilesView()
    {
        var path = TextField(null, "Folder or file path");
        path.Name = "WebFilesPath";
        path.PlaceholderText = "Folder or file path";
        path.VerticalContentAlignment = VerticalAlignment.Center;
        path.Padding = new Thickness(10, 0);
        path.UseTextEditingMenu();

        var up = ActionButton("Up", () =>
        {
            if (_webFilesDirectory is { } folder && Path.GetDirectoryName(folder) is { } parent)
            {
                SetWebFileLocation(parent);
            }
        });
        up.Name = "WebFilesUp";
        AutomationProperties.SetName(up, "Parent folder");
        var go = ActionButton("Go", () => SetWebFileLocation(path.Text ?? ""));
        go.Name = "WebFilesGo";
        path.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None) return;
            args.Handled = true;
            SetWebFileLocation(path.Text ?? "");
        };
        var address = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(path, 1);
        Grid.SetColumn(go, 2);
        address.Children.Add(up);
        address.Children.Add(path);
        address.Children.Add(go);

        var downloads = IconButton("Downloads", "LG.Icon.Download", () => SetWebFileLocation(null));
        downloads.Name = "WebFilesDownloads";
        AutomationProperties.SetName(downloads, "Downloads");
        var refresh = IconButton("Refresh", "LG.Icon.Refresh", RefreshWebFiles);
        refresh.Name = "WebFilesRefresh";
        AutomationProperties.SetName(refresh, "Refresh folder");
        var open = ActionButton("Open file", () =>
        {
            if (_webFilesView is { } view) _ = OpenWebFile(view);
        }, "primary");
        open.Name = "WebFilesOpen";
        open.IsEnabled = false;
        var selectionMessage = Label("This file is an incomplete download.", muted: true);
        selectionMessage.IsVisible = false;
        selectionMessage.Margin = new Thickness(8, 0);
        var commands = new StackPanel { Spacing = 6, Children = { Actions(downloads, refresh, open), selectionMessage } };
        Grid.SetRow(commands, 1);

        var entries = new ListBox
        {
            Name = "WebFilesEntries",
            Classes = { "lg" },
            AutoScrollToSelectedItem = true,
            ItemTemplate = new FuncDataTemplate<WebFileEntry>((entry, _) => entry == null ? null : BuildWebFileRow(entry))
        };
        AutomationProperties.SetName(entries, "Files and folders");
        ScrollViewer.SetHorizontalScrollBarVisibility(entries, ScrollBarVisibility.Disabled);
        entries.ContainerPrepared += (_, args) =>
        {
            if (args.Container is ListBoxItem row) row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        };
        var message = Label("", muted: true);
        message.Name = "WebFilesMessage";
        message.Margin = new Thickness(8, 6);
        message.VerticalAlignment = VerticalAlignment.Top;
        var listing = new Grid { ClipToBounds = true, Children = { entries, message } };
        Grid.SetRow(listing, 2);

        var surface = new Grid
        {
            Name = "WebFilesSurface",
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 8,
            Children = { address, commands, listing }
        };
        var filesView = new WebFilesView(surface, path, entries, message, selectionMessage, up, open);
        entries.SelectionChanged += (_, _) => UpdateWebFileAction(filesView);
        entries.DoubleTapped += (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Content is not WebFileEntry { IsFolder: true } entry)
            {
                return;
            }
            args.Handled = true;
            SetWebFileLocation(entry.FullPath);
        };
        entries.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None ||
                entries.SelectedItem is not WebFileEntry { IsFolder: true } entry) return;
            args.Handled = true;
            entries.Focus();
            SetWebFileLocation(entry.FullPath);
        }, RoutingStrategies.Tunnel);
        return filesView;
    }

    private Control BuildWebFileRow(WebFileEntry entry)
    {
        var icon = Symbol(entry.IsFolder ? "LG.Icon.FolderOpen" : "LG.Icon.Play");
        if (!entry.IsFolder && !entry.IsExecutable)
        {
            icon.Data = LucideIconCatalog.Find("file-text")?.Geometry;
        }

        var name = new TextBlock
        {
            Text = entry.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(name, entry.FullPath);
        var kind = Label(entry.IsFolder ? "Folder" : entry.IsExecutable ? "Application" : "File", muted: true);
        kind.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            Children = { icon, name, kind }
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(kind, 2);
        return row;
    }

    private void SetWebFileLocation(string? path)
    {
        _webFilesLocation = path ?? WebDownloadDirectory;
        if (_webFilesView is { } view) _ = ReadWebFileLocation(view, _webFilesLocation);
    }

    private void RefreshWebFiles()
    {
        if (_webFilesView is not { } view || _webFilesLoading != null) return;
        var selected = (view.Entries.SelectedItem as WebFileEntry)?.FullPath;
        _ = ReadWebFileLocation(view, _webFilesDirectory ?? _webFilesLocation ?? WebDownloadDirectory, selected);
    }

    private async Task ReadWebFileLocation(WebFilesView view, string location, string? selectedFile = null)
    {
        var downloads = WebDownloadDirectory;
        var currentFolder = _webFilesDirectory ?? downloads;
        _webFilesLoading?.Cancel();
        using var loading = new CancellationTokenSource();
        _webFilesLoading = loading;

        view.Path.Text = location;
        view.Up.IsEnabled = false;
        view.Entries.ItemsSource = null;
        view.Message.Text = "Reading folder…";
        view.Message.IsVisible = true;
        try
        {
            var listing = await Task.Run(() => ListWebFiles(location, currentFolder, downloads, selectedFile, loading.Token), loading.Token);
            if (_closed || loading.IsCancellationRequested) return;

            _webFilesDirectory = listing.Folder;
            _webFilesLocation = listing.SelectedFile ?? listing.Folder ?? location;
            view.Path.Text = listing.Folder ?? location;
            ToolTip.SetTip(view.Path, view.Path.Text);
            view.Up.IsEnabled = listing.Folder != null && Path.GetDirectoryName(listing.Folder) != null;
            view.Entries.ItemsSource = listing.Entries;
            view.Message.Text = listing.Message;
            view.Message.IsVisible = listing.Message.Length > 0;
            if (listing.SelectedFile != null)
            {
                view.Entries.SelectedItem = Array.Find(listing.Entries, entry =>
                    String.Equals(entry.FullPath, listing.SelectedFile, StringComparison.OrdinalIgnoreCase));
            }
            UpdateWebFileAction(view);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_webFilesLoading, loading)) _webFilesLoading = null;
        }
    }

    private static WebFileListing ListWebFiles(string location, string currentFolder, string downloads, string? selectedFile,
        CancellationToken cancellation)
    {
        var enteredPath = location.Trim();
        if (enteredPath.Length >= 2 && enteredPath[0] == '"' && enteredPath[^1] == '"') enteredPath = enteredPath[1..^1];
        if (enteredPath.Length == 0) return new(null, [], null, "Enter a folder or file path.");

        string? folder = null;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NormalizeWebFilePathInput(enteredPath), currentFolder));
            folder = ResolveWebFilePath(folder, downloads);

            if (!File.GetAttributes(folder).HasFlag(FileAttributes.Directory))
            {
                selectedFile = folder;
                folder = Path.GetDirectoryName(folder);
                if (folder == null) return new(null, [], null, "The file’s containing folder could not be found.");
            }

            var entries = new List<WebFileEntry>();
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                if (IsWebFileStagingPath(item.FullName, downloads)) continue;
                entries.Add(new WebFileEntry(item.FullName, item.Name, item is DirectoryInfo));
            }
            var ordered = entries.OrderByDescending(entry => entry.IsFolder)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return new(folder, ordered, selectedFile, ordered.Length == 0 ? "This folder is empty." : "");
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

    private static bool IsWebFileStagingPath(string path, string downloads)
    {
        var candidate = Path.TrimEndingDirectorySeparator(WithoutWebFileDevicePrefix(path));
        var staging = Path.Combine(WithoutWebFileDevicePrefix(downloads), WebTransfers.StagingDirectoryName);
        return String.Equals(candidate, staging, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
        if (IsWebFileStagingPath(path, downloads)) throw new WebFileInProgressException();
        if (!OperatingSystem.IsWindows()) return path;

        var localPath = NormalizeWebFilePathInput(path);
        ValidateWindowsFilePath(localPath);
        var staging = Path.Combine(WithoutWebFileDevicePrefix(downloads), WebTransfers.StagingDirectoryName);
        using var target = OpenWebFilePath(localPath);
        using var stagingHandle = OpenWindowsFileHandle(staging);
        var stagingError = Marshal.GetLastPInvokeError();
        if (!stagingHandle.IsInvalid)
        {
            const uint ntVolumeName = 2;
            var finalStage = Path.TrimEndingDirectorySeparator(WindowsFinalPath(stagingHandle, ntVolumeName));
            var finalTarget = Path.TrimEndingDirectorySeparator(WindowsFinalPath(target, ntVolumeName));
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
        var incomplete = entry is { IsFolder: false } && IsWebDownloadIncomplete(entry.FullPath);
        view.Open.Content = entry switch { { IsFolder: true } => "Open folder", { IsExecutable: true } => "Run", _ => "Open file" };
        view.Open.IsEnabled = entry != null && !incomplete;
        view.SelectionMessage.IsVisible = incomplete;
    }

    private async Task OpenWebFile(WebFilesView view)
    {
        if (view.Entries.SelectedItem is not WebFileEntry entry) return;
        if (entry.IsFolder)
        {
            SetWebFileLocation(entry.FullPath);
            return;
        }
        if (IsWebDownloadIncomplete(entry.FullPath))
        {
            UpdateWebFileAction(view);
            return;
        }

        if (entry.IsExecutable && !await Confirm("Run application?", $"Run “{entry.Name}”?\n\n{entry.FullPath}", "Run")) return;
        if (WebShellProgramExtensions.Contains(entry.Extension) &&
            !await Confirm("Open file?", $"Opening “{entry.Name}” may run software on this device.\n\n{entry.FullPath}", "Open")) return;

        if (_closed || !Equals(view.Entries.SelectedItem, entry)) return;
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
            using var process = Process.Start(new ProcessStartInfo(resolved)
            {
                WorkingDirectory = Path.GetDirectoryName(resolved),
                UseShellExecute = true
            });
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
}
