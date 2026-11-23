using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LGLauncher
{
    internal sealed class SteamMatch
    {
        public int Id { get; private set; }
        public string Title { get; private set; }
        public string ImageUrl { get; private set; }
        public string Label { get { return Title + "  ·  App ID " + Id; } }

        public SteamMatch(int id, string title, string imageUrl)
        {
            Id = id;
            Title = title;
            ImageUrl = imageUrl;
        }
    }

    internal sealed class SteamCatalog
    {
        private const string Store = "https://store.steampowered.com";
        private const string Community = "https://steamcommunity.com";
        private const string AssetHost = "https://shared.fastly.steamstatic.com/";
        private static readonly Regex CommunityIcon = new Regex(
            @"<div\b[^>]*\bclass\s*=\s*[""'][^""']*\bapphub_AppIcon\b[^""']*[""'][^>]*>\s*(?:<a\b[^>]*>\s*)?<img\b[^>]*\ssrc\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex StorePageUrl = new Regex(
            @"<meta\b(?=[^>]*\sproperty\s*=\s*[""']og:url[""'])[^>]*\scontent\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex StoreTagLink = new Regex(
            @"<a\b(?=[^>]*\sclass\s*=\s*[""'](?:[^""']*\s)?app_tag(?:\s[^""']*)?[""'])[^>]*\shref\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>(?<name>[^<]+)</a\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly HttpClient Client = MakeClient();
        private static readonly HttpClient BackdropClient = MakeClient(12 * 1024 * 1024);
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private readonly SemaphoreSlim requests = new SemaphoreSlim(2);
        private readonly SemaphoreSlim backdropRequests = new SemaphoreSlim(1);
        private readonly SemaphoreSlim portraitRequests = new SemaphoreSlim(2);
        private readonly SemaphoreSlim logoRequests = new SemaphoreSlim(1);
        private readonly SemaphoreSlim artworkValidation = new SemaphoreSlim(1);
        private readonly ConcurrentDictionary<int, string> pictures = new ConcurrentDictionary<int, string>();
        private readonly string directory;
        private readonly Func<string, bool> canDecodeArtwork;
        private readonly HttpClient client;
        private readonly HttpClient backdropClient;

        public SteamCatalog(string directory, Func<string, bool> canDecodeArtwork, HttpClient client = null)
        {
            this.directory = directory;
            this.canDecodeArtwork = canDecodeArtwork ?? throw new ArgumentNullException(nameof(canDecodeArtwork));
            this.client = client ?? Client;
            backdropClient = client ?? BackdropClient;
        }

        private static HttpClient MakeClient(int maximumResponseBytes = 4 * 1024 * 1024)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(9), MaxResponseContentBufferSize = maximumResponseBytes };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LGLauncher/1.0");
            return client;
        }

        public async Task<List<SteamMatch>> Search(string title, CancellationToken token)
        {
            await requests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var country = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                var url = Store + "/api/storesearch/?l=english&cc=" + Uri.EscapeDataString(country) + "&term=" + Uri.EscapeDataString(title);
                var result = ParseSearch(await GetText(url, token).ConfigureAwait(false));
                foreach (var match in result)
                    if (IsSteamImage(match.ImageUrl)) pictures[match.Id] = match.ImageUrl;
                return result;
            }
            finally { requests.Release(); }
        }

        private async Task<string> GetText(string url, CancellationToken token)
        {
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
        }

        public static List<SteamMatch> ParseSearch(string json)
        {
            var list = new List<SteamMatch>();
            var document = ParseMetadata(json);
            var items = Get(document, "items") as object[];
            if (items == null) return list;
            foreach (var entry in items.OfType<Dictionary<string, object>>())
            {
                var title = Get(entry, "name") as string;
                var type = Get(entry, "type") as string;
                int id;
                if ((type == null || type == "app") && !String.IsNullOrWhiteSpace(title) &&
                    Int32.TryParse(Convert.ToString(Get(entry, "id"), CultureInfo.InvariantCulture), out id) && id > 0)
                    list.Add(new SteamMatch(id, title, Get(entry, "tiny_image") as string));
            }
            return list;
        }

        private static object Get(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        public static SteamMatch ExactMatch(string title, IEnumerable<SteamMatch> matches)
        {
            var normalized = Normalize(title);
            if (normalized.Length == 0) return null;
            var exact = matches.Where(match => Normalize(match.Title) == normalized).GroupBy(match => match.Id).Take(2).ToList();
            return exact.Count == 1 ? exact[0].First() : null;
        }

        private static string Normalize(string title)
        {
            var value = title.ToLowerInvariant().Replace("™", "").Replace("®", "").Normalize(NormalizationForm.FormD);
            return Regex.Replace(value, @"[^\p{L}\p{Nd}]", "");
        }

        public string CachedArtwork(int id)
        {
            if (id <= 0) return null;
            var path = Path.Combine(directory, id.ToString(CultureInfo.InvariantCulture) + ".jpg");
            return File.Exists(path) ? path : null;
        }

        public string CachedPortrait(int id)
        {
            if (id <= 0) return null;
            var path = PortraitPath(id);
            return File.Exists(path) ? path : null;
        }

        private string PortraitPath(int id)
        {
            return Path.Combine(directory, "portraits", id.ToString(CultureInfo.InvariantCulture) + ".jpg");
        }

        public async Task<string> GetPortrait(int id, CancellationToken token, bool refresh = false)
        {
            if (id <= 0) return null;
            var path = PortraitPath(id);
            if (!refresh && File.Exists(path)) return path;
            await portraitRequests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!refresh && File.Exists(path)) return path;
                var tried = new HashSet<string>(StringComparer.Ordinal);
                var direct = AssetHost + "store_item_assets/steam/apps/" + id.ToString(CultureInfo.InvariantCulture) + "/";
                if (await TrySaveArtwork(path, new[] { direct + "library_600x900_2x.jpg" }, tried, client, token, replace: refresh).ConfigureAwait(false))
                    return path;
                var metadata = await ReadLibraryAssetMetadata(id, token).ConfigureAwait(false);
                var candidates = LibraryPortraitUrls(id, metadata).Concat(new[] { direct + "library_600x900.jpg" });
                return await TrySaveArtwork(path, candidates, tried, client, token, replace: refresh).ConfigureAwait(false) ? path : null;
            }
            finally { portraitRequests.Release(); }
        }

        public string CachedLogo(int id)
        {
            if (id <= 0) return null;
            var path = LogoPath(id);
            return File.Exists(path) ? path : null;
        }

        private string LogoPath(int id)
        {
            return Path.Combine(directory, "logos", id.ToString(CultureInfo.InvariantCulture) + ".png");
        }

        public async Task<string> GetLogo(int id, CancellationToken token, bool refresh = false)
        {
            if (id <= 0) return null;
            await logoRequests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var path = LogoPath(id);
                if (!refresh && File.Exists(path)) return path;
                var tried = new HashSet<string>(StringComparer.Ordinal);
                Func<byte[], bool> valid = bytes => bytes.Take(PngSignature.Length).SequenceEqual(PngSignature);
                var found = await TrySaveArtwork(path, LibraryLogoUrls(id), tried, client, token, valid, refresh).ConfigureAwait(false);
                if (!found && refresh)
                {
                    var metadata = await ReadLibraryAssetMetadata(id, token).ConfigureAwait(false);
                    found = await TrySaveArtwork(path, LibraryAssetUrls(id, metadata, "library_logo_2x", "library_logo", "logo_2x", "logo"),
                        tried, client, token, valid, refresh).ConfigureAwait(false);
                }
                return found ? path : null;
            }
            finally { logoRequests.Release(); }
        }

        internal static IEnumerable<string> LibraryLogoUrls(int id)
        {
            if (id <= 0) yield break;
            var app = AssetHost + "store_item_assets/steam/apps/" + id.ToString(CultureInfo.InvariantCulture) + "/";
            yield return app + "logo_2x.png";
            yield return app + "logo.png";
            // Steam's flat logo aliases are unavailable for PRAGMATA. The client publishes this versioned asset.
            if (id == 3357650)
            {
                const string pragmataLogo = "405574ce52d96aaad729fa0fe926db98078e8efb/";
                yield return app + pragmataLogo + "logo_2x.png";
                yield return app + pragmataLogo + "logo.png";
            }
        }

        internal static IEnumerable<string> LocalLogoPaths(int id, string steamDirectory)
        {
            if (id <= 0 || String.IsNullOrWhiteSpace(steamDirectory)) yield break;
            var key = id.ToString(CultureInfo.InvariantCulture);
            var cache = Path.Combine(steamDirectory, "appcache", "librarycache");
            var app = Path.Combine(cache, key);
            var names = new[] { "logo_2x.png", "logo.png" };
            foreach (var name in names)
            {
                var path = Path.Combine(app, name);
                if (File.Exists(path)) yield return path;
            }
            if (Directory.Exists(app))
            {
                var versioned = Directory.EnumerateDirectories(app)
                    .SelectMany(folder => names.Select(name => new FileInfo(Path.Combine(folder, name))))
                    .Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc);
                foreach (var file in versioned) yield return file.FullName;
            }
            var legacy = Path.Combine(cache, key + "_logo.png");
            if (File.Exists(legacy)) yield return legacy;
        }

        public async Task<List<string>> GetGenres(int id, CancellationToken token)
        {
            if (id <= 0) return null;
            var key = id.ToString(CultureInfo.InvariantCulture);
            await requests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var json = await GetText(Store + "/api/appdetails?l=english&filters=genres&appids=" + key, token).ConfigureAwait(false);
                return ParseGenres(id, json);
            }
            finally { requests.Release(); }
        }

        internal static List<string> ParseGenres(int id, string json)
        {
            var app = Get(ParseMetadata(json), id.ToString(CultureInfo.InvariantCulture)) as Dictionary<string, object>;
            var data = Get(app, "data") as Dictionary<string, object>;
            var genres = Get(data, "genres") as object[];
            if (!Object.Equals(Get(app, "success"), true)) return null;
            if (genres == null) return data == null ? null : new List<string>();
            return genres.OfType<Dictionary<string, object>>()
                .Select(genre => Get(genre, "description") as string)
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<List<string>> GetStoreTags(int id, CancellationToken token)
        {
            if (id <= 0) return null;
            await requests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var url = Store + "/app/" + id.ToString(CultureInfo.InvariantCulture) + "/?l=english";
                return ParseStoreTags(id, await GetText(url, token).ConfigureAwait(false));
            }
            finally { requests.Release(); }
        }

        internal static List<string> ParseStoreTags(int id, string html)
        {
            if (id <= 0 || String.IsNullOrEmpty(html)) return null;
            var pageUrl = StorePageUrl.Match(html).Groups["url"].Value;
            var appPath = "/app/" + id.ToString(CultureInfo.InvariantCulture) + "/";
            Uri app;
            if (!Uri.TryCreate(WebUtility.HtmlDecode(pageUrl), UriKind.Absolute, out app) ||
                app.Scheme != Uri.UriSchemeHttps || app.Host != "store.steampowered.com" ||
                !app.AbsolutePath.StartsWith(appPath, StringComparison.Ordinal)) return null;

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in StoreTagLink.Matches(html))
            {
                Uri tag;
                if (!Uri.TryCreate(app, WebUtility.HtmlDecode(match.Groups["url"].Value), out tag) ||
                    tag.Scheme != Uri.UriSchemeHttps || tag.Host != app.Host ||
                    !tag.AbsolutePath.StartsWith("/tags/en/", StringComparison.OrdinalIgnoreCase)) continue;
                var name = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
                if (name.Length > 0 && name.Length <= 100 && seen.Add(name)) names.Add(name);
                if (names.Count == 20) break;
            }
            // Keep the last known tags if Steam returns an age gate or a changed page.
            return names.Count > 0 ? names : null;
        }

        public async Task<string> GetBackdrop(int id, CancellationToken token, bool refresh = false)
        {
            if (id <= 0) return null;
            var key = id.ToString(CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, "backdrops", key + ".jpg");
            if (!refresh && File.Exists(path)) return path;
            await backdropRequests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!refresh && File.Exists(path)) return path;
                var tried = new HashSet<string>(StringComparer.Ordinal);
                var direct = AssetHost + "store_item_assets/steam/apps/" + key + "/";
                if (await TrySaveArtwork(path, new[] { direct + "library_hero_2x.jpg" }, tried, backdropClient, token, replace: refresh).ConfigureAwait(false))
                    return path;

                var metadata = await ReadLibraryAssetMetadata(id, token).ConfigureAwait(false);
                var heroes = LibraryHeroUrls(id, metadata).Concat(new[] { direct + "library_hero.jpg" });
                if (await TrySaveArtwork(path, heroes, tried, backdropClient, token, replace: refresh).ConfigureAwait(false))
                    return path;

                var details = await ReadOptionalMetadata(Store + "/api/appdetails?l=english&filters=screenshots&appids=" + key, token).ConfigureAwait(false);
                return await TrySaveArtwork(path, ScreenshotUrls(id, details).Take(3), tried, backdropClient, token, replace: refresh).ConfigureAwait(false) ? path : null;
            }
            finally { backdropRequests.Release(); }
        }

        private Task<string> ReadLibraryAssetMetadata(int id, CancellationToken token)
        {
            var request = JsonSerializer.Serialize(new
            {
                ids = new[] { new { appid = id } },
                context = new { language = "english", country_code = RegionInfo.CurrentRegion.TwoLetterISORegionName, steam_realm = 1 },
                data_request = new { include_assets = true }
            });
            return ReadOptionalMetadata("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" +
                Uri.EscapeDataString(request), token);
        }

        private async Task<string> ReadOptionalMetadata(string url, CancellationToken token)
        {
            try { return await GetText(url, token).ConfigureAwait(false); }
            catch (HttpRequestException) { return null; }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return null; }
        }

        private async Task<bool> TrySaveArtwork(string path, IEnumerable<string> urls, HashSet<string> tried,
            HttpClient imageClient, CancellationToken token, Func<byte[], bool> validImage = null, bool replace = false)
        {
            foreach (var url in urls)
            {
                if (!tried.Add(url)) continue;
                byte[] bytes;
                try { bytes = await GetImageBytes(url, token, imageClient).ConfigureAwait(false); }
                catch (HttpRequestException) { continue; }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); continue; }
                if (bytes == null || bytes.Length == 0 || (validImage != null && !validImage(bytes))) continue;
                if (await TryPublishImage(path, bytes, token, replace).ConfigureAwait(false)) return true;
            }
            return false;
        }

        internal static IEnumerable<string> LibraryHeroUrls(int id, string json)
        {
            return LibraryAssetUrls(id, json, "library_hero_2x", "library_hero");
        }

        internal static IEnumerable<string> LibraryPortraitUrls(int id, string json)
        {
            return LibraryAssetUrls(id, json, "library_capsule_2x", "library_capsule");
        }

        private static IEnumerable<string> LibraryAssetUrls(int id, string json, params string[] sizes)
        {
            const string fileToken = "\u0024{FILENAME}";
            var key = id.ToString(CultureInfo.InvariantCulture);
            var response = Get(ParseMetadata(json), "response") as Dictionary<string, object>;
            var items = Get(response, "store_items") as object[];
            if (items == null) yield break;
            var app = items.OfType<Dictionary<string, object>>().FirstOrDefault(item =>
                Convert.ToString(Get(item, "appid"), CultureInfo.InvariantCulture) == key);
            var assets = Get(app, "assets") as Dictionary<string, object>;
            var format = Get(assets, "asset_url_format") as string ?? ("steam/apps/" + key + "/" + fileToken);
            foreach (var size in sizes)
            {
                var file = Get(assets, size) as string;
                if (String.IsNullOrWhiteSpace(file)) continue;
                var url = file.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? file : format.Replace(fileToken, file);
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    url = url.TrimStart('/');
                    url = AssetHost + (url.StartsWith("store_item_assets/", StringComparison.Ordinal) ? "" : "store_item_assets/") + url;
                }
                if (IsGameAsset(url, key)) yield return url;
            }
        }

        internal static IEnumerable<string> ScreenshotUrls(int id, string json)
        {
            var key = id.ToString(CultureInfo.InvariantCulture);
            var app = Get(ParseMetadata(json), key) as Dictionary<string, object>;
            var data = Get(app, "data") as Dictionary<string, object>;
            var screenshots = Get(data, "screenshots") as object[];
            if (screenshots == null) yield break;
            foreach (var screenshot in screenshots.OfType<Dictionary<string, object>>())
            {
                var url = Get(screenshot, "path_full") as string;
                if (IsGameAsset(url, key)) yield return url;
            }
        }

        private static Dictionary<string, object> ParseMetadata(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var document = JsonDocument.Parse(json))
                    return ReadJson(document.RootElement) as Dictionary<string, object>;
            }
            catch (JsonException) { return null; }
        }

        private static object ReadJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var fields = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var field in element.EnumerateObject()) fields[field.Name] = ReadJson(field.Value);
                    return fields;
                case JsonValueKind.Array: return element.EnumerateArray().Select(ReadJson).ToArray();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number: return element.GetRawText();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }

        private static bool IsGameAsset(string url, string appId)
        {
            if (!IsSteamImage(url)) return false;
            var path = new Uri(url).AbsolutePath;
            var appPath = "steam/apps/" + appId + "/";
            return path.StartsWith("/store_item_assets/" + appPath, StringComparison.Ordinal) ||
                path.StartsWith("/" + appPath, StringComparison.Ordinal);
        }

        public async Task<string> GetIcon(int id, CancellationToken token, bool refresh = false)
        {
            if (id <= 0) return null;
            var key = id.ToString(CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, "icons", key + ".jpg");
            if (!refresh && File.Exists(path)) return path;
            await requests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!refresh && File.Exists(path)) return path;
                var html = await GetText(Community + "/app/" + key + "/discussions/", token).ConfigureAwait(false);
                var url = FindCommunityIcon(id, html);
                if (url == null) return null;
                var bytes = await GetImageBytes(url, token, client).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return null;
                return await TryPublishImage(path, bytes, token, refresh).ConfigureAwait(false) ? path : null;
            }
            finally { requests.Release(); }
        }

        internal static string FindCommunityIcon(int id, string html)
        {
            if (id <= 0) return null;
            var key = id.ToString(CultureInfo.InvariantCulture);
            foreach (Match match in CommunityIcon.Matches(html))
            {
                var value = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!IsSteamImage(value)) continue;
                var path = new Uri(value).AbsolutePath;
                if (path.StartsWith("/community_assets/images/apps/" + key + "/", StringComparison.Ordinal) ||
                    path.StartsWith("/steamcommunity/public/images/apps/" + key + "/", StringComparison.Ordinal))
                    return value;
            }
            return null;
        }

        public async Task<string> DownloadArtwork(int id, CancellationToken token, bool refresh = false)
        {
            if (id <= 0) return null;
            var cached = CachedArtwork(id);
            if (!refresh && cached != null) return cached;
            await requests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                cached = CachedArtwork(id);
                if (!refresh && cached != null) return cached;
                var key = id.ToString(CultureInfo.InvariantCulture);
                string header = null;
                Exception networkFailure = null;
                try
                {
                    var json = await GetText(Store + "/api/appdetails?l=english&appids=" + key, token).ConfigureAwait(false);
                    var document = ParseMetadata(json);
                    var entry = Get(document, key) as Dictionary<string, object>;
                    header = Get(Get(entry, "data") as Dictionary<string, object>, "header_image") as string;
                }
                catch (HttpRequestException error) { networkFailure = error; }
                catch (OperationCanceledException error) { token.ThrowIfCancellationRequested(); networkFailure = error; }
                catch (ArgumentException) { }
                string thumbnail;
                pictures.TryGetValue(id, out thumbnail);
                var urls = new[] { header, "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/" + key + "/header.jpg", thumbnail };
                foreach (var url in urls.Where(IsSteamImage).Distinct())
                {
                    try
                    {
                        var bytes = await GetImageBytes(url, token, client).ConfigureAwait(false);
                        if (bytes == null || bytes.Length == 0) continue;
                        var path = Path.Combine(directory, key + ".jpg");
                        if (await TryPublishImage(path, bytes, token, refresh).ConfigureAwait(false)) return path;
                    }
                    catch (HttpRequestException error) { networkFailure = error; }
                    catch (OperationCanceledException error) { token.ThrowIfCancellationRequested(); networkFailure = error; }
                }
                if (networkFailure != null) throw new HttpRequestException("Steam artwork sources could not be reached.", networkFailure);
                return null;
            }
            finally { requests.Release(); }
        }

        private async Task<bool> TryPublishImage(string path, byte[] bytes, CancellationToken token, bool replace)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                // Codecs may need a full raster for some formats. Limit validation to one decode at a time.
                await artworkValidation.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!canDecodeArtwork(temporary)) return false;
                }
                finally { artworkValidation.Release(); }
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, overwrite: replace); }
                catch (IOException) when (!replace && File.Exists(path)) { }
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static async Task<byte[]> GetImageBytes(string url, CancellationToken token, HttpClient client)
        {
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                var contentType = response.Content.Headers.ContentType;
                if (!response.IsSuccessStatusCode || contentType == null ||
                    !contentType.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
                return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            }
        }

        private static bool IsSteamImage(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase));
        }
    }
}
