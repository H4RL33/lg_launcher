using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LGLauncher;

internal sealed record ModRenoDxOption(string Title, string? Variant, string? Status, string? Api, string? Executable,
    string? Loader, string File, string Version, string Digest, long Size)
{
    internal static readonly ModDependencyLink ReShadeRequirement = new(ModProviderId.RenoDx, ModProviderId.ReShade, "full add-on support");
    internal string Label => String.Join(" · ", new[] { Title, Variant, Status is "beta" or "experimental" ? Status : null }
        .Where(value => !String.IsNullOrWhiteSpace(value)));
    internal ModPackageDescriptor Package => new(new(ModProviderId.RenoDx, File, Version, Digest),
        new(ModRenoDxCatalog.ReleaseFiles, File), Size, [File]);
    internal string ReShadeHelp => Api == "vulkan"
        ? "Install full add-on ReShade for Vulkan for this game first."
        : "Install full add-on ReShade for this game using its graphics API first.";

    internal bool Supports(ModInstallationRecord? record)
    {
        if (record?.HasDependency(ReShadeRequirement) != true) return false;
        if (!String.IsNullOrWhiteSpace(Executable) &&
            !Path.GetFileName(record.Target.Executable).Equals(Executable, StringComparison.OrdinalIgnoreCase)) return false;
        if (record.ReShadeVulkan) return Api is null or "" or "vulkan";
        if (Api == "vulkan") return false;
        var loader = record.ReShadeChain != null ? "dxgi.dll" :
            Path.GetFileName(record.Files.First(file => file.Owner == ModProviderId.ReShade).Relative);
        var expected = String.IsNullOrWhiteSpace(Loader) ? Api switch
        {
            "d3d9" => "d3d9.dll",
            "d3d10" or "d3d11" or "d3d12" => "dxgi.dll",
            "opengl" => "opengl32.dll",
            _ => null
        } : Loader;
        return expected == null || expected.Equals(loader, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ModRenoDxGame(string Title, int SteamId, string[] Aliases, ModRenoDxOption[] Options);

internal sealed class ModRenoDxCatalog
{
    internal static readonly Uri Source = new("https://renodx.com/games-index.json");
    internal static readonly Uri Release = new("https://api.github.com/repos/clshortfuse/renodx/releases/tags/snapshot");
    internal static readonly Uri ReleaseFiles = new("https://github.com/clshortfuse/renodx/releases/download/snapshot/");
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = 20 };
    private readonly string _cache;
    private readonly SemaphoreSlim _refresh = new(1);
    private ModRenoDxGame[]? _games;

    static ModRenoDxCatalog() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("LGLauncher/" + UpdateFeed.CurrentVersion);

    internal ModRenoDxCatalog(string profile)
    {
        _cache = Path.Combine(profile, "mods", "renodx-catalog-v1.json");
        try
        {
            ModPaths.RejectReparsePoints(_cache);
            if (File.Exists(_cache) && new FileInfo(_cache).Length <= MaximumBytes)
                _games = ModInstallState.Read<ModRenoDxGame[]>(_cache);
        }
        catch (Exception error) when (ModTransactions.ModFailure(error)) { }
    }

    internal bool Available => _games != null;

    internal ModRenoDxGame? Find(Game game)
    {
        if (_games is not { } games) return null;
        if (game.SteamId > 0 && games.FirstOrDefault(entry => entry.SteamId == game.SteamId) is { } byId) return byId;
        var title = ModCompatibility.Normalize(game.Title);
        if (title.Length == 0) return null;
        var matches = games.Where(entry => (game.SteamId == 0 || entry.SteamId == 0) &&
            (ModCompatibility.Normalize(entry.Title) == title || entry.Aliases.Any(alias => ModCompatibility.Normalize(alias) == title)))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal async Task<bool> RefreshAsync(CancellationToken token, HttpClient? http = null)
    {
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var client = http ?? Client;
            var indexRequest = Download(client, Source, deadline.Token);
            var releaseRequest = Download(client, Release, deadline.Token);
            await Task.WhenAll(indexRequest, releaseRequest).ConfigureAwait(false);
            var games = Parse(await indexRequest.ConfigureAwait(false), await releaseRequest.ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            ModPaths.RejectReparsePoints(_cache);
            Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
            ModInstallState.Write(_cache, games);
            _games = games;
            return true;
        }
        catch (Exception error) when (ModTransactions.ModFailure(error) || error is HttpRequestException ||
            (error is OperationCanceledException && !token.IsCancellationRequested))
        {
            return false;
        }
        finally { _refresh.Release(); }
    }

    private static async Task<byte[]> Download(HttpClient client, Uri address, CancellationToken token)
    {
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("The RenoDX catalog is too large.");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("The RenoDX catalog is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static ModRenoDxGame[] Parse(byte[] indexBytes, byte[] releaseBytes)
    {
        var release = JsonSerializer.Deserialize<ReleaseData>(releaseBytes, Json);
        if (release is not { TagName: "snapshot", Assets.Length: > 0 and <= 5000 })
            throw new InvalidDataException("The RenoDX snapshot release is invalid.");
        var assets = release.Assets.Where(asset => asset != null && asset.Name != null)
            .ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        if (!assets.TryGetValue("games-index.json", out var index) || index.Size != indexBytes.Length ||
            !String.Equals(index.Hash, Convert.ToHexString(SHA256.HashData(indexBytes)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RenoDX website index does not match the official snapshot.");

        var catalog = JsonSerializer.Deserialize<GameIndex>(indexBytes, Json);
        if (catalog is not { Games.Length: > 0 and <= 5000 })
            throw new InvalidDataException("The RenoDX game index is invalid.");
        var games = catalog.Games.Where(game => game != null && !String.IsNullOrWhiteSpace(game.Title))
            .Select(game => new ModRenoDxGame(game.Title, game.SteamAppid ?? game.Deploy?.SteamAppid ?? 0,
                game.Aliases ?? [], ReadOptions(game, assets).DistinctBy(option => option.File).ToArray())).ToArray();
        if (!games.Any(game => game.Options.Length > 0))
            throw new InvalidDataException("The RenoDX snapshot included no verifiable x64 game add-ons.");
        return games;
    }

    private static IEnumerable<ModRenoDxOption> ReadOptions(GameData game, Dictionary<string, Asset> assets)
    {
        foreach (var mod in game.Mods ?? [])
        {
            if (mod is not { Category: "game" } || mod.Compatibility == "in-progress") continue;
            foreach (var artifact in mod.Artifacts ?? [])
            {
                if (artifact is not { Arch: "x64" } || !IsAddonName(artifact.Name) ||
                    !assets.TryGetValue(artifact.Name, out var asset) || asset.Size != artifact.Size ||
                    asset.Size is <= 0 or > 256L * 1024 * 1024 || asset.Hash is not { } hash || !VerifiedDownload.IsSha256(hash) ||
                    asset.BrowserDownloadUrl != new Uri(ReleaseFiles, artifact.Name).AbsoluteUri) continue;
                yield return new(mod.Title ?? game.Title, mod.Variant, mod.Status,
                    mod.Deploy?.Api ?? game.Deploy?.Api, mod.Deploy?.GameExe ?? game.Deploy?.GameExe,
                    mod.Deploy?.ReshadePreferredName ?? game.Deploy?.ReshadePreferredName, artifact.Name,
                    asset.UpdatedAt.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture), hash.ToUpperInvariant(), asset.Size);
            }
        }
    }

    internal static bool IsAddonName(string? name) => name is { Length: > 15 and <= 128 } &&
        name.StartsWith("renodx-", StringComparison.Ordinal) && name.EndsWith(".addon64", StringComparison.Ordinal) &&
        name.All(character => Char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private sealed record GameIndex(GameData[] Games);
    private sealed record GameData(string Title, int? SteamAppid, string[]? Aliases, Deployment? Deploy, ModData[]? Mods);
    private sealed record ModData(string? Title, string? Variant, string? Status, string? Category, string? Compatibility,
        Deployment? Deploy, Artifact[]? Artifacts);
    private sealed record Deployment(int? SteamAppid, string? Api, string? GameExe, string? ReshadePreferredName);
    private sealed record Artifact(string Name, string? Arch, long Size);
    private sealed record ReleaseData(string TagName, Asset[] Assets);
    private sealed record Asset(string Name, string? Digest, long Size, string? BrowserDownloadUrl, DateTimeOffset UpdatedAt)
    {
        internal string? Hash => Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? Digest[7..] : null;
    }
}
