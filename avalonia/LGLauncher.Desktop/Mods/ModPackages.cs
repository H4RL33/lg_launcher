using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LGLauncher;

internal enum ModProviderId { OptiScaler, ReFramework, SpecialK, ReShade, RenoDx }

internal sealed record ModPackageKey(ModProviderId Provider, string Variant, string Version, string Digest)
{
    internal string StoreId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Provider}/{Variant}/{Version}/{Digest}")));
}

internal sealed record ModPackageDescriptor(ModPackageKey Key, Uri Address, long Length, string[] Files);
internal sealed record ModUpstreamRelease(string Version, Uri Page);
internal sealed record ModPackageCheck(string Status, ModPackageDescriptor? Download, ModUpstreamRelease? Release = null);
internal sealed record CachedModPackage(ModPackageKey Key, string ContentDirectory, Dictionary<string, string> Files,
    ModPackageDescriptor? Source = null);

internal sealed class ModPackageStore(string profile, HttpClient? http = null)
{
    internal const string ReShadeModule = "ReShade64.dll";
    internal const int ReShadeModuleLength = 5592064;
    internal const string ReShadeModuleDigest = "0CEE63F9C9F13F3AC909C5B4903F4DBB4B719A7AB3B4F13B0DEAF83C814B94F7";
    internal const string StandardReShadeModuleDigest = "B2945C29E7095491A901746B400E58DB9B1592AB092BACF2A888CE37F02D08DA";
    internal const string ReShadeVulkanManifest = "ReShade64.json";
    internal const string ReShadeVulkanManifestDigest = "AA21713718843E531DA396E2BFC80772C9CB3C369D6C30B836BE6B0AE812D503";
    internal static readonly ModProviderId[] FixedPackageProviders = [ModProviderId.OptiScaler, ModProviderId.ReFramework,
        ModProviderId.SpecialK, ModProviderId.ReShade];
    private sealed record Entry(ModPackageKey Key, Dictionary<string, string> Files, Uri? Address = null, long Length = 0);

    private static readonly HttpClient Downloads = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 256 * 1024
    };
    private readonly SemaphoreSlim _operation = new(1);
    private readonly string _root = Path.Combine(profile, "mods", "packages");
    private HttpClient Client => http ?? Downloads;

    static ModPackageStore() => Downloads.DefaultRequestHeaders.UserAgent.ParseAdd("LGLauncher/" + UpdateFeed.CurrentVersion);

    internal static string Name(ModProviderId provider) => provider switch
    {
        ModProviderId.OptiScaler => "OptiScaler",
        ModProviderId.ReFramework => "REFramework",
        ModProviderId.SpecialK => "Special K",
        ModProviderId.ReShade => "ReShade",
        ModProviderId.RenoDx => "RenoDX",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    internal static ModPackageDescriptor Approved(ModProviderId provider) => provider switch
    {
        ModProviderId.OptiScaler => new(
            new(provider, "stable", "v0.9.5", "77E3ED76BED04E1FB32BACB5A72799FF172F3DA22ED5231C0CF3E84FE6761662"),
            new("https://github.com/optiscaler/OptiScaler/releases/download/v0.9.5/Optiscaler_0.9.5-final.20261116.7z"),
            70418897,
            ["OptiScaler.dll", "OptiScaler.ini", "fakenvapi.dll", "fakenvapi.ini", "dlssg_to_fsr3_amd_is_better.dll",
                "amd_fidelityfx_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_framegeneration_vk.dll",
                "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_upscaler_vk.dll", "amd_fidelityfx_vk.dll",
                "libxell.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "D3D12_Optiscaler/D3D12Core.dll",
                "Licenses/DirectX_LICENSE.txt", "Licenses/FidelityFX_v1_LICENSE.md", "Licenses/FidelityFX_v2_LICENSE.md", "Licenses/XeSS_LICENSE.txt"]),
        ModProviderId.ReFramework => new(
            new(provider, "unified non-VR", "nightly-01441", "49A897335CEA7368885477426E90A524659329E2AC74DDE302EB5769BA777C11"),
            new("https://github.com/praydog/REFramework-nightly/releases/download/nightly-01441-49c21b26d50b23a333e9025743c8d45c46bb460e/REFramework.zip"),
            13279860, ["dinput8.dll"]),
        ModProviderId.SpecialK => new(
            new(provider, "local x64", "26.10.10", "21608BBD6BE1BA6DB90FB123402674C83DE1F7D7F4C17724332890FE0B53CB95"),
            new("https://sk-data.special-k.info/SpecialK.7z"), 11083566, ["SpecialK64.dll"]),
        ModProviderId.ReShade => new(
            new(provider, "full add-on support", "6.8.0", "AFE4C8F13048306307983B8B3D41D5BF00A86820440B0E57DEA10950E1176445"),
            new("https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe"), 4318424, [ReShadeModule, ReShadeVulkanManifest]),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    internal async Task<ModPackageCheck> CheckAsync(ModProviderId provider, CancellationToken token)
    {
        var approved = Approved(provider);
        if (provider == ModProviderId.ReShade)
        {
            var tags = await Client.GetStringAsync("https://api.github.com/repos/crosire/reshade/tags", token).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(tags);
            var latest = parsed.RootElement.EnumerateArray().Select(tag =>
            {
                var name = tag.GetProperty("name").GetString();
                return name is ['v', ..] && Version.TryParse(name.AsSpan(1), out var version) && version.Build >= 0 && version.Revision < 0
                    ? version : null;
            }).OfType<Version>().Max();
            if (latest == null) throw new InvalidDataException("The ReShade release list contained no stable versions.");
            return latest > Version.Parse(approved.Key.Version)
                ? new($"version {latest} available upstream (awaiting review)", approved,
                    new(latest.ToString(), new("https://reshade.me/#download")))
                : new(ApprovedStatus(), approved);
        }
        if (provider == ModProviderId.SpecialK)
        {
            try { await Download(approved, Stream.Null, token).ConfigureAwait(false); }
            catch (InvalidDataException) { return new("The official archive changed and is awaiting verification.", null); }
            return new(ApprovedStatus(), approved);
        }

        var repository = provider == ModProviderId.OptiScaler ? "optiscaler/OptiScaler" : "praydog/REFramework-nightly";
        var json = await Client.GetStringAsync($"https://api.github.com/repos/{repository}/releases/latest", token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "unknown";
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var officialMatch = assets.Any(asset =>
            asset.TryGetProperty("browser_download_url", out var address) && address.GetString() == approved.Address.AbsoluteUri &&
            asset.TryGetProperty("digest", out var digest) && String.Equals(digest.GetString(), "sha256:" + approved.Key.Digest, StringComparison.OrdinalIgnoreCase));
        if (officialMatch) return new(ApprovedStatus(), approved);
        var versionLabel = provider == ModProviderId.ReFramework ? String.Join('-', tag.Split('-').Take(2)) : tag;
        ModUpstreamRelease? upstream = null;
        if (IsNewer(provider, versionLabel, approved.Key.Version))
        {
            upstream = new(versionLabel, new($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag)}"));
            var candidates = assets.Select(asset => ReadUpdateAsset(asset, versionLabel, approved)).OfType<ModPackageDescriptor>().Take(2).ToArray();
            if (candidates.Length == 1)
            {
                var download = candidates[0];
                var cached = List(provider).Any(package => package.Key == download.Key);
                return new(cached ? $"up-to-date ({download.Key.Version})" : $"version {download.Key.Version} available", download, upstream);
            }
        }
        return new($"Upstream release {tag} is awaiting verification. Approved version {approved.Key.Version} remains available.", approved, upstream);

        string ApprovedStatus() => List(provider).Any(package => package.Key == approved.Key)
            ? "up-to-date"
            : $"version {approved.Key.Version} available";
    }

    internal static bool IsNewer(ModProviderId provider, string candidate, string current)
    {
        if (provider == ModProviderId.ReFramework)
            return Nightly(candidate, out var next) && Nightly(current, out var previous) && next > previous;
        return Version.TryParse(candidate.TrimStart('v'), out var nextVersion) &&
            Version.TryParse(current.TrimStart('v'), out var previousVersion) && nextVersion > previousVersion;

        static bool Nightly(string value, out int revision) =>
            Int32.TryParse(value.StartsWith("nightly-", StringComparison.Ordinal) ? value.Split('-').ElementAtOrDefault(1) : null, out revision);
    }

    private static ModPackageDescriptor? ReadUpdateAsset(JsonElement asset, string version, ModPackageDescriptor approved)
    {
        if (!asset.TryGetProperty("name", out var nameValue) || nameValue.GetString() is not { } name ||
            !asset.TryGetProperty("browser_download_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address) ||
            !asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size is <= 0 or > 256L * 1024 * 1024 ||
            !asset.TryGetProperty("digest", out var digestValue) || digestValue.GetString() is not { } digest ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !VerifiedDownload.IsSha256(digest[7..]) ||
            !AllowedAddress(address, approved.Key.Provider, redirect: false) || address.Segments[^1] != name) return null;
        var expected = approved.Key.Provider == ModProviderId.ReFramework ? name == "REFramework.zip"
            : name.StartsWith($"Optiscaler_{version.TrimStart('v')}", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("debug", StringComparison.OrdinalIgnoreCase);
        return expected ? new(approved.Key with { Version = version, Digest = digest[7..].ToUpperInvariant() }, address, size, approved.Files) : null;
    }

    internal async Task<CachedModPackage> AcquireAsync(ModPackageDescriptor descriptor, CancellationToken token, IProgress<int>? progress = null)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var destination = Path.Combine(_root, descriptor.Key.StoreId);
            if (Read(destination) is { } current && current.Key == descriptor.Key) return current;
            ModPaths.RejectReparsePoints(_root);
            var stage = Path.Combine(_root, ".stage-" + Guid.NewGuid().ToString("N"));
            var archive = Path.Combine(stage, "archive");
            var content = Path.Combine(stage, "files");
            Directory.CreateDirectory(content);
            try
            {
                using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                    await Download(descriptor, output, token, progress).ConfigureAwait(false);
                var files = ModArchiveReader.Extract(archive, content, descriptor, token);
                if (descriptor.Key.Provider == ModProviderId.ReShade &&
                    (files.GetValueOrDefault(ReShadeModule) != ReShadeModuleDigest ||
                        files.GetValueOrDefault(ReShadeVulkanManifest) != ReShadeVulkanManifestDigest))
                    throw new InvalidDataException("The ReShade payload does not match the reviewed x64 release.");
                File.Delete(archive);
                WriteEntry(Path.Combine(stage, "package.json"), new(descriptor.Key, files, descriptor.Address, descriptor.Length));
                token.ThrowIfCancellationRequested();
                Directory.Move(stage, destination);
                return new(descriptor.Key, Path.Combine(destination, "files"), files, descriptor);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
        }
        finally { _operation.Release(); }
    }

    internal IReadOnlyList<CachedModPackage> List(ModProviderId provider)
    {
        if (!Directory.Exists(_root)) return [];
        ModPaths.RejectReparsePoints(_root);
        return Directory.EnumerateDirectories(_root).Where(path => VerifiedDownload.IsSha256(Path.GetFileName(path)))
            .Select(Read).OfType<CachedModPackage>().Where(package => package.Key.Provider == provider).ToArray();
    }

    internal ModPackageDescriptor Preferred(ModProviderId provider, ModPackageDescriptor? checkedPackage = null,
        IReadOnlyList<CachedModPackage>? cached = null)
    {
        var approved = Approved(provider);
        var newest = checkedPackage ?? approved;
        if (provider is not (ModProviderId.OptiScaler or ModProviderId.ReFramework)) return newest;
        foreach (var package in cached ?? List(provider))
        {
            if (package.Source is { } source && source.Key.Variant == approved.Key.Variant &&
                source.Files.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(approved.Files) &&
                IsNewer(provider, source.Key.Version, newest.Key.Version))
                newest = source;
        }
        return newest;
    }

    internal async Task DeleteAsync(ModPackageKey key, CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var destination = Path.Combine(_root, key.StoreId);
            ModPaths.RejectReparsePoints(destination);
            if (Read(destination)?.Key != key) return;
            var retired = Path.Combine(_root, ".deleted-" + Guid.NewGuid().ToString("N"));
            Directory.Move(destination, retired);
            Directory.Delete(retired, recursive: true);
        }
        finally { _operation.Release(); }
    }

    internal async Task<IDisposable> LeaseAsync(CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        return new Lease(_operation);
    }

    private static CachedModPackage? Read(string directory)
    {
        var marker = Path.Combine(directory, "package.json");
        if (!File.Exists(marker)) return null;
        ModPaths.RejectReparsePoints(marker);
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(marker));
            if (entry == null || Path.GetFileName(directory) != entry.Key.StoreId) return null;
            var source = entry.Address != null && entry.Length is > 0 and <= 256L * 1024 * 1024 &&
                AllowedAddress(entry.Address, entry.Key.Provider, redirect: false)
                ? new ModPackageDescriptor(entry.Key, entry.Address, entry.Length, entry.Files.Keys.ToArray()) : null;
            return new(entry.Key, Path.Combine(directory, "files"), new(entry.Files, StringComparer.OrdinalIgnoreCase), source);
        }
        catch (JsonException) { return null; }
    }

    private static void WriteEntry(string path, Entry entry)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, entry);
        stream.Flush(true);
    }

    private async Task Download(ModPackageDescriptor descriptor, Stream output, CancellationToken token, IProgress<int>? progress = null)
    {
        if (!VerifiedDownload.IsSha256(descriptor.Key.Digest) || descriptor.Length is <= 0 or > 256L * 1024 * 1024 ||
            !AllowedAddress(descriptor.Address, descriptor.Key.Provider, redirect: false))
            throw new InvalidDataException("The package source is invalid.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        using var response = await Request(descriptor, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await VerifiedDownload.CopyAsync(response.Content, output, descriptor.Length, descriptor.Key.Digest, progress, deadline.Token)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> Request(ModPackageDescriptor descriptor, CancellationToken token)
    {
        var address = descriptor.Address;
        for (int hop = 0; hop < 4; hop++)
        {
            var response = await Client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                if (response.Headers.Location is not { } location) break;
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                if (!AllowedAddress(address, descriptor.Key.Provider, redirect: true)) break;
            }
        }
        throw new InvalidDataException("The package download redirected to an unexpected location.");
    }

    private static bool AllowedAddress(Uri address, ModProviderId provider, bool redirect)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort || address.UserInfo.Length != 0)
            return false;
        if (provider == ModProviderId.SpecialK)
            return address.Host == "sk-data.special-k.info" && address.AbsolutePath == "/SpecialK.7z";
        if (provider == ModProviderId.ReShade)
            return address == Approved(provider).Address;
        if (redirect) return address.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
        if (provider == ModProviderId.RenoDx)
            return address.Host == ModRenoDxCatalog.ReleaseFiles.Host &&
                address.AbsolutePath.StartsWith(ModRenoDxCatalog.ReleaseFiles.AbsolutePath, StringComparison.Ordinal) &&
                ModRenoDxCatalog.IsAddonName(address.AbsolutePath[ModRenoDxCatalog.ReleaseFiles.AbsolutePath.Length..]);
        var prefix = provider == ModProviderId.OptiScaler ? "/optiscaler/OptiScaler/" : "/praydog/REFramework-nightly/";
        return address.Host == "github.com" && address.AbsolutePath.StartsWith(prefix + "releases/download/", StringComparison.Ordinal);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
