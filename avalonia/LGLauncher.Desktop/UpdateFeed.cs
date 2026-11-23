using System.Text.Json;

namespace LGLauncher;

internal static class UpdateFeed
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(6),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    static UpdateFeed() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("LGLauncher/" + CurrentVersion);

    internal static Version CurrentVersion => typeof(UpdateFeed).Assembly.GetName().Version!;
    private const string ReleasesApi = "https://api.github.com/repos/H4RL33/lg_launcher/releases";
    private static readonly Uri LatestRelease = new(ReleasesApi + "/latest");
    private static readonly Uri RecentReleases = new(ReleasesApi + "?per_page=20");
    private const string Releases = "https://github.com/H4RL33/lg_launcher/releases";
    internal const long MaximumPackageSize = 256L * 1024 * 1024;
    internal static string DisplayVersion(Version version) => version.Revision > 0
        ? $"{version.ToString(3)}-next.{version.Revision}" : version.ToString(3);
    internal static string ReleaseTag(Version version) => $"v{DisplayVersion(version)}";
    internal static Uri ReleasePage(Version version) => new($"{Releases}/tag/{ReleaseTag(version)}");

    internal sealed record Result(string Message, Uri? ReleasePage = null, bool UpdateAvailable = false, Package? Installer = null,
        ReleaseNotes? AvailableRelease = null, Package? SetupInstaller = null);
    internal sealed record Package(Uri Url, string Sha256, long Length, Version Version);
    internal sealed record ReleaseNotes(Version Version, string? Markdown);

    internal static async Task<Result> Check(CancellationToken token, HttpClient? client = null, bool includePreviews = false)
    {
        try
        {
            var address = includePreviews ? RecentReleases : LatestRelease;
            var json = await (client ?? Client).GetStringAsync(address, token).ConfigureAwait(false);
            return Read(json, CurrentVersion, includePreviews);
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
            if (Text(release, "tag_name") != tag || Flag(release, "draft") ||
                Flag(release, "prerelease") != (CurrentVersion.Revision > 0)) return null;
            return ReleaseBody(release);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static Result Read(string json, Version current, bool includePreviews = false)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        var latest = ReadVersion(release, includePreviews: false);
        if (includePreviews)
        {
            if (release.ValueKind != JsonValueKind.Array) return new("GitHub did not provide a valid Next update feed.");
            latest = null;
            foreach (var candidate in document.RootElement.EnumerateArray())
            {
                var version = ReadVersion(candidate, includePreviews: true);
                if (version == null || latest != null && Compare(version, latest) <= 0) continue;
                latest = version;
                release = candidate;
            }
        }
        if (latest == null) return new(includePreviews ? "No Next updates are currently listed." : "GitHub did not provide a valid stable release.");
        if (Compare(latest, current) <= 0)
            return new(!includePreviews && current.Revision > 0
                ? "You’re on a Next preview. This copy will update when a newer Stable release is available."
                : "LG Launcher is up to date.");
        var (portable, setup) = ReadPackages(release, ReleaseTag(latest), latest);
        return new($"LG Launcher {DisplayVersion(latest)} is available.", ReleasePage(latest),
            true, portable, new(latest, ReleaseBody(release)), setup);
    }

    private static Version? ReadVersion(JsonElement release, bool includePreviews)
    {
        var tag = Text(release, "tag_name");
        if (Flag(release, "draft") || tag == null || !tag.StartsWith('v') ||
            tag is "v1.1.1" or "v1.1.2" or "v1.2.0" or "v1.2.1" or "v1.2.2") return null;
        var preview = Flag(release, "prerelease");
        if (preview && !includePreviews) return null;
        var versionText = preview ? tag[1..].Replace("-next.", ".", StringComparison.Ordinal) : tag[1..];
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 ||
            (preview ? version.Revision is <= 0 or > 65534 : version.Revision != -1) ||
            tag != ReleaseTag(version)) return null;
        return version;
    }

    internal static int Compare(Version left, Version right)
    {
        var threePart = new Version(left.Major, left.Minor, Math.Max(0, left.Build))
            .CompareTo(new(right.Major, right.Minor, Math.Max(0, right.Build)));
        if (threePart != 0) return threePart;
        // Stable sorts after every preview of the same version even though its build revision is zero.
        return (left.Revision > 0 ? left.Revision : Int32.MaxValue)
            .CompareTo(right.Revision > 0 ? right.Revision : Int32.MaxValue);
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
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !VerifiedDownload.IsSha256(digest[7..])) return null;
        var releaseUrl = new Uri($"{Releases}/download/{tag}/{name}");
        if (Text(asset, "browser_download_url") != releaseUrl.AbsoluteUri ||
            !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out var length) || length <= 0 || length > MaximumPackageSize) return null;
        return new Package(releaseUrl, digest[7..], length, version);
    }

    private static bool Flag(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
