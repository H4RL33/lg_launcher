using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LGLauncher;

internal sealed record RssArticle(string Title, string? Link, string Text, DateTimeOffset? Published,
    RssBlock[]? Blocks = null, string[]? Transfers = null);
internal sealed record RssSnapshot(string Title, RssArticle[] Articles, string? ETag = null, DateTimeOffset? Modified = null,
    int MarkupVersion = 0);
internal sealed record RssSource(string Location, string? Name = null, bool IsFile = false);

internal sealed class RssReader(string profile, HttpClient? client = null, long maximumCacheBytes = RssReader.MaximumCacheBytes)
{
    internal const int DefaultMinutes = 30;
    internal const int MaximumMinutes = 1440;
    internal const int MaximumArticleEnclosures = 16;
    private const int CurrentMarkupVersion = 1;
    private const int MaximumFeedBytes = 4 * 1024 * 1024;
    private const long MaximumCacheBytes = 6L * MaximumFeedBytes;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        MaxAutomaticRedirections = 5
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaximumFeedBytes
    };

    private const RegexOptions HtmlOptions = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking;
    private static readonly Regex HiddenMarkup = new(@"<!--.*?-->|<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>", HtmlOptions);
    private static readonly Regex ParagraphMarkup = new(@"<br\b[^>]*>|</(?:p|div|li|h[1-6]|blockquote)\s*>", HtmlOptions);
    private static readonly Regex Tags = new(@"</?[a-z][^>]*>", HtmlOptions);

    static RssReader() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("LGLauncher/" + UpdateFeed.CurrentVersion.ToString(3));

    internal static bool TryWebAddress(string? address, out Uri uri)
    {
        if (Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var target) &&
            target.Scheme is "https" or "http" && target.UserInfo.Length == 0 && target.Host.Length != 0)
        {
            uri = target;
            return true;
        }
        uri = null!;
        return false;
    }

    internal static Uri? ResolveWebAddress(string? address, Uri? origin)
    {
        if (String.IsNullOrWhiteSpace(address)) return null;
        if (TryWebAddress(address, out var absolute)) return absolute;
        return origin != null && Uri.TryCreate(origin, address.Trim(), out var resolved) &&
            TryWebAddress(resolved.AbsoluteUri, out var web) ? web : null;
    }

    internal static Uri? ResolveTransferAddress(string? address, Uri? origin = null)
    {
        var candidate = address?.Trim();
        if (String.IsNullOrEmpty(candidate) || candidate.Length > 8192 || candidate.Any(Char.IsControl)) return null;
        if (candidate.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(candidate, UriKind.Absolute, out var magnet) && WebTransfers.IsMagnetLink(magnet.AbsoluteUri)
                ? magnet : null;

        return ResolveWebAddress(candidate, origin) is { } web && WebTransfers.HasTorrentExtension(web)
            ? new UriBuilder(web) { Fragment = "" }.Uri : null;
    }

    internal static RssSource[] Import(string path)
    {
        var root = ReadFile(path);
        if (root.Name.LocalName == "opml")
        {
            return (Child(root, "body")?.Descendants() ?? [])
                .Where(node => node.Name.LocalName == "outline" && TryWebAddress((string?)node.Attribute("xmlUrl"), out _))
                .Select(node => new RssSource(new Uri((string)node.Attribute("xmlUrl")!).AbsoluteUri,
                    (string?)node.Attribute("title") ?? (string?)node.Attribute("text")))
                .ToArray();
        }
        var feed = Parse(root, null);
        return [new RssSource(path, feed.Title, IsFile: true)];
    }

    internal async Task<(RssSnapshot? Snapshot, string? Error)> Refresh(RssSubscription feed, CancellationToken token)
    {
        var saved = ReadCache(feed.Id);
        RssSnapshot current;
        try
        {
            token.ThrowIfCancellationRequested();
            if (feed.IsFile) current = Parse(ReadFile(feed.Location), null);
            else
            {
                if (!TryWebAddress(feed.Location, out var address)) throw new InvalidDataException("Enter an HTTP or HTTPS feed URL.");
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                if (saved?.MarkupVersion == CurrentMarkupVersion)
                {
                    if (EntityTagHeaderValue.TryParse(saved.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
                    request.Headers.IfModifiedSince = saved.Modified;
                }
                using var response = await (client ?? Client).SendAsync(request, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified && saved != null) return (saved, null);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                current = Parse(ReadXml(stream), response.RequestMessage?.RequestUri ?? address) with
                {
                    ETag = response.Headers.ETag?.ToString(),
                    Modified = response.Content.Headers.LastModified
                };
            }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (IsFeedError(error) || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return (saved, error switch
            {
                OperationCanceledException => "The feed request timed out.",
                HttpRequestException http when http.StatusCode is { } code => $"The server returned HTTP {(int)code}.",
                HttpRequestException => "The feed could not be downloaded.",
                FileNotFoundException or DirectoryNotFoundException => "The feed file could not be found.",
                _ => "The feed could not be read as RSS or Atom."
            });
        }

        try { WriteCache(feed.Id, current); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (current, "Articles loaded, but the offline copy could not be saved.");
        }
        return (current, null);
    }

    private static bool IsFeedError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or HttpRequestException or ArgumentException;

    private static XElement ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumFeedBytes) throw new InvalidDataException("The feed file is larger than 4 MB.");
        return ReadXml(stream);
    }

    private static XElement ReadXml(Stream stream)
    {
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFeedBytes
        });
        return XDocument.Load(xml, LoadOptions.PreserveWhitespace).Root ?? throw new InvalidDataException("The file contains no feed.");
    }

    private static RssSnapshot Parse(XElement root, Uri? origin)
    {
        var atom = root.Name.LocalName == "feed";
        var channel = atom || root.Name.LocalName is "rss" or "RDF" ? (atom ? root : Child(root, "channel")) : null;
        if (channel == null) throw new InvalidDataException("The document is neither an RSS nor an Atom feed.");
        var itemParent = root.Name.LocalName == "RDF" ? root : channel;
        var articles = itemParent.Elements().Where(item => item.Name.LocalName == (atom ? "entry" : "item"))
            .Select(item =>
            {
                var title = FeedText(Child(item, "title"), false);
                var body = atom ? Child(item, "content") ?? Child(item, "summary") : Child(item, "encoded") ?? Child(item, "description");
                var date = (Child(item, atom ? "published" : "pubDate") ?? Child(item, atom ? "updated" : "date"))?.Value;
                var published = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                    ? (DateTimeOffset?)time : null;
                var link = ArticleLink(item, atom, origin);
                var articleOrigin = TryWebAddress(link, out var web) ? web : origin;
                var bodyOrigin = ElementWebBase(body ?? item, origin) ?? articleOrigin;
                var blocks = RssMarkup.Read(body, !atom || (string?)body?.Attribute("type") == "html", bodyOrigin);
                if (!blocks.Any(block => block.Image != null) && ArticleImage(item, atom, origin ?? articleOrigin) is { } image)
                    blocks = [.. blocks, image];
                var transfers = ArticleTransfers(item, atom, origin ?? articleOrigin)
                    .Where(address => address != link).Distinct(StringComparer.Ordinal).Take(MaximumArticleEnclosures).ToArray();
                return new RssArticle(title.Length == 0 ? "Untitled article" : title,
                    link, RssMarkup.PlainText(blocks), published, blocks, transfers.Length > 0 ? transfers : null);
            }).ToArray();
        return new RssSnapshot(FeedText(Child(channel, "title"), false), articles, MarkupVersion: CurrentMarkupVersion);
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static string FeedText(XElement? element, bool html)
    {
        if (element == null) return "";
        if (element.HasElements)
            return String.Join("\n", element.DescendantNodes().OfType<XText>().Select(text => text.Value.Trim()).Where(value => value.Length != 0));
        var value = element.Value;
        if (html || (string?)element.Attribute("type") == "html")
            value = WebUtility.HtmlDecode(Tags.Replace(ParagraphMarkup.Replace(HiddenMarkup.Replace(value, ""), "\n"), ""));
        return value.Trim();
    }

    private static string? ArticleLink(XElement item, bool atom, Uri? origin)
    {
        var link = atom ? item.Elements().FirstOrDefault(node => node.Name.LocalName == "link" &&
            (string?)node.Attribute("rel") is null or "alternate" && node.Attribute("href") != null) : Child(item, "link");
        var address = atom ? (string?)link?.Attribute("href") : link?.Value;
        var linkOrigin = ElementWebBase(link ?? item, origin) ?? origin;
        return (ResolveTransferAddress(address, linkOrigin) ?? ResolveWebAddress(address, linkOrigin))?.AbsoluteUri;
    }

    private static IEnumerable<string> ArticleTransfers(XElement item, bool atom, Uri? origin)
    {
        foreach (var element in item.Elements())
        {
            var target = atom
                ? element.Name.LocalName == "link" && (string?)element.Attribute("rel") == "enclosure"
                    ? (string?)element.Attribute("href") : null
                : element.Name.LocalName == "enclosure" ? (string?)element.Attribute("url") : null;
            if (ResolveTransferAddress(target, ElementWebBase(element, origin) ?? origin) is { } address)
                yield return address.AbsoluteUri;
        }
    }

    private static Uri? ElementWebBase(XElement element, Uri? origin)
    {
        Uri? resolved = null;
        foreach (var ancestor in element.AncestorsAndSelf().Reverse())
        {
            var address = (string?)ancestor.Attribute(XNamespace.Xml + "base");
            resolved = ResolveWebAddress(address, resolved ?? origin) ?? resolved;
        }
        return resolved;
    }

    private static RssBlock? ArticleImage(XElement item, bool atom, Uri? origin)
    {
        XNamespace media = "http://search.yahoo.com/mrss/";
        foreach (var element in item.Elements())
        {
            var type = (string?)element.Attribute("type");
            var isImage = type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
            string? url;
            if (atom && element.Name.LocalName == "link" && (string?)element.Attribute("rel") == "enclosure" && isImage)
                url = (string?)element.Attribute("href");
            else if ((!atom && element.Name.LocalName == "enclosure" && isImage) ||
                element.Name == media + "thumbnail" ||
                element.Name == media + "content" && (isImage || (string?)element.Attribute("medium") == "image"))
                url = (string?)element.Attribute("url");
            else continue;
            if (url?.Length <= 4096 && ResolveWebAddress(url, ElementWebBase(element, origin) ?? origin) is { } address)
                return new RssBlock(Image: address.AbsoluteUri);
        }
        return null;
    }

    private string CachePath(Guid id) => Path.Combine(profile, "rss", id.ToString("N") + ".json");

    private RssSnapshot? ReadCache(Guid id)
    {
        try
        {
            var file = new FileInfo(CachePath(id));
            if (!file.Exists || file.Length > maximumCacheBytes) return null;
            using var stream = file.OpenRead();
            var saved = JsonSerializer.Deserialize<RssSnapshot>(stream);
            return saved?.Articles != null ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void WriteCache(Guid id, RssSnapshot snapshot)
    {
        var path = CachePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new CacheWriteStream(temporary, maximumCacheBytes)) JsonSerializer.Serialize(stream, snapshot);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class CacheWriteStream(string path, long limit) : FileStream(path, FileMode.CreateNew, FileAccess.Write)
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckSize(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckSize(count);
            base.Write(buffer, offset, count);
        }

        private void CheckSize(int count)
        {
            if (count > limit - Position) throw new IOException("The offline RSS snapshot exceeds the cache size limit.");
        }
    }

    internal void RemoveCache(Guid id)
    {
        try { File.Delete(CachePath(id)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
