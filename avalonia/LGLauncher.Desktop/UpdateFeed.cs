using System.Text.Json;

namespace LGLauncher;

internal static class UpdateFeed
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(6),
        MaxResponseContentBufferSize = 256 * 1024
    };

    static UpdateFeed() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("LGLauncher/" + CurrentVersion);

    internal static Version CurrentVersion => typeof(UpdateFeed).Assembly.GetName().Version!;
    private const string ReleasesApi = "https://api.github.com/repos/H4RL33/lg_launcher/releases";
    private static readonly Uri LatestRelease = new(ReleasesApi + "/latest");
    private const string Releases = "https://github.com/H4RL33/lg_launcher/releases";
    internal const long MaximumPackageSize = 256L * 1024 * 1024;
    private static string ReleaseTag(Version version) => $"v{version.ToString(3)}";
    internal static Uri ReleasePage(Version version) => new($"{Releases}/tag/{ReleaseTag(version)}");

    internal sealed record Result(string Message, Uri? ReleasePage = null, bool UpdateAvailable = false, Package? Installer = null,
        ReleaseNotes? AvailableRelease = null, Package? SetupInstaller = null);
    internal sealed record Package(Uri Url, string Sha256, long Length, Version Version);
    internal sealed record ReleaseNotes(Version Version, string? Markdown);

    internal static async Task<Result> Check(CancellationToken token, HttpClient? client = null)
    {
        try
        {
            var json = await (client ?? Client).GetStringAsync(LatestRelease, token).ConfigureAwait(false);
            return Read(json, CurrentVersion);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return new("Could not check for updates.");
        }
    }

    internal static async Task<string?> GetReleaseNotes(CancellationToken token, HttpClient? client = null)
    {
        var tag = ReleaseTag(CurrentVersion);
        try
        {
            var json = await (client ?? Client).GetStringAsync($"{ReleasesApi}/tags/{tag}", token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var release = document.RootElement;
            if (Text(release, "tag_name") != tag || Flag(release, "draft") || Flag(release, "prerelease")) return null;
            return ReleaseBody(release);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static Result Read(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (Flag(release, "draft") || Flag(release, "prerelease")) return new("The update feed did not provide a stable release.");
        var tag = Text(release, "tag_name");
        if (tag == null || !tag.StartsWith('v') || !Version.TryParse(tag.AsSpan(1), out var latest) ||
            latest.Build < 0 || latest.Revision >= 0 || tag != $"v{latest}")
            return new("GitHub did not provide a valid stable release version.");
        if (tag is "v1.1.1" or "v1.1.2" or "v1.2.0" or "v1.2.1" or "v1.2.2")
            return new("The official feed is showing a release from before the 0.1.x version reset.");
        if (Normalize(latest) <= Normalize(current)) return new("LG Launcher is up to date.");
        var (portable, setup) = ReadPackages(release, tag, latest);
        return new($"LG Launcher {latest} is available.", ReleasePage(latest),
            true, portable, new(latest, ReleaseBody(release)), setup);
    }

    private static string? ReleaseBody(JsonElement release)
    {
        var body = Text(release, "body");
        return body is { Length: <= 64 * 1024 } && !String.IsNullOrWhiteSpace(body) ? body : null;
    }

    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static (Package? Portable, Package? Setup) ReadPackages(JsonElement release, string tag, Version version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return default;
        Package? portable = null;
        Package? setup = null;
        var setupName = $"LG-Launcher-Setup-{version}.exe";
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name");
            if (name == setupName) setup ??= ReadPackage(asset, tag, setupName, version);
            else if (name is "LG-Launcher.exe" or "LG.Launcher.exe") portable ??= ReadPackage(asset, tag, name, version);
        }
        return (portable, setup);
    }

    private static Package? ReadPackage(JsonElement asset, string tag, string name, Version version)
    {
        if (Text(asset, "state") != "uploaded") return null;
        var digest = Text(asset, "digest");
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsDigest(digest[7..])) return null;
        var releaseUrl = new Uri($"{Releases}/download/{tag}/{name}");
        if (Text(asset, "browser_download_url") != releaseUrl.AbsoluteUri ||
            !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out var length) || length <= 0 || length > MaximumPackageSize) return null;
        return new Package(releaseUrl, digest[7..], length, version);
    }

    internal static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool Flag(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
