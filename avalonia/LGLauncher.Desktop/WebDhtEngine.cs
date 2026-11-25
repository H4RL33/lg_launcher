using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht;

namespace LGLauncher;

internal sealed class WebDhtEngine(string dataDirectory) : DhtEngine, IDhtEngine
{
    private const string CacheFileName = "dht-nodes.bin";
    private const int NodeIdLength = 20;
    private const int AddressLength = 4;
    private const int PortOffset = NodeIdLength + AddressLength;
    private const int NodeLength = PortOffset + sizeof(ushort);
    private const int MaximumCachedNodes = 2048;

    private readonly Lazy<Task<byte[]>> _bootstrapNodes = new(() => ReadBootstrapNodesAsync(dataDirectory));

    internal Task PrepareBootstrapAsync() => _bootstrapNodes.Value;

    Task IDhtEngine.StartAsync() => StartWithCacheAsync(ReadOnlyMemory<byte>.Empty);

    Task IDhtEngine.StartAsync(ReadOnlyMemory<byte> initialNodes) => StartWithCacheAsync(initialNodes);

    private async Task StartWithCacheAsync(ReadOnlyMemory<byte> initialNodes)
    {
        // Registering a torrent can replace pending nodes in MonoTorrent 3.0.2. Supply the
        // cache and routers here so they remain available alongside any torrent-provided nodes.
        var nodes = await _bootstrapNodes.Value.ConfigureAwait(false);
        if (!initialNodes.IsEmpty) nodes = [.. nodes, .. initialNodes.Span];
        await base.StartAsync(nodes).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBootstrapNodesAsync(string directory)
    {
        var cached = ReadNodesAsync(directory);
        var routers = ResolveRouterNodesAsync();
        return [.. await cached.ConfigureAwait(false), .. await routers.ConfigureAwait(false)];
    }

    private static async Task<byte[]> ReadNodesAsync(string directory)
    {
        try
        {
            var path = Path.Combine(directory, CacheFileName);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 4096, FileOptions.Asynchronous);
            var length = stream.Length;
            if (length <= 0 || length > MaximumCachedNodes * NodeLength || length % NodeLength != 0) return [];

            var nodes = new byte[(int)length];
            await stream.ReadExactlyAsync(nodes).ConfigureAwait(false);
            for (var offset = 0; offset < nodes.Length; offset += NodeLength)
            {
                if (!IsUsableNode(nodes.AsSpan(offset, NodeLength))) return [];
            }
            return nodes;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not read the torrent DHT cache: {0}", error.Message);
            return [];
        }
    }

    internal async Task SaveAsync()
    {
        var snapshot = await SaveNodesAsync().ConfigureAwait(false);
        var nodes = BEncodedValue.Decode<BEncodedList>(snapshot.Span).OfType<BEncodedString>()
            .Select(node => node.AsMemory()).Where(node => IsUsableNode(node.Span))
            .Take(MaximumCachedNodes).ToArray();
        if (nodes.Length == 0) return;

        var compact = new byte[nodes.Length * NodeLength];
        for (var index = 0; index < nodes.Length; index++)
        {
            nodes[index].CopyTo(compact.AsMemory(index * NodeLength, NodeLength));
        }

        var path = Path.Combine(dataDirectory, CacheFileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(dataDirectory);
            try
            {
                await File.WriteAllBytesAsync(temporary, compact).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not save the torrent DHT cache: {0}", error.Message);
        }
    }

    private static bool IsUsableNode(ReadOnlySpan<byte> node) =>
        node.Length == NodeLength && node[NodeIdLength] is > 0 and < 224 &&
        BinaryPrimitives.ReadUInt16BigEndian(node[PortOffset..]) != 0;

    private static async Task<byte[]> ResolveRouterNodesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        (string Host, ushort Port)[] routers =
        [
            ("router.bittorrent.com", 6881),
            ("router.utorrent.com", 6881),
            ("dht.transmissionbt.com", 6881),
            ("dht.libtorrent.org", 25401),
        ];
        var resolved = await Task.WhenAll(routers.Select(router => ResolveRouterAsync(router.Host, router.Port)))
            .ConfigureAwait(false);
        var endpoints = resolved.SelectMany(group => group).Distinct().ToArray();
        var nodes = new byte[endpoints.Length * NodeLength];
        for (var index = 0; index < endpoints.Length; index++)
        {
            var node = nodes.AsSpan(index * NodeLength, NodeLength);
            var endpoint = endpoints[index];
            // MonoTorrent learns the router's real node ID when it replies.
            RandomNumberGenerator.Fill(node[..NodeIdLength]);
            endpoint.Address.TryWriteBytes(node.Slice(NodeIdLength, AddressLength), out _);
            BinaryPrimitives.WriteUInt16BigEndian(node[PortOffset..], (ushort)endpoint.Port);
        }
        return nodes;

        async Task<IPEndPoint[]> ResolveRouterAsync(string host, ushort port)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, deadline.Token).ConfigureAwait(false);
                return addresses.Select(address => new IPEndPoint(address, port)).ToArray();
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                return [];
            }
        }
    }
}
