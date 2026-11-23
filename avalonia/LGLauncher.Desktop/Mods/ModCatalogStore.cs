using System.Text.Json;
using System.Text.Json.Serialization;

namespace LGLauncher;

internal enum ModCatalogRefresh { Updated, Current, Unavailable }

internal sealed record ModCatalogReport(string Title, int SteamId, string Status, string Page, string Inputs, string Note)
{
    internal string Label => Status == "Reported" ? "Reported compatible" : "See compatibility notes";
}

internal sealed record ModCatalog(int SchemaVersion, long Version, string Revision, ModCatalogReport[] Entries);

internal sealed class ModCatalogStore
{
    internal static readonly Uri Source = new("https://raw.githubusercontent.com/H4RL33/lg_launcher/refs/heads/mods-support/compatibility-catalog/optiscaler-v1.json");
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };

    private readonly string _path;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    internal ModCatalog? Current { get; private set; }
    internal DateTimeOffset? LastChecked { get; private set; }

    internal ModCatalogStore(string profile)
    {
        _path = Path.Combine(profile, "mods", "optiscaler-catalog-v1.json");
        try
        {
            ModPaths.RejectReparsePoints(_path);
            if (!File.Exists(_path)) return;
            using var file = File.OpenRead(_path);
            if (file.Length <= 0 || file.Length > MaximumBytes) return;
            var payload = new byte[(int)file.Length];
            file.ReadExactly(payload);
            var catalog = Read(payload);
            LastChecked = File.GetLastWriteTimeUtc(_path);
            Current = catalog;
        }
        catch (Exception error) when (ModTransactions.ModFailure(error)) { }
    }

    internal ModCatalogReport? Find(Game game)
    {
        if (Current is not { } catalog) return null;
        var title = ModCompatibility.Normalize(game.Title);
        if (game.SteamId > 0 && catalog.Entries.FirstOrDefault(entry => entry.SteamId == game.SteamId) is { } byId) return byId;
        return title.Length == 0 ? null : catalog.Entries.FirstOrDefault(entry =>
            (game.SteamId == 0 || entry.SteamId == 0) && ModCompatibility.Normalize(entry.Title) == title);
    }

    internal async Task<ModCatalogRefresh> RefreshAsync(CancellationToken token, HttpClient? client = null)
    {
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(Timeout);
            var payload = await Download(client ?? Client, deadline.Token).ConfigureAwait(false);
            var incoming = Read(payload);
            token.ThrowIfCancellationRequested();
            if (Current is { } active)
            {
                if (incoming.Version < active.Version) return ModCatalogRefresh.Unavailable;
                if (incoming.Version == active.Version)
                {
                    if (incoming.Revision != active.Revision || !incoming.Entries.SequenceEqual(active.Entries))
                        return ModCatalogRefresh.Unavailable;
                    ModPaths.RejectReparsePoints(_path);
                    File.SetLastWriteTimeUtc(_path, DateTime.UtcNow);
                    LastChecked = DateTimeOffset.UtcNow;
                    return ModCatalogRefresh.Current;
                }
            }
            Save(payload);
            Current = incoming;
            LastChecked = DateTimeOffset.UtcNow;
            return ModCatalogRefresh.Updated;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ModCatalogRefresh.Unavailable; }
        catch (Exception error) when (ModTransactions.ModFailure(error) || error is HttpRequestException) { return ModCatalogRefresh.Unavailable; }
        finally { _refresh.Release(); }
    }

    private static async Task<byte[]> Download(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("The compatibility catalog is too large.");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("The compatibility catalog is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static ModCatalog Read(byte[] payload)
    {
        var catalog = JsonSerializer.Deserialize<ModCatalog>(payload, Json);
        if (catalog is not { SchemaVersion: 1, Version: > 0, Revision.Length: 40, Entries.Length: > 0 and <= 10000 } ||
            !catalog.Revision.All(Uri.IsHexDigit) || catalog.Entries.Any(entry => !Valid(entry)))
            throw new InvalidDataException("The compatibility catalog is invalid.");
        return catalog;
    }

    private static bool Valid(ModCatalogReport? entry) =>
        entry is
        {
            Title.Length: > 0 and <= 256, SteamId: >= 0, Status: "Reported" or "Notes",
            Page.Length: <= 160, Inputs.Length: <= 256, Note.Length: <= 4096
        }
        && ModCompatibility.Normalize(entry.Title).Length != 0
        && !entry.Title.Any(Char.IsControl) && !entry.Inputs.Any(Char.IsControl) && !entry.Note.Any(Char.IsControl)
        && entry.Page is not ("." or "..")
        && entry.Page.All(character => Char.IsLetterOrDigit(character) || "-_.'&()‐–".Contains(character));

    private void Save(byte[] payload)
    {
        ModPaths.RejectReparsePoints(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(payload);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
