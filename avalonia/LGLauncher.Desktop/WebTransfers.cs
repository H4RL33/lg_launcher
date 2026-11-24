using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using MonoTorrent;
using MonoTorrent.Client;

namespace LGLauncher;

internal enum WebDownloadState
{
    Active,
    Completed,
    Cancelled,
    Failed
}

internal readonly record struct WebDownloadProgress(long Received, long? Total, WebDownloadState State, string? Detail = null);

internal sealed record WebTransferSnapshot(string Name, WebDownloadProgress Progress, string? OutputPath = null);

internal sealed class WebTransfer(WebTransferSnapshot initial, Action cancel, InfoHashes? torrentHash = null)
{
    private WebTransferSnapshot _snapshot = initial;
    private Action? _cancel = cancel;

    internal WebTransferSnapshot Snapshot
    {
        get => Volatile.Read(ref _snapshot);
        set => Volatile.Write(ref _snapshot, value);
    }

    internal InfoHashes? TorrentHash { get; } = torrentHash;
    internal bool CanCancel => Volatile.Read(ref _cancel) != null && Snapshot.Progress.State == WebDownloadState.Active;

    internal void RequestCancel()
    {
        var cancel = Interlocked.Exchange(ref _cancel, null);
        if (cancel == null || Snapshot.Progress.State != WebDownloadState.Active) return;
        try
        {
            cancel();
        }
        catch
        {
            if (Snapshot.Progress.State == WebDownloadState.Active)
                Interlocked.CompareExchange(ref _cancel, cancel, null);
            throw;
        }
    }

    internal void ReleaseCancellation() => Interlocked.Exchange(ref _cancel, null);
}

internal sealed class WebTransfers(string directory)
{
    internal const string StagingDirectoryName = ".transfers";
    private const string StageOwnerFileName = ".owner";
    private const string SessionPrefix = "session-";
    private const int MaximumNameLength = 120;
    private const int MaximumStageExtensionLength = 16;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly char[] Separators = ['/', '\\'];
    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    private readonly string _directory = Path.GetFullPath(directory);
    private readonly List<WebTransfer> _items = [];
    private readonly Dictionary<string, string> _reservedBrowserPaths = new(PathComparer);
    private readonly List<BrowserDownload> _browserDownloads = [];
    private readonly List<Task> _magnetTasks = [];
    private ClientEngine? _engine;
    private string? _engineDirectory;
    private string? _sessionDirectory;
    private FileStream? _sessionOwner;
    private bool _stopping;
    private Task? _shutdown;

    internal IReadOnlyList<WebTransfer> Items => _items;
    internal event Action? Added;

    // The browser callbacks, reservations and queue membership belong to the UI thread.
    internal string ReserveBrowserPath(string suggestedName)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var stage = CreateStageDirectory();
        var name = SafeName(suggestedName, "Download");
        var extension = Path.GetExtension(name);
        // WebView2 writes its own temporary suffix, so give it a short path and retain the intended name separately.
        var stageExtension = extension.Length <= MaximumStageExtensionLength ? extension : string.Empty;
        var path = Path.Combine(stage, "file" + stageExtension);
        _reservedBrowserPaths.Add(path, name);
        return path;
    }

    internal void TrackBrowserDownload(string path, Func<WebDownloadProgress> snapshot, Action cancel)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (!_reservedBrowserPaths.Remove(path, out var name))
            throw new ArgumentException("Reserve a download destination before tracking it.", nameof(path));

        var transfer = new WebTransfer(new(name, new(0, null, WebDownloadState.Active)), cancel);
        _browserDownloads.Add(new(transfer, path, snapshot));
        _items.Add(transfer);
        Added?.Invoke();
    }

    internal void ReleaseBrowserPath(string path)
    {
        if (_reservedBrowserPaths.Remove(path)) DeleteStage(Path.GetDirectoryName(path));
    }

    internal void PollBrowserDownloads()
    {
        for (var index = _browserDownloads.Count - 1; index >= 0; index--)
        {
            var download = _browserDownloads[index];
            var transfer = download.Transfer;
            var current = transfer.Snapshot;
            var stage = Path.GetDirectoryName(download.Path);
            try
            {
                var progress = download.GetProgress();
                if (progress.State == WebDownloadState.Active)
                {
                    transfer.Snapshot = current with { Progress = progress };
                    continue;
                }

                string? output = null;
                if (progress.State == WebDownloadState.Completed)
                {
                    CheckRegularPath(download.Path);
                    output = Publish(download.Path, current.Name, isDirectory: false);
                }

                transfer.Snapshot = new(output == null ? current.Name : Path.GetFileName(output), progress, output);
            }
            catch (Exception error) when (IsTransferFailure(error))
            {
                transfer.Snapshot = current with { Progress = current.Progress with { State = WebDownloadState.Failed, Detail = error.Message } };
            }

            transfer.ReleaseCancellation();
            _browserDownloads.RemoveAt(index);
            DeleteStage(stage);
        }
    }

    internal Task AddMagnetAsync(string input)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var value = input.Trim();
        if (!value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || value.Length > 8192 ||
            !MagnetLink.TryParse(value, out var link) || link == null)
            throw new ArgumentException("Enter a magnet link with a valid BitTorrent info hash.", nameof(input));

        if (_items.Any(item => item.Snapshot.Progress.State == WebDownloadState.Active &&
            item.TorrentHash is { } hash && SharesHash(hash, link.InfoHashes)))
            throw new InvalidOperationException("This magnet is already in Downloads.");

        var engine = GetEngine();
        var cancellation = new CancellationTokenSource();
        var name = SafeName(link.Name, "Magnet download");
        var transfer = new WebTransfer(new(name, new(0, null, WebDownloadState.Active, "Finding torrent metadata…")),
            cancellation.Cancel, link.InfoHashes);
        _items.Add(transfer);
        _magnetTasks.RemoveAll(task => task.IsCompleted);
        _magnetTasks.Add(Task.Run(() => RunMagnetAsync(engine, link, transfer, cancellation)));
        Added?.Invoke();
        return Task.CompletedTask;
    }

    private ClientEngine GetEngine()
    {
        if (_engine != null) return _engine;

        var cache = CreateStageDirectory();
        try
        {
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = cache,
                DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
                ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Any, 0) },
                AllowPortForwarding = false,
                AllowLocalPeerDiscovery = false,
                AutoSaveLoadDhtCache = false,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
                // MonoTorrent resolves metadata paths before returning them. This setting prevents moves during resolution.
                UsePartialFiles = true
            };
            _engine = new ClientEngine(settings.ToSettings());
            _engineDirectory = cache;
            return _engine;
        }
        catch
        {
            DeleteStage(cache);
            throw;
        }
    }

    private async Task RunMagnetAsync(ClientEngine engine, MagnetLink link, WebTransfer transfer, CancellationTokenSource cancellation)
    {
        string? stage = null;
        TorrentManager? manager = null;
        var token = cancellation.Token;
        var current = transfer.Snapshot;
        WebTransferSnapshot outcome;
        try
        {
            using var metadataCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            metadataCancellation.CancelAfter(MetadataTimeout);
            Torrent torrent;
            try
            {
                var metadata = await engine.DownloadMetadataAsync(link, metadataCancellation.Token).ConfigureAwait(false);
                torrent = Torrent.Load(metadata.Span);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("No peers supplied the torrent metadata. Check the link and try again.");
            }

            token.ThrowIfCancellationRequested();
            ValidatePaths(torrent.Files.Select(file => file.Path));
            current = new(SafeName(torrent.Name, current.Name), new(0, torrent.Size, WebDownloadState.Active, "Preparing torrent…"));
            transfer.Snapshot = current;

            stage = CreateStageDirectory();
            var settings = new TorrentSettingsBuilder { CreateContainingDirectory = false };
            manager = await engine.AddAsync(torrent, stage, settings.ToSettings()).ConfigureAwait(false);
            ValidatePaths(manager.Files.SelectMany(file => new[]
            {
                Path.GetRelativePath(stage, file.DownloadCompleteFullPath),
                Path.GetRelativePath(stage, file.DownloadIncompleteFullPath)
            }));

            token.ThrowIfCancellationRequested();
            await manager.StartAsync().ConfigureAwait(false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (manager.State == TorrentState.Error)
                    throw new IOException("The torrent failed while downloading.", manager.Error?.Exception);
                if (manager.State == TorrentState.Seeding && manager.Complete) break;

                var received = (long)(torrent.Size * Math.Clamp(manager.Progress / 100, 0, 1));
                var detail = manager.OpenConnections == 0 ? "Looking for peers…" : null;
                current = current with { Progress = new(received, torrent.Size, WebDownloadState.Active, detail) };
                transfer.Snapshot = current;
                await Task.Delay(350, token).ConfigureAwait(false);
            }

            var files = manager.Files.ToArray();
            await StopTorrentAsync(engine, manager).ConfigureAwait(false);
            manager = null;
            token.ThrowIfCancellationRequested();

            foreach (var file in files)
            {
                var path = file.DownloadCompleteFullPath;
                if (file.Length == 0 && !File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = new FileStream(path, FileMode.CreateNew);
                }

                CheckRegularPath(path);
                if (new FileInfo(path).Length != file.Length)
                    throw new IOException("A completed torrent file was missing or had an unexpected length.");
            }

            var firstFile = files[0].DownloadCompleteFullPath;
            var isSingleFile = files.Length == 1 && PathComparer.Equals(Path.GetDirectoryName(firstFile), stage);
            var output = isSingleFile
                ? Publish(firstFile, Path.GetFileName(firstFile), isDirectory: false)
                : Publish(stage, current.Name, isDirectory: true);
            outcome = new(Path.GetFileName(output), new(torrent.Size, torrent.Size, WebDownloadState.Completed), output);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = current with { Progress = current.Progress with { State = WebDownloadState.Cancelled, Detail = null } };
        }
        catch (Exception error)
        {
            var detail = error.InnerException?.Message ?? error.Message;
            outcome = current with { Progress = current.Progress with { State = WebDownloadState.Failed, Detail = detail } };
        }
        finally
        {
            if (manager != null)
            {
                try
                {
                    await StopTorrentAsync(engine, manager).ConfigureAwait(false);
                }
                catch (Exception error) when (IsTransferFailure(error))
                {
                    // The engine may still own the staged files if it failed to stop.
                    stage = null;
                }
            }

            DeleteStage(stage);
            transfer.ReleaseCancellation();
            cancellation.Dispose();
        }
        transfer.Snapshot = outcome;
    }

    private static async Task StopTorrentAsync(ClientEngine engine, TorrentManager manager)
    {
        await manager.StopAsync(StopTimeout).ConfigureAwait(false);
        await engine.RemoveAsync(manager).ConfigureAwait(false);
    }

    private static bool SharesHash(InfoHashes first, InfoHashes second) =>
        first.V1 != null && first.V1.Equals(second.V1) || first.V2 != null && first.V2.Equals(second.V2);

    private static void ValidatePaths(IEnumerable<string> relativePaths)
    {
        var files = new HashSet<string>(PathComparer);
        var directories = new HashSet<string>(PathComparer);
        foreach (var path in relativePaths)
        {
            var parts = path.Split(Separators);
            if (parts.Any(part => !IsSafeComponent(part)))
                throw new IOException("The torrent contains a filename that is unsafe on Windows.");

            var canonical = string.Join('/', parts);
            if (!files.Add(canonical) || directories.Contains(canonical))
                throw new IOException("The torrent contains conflicting file paths.");

            for (var separator = canonical.LastIndexOf('/'); separator >= 0; separator = canonical.LastIndexOf('/', separator - 1))
            {
                var parent = canonical[..separator];
                if (files.Contains(parent)) throw new IOException("The torrent contains conflicting file paths.");
                if (!directories.Add(parent)) break;
            }
        }
    }

    private string CreateStageDirectory()
    {
        // Reservation and engine construction initialise the session on the UI thread before a torrent worker uses it.
        var session = _sessionDirectory ?? StartStageSession();
        CheckRegularPath(_directory);
        CheckRegularPath(Path.Combine(_directory, StagingDirectoryName));
        CheckRegularPath(session);
        var stage = Path.Combine(session, Guid.NewGuid().ToString("N")[..16]);
        if (Path.Exists(stage)) throw new IOException("Could not reserve a new download directory.");
        EnsureRegularDirectory(stage);
        return stage;
    }

    private string StartStageSession()
    {
        EnsureRegularDirectory(_directory);
        var stagingRoot = Path.Combine(_directory, StagingDirectoryName);
        EnsureRegularDirectory(stagingRoot);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = Path.Combine(stagingRoot, SessionPrefix + sessionId);
        if (Path.Exists(session)) throw new IOException("Could not reserve a new download session.");
        EnsureRegularDirectory(session);

        var markerPath = Path.Combine(session, StageOwnerFileName);
        FileStream? owner = null;
        try
        {
            owner = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            owner.Write(StageOwnerMarker(sessionId));
            owner.Flush();
            _sessionDirectory = session;
            _sessionOwner = owner;
        }
        catch
        {
            owner?.Dispose();
            DeleteStage(session);
            throw;
        }

        _ = Task.Run(() => ReclaimStaleStages(stagingRoot, session));
        return session;
    }

    private static byte[] StageOwnerMarker(string sessionId) => Encoding.ASCII.GetBytes($"LG Launcher web transfers 1\n{sessionId}\n");

    private void ReclaimStaleStages(string stagingRoot, string currentSession)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot, SessionPrefix + "*"))
            {
                var name = Path.GetFileName(directory);
                if (PathComparer.Equals(directory, currentSession) || !name.StartsWith(SessionPrefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[SessionPrefix.Length..], "N", out var id) || name != SessionPrefix + id.ToString("N")) continue;

                try
                {
                    CheckRegularPath(directory);
                    var markerPath = Path.Combine(directory, StageOwnerFileName);
                    CheckRegularPath(markerPath);
                    using (var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        var expected = StageOwnerMarker(id.ToString("N"));
                        if (marker.Length != expected.Length) continue;
                        var actual = new byte[expected.Length];
                        marker.ReadExactly(actual);
                        if (!actual.AsSpan().SequenceEqual(expected)) continue;
                    }

                    // A live launcher keeps the marker locked until its transfers stop.
                    DeleteStage(directory);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Skip incomplete markers, inaccessible paths and live sessions owned by another launcher.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unavailable staging root does not prevent the active session from continuing.
        }
    }

    private string Publish(string source, string name, bool isDirectory)
    {
        CheckRegularPath(_directory);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        var stem = name[..(name.Length - extension.Length)];
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? name : $"{stem} ({index}){extension}";
            var destination = Path.Combine(_directory, candidate);
            if (Path.Exists(destination)) continue;

            try
            {
                // The move itself checks the destination again if another transfer wins this name.
                if (isDirectory) Directory.Move(source, destination);
                else File.Move(source, destination);
                return destination;
            }
            catch (IOException) when (Path.Exists(destination))
            {
                // Retry with the next suffix.
            }
        }
    }

    private static void EnsureRegularDirectory(string path)
    {
        Directory.CreateDirectory(path);
        CheckRegularPath(path);
    }

    private static void CheckRegularPath(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The download location contains a symbolic link or junction.");
    }

    private void DeleteStage(string? path)
    {
        if (path == null || !Directory.Exists(path)) return;
        try
        {
            var stagingRoot = Path.Combine(_directory, StagingDirectoryName);
            var parent = Path.GetDirectoryName(path);
            var session = PathComparer.Equals(parent, stagingRoot) ? path : parent;
            if (session == null || !PathComparer.Equals(Path.GetDirectoryName(session), stagingRoot)) return;

            CheckRegularPath(_directory);
            CheckRegularPath(stagingRoot);
            CheckRegularPath(session);
            CheckRegularPath(path);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Inaccessible staging data stays outside the published downloads.
        }
    }

    private static string SafeName(string? suggested, string fallback)
    {
        var leaf = suggested?.Split(Separators)[^1] ?? string.Empty;
        var name = new string(leaf.Select(character => IsInvalidNameCharacter(character) ? '_' : character).ToArray()).Trim().TrimEnd(' ', '.');
        if (name.Length == 0) return fallback;
        if (IsDeviceName(name)) name = "_" + name;

        var extension = Path.GetExtension(name);
        if (extension.Length > MaximumStageExtensionLength) extension = string.Empty;
        if (name.Length > MaximumNameLength)
        {
            var limit = MaximumNameLength - extension.Length;
            if (char.IsHighSurrogate(name[limit - 1])) limit--;
            name = name[..limit].TrimEnd(' ', '.') + extension;
        }

        return name;
    }

    private static bool IsSafeComponent(string name) =>
        name.Length is > 0 and <= 255 && !name.EndsWith('.') && !name.EndsWith(' ') &&
        !name.Any(IsInvalidNameCharacter) && !IsDeviceName(name);

    private static bool IsInvalidNameCharacter(char character) =>
        char.IsControl(character) || InvalidNameCharacters.Contains(character);

    private static bool IsDeviceName(string name)
    {
        var device = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(device[3]);
    }

    private static bool IsTransferFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            FormatException or TimeoutException or TorrentException or COMException;

    internal Task StopAsync()
    {
        if (_shutdown != null) return _shutdown;
        _stopping = true;
        _shutdown = StopTransfersAsync();
        return _shutdown;
    }

    private async Task StopTransfersAsync()
    {
        using var deadline = new CancellationTokenSource(ShutdownTimeout);
        foreach (var item in _items)
        {
            try
            {
                item.RequestCancel();
            }
            catch (Exception error) when (IsTransferFailure(error))
            {
                // A browser operation can already have disconnected during shutdown.
            }
        }

        foreach (var path in _reservedBrowserPaths.Keys) DeleteStage(Path.GetDirectoryName(path));
        _reservedBrowserPaths.Clear();
        var engineCleanup = Task.Run(StopEngineAsync);
        try
        {
            // Browser snapshots belong to the UI thread. Wait for the native operation to release the temp file.
            PollBrowserDownloads();
            while (_browserDownloads.Count > 0)
            {
                await Task.Delay(75, deadline.Token);
                PollBrowserDownloads();
            }

            await engineCleanup.WaitAsync(deadline.Token);
            await Task.Run(ReleaseStageSession).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Any remaining stage stays owned until this process exits. A future session can reclaim it.
        }
    }

    private async Task StopEngineAsync()
    {
        try
        {
            try
            {
                await Task.WhenAll(_magnetTasks).ConfigureAwait(false);
            }
            finally
            {
                _engine?.Dispose();
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Could not stop the torrent engine: {0}", error.Message);
        }
        finally
        {
            DeleteStage(_engineDirectory);
        }
    }

    private void ReleaseStageSession()
    {
        _sessionOwner?.Dispose();
        DeleteStage(_sessionDirectory);
    }

    private sealed record BrowserDownload(WebTransfer Transfer, string Path, Func<WebDownloadProgress> GetProgress);
}
