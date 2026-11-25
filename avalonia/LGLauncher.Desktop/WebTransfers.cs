using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using ReusableTasks;

namespace LGLauncher;

internal enum WebDownloadState
{
    Active,
    Stopped,
    Completed,
    Cancelled,
    Failed
}

internal readonly record struct WebDownloadProgress(long Received, long? Total, WebDownloadState State, string? Detail = null,
    long? DownloadRate = null, long? UploadRate = null, double? Ratio = null);

internal sealed record WebTransferSnapshot(string Name, WebDownloadProgress Progress, string? OutputPath = null);

internal sealed class WebTransfer(WebTransferSnapshot initial, Action cancel, string targetDirectory, InfoHashes? torrentHash = null)
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private WebTransferSnapshot _snapshot = initial;
    private Action? _cancel = cancel;

    internal WebTransferSnapshot Snapshot
    {
        get => Volatile.Read(ref _snapshot);
        set => Volatile.Write(ref _snapshot, value);
    }

    internal InfoHashes? TorrentHash { get; } = torrentHash;
    internal string TargetDirectory { get; } = targetDirectory;
    internal Func<Task>? Start { get; set; }
    internal Func<Task>? Stop { get; set; }
    internal string? Stage { get; set; }
    internal string? RetainedStage { get; set; }
    internal Action? DeleteOutput { get; set; }
    internal bool CanStart => Start != null && Snapshot.Progress.State == WebDownloadState.Stopped;
    internal bool CanStop => Stop != null && Snapshot.Progress.State == WebDownloadState.Active;
    internal bool CanCancel => Volatile.Read(ref _cancel) != null &&
        Snapshot.Progress.State is WebDownloadState.Active or WebDownloadState.Stopped;

    internal Task RequestStartAsync() => RunCommandAsync(() =>
        CanStart && Start is { } start ? start() : Task.CompletedTask);

    internal Task RequestStopAsync() => RunCommandAsync(() =>
        CanStop && Stop is { } stop ? stop() : Task.CompletedTask);

    internal async Task RunCommandAsync(Func<Task> command)
    {
        await _commands.WaitAsync();
        try
        {
            await command();
        }
        finally
        {
            _commands.Release();
        }
    }

    internal void RequestCancel()
    {
        if (!CanCancel) return;
        var cancel = Interlocked.Exchange(ref _cancel, null);
        if (cancel == null) return;
        try
        {
            cancel();
        }
        catch
        {
            Interlocked.CompareExchange(ref _cancel, cancel, null);
            throw;
        }
    }

    internal void ReleaseCommands()
    {
        Interlocked.Exchange(ref _cancel, null);
        Start = null;
        Stop = null;
    }
}

internal sealed class WebTransfers(string directory, string dataDirectory,
    Func<(bool EnableDht, string? BindAddress, string? Protocol)>? networkSettings = null)
{
    internal const string StagingDirectoryName = ".transfers";
    private const string StageOwnerFileName = ".owner";
    private const int StageSessionVersion = 2;
    private const string SessionPrefix = "session-";
    private const string PartialStageSeparator = ".lg-transfer-";
    private const string PartialStageSuffix = ".part";
    private const int PartialStageIdLength = 16;
    private const int MaximumStageLabelLength = 32;
    internal const double DefaultStopRatio = 1d;
    private const int MaximumNameLength = 120;
    private const int MaximumStageExtensionLength = 16;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumTorrentDescriptorBytes = 8 * 1024 * 1024;
    private const int MaximumTorrentRedirects = 5;
    private static readonly TimeSpan TorrentDescriptorTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient TorrentDescriptorClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly char[] Separators = ['/', '\\'];
    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    private readonly string _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    private readonly List<WebTransfer> _items = [];
    private readonly Dictionary<string, BrowserReservation> _reservedBrowserPaths = new(PathComparer);
    private readonly List<BrowserDownload> _browserDownloads = [];
    private readonly Dictionary<WebTransfer, MagnetDownload> _magnetDownloads = [];
    private readonly HashSet<Task> _removals = [];
    private readonly ConcurrentDictionary<string, StageOwner> _stageOwners = new(PathComparer);
    private readonly object _stageMarker = new();
    private ClientEngine? _engine;
    private WebDhtEngine? _dht;
    private string? _engineDirectory;
    private readonly Dictionary<string, StageSession> _stageSessions = new(PathComparer);
    private bool _stopping;
    private Task? _shutdown;
    private double _stopRatio = DefaultStopRatio;

    internal IReadOnlyList<WebTransfer> Items => _items;
    internal event Action? Added;

    internal void SetStopRatio(double? ratio) =>
        Volatile.Write(ref _stopRatio, ratio is >= 0 && double.IsFinite(ratio.Value) ? ratio.Value : DefaultStopRatio);

    private bool ShouldSeed(double? ratio) => Volatile.Read(ref _stopRatio) > (ratio ?? 0);

    // The browser callbacks, reservations and queue membership belong to the UI thread.
    internal string ReserveBrowserPath(string suggestedName, string? targetDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var name = SafeName(suggestedName, "Download");
        var target = ResolveTargetDirectory(targetDirectory);
        var stage = CreateStageDirectory(target, name);
        var extension = Path.GetExtension(name);
        // WebView2 writes its own temporary suffix, so give it a short path and retain the intended name separately.
        var stageExtension = extension.Length <= MaximumStageExtensionLength ? extension : string.Empty;
        var path = Path.Combine(stage, "file" + stageExtension);
        SetExpectedStageEntries(stage, [path, path + ".crdownload"]);
        _reservedBrowserPaths.Add(path, new(name, target));
        return path;
    }

    internal void TrackBrowserDownload(string path, Func<WebDownloadProgress> snapshot, Action cancel,
        Action? pause = null, Action? resume = null, Action? released = null)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (!_reservedBrowserPaths.TryGetValue(path, out var reservation))
            throw new ArgumentException("Reserve a download destination before tracking it.", nameof(path));

        var stage = Path.GetDirectoryName(path) ?? throw new IOException("The browser staging folder is unavailable.");
        var guard = GuardTransferStage(stage);
        _reservedBrowserPaths.Remove(path);
        var transfer = new WebTransfer(new(reservation.Name, new(0, null, WebDownloadState.Active)), cancel, reservation.TargetDirectory)
        {
            Stage = stage
        };
        var download = new BrowserDownload(transfer, path, snapshot, pause, released, guard);
        if (pause != null) transfer.Stop = () => ControlBrowserAsync(download, pause, WebDownloadState.Active);
        if (resume != null) transfer.Start = () => ControlBrowserAsync(download, resume, WebDownloadState.Stopped);
        _browserDownloads.Add(download);
        _items.Add(transfer);
        if (!_stopping) Added?.Invoke();
    }

    internal void ReleaseBrowserPath(string path)
    {
        if (_reservedBrowserPaths.Remove(path)) DeleteStage(Path.GetDirectoryName(path));
    }

    private async Task ControlBrowserAsync(BrowserDownload download, Action control, WebDownloadState previous)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        control();
        await WaitForBrowserStateAsync(download, state => state == previous);
        if (!_stopping) Added?.Invoke();
    }

    private async Task WaitForBrowserStateAsync(BrowserDownload download, Func<WebDownloadState, bool> pending)
    {
        using var deadline = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            while (_browserDownloads.Contains(download))
            {
                PollBrowserDownloads();
                if (!_browserDownloads.Contains(download) || !pending(download.Transfer.Snapshot.Progress.State)) return;
                await Task.Delay(75, deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new IOException("The browser download is still changing state. Try again shortly.");
        }
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
                if (progress.State is WebDownloadState.Active or WebDownloadState.Stopped)
                {
                    transfer.Snapshot = current with { Progress = progress };
                    continue;
                }

                string? output = null;
                if (progress.State == WebDownloadState.Completed && !download.DeleteWhenFinished)
                {
                    if (download.Preserving) continue;
                    if (stage == null) throw new IOException("The browser staging folder is unavailable.");
                    using (GuardTransferStage(stage))
                    {
                        CheckRegularPath(download.Path);
                        var owned = CaptureFile(download.Path);
                        output = Publish(download.Path, current.Name, transfer.TargetDirectory, isDirectory: false);
                        RecordPublishedFile(transfer, output, owned);
                    }
                    download.Guard.Dispose();
                    DeleteStage(stage);
                    transfer.Stage = null;
                }

                transfer.Snapshot = new(output == null ? current.Name : Path.GetFileName(output), progress, output);
            }
            catch (Exception error) when (IsTransferFailure(error))
            {
                transfer.Snapshot = current with { Progress = current.Progress with { State = WebDownloadState.Failed, Detail = error.Message } };
            }

            download.Guard.Dispose();
            transfer.ReleaseCommands();
            _browserDownloads.RemoveAt(index);
            download.Released?.Invoke();
        }
    }

    internal static void ValidateLink(string input)
    {
        if (input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            _ = ParseMagnet(input);
        else
            _ = ParseTorrentUrl(input);
    }

    internal async Task AddLinkAsync(string input, string? destination = null)
    {
        var address = input.Trim();
        if (address.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            await AddMagnetAsync(address, destination);
            return;
        }

        var (_, torrent) = await DownloadTorrentDescriptorAsync(ParseTorrentUrl(address), CancellationToken.None);
        await AddTorrentAsync(torrent, destination);
    }

    internal Task AddTorrentAsync(Torrent torrent, string? destination = null) =>
        AddTorrentDownload(torrent.InfoHashes, torrent.Name, link: null, torrent, destination);

    internal static async Task<(byte[] Bytes, Torrent Torrent)> DownloadTorrentDescriptorAsync(Uri url,
        CancellationToken token, HttpClient? client = null)
    {
        if (!IsTorrentWebAddress(url) || !HasTorrentExtension(url))
            throw new ArgumentException("Enter a valid HTTP or HTTPS .torrent URL.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TorrentDescriptorTimeout);
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var response = await (client ?? TorrentDescriptorClient)
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (IsTorrentRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (redirects >= MaximumTorrentRedirects || location == null ||
                        !Uri.TryCreate(url, location, out var next) || !IsTorrentWebAddress(next) ||
                        url.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                        throw new IOException("The .torrent URL redirected to an unsupported address.");

                    url = next;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(MaximumTorrentDescriptorBytes, timeout.Token).ConfigureAwait(false);
                var descriptor = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                if (!Torrent.TryLoad(descriptor, out var torrent) || torrent == null)
                    throw new ArgumentException("The URL did not return a valid .torrent file.");

                ValidatePaths(torrent.Files.Select(file => file.Path));
                return (descriptor, torrent);
            }
        }
        catch (HttpRequestException error)
        {
            throw new IOException("Could not download the .torrent file. Check its URL and try again.", error);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The .torrent file did not respond in time.", error);
        }
    }

    private static bool IsTorrentRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsTorrentWebAddress(Uri address) =>
        address.IsAbsoluteUri && address.Scheme is "http" or "https" &&
        address.Host.Length != 0 && address.UserInfo.Length == 0;

    internal static bool HasTorrentExtension(Uri address) =>
        address.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    internal Task AddMagnetAsync(string input, string? destination = null)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var link = ParseMagnet(input.Trim());
        return AddTorrentDownload(link.InfoHashes, link.Name, link, torrent: null, destination);
    }

    internal static bool IsMagnetLink(string address) => TryParseMagnet(address) != null;

    private static MagnetLink? TryParseMagnet(string address) =>
        address.Length <= 8192 && address.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) &&
        MagnetLink.TryParse(address, out var link) ? link : null;

    private static MagnetLink ParseMagnet(string address) =>
        TryParseMagnet(address) ?? throw new ArgumentException("Enter a magnet link with a valid BitTorrent info hash.");

    private static Uri ParseTorrentUrl(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !IsTorrentWebAddress(url) || !HasTorrentExtension(url))
            throw new ArgumentException("Enter a valid magnet link or an HTTPS .torrent URL.");

        return url;
    }

    private Task AddTorrentDownload(InfoHashes hashes, string? suggestedName, MagnetLink? link, Torrent? torrent, string? destination)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_items.Any(item => item.Snapshot.Progress.State is WebDownloadState.Active or WebDownloadState.Stopped &&
            item.TorrentHash is { } hash && SharesHash(hash, hashes)))
            throw new InvalidOperationException("This torrent is already in Downloads.");

        var target = ResolveTargetDirectory(destination);
        var name = SafeName(suggestedName, "Torrent download");
        var magnet = new MagnetDownload(GetEngine(), link) { Torrent = torrent };
        var transfer = new WebTransfer(new(name, new(0, torrent?.Size, WebDownloadState.Active)),
            () => CancelMagnet(magnet, WebDownloadState.Cancelled), target, hashes)
        {
            Stage = CreateStageDirectory(target, name),
        };
        transfer.Start = () => StartMagnetAsync(magnet, transfer);
        transfer.Stop = () => StopMagnetAsync(magnet);
        _magnetDownloads.Add(transfer, magnet);
        _items.Add(transfer);
        return StartMagnetAsync(magnet, transfer);
    }

    private string ResolveTargetDirectory(string? directory)
    {
        var target = directory == null ? _directory : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (IsStagingDestination(target) || IsIncompletePath(target))
            throw new IOException("Choose a folder outside the transfer staging area.");

        if (!PathComparer.Equals(target, _directory))
        {
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The selected download folder no longer exists.");
            CheckRegularPath(target);
        }

        return target;
    }

    private Task StartMagnetAsync(MagnetDownload download, WebTransfer transfer)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        download.Cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        download.Cancellation = cancellation;
        var current = transfer.Snapshot;
        var detail = download.Torrent == null ? "Finding torrent metadata…" : "Checking saved progress…";
        transfer.Snapshot = current with
        {
            Progress = current.Progress with { State = WebDownloadState.Active, Detail = detail }
        };
        download.Run = Task.Run(() => RunMagnetAsync(download, transfer, cancellation.Token));
        if (!_stopping) Added?.Invoke();
        return Task.CompletedTask;
    }

    private async Task StopMagnetAsync(MagnetDownload download)
    {
        CancelMagnet(download, WebDownloadState.Stopped);
        await download.Run;
        if (!_stopping) Added?.Invoke();
    }

    private static void CancelMagnet(MagnetDownload download, WebDownloadState state)
    {
        if (download.Run.IsCompleted) return;
        if (download.Cancellation is { IsCancellationRequested: false } cancellation)
        {
            download.CancellationState = state;
            cancellation.Cancel();
        }
    }

    private ClientEngine GetEngine()
    {
        if (_engine != null) return _engine;

        var network = networkSettings?.Invoke() ?? (true, null, null);
        var listeners = TorrentListenEndPoints(network.BindAddress, network.Protocol);
        var dhtEndPoint = network.EnableDht ? listeners.GetValueOrDefault("ipv4") : null;
        EnsureRegularDirectory(dataDirectory);
        var cache = CreateStageDirectory(dataDirectory);
        try
        {
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = cache,
                DhtEndPoint = dhtEndPoint,
                ListenEndPoints = listeners,
                AllowPortForwarding = false,
                AllowLocalPeerDiscovery = false,
                AutoSaveLoadDhtCache = false,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
                WebSeedDelay = TimeSpan.FromSeconds(15),
                // MonoTorrent resolves metadata paths before returning them. This setting prevents moves during resolution.
                UsePartialFiles = true
            };
            var factories = Factories.Default;
            foreach (var scheme in new[] { "ipv4", "ipv6" })
            {
                if (listeners.GetValueOrDefault(scheme) is { } local)
                {
                    var connector = new BoundPeerConnector(local);
                    factories = factories.WithPeerConnectionCreator(scheme, uri => new SocketPeerConnection(uri, connector));
                }
                else
                {
                    factories = factories.WithPeerConnectionCreator(scheme, _ => null!);
                }
            }

            // The pinned DHT uses compact IPv4 nodes and an IPv4 UDP listener.
            var dht = dhtEndPoint == null ? null : new WebDhtEngine(dataDirectory);
            if (dht != null) factories = factories.WithDhtCreator(() => dht);
            try
            {
                _engine = new ClientEngine(settings.ToSettings(), factories);
                _dht = dht;
            }
            catch
            {
                dht?.Dispose();
                throw;
            }
            _engineDirectory = cache;
            return _engine;
        }
        catch
        {
            DeleteStage(cache);
            throw;
        }
    }

    private static Dictionary<string, IPEndPoint> TorrentListenEndPoints(string? bindAddress, string? protocol)
    {
        IPAddress? address = null;
        if (!string.IsNullOrWhiteSpace(bindAddress) &&
            (!IPAddress.TryParse(bindAddress.Trim(), out address) || address.IsIPv4MappedToIPv6))
            throw new ArgumentException("Enter a local IPv4 or IPv6 address for the torrent bind address.");

        var useIPv4 = protocol != "IPv6";
        var useIPv6 = protocol is "IPv6" or "IPv4+IPv6";
        if (address?.AddressFamily == AddressFamily.InterNetwork && !useIPv4 ||
            address?.AddressFamily == AddressFamily.InterNetworkV6 && !useIPv6)
            throw new ArgumentException("The torrent bind address must match the selected protocol.");

        var listeners = new Dictionary<string, IPEndPoint>();
        if (useIPv4)
            listeners.Add("ipv4", new(address?.AddressFamily == AddressFamily.InterNetwork ? address : IPAddress.Any, 0));
        if (useIPv6)
            listeners.Add("ipv6", new(address?.AddressFamily == AddressFamily.InterNetworkV6 ? address : IPAddress.IPv6Any, 0));
        return listeners;
    }

    private sealed class BoundPeerConnector(IPEndPoint local) : ISocketConnector
    {
        public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            var address = IPAddress.Parse(uri.Host);
            if (address.AddressFamily != local.AddressFamily || address.IsIPv4MappedToIPv6)
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);

            var socket = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(local);
                await socket.ConnectAsync(new IPEndPoint(address, uri.Port), cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private async Task RunMagnetAsync(MagnetDownload download, WebTransfer transfer, CancellationToken token)
    {
        var engine = download.Engine;
        TorrentManager? manager = null;
        SafeFileHandle? stageGuard = null;
        var seedGuards = new List<SafeFileHandle>();
        var current = transfer.Snapshot;
        WebTransferSnapshot outcome;
        try
        {
            var torrent = download.Torrent;
            var stage = transfer.Stage ?? throw new IOException("The torrent staging folder is unavailable.");
            stageGuard = GuardTransferStage(stage);
            var settings = new TorrentSettingsBuilder
            {
                CreateContainingDirectory = false,
                AllowDht = engine.Settings.DhtEndPoint != null,
            }.ToSettings();
            if (settings.AllowDht && _dht is { } dht)
                await dht.PrepareBootstrapAsync().WaitAsync(token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            manager = torrent == null
                ? await engine.AddAsync(download.Link!, stage, settings).ConfigureAwait(false)
                : await engine.AddAsync(torrent, stage, settings).ConfigureAwait(false);
            download.Manager = manager;
            torrent ??= await FetchMetadataAsync(manager, token).ConfigureAwait(false);
            ValidatePaths(torrent.Files.Select(file => file.Path));
            var payloadPaths = manager.Files.SelectMany(file => new[]
            {
                file.DownloadCompleteFullPath,
                file.DownloadIncompleteFullPath
            }).ToArray();
            ValidatePaths(payloadPaths.Select(path => Path.GetRelativePath(stage, path)));
            SetExpectedStageEntries(stage, payloadPaths);
            download.Torrent = torrent;
            current = new(SafeName(torrent.Name, current.Name),
                new(current.Progress.Received, torrent.Size, WebDownloadState.Active, "Checking saved progress…"));
            transfer.Snapshot = current;

            token.ThrowIfCancellationRequested();
            await manager.StartAsync().ConfigureAwait(false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (manager.State == TorrentState.Error)
                    throw new IOException("The torrent failed while downloading.", manager.Error?.Exception);
                if (manager.State == TorrentState.Seeding && manager.Complete) break;

                var received = manager.HashChecked ? (long)(torrent.Size * Math.Clamp(manager.Progress / 100, 0, 1)) : current.Progress.Received;
                var detail = !manager.HashChecked ? "Checking saved progress…" : manager.OpenConnections == 0 ? "Looking for peers…" : null;
                var monitor = manager.Monitor;
                current = current with
                {
                    Progress = new(received, torrent.Size, WebDownloadState.Active, detail,
                        monitor.DownloadRate, monitor.UploadRate, download.RatioWith(monitor))
                };
                transfer.Snapshot = current;
                await Task.Delay(350, token).ConfigureAwait(false);
            }

            var files = manager.Files.ToArray();
            download.RecordPayload(manager.Monitor);
            current = current with { Progress = new(torrent.Size, torrent.Size, WebDownloadState.Active, "Finalizing…", Ratio: download.Ratio) };
            transfer.Snapshot = current;
            await manager.StopAsync(StopTimeout).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _ = InspectTransferStage(stage).Count();

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
            string output;
            OwnedFile[] publishedFiles;
            OwnedFile[] publishedFolders = [];
            if (isSingleFile)
            {
                var owned = CaptureFile(firstFile);
                output = Publish(firstFile, Path.GetFileName(firstFile), transfer.TargetDirectory, isDirectory: false);
                RecordPublishedFile(transfer, output, owned);
                publishedFiles = [new(Path.GetFileName(output), owned)];
            }
            else
            {
                publishedFiles = files.Select(file => new OwnedFile(Path.GetRelativePath(stage, file.DownloadCompleteFullPath),
                    CaptureFile(file.DownloadCompleteFullPath))).ToArray();
                publishedFolders = publishedFiles.SelectMany(file => ParentDirectories(file.RelativePath))
                    .Distinct(PathComparer).Select(folder => new OwnedFile(folder, CaptureFile(Path.Combine(stage, folder)))).ToArray();
                stageGuard.Dispose();
                stageGuard = null;
                output = Publish(stage, current.Name, transfer.TargetDirectory, isDirectory: true);
                transfer.DeleteOutput = () => DeletePublishedDirectory(output, transfer.TargetDirectory, publishedFiles, publishedFolders);
            }

            stageGuard?.Dispose();
            stageGuard = null;
            DeleteStage(stage);
            transfer.Stage = null;
            current = new(Path.GetFileName(output), current.Progress, output);
            if (torrent.Size > 0 && ShouldSeed(download.Ratio))
            {
                transfer.Snapshot = current;
                var publishedRoot = isSingleFile ? transfer.TargetDirectory : output;
                await SeedPublishedTorrentAsync(download, transfer, manager, publishedRoot, publishedFiles, publishedFolders, seedGuards, token)
                    .ConfigureAwait(false);
            }
            outcome = current with { Progress = new(torrent.Size, torrent.Size, WebDownloadState.Completed) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = current with
            {
                Progress = current.Progress with
                {
                    State = current.OutputPath == null ? download.CancellationState : WebDownloadState.Completed,
                    Detail = null
                }
            };
        }
        catch (Exception error)
        {
            var detail = error.InnerException?.Message ?? error.Message;
            outcome = current with
            {
                Progress = current.Progress with
                {
                    State = current.OutputPath == null ? WebDownloadState.Failed : WebDownloadState.Completed,
                    Detail = detail
                }
            };
        }
        finally
        {
            if (manager != null)
            {
                try
                {
                    download.RecordPayload(manager.Monitor);
                    await StopTorrentAsync(engine, manager).ConfigureAwait(false);
                    download.Manager = null;
                }
                catch (Exception error) when (IsTransferFailure(error))
                {
                    // Keep the registration so removal can retry stopping before touching its files.
                }
            }
            stageGuard?.Dispose();
            foreach (var guard in seedGuards) guard.Dispose();
        }

        if (download.Manager != null)
            outcome = outcome with { Progress = outcome.Progress with { State = WebDownloadState.Failed, Detail = "Could not stop the torrent." } };
        outcome = outcome with { Progress = outcome.Progress with { Ratio = download.Ratio } };
        if (outcome.Progress.State != WebDownloadState.Stopped) transfer.ReleaseCommands();
        transfer.Snapshot = outcome;
    }

    private async Task SeedPublishedTorrentAsync(MagnetDownload download, WebTransfer transfer, TorrentManager manager,
        string publishedRoot, OwnedFile[] files, OwnedFile[] folders, List<SafeFileHandle> guards, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Protect the published path and verified bytes while the torrent engine reads them.
        guards.Add(GuardPublishedSeedPath(transfer.TargetDirectory, CaptureFile(transfer.TargetDirectory), isDirectory: true));
        foreach (var folder in folders.OrderBy(folder => folder.RelativePath.Length))
            guards.Add(GuardPublishedSeedPath(Path.Combine(publishedRoot, folder.RelativePath), folder.Stamp, isDirectory: true));
        for (var index = 0; index < files.Length; index++)
        {
            var path = Path.Combine(publishedRoot, files[index].RelativePath);
            guards.Add(GuardPublishedSeedPath(path, files[index].Stamp, isDirectory: false));
            SetPublishedTorrentFilePath(manager.Files[index], (path, path, path));
        }

        await manager.HashCheckAsync(autoStart: false).WaitAsync(token).ConfigureAwait(false);
        if (!manager.HashChecked || !manager.Complete)
            throw new IOException("The published torrent could not be verified for seeding.", manager.Error?.Exception);
        if (!ShouldSeed(download.Ratio)) return;

        token.ThrowIfCancellationRequested();
        download.BeginPayload();
        await manager.StartAsync().ConfigureAwait(false);
        var published = transfer.Snapshot;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (manager.State is TorrentState.Error or TorrentState.Downloading)
                throw new IOException("The published torrent could not continue seeding.", manager.Error?.Exception);

            var monitor = manager.Monitor;
            var ratio = download.RatioWith(monitor);
            if (!ShouldSeed(ratio)) return;
            if (manager.State == TorrentState.Seeding)
            {
                transfer.Snapshot = published with
                {
                    Progress = published.Progress with { Detail = "Seeding", DownloadRate = 0, UploadRate = monitor.UploadRate, Ratio = ratio },
                };
            }
            await Task.Delay(350, token).ConfigureAwait(false);
        }
    }

    private static SafeFileHandle GuardPublishedSeedPath(string path, FileStamp? expected, bool isDirectory)
    {
        var guard = OpenArtifact(path, canDelete: false, denyMove: true, readContents: true, denyWrite: !isDirectory);
        if (expected != null && !guard.IsInvalid && ReadStamp(guard) is { } current &&
            (isDirectory ? current.Identity == expected.Identity : current == expected))
            return guard;

        guard.Dispose();
        throw new IOException("The published torrent files changed before seeding could start.");
    }

    // The pinned MonoTorrent public move methods also move files and can pick a partial-file path.
    // The manager is stopped here, and publication has already renamed the verified files.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdatePaths")]
    private static extern void SetPublishedTorrentFilePath(
        [UnsafeAccessorType("MonoTorrent.Client.TorrentFileInfo, MonoTorrent.Client")] object file,
        (string Full, string Complete, string Incomplete) paths);

    private static async Task<Torrent> FetchMetadataAsync(TorrentManager manager, CancellationToken token)
    {
        using var metadataCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        metadataCancellation.CancelAfter(MetadataTimeout);
        try
        {
            await StartTorrentWithMetadataOnly(manager, metadataOnly: true).ConfigureAwait(false);
            await manager.WaitForMetadataAsync(metadataCancellation.Token).ConfigureAwait(false);
            return manager.Torrent ?? throw new IOException("The torrent metadata was unavailable.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("No peers supplied the torrent metadata. Try again or use the publisher’s .torrent link if available.");
        }
    }

    // The pinned MonoTorrent version exposes metadata-only startup internally. Use the same manager
    // so we keep its peer list, while validating all output paths before enabling payload downloads.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TorrentManager.StartAsync))]
    private static extern Task StartTorrentWithMetadataOnly(TorrentManager manager, bool metadataOnly);

    private static async Task StopTorrentAsync(ClientEngine engine, TorrentManager manager)
    {
        await manager.StopAsync(StopTimeout).ConfigureAwait(false);
        await engine.RemoveAsync(manager).ConfigureAwait(false);
    }

    internal async Task RemoveAsync(WebTransfer transfer, bool deleteFiles)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var removal = transfer.RunCommandAsync(() => RemoveTransferAsync(transfer, deleteFiles));
        _removals.Add(removal);
        try
        {
            await removal;
        }
        finally
        {
            _removals.Remove(removal);
        }
    }

    private async Task RemoveTransferAsync(WebTransfer transfer, bool deleteFiles)
    {
        if (!_items.Contains(transfer)) return;

        PollBrowserDownloads();
        if (_browserDownloads.FirstOrDefault(download => download.Transfer == transfer) is { } browser)
            await StopBrowserForRemovalAsync(browser, deleteFiles);

        if (_magnetDownloads.TryGetValue(transfer, out var magnet))
        {
            await StopMagnetAsync(magnet);
            if (magnet.Manager is { } manager)
            {
                await StopTorrentAsync(magnet.Engine, manager);
                magnet.Manager = null;
            }
        }

        if (deleteFiles)
        {
            if (transfer.DeleteOutput is { } removeOutput) await Task.Run(removeOutput);
        }
        else if (transfer.RetainedStage == null && transfer.Stage is { } stage)
        {
            transfer.RetainedStage = await Task.Run(() => PreserveStage(stage, copy: false));
        }

        if (transfer.Stage is { } remaining) await Task.Run(() => DeleteTransferStage(remaining));
        if (deleteFiles && transfer.RetainedStage is { } retained)
            await Task.Run(() => DeleteTransferStage(retained));
        else if (transfer.RetainedStage is { } preserved)
            await Task.Run(() => ReleaseTransferStage(preserved));

        if (_magnetDownloads.Remove(transfer, out var removed)) removed.Cancellation?.Dispose();
        transfer.ReleaseCommands();
        _items.Remove(transfer);
        if (!_stopping) Added?.Invoke();
    }

    private async Task StopBrowserForRemovalAsync(BrowserDownload browser, bool deleteFiles)
    {
        var transfer = browser.Transfer;
        browser.DeleteWhenFinished = deleteFiles;
        try
        {
            if (!deleteFiles && transfer.RetainedStage == null && transfer.Stage is { } stage)
            {
                if (transfer.Snapshot.Progress.State == WebDownloadState.Active && browser.Pause is { } pause)
                {
                    pause();
                    await WaitForBrowserStateAsync(browser, state => state == WebDownloadState.Active);
                }
                if (transfer.Snapshot.OutputPath == null) await CopyBrowserPartialAsync(browser, stage);
            }

            transfer.RequestCancel();
            await WaitForBrowserStateAsync(browser, _ => true);
        }
        finally
        {
            browser.DeleteWhenFinished = false;
        }
    }

    private async Task CopyBrowserPartialAsync(BrowserDownload browser, string stage)
    {
        var transfer = browser.Transfer;
        browser.Preserving = true;
        try
        {
            transfer.RetainedStage = await Task.Run(() => PreserveStage(stage, copy: true));
        }
        catch (IOException)
        {
            browser.Preserving = false;
            PollBrowserDownloads();
            if (transfer.Snapshot.OutputPath == null) throw;
        }
        finally
        {
            browser.Preserving = false;
        }

        PollBrowserDownloads();
        if (transfer.Snapshot.OutputPath != null && transfer.RetainedStage is { } retained)
        {
            await Task.Run(() => DeleteTransferStage(retained));
            transfer.RetainedStage = null;
        }
    }

    private string? PreserveStage(string stage, bool copy)
    {
        if (!Directory.Exists(stage)) return null;
        using var guard = GuardTransferStage(stage, canDelete: !copy);
        var files = InspectTransferStage(stage).ToArray();
        if (files.Length == 0) return null;

        var targetDirectory = GetStageDestination(stage) ?? throw new IOException("The selected download folder is unavailable.");
        var retainedRoot = Path.Combine(targetDirectory, StagingDirectoryName, "retained");
        EnsureRegularDirectory(retainedRoot);
        var destination = Path.Combine(retainedRoot, Guid.NewGuid().ToString("N"));
        if (!copy)
        {
            MoveOwnedDirectory(guard, destination);
            lock (_stageMarker)
            {
                if (_stageOwners.TryRemove(stage, out var owner))
                {
                    _stageOwners[destination] = owner with { Claim = null };
                    DeleteStageClaim(stage, _stageSessions[targetDirectory], owner.Claim);
                }
            }
            return destination;
        }

        EnsureRegularDirectory(destination);
        RegisterTransferStage(destination);
        try
        {
            // WebView2 can remove its partial file on cancel. Copy the paused bytes first.
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(stage, file) + ".partial";
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        catch
        {
            DeleteTransferStage(destination);
            throw;
        }
        return destination;
    }

    private void DeleteTransferStage(string stage)
    {
        if (!Directory.Exists(stage))
        {
            if (Path.Exists(stage)) MarkStageChanged(stage);
            ReleaseTransferStage(stage);
            return;
        }

        using var guard = GuardTransferStage(stage, canDelete: true);
        var folders = new List<string>();
        var files = InspectTransferStage(stage, folders).Select(path => (Path: path, Stamp: CaptureFile(path))).ToArray();
        foreach (var file in files) DeletePublishedPath(file.Path, stage, file.Stamp, isDirectory: false);
        foreach (var folder in folders.OrderByDescending(folder => folder.Length))
        {
            try
            {
                Directory.Delete(folder);
            }
            catch (IOException) when (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                // A new entry appeared after validation. Do not remove it.
            }
        }
        DeleteByHandle(guard, isDirectory: true);
        guard.Dispose();
        ReleaseTransferStage(stage);
    }

    private void RegisterTransferStage(string stage)
    {
        var stamp = CaptureFile(stage) ?? throw new IOException("Could not verify the download staging directory.");
        _stageOwners[stage] = new(stamp.Identity);
    }

    private void ReleaseTransferStage(string stage)
    {
        lock (_stageMarker)
        {
            if (!_stageOwners.TryRemove(stage, out var owner)) return;
            var destination = GetStageDestination(stage);
            if (destination == null || !_stageSessions.TryGetValue(destination, out var session)) return;
            DeleteStageClaim(stage, session, owner.Claim);
            if (_stageOwners.Keys.Any(other => PathComparer.Equals(GetStageDestination(other), destination))) return;

            // Drop the last handle in a destination so Files can rename or move the folder on Windows.
            CloseStageSession(session);
            _stageSessions.Remove(destination);
        }
    }

    private static void DeleteStageClaim(string stage, StageSession session, FileStamp? claim)
    {
        if (claim == null) return;
        try
        {
            DeletePublishedPath(Path.Combine(session.Path, Path.GetFileName(stage)), session.Path, claim, isDirectory: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The session lock still distinguishes a live transfer from stale bookkeeping.
        }
    }

    private void SetExpectedStageEntries(string stage, IEnumerable<string> paths)
    {
        if (!_stageOwners.TryGetValue(stage, out var owner))
            throw new IOException("The transfer no longer owns its staging folder.");

        var entries = paths.Select(path => Path.GetRelativePath(stage, path))
            .SelectMany(path => ParentDirectories(path).Append(path)).ToHashSet(PathComparer);
        _stageOwners[stage] = owner with { ExpectedEntries = entries };
    }

    private SafeFileHandle GuardTransferStage(string stage, bool canDelete = false)
    {
        try
        {
            CheckTransferStage(stage);
            var handle = OpenArtifact(stage, canDelete, denyMove: true);
            if (!handle.IsInvalid && _stageOwners.TryGetValue(stage, out var owner) &&
                ReadStamp(handle) is { } stamp && stamp.Identity == owner.Identity)
                return handle;

            handle.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MarkStageChanged(stage);
            throw;
        }

        MarkStageChanged(stage);
        throw new IOException("The transfer staging directory changed. Its files have been left in place.");
    }

    private void MarkStageChanged(string stage)
    {
        // An ambiguous stage also prevents stale cleanup of its session on the next launch.
        var destination = GetStageDestination(stage);
        lock (_stageMarker)
        {
            if (destination != null && _stageSessions.TryGetValue(destination, out var session) && session.Owner.CanWrite)
                session.Owner.SetLength(0);
        }
    }

    private static string? GetStageDestination(string stage)
    {
        var parent = Path.GetDirectoryName(stage);
        if (IsVisibleStageName(Path.GetFileName(stage))) return parent;

        var root = parent == null ? null : Path.GetDirectoryName(parent);
        return root != null && PathComparer.Equals(Path.GetFileName(root), StagingDirectoryName) ? Path.GetDirectoryName(root) : null;
    }

    internal static bool IsIncompletePath(string path) => IsIncompletePath(path, out _);

    internal static bool IsIncompletePath(string path, out bool insideVisibleStage)
    {
        insideVisibleStage = false;
        for (var folder = path; folder != null; folder = Path.GetDirectoryName(folder))
        {
            if (!IsVisibleStageName(Path.GetFileName(folder))) continue;
            insideVisibleStage = true;
            if (HasLiveStageClaim(folder)) return true;
        }
        return false;
    }

    private static bool HasLiveStageClaim(string stage)
    {
        if (Path.GetDirectoryName(stage) is not { } destination) return false;
        var root = Path.Combine(destination, StagingDirectoryName);
        try
        {
            CheckRegularPath(root);
            foreach (var session in Directory.EnumerateDirectories(root, SessionPrefix + "*"))
            {
                if (!TryGetStageSessionId(session, out var id)) continue;
                try
                {
                    CheckRegularPath(session);
                    var ownerPath = Path.Combine(session, StageOwnerFileName);
                    CheckRegularPath(ownerPath);
                    try
                    {
                        using var idle = new FileStream(ownerPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        continue;
                    }
                    catch (IOException error) when (IsSharingViolation(error))
                    {
                        // The session's writer is still open. Read the marker with compatible sharing.
                    }

                    try
                    {
                        using var owner = new FileStream(ownerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (owner.Length != 0 && !MatchesMarker(owner, StageOwnerMarker(id))) continue;
                    }
                    catch (IOException error) when (IsSharingViolation(error))
                    {
                        // An older launcher locks its marker exclusively and has no per-stage claims.
                        return true;
                    }

                    var claimPath = Path.Combine(session, Path.GetFileName(stage));
                    CheckRegularPath(claimPath);
                    using var claim = new FileStream(claimPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    var stamp = CaptureFile(stage);
                    if (stamp == null || MatchesMarker(claim, StageClaimMarker(stamp.Identity))) return true;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    // An absent marker or claim provides no evidence of a live stage.
                }
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // Legacy partial directories can remain without a staging root or session marker.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the path protected when access fails and ownership cannot be ruled out.
            return true;
        }
        return false;
    }

    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xffff) == 32;

    private static bool IsVisibleStageName(string name)
    {
        var suffixStart = name.Length - PartialStageSuffix.Length;
        var idStart = suffixStart - PartialStageIdLength;
        var separatorStart = idStart - PartialStageSeparator.Length;
        if (separatorStart <= 0 || !name.EndsWith(PartialStageSuffix, StringComparison.OrdinalIgnoreCase) ||
            !name.AsSpan(separatorStart, PartialStageSeparator.Length).Equals(PartialStageSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var character in name.AsSpan(idStart, PartialStageIdLength))
        {
            if (!char.IsAsciiHexDigit(character)) return false;
        }
        return true;
    }

    internal static bool IsStagingDestination(string path)
    {
        for (var folder = path; folder != null; folder = Path.GetDirectoryName(folder))
        {
            if (PathComparer.Equals(Path.GetFileName(folder), StagingDirectoryName)) return true;
        }
        return false;
    }

    private void CheckTransferStage(string stage)
    {
        var destination = GetStageDestination(stage);
        var parent = Path.GetDirectoryName(stage);
        if (destination == null || parent == null) throw new IOException("The transfer no longer owns its staging folder.");
        var stagingRoot = Path.Combine(destination, StagingDirectoryName);
        lock (_stageMarker)
        {
            if (!_stageSessions.TryGetValue(destination, out var session) ||
                (!PathComparer.Equals(parent, session.Path) && !PathComparer.Equals(parent, Path.Combine(stagingRoot, "retained")) &&
                    !PathComparer.Equals(parent, destination)))
                throw new IOException("The transfer no longer owns its staging folder.");
        }

        CheckRegularPath(destination);
        CheckRegularPath(stagingRoot);
        CheckRegularPath(parent);
        CheckRegularPath(stage);
    }

    private IEnumerable<string> InspectTransferStage(string stage, List<string>? folders = null)
    {
        CheckTransferStage(stage);
        var expectedEntries = _stageOwners.GetValueOrDefault(stage)?.ExpectedEntries;
        var pending = new Stack<string>();
        pending.Push(stage);
        while (pending.TryPop(out var directory))
        {
            CheckRegularPath(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (expectedEntries != null && !expectedEntries.Contains(Path.GetRelativePath(stage, entry)))
                    throw new IOException("The partial download contains an unexpected item. Its files have been left in place.");

                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The transfer staging folder contains a symbolic link or junction.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    folders?.Add(entry);
                    pending.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    private static IEnumerable<string> ParentDirectories(string relativeFile)
    {
        for (var parent = Path.GetDirectoryName(relativeFile); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            yield return parent;
        yield return string.Empty;
    }

    private void RecordPublishedFile(WebTransfer transfer, string output, FileStamp? stamp) =>
        transfer.DeleteOutput = () => DeletePublishedPath(output, transfer.TargetDirectory, stamp, isDirectory: false);

    private void DeletePublishedDirectory(string output, string destination, OwnedFile[] files, OwnedFile[] folders)
    {
        var root = folders.Single(folder => folder.RelativePath.Length == 0);
        if (!MatchesPublishedDirectory(output, destination, root.Stamp)) return;

        foreach (var file in files)
            DeletePublishedPath(Path.Combine(output, file.RelativePath), destination, file.Stamp, isDirectory: false);
        foreach (var folder in folders.OrderByDescending(folder => folder.RelativePath.Length))
            DeletePublishedPath(Path.Combine(output, folder.RelativePath), destination, folder.Stamp, isDirectory: true);
    }

    private static bool MatchesPublishedDirectory(string path, string destination, FileStamp? stamp)
    {
        if (stamp == null || !IsRegularPublishedPath(path, destination)) return false;
        using var handle = OpenArtifact(path, canDelete: false);
        return !handle.IsInvalid && ReadStamp(handle) is { } current && current.Identity == stamp.Identity;
    }

    private static void DeletePublishedPath(string path, string destination, FileStamp? stamp, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows() || stamp == null || !IsRegularPublishedPath(path, destination)) return;
        using var handle = OpenArtifact(path, canDelete: true);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return;
            throw new IOException(new Win32Exception(error).Message);
        }

        var current = ReadStamp(handle);
        if (current == null || current.Identity != stamp.Identity || (!isDirectory && current != stamp)) return;

        // Use the verified handle so a path replacement cannot redirect the deletion.
        DeleteByHandle(handle, isDirectory);
    }

    private static void DeleteByHandle(SafeFileHandle handle, bool isDirectory)
    {
        byte delete = 1;
        if (SetFileInformationByHandle(handle, 4, ref delete, 1)) return;
        var failure = Marshal.GetLastWin32Error();
        if (isDirectory && failure == 145) return;
        throw new IOException(new Win32Exception(failure).Message);
    }

    private static void MoveOwnedDirectory(SafeFileHandle directory, string destination)
    {
        // FILE_RENAME_INFO has an aligned header and a NUL-terminated UTF-16 path.
        var nativePath = NativeFilePath(destination);
        var nameOffset = 2 * IntPtr.Size + sizeof(int);
        var data = new byte[nameOffset + Encoding.Unicode.GetByteCount(nativePath) + sizeof(char)];
        var nameLength = Encoding.Unicode.GetBytes(nativePath.AsSpan(), data.AsSpan(nameOffset));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2 * IntPtr.Size), nameLength);
        if (!SetFileRenameInformationByHandle(directory, 3, data, (uint)data.Length))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private static bool IsRegularPublishedPath(string path, string destination)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (!Path.Exists(current) || File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            if (PathComparer.Equals(current, destination)) return true;
        }
        return false;
    }

    private static FileStamp? CaptureFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = OpenArtifact(path, canDelete: false);
        return handle.IsInvalid ? null : ReadStamp(handle);
    }

    private static FileStamp? ReadStamp(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var data) || ((FileAttributes)data.Attributes).HasFlag(FileAttributes.ReparsePoint))
            return null;
        var identity = new FileIdentity(data.Volume, data.IndexHigh, data.IndexLow, FileTime(data.Created));
        return new(identity, FileTime(data.Modified), ((ulong)data.SizeHigh << 32) | data.SizeLow);
    }

    private static long FileTime(FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private static SafeFileHandle OpenArtifact(string path, bool canDelete, bool denyMove = false, bool readContents = false, bool denyWrite = false)
    {
        const uint readData = 0x01;
        const uint readAttributes = 0x80;
        const uint delete = 0x10000;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        var access = canDelete ? delete | readAttributes : readAttributes;
        // Windows enforces sharing on data access. An attribute-only handle cannot guard the payload.
        if (readContents) access |= readData;
        var sharing = canDelete || denyWrite ? FileShare.Read : FileShare.ReadWrite;
        if (!canDelete && !denyMove) sharing |= FileShare.Delete;
        return CreateFile(NativeFilePath(path), access, sharing, IntPtr.Zero, FileMode.Open, backupSemantics | openReparsePoint, IntPtr.Zero);
    }

    private static string NativeFilePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath :
            fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security,
        FileMode disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileRenameInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal FILETIME Created;
        internal FILETIME Accessed;
        internal FILETIME Modified;
        internal uint Volume;
        internal uint SizeHigh;
        internal uint SizeLow;
        internal uint LinkCount;
        internal uint IndexHigh;
        internal uint IndexLow;
    }

    private sealed record OwnedFile(string RelativePath, FileStamp? Stamp);
    private sealed record FileStamp(FileIdentity Identity, long Modified, ulong Size);
    private readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow, long Created);

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

    private string CreateStageDirectory(string destination, string? visibleLabel = null)
    {
        lock (_stageMarker)
        {
            if (!_stageSessions.TryGetValue(destination, out var session))
            {
                session = StartStageSession(destination);
                _stageSessions.Add(destination, session);
            }

            return CreateStageDirectory(destination, session, visibleLabel);
        }
    }

    private string CreateStageDirectory(string destination, StageSession session, string? visibleLabel)
    {
        CheckRegularPath(destination);
        CheckRegularPath(Path.Combine(destination, StagingDirectoryName));
        CheckRegularPath(session.Path);
        var id = Guid.NewGuid().ToString("N")[..PartialStageIdLength];
        var stage = Path.Combine(session.Path, id);
        if (Path.Exists(stage)) throw new IOException("Could not reserve a new download directory.");
        EnsureRegularDirectory(stage);
        RegisterTransferStage(stage);
        if (visibleLabel == null) return stage;

        // Move the verified directory to a visible name without replacing any existing path.
        // The launcher blocks this container and its contents from file actions until publication.
        var labelLength = Math.Min(MaximumStageLabelLength, visibleLabel.Length);
        if (char.IsHighSurrogate(visibleLabel[labelLength - 1])) labelLength--;
        var label = SafeName(visibleLabel[..labelLength], "Download");
        var visibleStage = Path.Combine(destination, label + PartialStageSeparator + id + PartialStageSuffix);
        FileStamp? claim = null;
        try
        {
            using var handle = GuardTransferStage(stage, canDelete: true);
            var owner = _stageOwners[stage];
            var claimPath = Path.Combine(session.Path, Path.GetFileName(visibleStage));
            using (var marker = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write))
                marker.Write(StageClaimMarker(owner.Identity));
            claim = CaptureFile(claimPath) ?? throw new IOException("Could not verify the visible staging claim.");
            MoveOwnedDirectory(handle, visibleStage);
            _stageOwners[visibleStage] = owner with { ExpectedEntries = new HashSet<string>(PathComparer), Claim = claim };
            _stageOwners.TryRemove(stage, out _);
            return visibleStage;
        }
        catch
        {
            DeleteStageClaim(visibleStage, session, claim);
            DeleteStage(stage);
            throw;
        }
    }

    private StageSession StartStageSession(string destination)
    {
        if (PathComparer.Equals(destination, _directory)) EnsureRegularDirectory(destination);
        else CheckRegularPath(destination);
        var stagingRoot = Path.Combine(destination, StagingDirectoryName);
        EnsureRegularDirectory(stagingRoot);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = Path.Combine(stagingRoot, SessionPrefix + sessionId);
        if (Path.Exists(session)) throw new IOException("Could not reserve a new download session.");
        EnsureRegularDirectory(session);
        var identity = CaptureFile(session)?.Identity ?? throw new IOException("Could not verify the download session directory.");

        var markerPath = Path.Combine(session, StageOwnerFileName);
        FileStream? owner = null;
        try
        {
            owner = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            owner.Write(StageOwnerMarker(sessionId));
            owner.Flush();
        }
        catch
        {
            owner?.Dispose();
            DeleteStage(session, stagingRoot);
            throw;
        }

        _ = Task.Run(() => ReclaimStaleStages(stagingRoot, session));
        return new StageSession(session, identity, owner);
    }

    private static byte[] StageOwnerMarker(string sessionId, int version = StageSessionVersion) =>
        Encoding.ASCII.GetBytes($"LG Launcher web transfers {version}\n{sessionId}\n");

    private static byte[] StageClaimMarker(FileIdentity identity) =>
        Encoding.ASCII.GetBytes($"LG Launcher visible transfer 1\n{identity.Volume:x8}{identity.IndexHigh:x8}{identity.IndexLow:x8}{identity.Created:x16}\n");

    private static bool MatchesMarker(FileStream stream, byte[] expected)
    {
        if (stream.Length != expected.Length) return false;
        stream.Position = 0;
        Span<byte> contents = stackalloc byte[expected.Length];
        stream.ReadExactly(contents);
        return contents.SequenceEqual(expected);
    }

    private static bool TryGetStageSessionId(string path, out string id)
    {
        var name = Path.GetFileName(path);
        id = name.StartsWith(SessionPrefix, StringComparison.Ordinal) ? name[SessionPrefix.Length..] : "";
        return Guid.TryParseExact(id, "N", out var parsed) && id == parsed.ToString("N");
    }

    private void ReclaimStaleStages(string stagingRoot, string currentSession)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot, SessionPrefix + "*"))
            {
                if (PathComparer.Equals(directory, currentSession) || !TryGetStageSessionId(directory, out var id)) continue;

                try
                {
                    CheckRegularPath(directory);
                    var markerPath = Path.Combine(directory, StageOwnerFileName);
                    CheckRegularPath(markerPath);
                    using (var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        if (!MatchesMarker(marker, StageOwnerMarker(id)) && !MatchesMarker(marker, StageOwnerMarker(id, version: 1))) continue;
                    }

                    // A live launcher keeps the marker locked until its transfers stop.
                    DeleteStage(directory, stagingRoot);
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

    private string Publish(string source, string name, string targetDirectory, bool isDirectory)
    {
        CheckRegularPath(targetDirectory);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        var stem = name[..(name.Length - extension.Length)];
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? name : $"{stem} ({index}){extension}";
            var destination = Path.Combine(targetDirectory, candidate);
            if (IsVisibleStageName(candidate) || Path.Exists(destination)) continue;

            try
            {
                // The move itself checks the destination again if another transfer wins this name.
                if (isDirectory)
                {
                    using (var guard = GuardTransferStage(source, canDelete: true)) MoveOwnedDirectory(guard, destination);
                    ReleaseTransferStage(source);
                }
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

    private void DeleteStage(string? path, string? stagingRoot = null)
    {
        if (path == null) return;
        try
        {
            if (_stageOwners.ContainsKey(path))
            {
                DeleteTransferStage(path);
                return;
            }

            if (!Directory.Exists(path)) return;
            stagingRoot ??= Path.Combine(_directory, StagingDirectoryName);
            if (!PathComparer.Equals(Path.GetDirectoryName(path), stagingRoot)) return;

            CheckRegularPath(Path.GetDirectoryName(stagingRoot)!);
            CheckRegularPath(stagingRoot);
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
        try
        {
            await Task.WhenAll(_removals.ToArray()).WaitAsync(deadline.Token);
        }
        catch (Exception error) when (IsTransferFailure(error) || error is OperationCanceledException)
        {
            // Continue shutdown after any interrupted user removal.
        }

        foreach (var item in _items.ToArray())
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
        var downloads = _magnetDownloads.Values.ToArray();
        var engineCleanup = Task.Run(() => StopEngineAsync(downloads));
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
            // Ownership lasts until the process exits. Visible bytes remain available for manual recovery.
        }
    }

    private async Task StopEngineAsync(MagnetDownload[] downloads)
    {
        try
        {
            try
            {
                var saveDht = _dht?.SaveAsync() ?? Task.CompletedTask;
                await Task.WhenAll(downloads.Select(download => download.Run).Append(saveDht)).ConfigureAwait(false);
                foreach (var download in downloads) download.Cancellation?.Dispose();
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
        KeyValuePair<string, StageSession>[] sessions;
        lock (_stageMarker) sessions = _stageSessions.ToArray();
        foreach (var (destination, entry) in sessions)
        {
            foreach (var stage in _stageOwners.Keys.Where(stage =>
                PathComparer.Equals(Path.GetDirectoryName(stage), entry.Path) ||
                IsVisibleStageName(Path.GetFileName(stage)) && PathComparer.Equals(Path.GetDirectoryName(stage), destination)))
            {
                DeleteStage(stage);
            }

            lock (_stageMarker)
            {
                if (_stageSessions.Remove(destination)) CloseStageSession(entry);
            }
        }
    }

    private static void CloseStageSession(StageSession entry)
    {
        try
        {
            using var session = OpenArtifact(entry.Path, canDelete: true);
            var owned = !session.IsInvalid && ReadStamp(session) is { } stamp && stamp.Identity == entry.Identity;
            var empty = owned && Directory.EnumerateFileSystemEntries(entry.Path)
                .All(path => Path.GetFileName(path) == StageOwnerFileName);
            if (!empty && entry.Owner.CanWrite) entry.Owner.SetLength(0);
            entry.Owner.Dispose();
            if (!empty) return;

            File.Delete(Path.Combine(entry.Path, StageOwnerFileName));
            DeleteByHandle(session, isDirectory: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder can become unavailable independently of the other destinations.
        }
        finally
        {
            entry.Owner.Dispose();
        }
    }

    private sealed record StageOwner(FileIdentity Identity, IReadOnlySet<string>? ExpectedEntries = null, FileStamp? Claim = null);

    private sealed record StageSession(string Path, FileIdentity Identity, FileStream Owner);

    private sealed record BrowserReservation(string Name, string TargetDirectory);

    private sealed record BrowserDownload(WebTransfer Transfer, string Path, Func<WebDownloadProgress> GetProgress,
        Action? Pause, Action? Released, SafeFileHandle Guard)
    {
        internal bool DeleteWhenFinished { get; set; }
        internal bool Preserving { get; set; }
    }

    private sealed class MagnetDownload(ClientEngine engine, MagnetLink? link)
    {
        private long _received;
        private long _sent;
        private ConnectionMonitor? _recorded;
        internal ClientEngine Engine { get; } = engine;
        internal MagnetLink? Link { get; } = link;
        internal Task Run { get; set; } = Task.CompletedTask;
        internal CancellationTokenSource? Cancellation { get; set; }
        internal WebDownloadState CancellationState { get; set; } = WebDownloadState.Cancelled;
        internal Torrent? Torrent { get; set; }
        internal TorrentManager? Manager { get; set; }
        internal double? Ratio => RatioWith(null);

        internal double? RatioWith(ConnectionMonitor? monitor)
        {
            if (monitor == _recorded) monitor = null;
            var received = _received + (monitor?.DataBytesReceived ?? 0);
            var sent = _sent + (monitor?.DataBytesSent ?? 0);
            return received > 0 ? (double)sent / received : null;
        }

        // MonoTorrent resets this manager's counters on Stop; the new run starts at zero.
        internal void BeginPayload() => _recorded = null;

        internal void RecordPayload(ConnectionMonitor monitor)
        {
            if (monitor == _recorded) return;
            _received += monitor.DataBytesReceived;
            _sent += monitor.DataBytesSent;
            _recorded = monitor;
        }
    }
}
