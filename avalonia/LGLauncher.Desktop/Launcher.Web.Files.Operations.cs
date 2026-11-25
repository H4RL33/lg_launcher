using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyLeftShift = 0xA0;
    private const int VirtualKeyRightShift = 0xA1;
    private const string WebFileCopyStagingPrefix = ".lg-file-copy-";

    private sealed record WebFileClipboard(WebFileEntry Entry, bool Cut);

    private WebFileClipboard? _webFileClipboard;
    private bool _webFilesDirectoryReadable;
    private bool _webFileOperationPending;

    private ContextMenu WebFileRowMenu(ListBoxItem row, WebFileEntry entry)
    {
        var menu = LauncherMenus.Create();
        MenuItem? run = null;
        if (entry.IsExecutable)
        {
            run = menu.AddAction("Run", () =>
            {
                if (_webFilesView is not { } view) return;
                _ = OpenWebFile(view, entry);
            }, "LG.Icon.Play");
            run.Name = "WebFileRun";
            menu.AddSeparator();
        }

        var rename = menu.AddAction("Rename", () => _ = RenameWebFile(entry), "LG.Icon.Edit");
        var copy = menu.AddAction("Copy", () => SetWebFileClipboard(new(entry, Cut: false)), "LG.Icon.Copy");
        var cut = menu.AddAction("Cut", () => SetWebFileClipboard(new(entry, Cut: true)), "LG.Icon.Cut");
        var destroy = false;
        var delete = menu.AddAction("Delete", () => _ = DeleteWebFile(entry, destroy), "LG.Icon.Delete");
        rename.Name = "WebFileRename";
        copy.Name = "WebFileCopy";
        cut.Name = "WebFileCut";
        delete.Name = "WebFileDelete";
        delete.Classes.Add("web-file-delete");

        var deleteIcon = Symbol("LG.Icon.Delete");
        deleteIcon.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("LG.Text"));
        var destroyIcon = Symbol("LG.Icon.Warning");
        destroyIcon.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("LG.Error"));
        delete.Icon = deleteIcon;

        if (entry.IsFolder)
        {
            menu.AddSeparator();
            AddWebFolderQuickAccessAction(menu, entry.FullPath);
        }

        Border? rowVisual = null;
        menu.Opening += (_, _) =>
        {
            ClearWebFileSelection();
            var available = !_webFileOperationPending && !IsWebDownloadIncomplete(entry.FullPath);
            rename.IsEnabled = copy.IsEnabled = cut.IsEnabled = delete.IsEnabled = available;
            if (run != null) run.IsEnabled = available;
            SetDestroyMode(OperatingSystem.IsWindows() && GetKeyState(VirtualKeyShift) < 0);
        };
        menu.Opened += (_, _) =>
        {
            rowVisual = WebFileRowVisual(row);
            rowVisual?.Classes.Add("web-menu-open");
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(row.ContextMenu, menu)) rowVisual?.Classes.Remove("web-menu-open");
            rowVisual = null;
        };
        menu.AddHandler(KeyDownEvent, (_, args) => UpdateDestroyMode(args, keyDown: true),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.AddHandler(KeyUpEvent, (_, args) => UpdateDestroyMode(args, keyDown: false),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        return menu;

        void UpdateDestroyMode(KeyEventArgs args, bool keyDown)
        {
            if (args.Key is Key.LeftShift or Key.RightShift)
            {
                var otherShift = args.Key == Key.LeftShift ? VirtualKeyRightShift : VirtualKeyLeftShift;
                SetDestroyMode(keyDown || OperatingSystem.IsWindows() && GetKeyState(otherShift) < 0);
            }
            else if (keyDown)
            {
                SetDestroyMode(args.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
        }

        void SetDestroyMode(bool shiftHeld)
        {
            destroy = shiftHeld;
            delete.Header = destroy ? "Destroy" : "Delete";
            delete.Icon = destroy ? destroyIcon : deleteIcon;
            delete.Classes.Set("destructive", destroy);
        }
    }

    private ContextMenu WebFileListingMenu()
    {
        var menu = LauncherMenus.Create();
        var paste = menu.AddAction("Paste", () => _ = PasteWebFile(), "LG.Icon.Paste");
        var create = menu.AddAction("New Directory", () => _ = CreateWebDirectory(), "LG.Icon.Plus");
        var back = menu.AddAction("Go Back", () => MoveInWebFileHistory(-1), "LG.Icon.Back");
        paste.Name = "WebFilesPaste";
        create.Name = "WebFilesNewDirectory";
        back.Name = "WebFilesGoBack";
        menu.Opening += (_, _) =>
        {
            ClearWebFileSelection();
            var available = WebFileOperationFolder != null;
            paste.IsEnabled = available && _webFileClipboard != null;
            create.IsEnabled = available;
            back.IsEnabled = _webFilesHistoryIndex > 0;
        };
        return menu;
    }

    private string? WebFileOperationFolder => !_webFileOperationPending && _webFilesLoading == null &&
        _webFilesDirectoryReadable ? _webFilesDirectory : null;

    private void SetWebFileClipboard(WebFileClipboard? clipboard)
    {
        _webFileClipboard = clipboard;
        if (_webFilesView is not { } view) return;
        foreach (var row in view.Entries.GetVisualDescendants().OfType<Border>())
        {
            if (row.Tag is WebFileEntry entry) ApplyWebFileRowTint(row, entry);
        }
    }

    private void ClearWebFileClipboard(string path)
    {
        if (_webFileClipboard is { } clipboard && IsWebFileAtOrBelow(clipboard.Entry.FullPath, path))
        {
            SetWebFileClipboard(null);
        }
    }

    private async Task RenameWebFile(WebFileEntry entry)
    {
        if (WebFileOperationFolder is not { } folder) return;
        var name = await PromptForWebFileName("Rename", entry.Name, "Rename");
        if (name == null || name == entry.Name) return;

        var downloads = WebDownloadDirectory;
        await PerformWebFileOperation("rename item", folder, async () =>
        {
            var renamed = await Task.Run(() =>
            {
                var source = ValidateWebFileOperationSource(entry, downloads);
                var destination = WebFileDestination(folder, name, downloads);
                MoveWebFile(source, destination, entry.IsFolder);
                return destination;
            });
            ClearWebFileClipboard(entry.FullPath);
            return renamed;
        });
    }

    private async Task CreateWebDirectory()
    {
        if (WebFileOperationFolder is not { } folder) return;
        var name = await PromptForWebFileName("New Directory", "New Directory", "Create");
        if (name == null) return;

        var downloads = WebDownloadDirectory;
        await PerformWebFileOperation("create directory", folder, () => Task.Run(() =>
        {
            var destination = WebFileDestination(folder, name, downloads);
            CreateWebDirectoryPath(destination);
            return destination;
        }));
    }

    private async Task DeleteWebFile(WebFileEntry entry, bool destroy)
    {
        if (WebFileOperationFolder is not { } folder) return;
        if (destroy && !await Confirm("Destroy item?", $"Permanently delete “{entry.Name}”?\n\n{entry.FullPath}",
                "Destroy", destructive: true)) return;

        var downloads = WebDownloadDirectory;
        await PerformWebFileOperation(destroy ? "destroy item" : "recycle item", folder, async () =>
        {
            var path = await Task.Run(() =>
            {
                var source = ValidateWebFileOperationSource(entry, downloads);
                if (destroy)
                {
                    if (entry.IsFolder) Directory.Delete(source, recursive: true);
                    else File.Delete(source);
                }
                return source;
            });
            if (!destroy) await RecycleWebFile(path);
            ClearWebFileClipboard(entry.FullPath);
            return null;
        });
    }

    private async Task PasteWebFile()
    {
        if (WebFileOperationFolder is not { } folder || _webFileClipboard is not { } clipboard) return;
        var downloads = WebDownloadDirectory;
        await PerformWebFileOperation("paste item", folder, async () =>
        {
            var entry = clipboard.Entry;
            var (source, destination) = await Task.Run(() =>
            {
                var source = ValidateWebFileOperationSource(entry, downloads);
                var destination = WebFileDestination(folder, entry.Name, downloads);
                if (WebQuickAccessPathComparer.Equals(source, destination))
                {
                    if (clipboard.Cut) return (source, destination);
                    var extension = entry.IsFolder ? "" : Path.GetExtension(entry.Name);
                    if (extension.Length == entry.Name.Length) extension = "";
                    destination = WebFileCopyName(folder, entry.Name[..^extension.Length], extension);
                }
                if (entry.IsFolder && IsWebFileAtOrBelow(destination, source))
                {
                    throw new IOException("Choose a location outside the source folder.");
                }
                if (Path.Exists(destination)) throw new IOException("An item with that name already exists.");
                return (source, destination);
            });

            var selectDestination = true;
            if (clipboard.Cut)
            {
                if (!WebQuickAccessPathComparer.Equals(source, destination))
                {
                    selectDestination = await MoveWebClipboardItem(source, destination, entry.IsFolder);
                }
                if (ReferenceEquals(_webFileClipboard, clipboard)) SetWebFileClipboard(null);
            }
            else
            {
                await Task.Run(() =>
                {
                    if (entry.IsFolder) CopyWebDirectory(source, destination);
                    else CopyWebFile(source, destination);
                });
            }
            return selectDestination ? destination : null;
        });
    }

    private static string WebFileCopyName(string folder, string stem, string extension)
    {
        var destination = Path.Combine(folder, stem + " - Copy" + extension);
        for (var copy = 2; Path.Exists(destination); copy++)
        {
            destination = Path.Combine(folder, $"{stem} - Copy ({copy}){extension}");
        }
        return destination;
    }

    private async Task<string?> PromptForWebFileName(string title, string initial, string action)
    {
        var dialog = new ModalDialog(title, 453);
        var field = ModalTextField(initial, "Name");
        field.UseTextEditingMenu();
        var error = Label("", muted: true);
        error.IsVisible = false;
        Field(dialog.Body, "Name", field);
        dialog.Body.Children.Add(error);
        DialogActions(dialog, action, () =>
        {
            var name = field.Text?.TrimStart() ?? "";
            if (!IsWebFileName(name))
            {
                error.Text = "Enter a valid file or folder name.";
                error.IsVisible = true;
                field.Focus();
                return;
            }
            dialog.Close(true);
        });
        field.SelectAll();
        dialog.InitialFocus = field;
        return await ShowModal(dialog) ? field.Text?.TrimStart() : null;
    }

    private async Task PerformWebFileOperation(string action, string folder, Func<Task<string?>> operation)
    {
        if (!WebQuickAccessPathComparer.Equals(WebFileOperationFolder, folder)) return;
        _webFileOperationPending = true;
        try
        {
            var selected = await operation();
            if (!_closed && _webFilesView is { } view && WebQuickAccessPathComparer.Equals(_webFilesDirectory, folder) &&
                (_webFilesLoading == null || WebQuickAccessPathComparer.Equals(view.Path.Text, folder)))
            {
                _ = ReadWebFileLocation(view, folder, selected);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or Win32Exception or SecurityException or COMException)
        {
            if (!_closed)
            {
                await Notice($"Could not {action}", error is WebFileInProgressException
                    ? "The item contains an incomplete transfer."
                    : error.Message);
            }
        }
        finally
        {
            _webFileOperationPending = false;
        }
    }

    private static bool IsWebFileName(string name) =>
        name.Length > 0 && !name.EndsWith(' ') && !name.EndsWith('.') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !IsWindowsDeviceName(name);

    private string WebFileDestination(string folder, string name, string downloads)
    {
        if (!IsWebFileName(name)) throw new IOException("Enter a valid file or folder name.");
        var parent = ResolveWebFilePath(folder, downloads);
        if (!WebQuickAccessPathComparer.Equals(parent, folder))
        {
            throw new IOException("The destination folder has changed.");
        }
        var destination = Path.Combine(parent, name);
        if (IsWebDownloadIncomplete(destination)) throw new WebFileInProgressException();
        return destination;
    }

    private string ValidateWebFileOperationSource(WebFileEntry entry, string downloads)
    {
        var source = entry.FullPath;
        ValidateWebFileOperationItem(source);
        if (!WebQuickAccessPathComparer.Equals(ResolveWebFilePath(source, downloads), source))
        {
            throw new IOException("The source item has changed.");
        }
        var folder = File.GetAttributes(source).HasFlag(FileAttributes.Directory);
        if (entry.IsFolder != folder) throw new IOException("The source item has changed.");
        if (!folder) return source;

        // Validate the whole tree without following links before changing any user files.
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var parent))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                if (ValidateWebFileOperationItem(path).HasFlag(FileAttributes.Directory)) pending.Push(path);
            }
        }
        return source;
    }

    private FileAttributes ValidateWebFileOperationItem(string path)
    {
        if (IsWebDownloadIncomplete(path)) throw new WebFileInProgressException();
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("This operation cannot include symbolic links or junctions.");
        }
        return attributes;
    }

    private static bool IsWebFileAtOrBelow(string path, string ancestor) =>
        WebQuickAccessPathComparer.Equals(path, ancestor) ||
        path.StartsWith(Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> MoveWebClipboardItem(string source, string destination, bool folder)
    {
        var windows = OperatingSystem.IsWindows();
        var crossVolume = windows && !WebQuickAccessPathComparer.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination));
        if (!crossVolume)
        {
            try
            {
                await Task.Run(() => MoveWebFile(source, destination, folder));
                return true;
            }
            catch (IOException error) when (windows && (error.HResult & 0xffff) == 17)
            {
                // A volume mount point can lie under the same drive letter as the source.
            }
        }

        const uint renameOnCollision = 0x0008;
        const uint addUndoRecord = 0x20000000;
        var parent = Path.GetDirectoryName(destination)!;
        await RunWebShellFileOperation(source, parent, renameOnCollision | addUndoRecord,
            (operation, item, target) => operation.MoveItem(item, target!, Path.GetFileName(destination), null));
        // The shell can choose a different name if a competing process created the destination.
        return false;
    }

    private static void CreateWebDirectoryPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateDirectoryW(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        else
        {
            if (Path.Exists(path)) throw new IOException("An item with that name already exists.");
            Directory.CreateDirectory(path);
        }
    }

    private static void MoveWebFile(string source, string destination, bool folder)
    {
        if (OperatingSystem.IsWindows() && WebQuickAccessPathComparer.Equals(source, destination))
        {
            if (!MoveFileExW(source, destination, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        else if (folder)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static string WebFileCopyStagingPath(string destination) =>
        Path.Combine(Path.GetDirectoryName(destination)!, $"{WebFileCopyStagingPrefix}{Guid.NewGuid():N}");

    private void CopyWebFile(string source, string destination)
    {
        ValidateWebFileOperationItem(source);
        var temporary = WebFileCopyStagingPath(destination);
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination);
        }
        finally
        {
            CleanWebFileCopyStaging(temporary, folder: false);
        }
    }

    private void CopyWebDirectory(string source, string destination)
    {
        var temporary = WebFileCopyStagingPath(destination);
        CreateWebDirectoryPath(temporary);
        try
        {
            var pending = new Stack<(string Source, string Target)>();
            var copied = new Stack<(string Source, string Target, FileAttributes Attributes)>();
            pending.Push((source, temporary));
            while (pending.TryPop(out var folder))
            {
                var attributes = ValidateWebFileOperationItem(folder.Source);
                foreach (var path in Directory.EnumerateFileSystemEntries(folder.Source))
                {
                    var child = Path.Combine(folder.Target, Path.GetFileName(path));
                    if (ValidateWebFileOperationItem(path).HasFlag(FileAttributes.Directory))
                    {
                        CreateWebDirectoryPath(child);
                        pending.Push((path, child));
                    }
                    else
                    {
                        File.Copy(path, child);
                    }
                }
                copied.Push((folder.Source, folder.Target, attributes));
            }

            const FileAttributes copiedAttributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.ReadOnly;
            while (copied.TryPop(out var folder))
            {
                Directory.SetLastWriteTimeUtc(folder.Target, Directory.GetLastWriteTimeUtc(folder.Source));
                var attributes = folder.Attributes & copiedAttributes;
                if (attributes != 0) File.SetAttributes(folder.Target, attributes);
            }
            Directory.Move(temporary, destination);
        }
        finally
        {
            CleanWebFileCopyStaging(temporary, folder: true);
        }
    }

    private static void CleanWebFileCopyStaging(string path, bool folder)
    {
        // Only the private copy is eligible for cleanup after a failed copy or move.
        try
        {
            if (folder && Directory.Exists(path))
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return;
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(path, "*", options)) ClearReadOnly(file);
                Directory.Delete(path, recursive: true);
            }
            else if (!folder && File.Exists(path))
            {
                ClearReadOnly(path);
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        static void ClearReadOnly(string file)
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static Task RecycleWebFile(string path)
    {
        const uint noConfirmation = 0x0010;
        const uint recycleOnDelete = 0x00080000;
        const uint addUndoRecord = 0x20000000;
        return RunWebShellFileOperation(path, null, noConfirmation | recycleOnDelete | addUndoRecord,
            (operation, item, _) => operation.DeleteItem(item, new WebRecycleProgressSink()));
    }

    private static Task RunWebShellFileOperation(string path, string? target, uint flags,
        Action<IWebShellFileOperation, IWebShellItem, IWebShellItem?> enqueue)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows file operations are unavailable on this device.");
        }

        // Shell file operations need an STA, and large directory moves should leave the launcher responsive.
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                PerformWebShellFileOperation(path, target, flags, enqueue);
                result.SetResult();
            }
            catch (Exception error)
            {
                result.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "Web shell file operation"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return result.Task;
    }

    private static void PerformWebShellFileOperation(string path, string? target, uint flags,
        Action<IWebShellFileOperation, IWebShellItem, IWebShellItem?> enqueue)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        const uint silent = 0x0004;
        const uint noErrorUi = 0x0400;
        const uint noConnectedElements = 0x2000;
        const uint earlyFailure = 0x00100000;
        var operationClass = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), throwOnError: true)!;
        var operation = (IWebShellFileOperation)Activator.CreateInstance(operationClass)!;
        IWebShellItem? item = null;
        IWebShellItem? destination = null;
        try
        {
            item = CreateShellFileItem(path);
            if (target != null) destination = CreateShellFileItem(target);
            operation.SetOperationFlags(flags | silent | noErrorUi | noConnectedElements | earlyFailure);
            enqueue(operation, item, destination);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || Path.Exists(path))
            {
                throw new IOException(target == null ? "Windows could not move this item to the Recycle Bin." :
                    "Windows could not finish moving this item to the destination.");
            }
        }
        finally
        {
            if (destination != null) Marshal.ReleaseComObject(destination);
            if (item != null) Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(operation);
        }

        static IWebShellItem CreateShellFileItem(string path)
        {
            var shellItem = typeof(IWebShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItem, out var item));
            return item;
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        out IWebShellItem item);

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWebShellItem;

    // The unused COM members retain the slots of the native IFileOperation interface.
    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWebShellFileOperation
    {
        void Advise();
        void Unadvise();
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow();
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem(IWebShellItem item, IWebShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IWebShellFileOperationProgressSink? progressSink);
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem(IWebShellItem item, IWebShellFileOperationProgressSink progressSink);
        void DeleteItems();
        void NewItem();
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWebShellFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr newName);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr newItem);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newItem);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination, IntPtr newName);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class WebRecycleProgressSink : IWebShellFileOperationProgressSink
    {
        private const uint DeleteRecycle = 0x0080;
        private const int Abort = unchecked((int)0x80004004);

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, IntPtr newName) => 0;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr newItem) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newItem) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newItem) => 0;
        // An unavailable Recycle Bin must not turn a regular Delete into a permanent deletion.
        public int PreDeleteItem(uint flags, IntPtr item) => (flags & DeleteRecycle) != 0 ? 0 : Abort;
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem) => 0;
        public int PreNewItem(uint flags, IntPtr destination, IntPtr newName) => 0;
        public int PostNewItem(uint flags, IntPtr destination, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
