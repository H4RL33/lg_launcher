using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using LGLauncher;

internal static class CoreTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class SteamImageResponse : HttpMessageHandler
    {
        public byte[] Image = "undecodable artwork"u8.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri.Host == "api.steampowered.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

            var content = new ByteArrayContent(Image);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static void VerifySteamArtworkRefresh(string root)
    {
        var artwork = Path.Combine(root, "steam-artwork");
        var portrait = Path.Combine(Directory.CreateDirectory(Path.Combine(artwork, "portraits")).FullName, "620.jpg");
        var original = "readable portrait fixture"u8.ToArray();
        File.WriteAllBytes(portrait, original);
        var response = new SteamImageResponse();
        using var client = new HttpClient(response);
        var replacement = "readable replacement fixture"u8.ToArray();
        using var canceled = new CancellationTokenSource();
        var cancelValidation = false;
        var steam = new SteamCatalog(artwork, staged =>
        {
            var bytes = File.ReadAllBytes(staged);
            if (cancelValidation) canceled.Cancel();
            return bytes.SequenceEqual(replacement);
        }, client);

        var rejected = steam.GetPortrait(620, CancellationToken.None, refresh: true).GetAwaiter().GetResult();
        Check(rejected == null && File.ReadAllBytes(portrait).SequenceEqual(original),
            "An undecodable Steam image does not replace an existing portrait");

        response.Image = replacement;
        cancelValidation = true;
        try
        {
            steam.GetPortrait(620, canceled.Token, refresh: true).GetAwaiter().GetResult();
            throw new InvalidOperationException("Steam published an image after cancellation.");
        }
        catch (OperationCanceledException)
        {
            Check(File.ReadAllBytes(portrait).SequenceEqual(original),
                "An artwork refresh canceled during validation preserves the cache");
        }

        cancelValidation = false;
        var refreshed = steam.GetPortrait(620, CancellationToken.None, refresh: true).GetAwaiter().GetResult();
        Check(refreshed == portrait && File.ReadAllBytes(portrait).SequenceEqual(replacement) &&
            !Directory.EnumerateFiles(artwork, "*.tmp", SearchOption.AllDirectories).Any(),
            "A readable Steam image replaces the existing portrait and removes staged files");
    }

    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-priority")
        {
            var resultFile = args[1];
            if (!SpinWait.SpinUntil(() => File.Exists(resultFile + ".ready"), TimeSpan.FromSeconds(8))) return;
            using (var process = Process.GetCurrentProcess())
                File.WriteAllText(resultFile, process.PriorityClass.ToString());
            return;
        }

        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Star Voyage"));
            var nested = Directory.CreateDirectory(Path.Combine(folder.FullName, "Binaries", "Win64"));
            var executable = Path.Combine(nested.FullName, "Voyage-Win64-Shipping.exe");
            File.WriteAllText(executable, "game fixture");
            File.WriteAllText(Path.Combine(folder.FullName, "unins000.exe"), "uninstaller fixture");
            File.WriteAllText(Path.Combine(root, "Aurora.exe"), "standalone fixture");

            var scan = Discovery.Scan(root, CancellationToken.None);
            Check(scan.Any(game => game.Title == "Star Voyage" && game.Path == executable), "Nested executable uses its game folder name");
            Check(scan.Count == 2 && scan.Any(game => game.Title == "Aurora"), "Standalone executable is found and uninstaller is excluded");
            var matches = new[] { new SteamMatch(41, "Star Voyage 2", null), new SteamMatch(19, "STAR: VOYAGE", null) };
            Check(SteamCatalog.ExactMatch("Star Voyage", matches).Id == 19, "Only normalized exact titles auto-match");
            Check(SteamCatalog.ExactMatch("Aurora", matches) == null, "Similar titles are left unmatched");
            Check(SteamCatalog.ExactMatch("Star Voyage", new[] { matches[1], new SteamMatch(31, "Star Voyage", null) }) == null, "Ambiguous exact titles require manual confirmation");
            var json = "{\"items\":[{\"id\":620,\"name\":\"Portal 2\",\"type\":\"app\",\"tiny_image\":\"https://shared.fastly.steamstatic.com/portal.jpg\"},{\"id\":1,\"name\":\"Bundle\",\"type\":\"bundle\"}]}";
            Check(SteamCatalog.ParseSearch(json).Single().Id == 620, "Store search reads app IDs and skips bundles");
            const string steamIcon = "https://shared.fastly.steamstatic.com/community_assets/images/apps/2380050/7631e6879c528a753c764fb9af22ba7326736655.jpg";
            var iconPage = "<div class='apphub_AppIcon'><img src='" + steamIcon + "'></div>";
            Check(SteamCatalog.FindCommunityIcon(2380050, iconPage) == steamIcon, "Steam Community exposes the square icon for its app");
            Check(SteamCatalog.FindCommunityIcon(620, iconPage) == null &&
                SteamCatalog.FindCommunityIcon(2380050, iconPage.Replace("shared.fastly.steamstatic.com", "example.com")) == null,
                "Steam icon lookup rejects mismatched apps and unrelated image hosts");
            const string cdn = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/2380050/";
            const string heroMetadata = "{\"response\":{\"store_items\":[{\"appid\":2380050,\"assets\":{\"asset_url_format\":\"steam/apps/2380050/\u0024{FILENAME}\",\"library_hero\":\"asset-hash/library_hero.jpg\",\"library_hero_2x\":\"asset-hash/library_hero_2x.jpg\"}}]}}";
            Check(SteamCatalog.LibraryHeroUrls(2380050, heroMetadata).SequenceEqual(new[] { cdn + "asset-hash/library_hero_2x.jpg", cdn + "asset-hash/library_hero.jpg" }),
                "Versioned Steam artwork URLs prefer the 2x Library Hero");
            Check(!SteamCatalog.LibraryHeroUrls(620, heroMetadata).Any(), "Library Hero metadata is matched to the selected game");
            var portraitMetadata = heroMetadata.Replace("library_hero", "library_capsule");
            Check(SteamCatalog.LibraryPortraitUrls(2380050, portraitMetadata)
                    .SequenceEqual(new[] { cdn + "asset-hash/library_capsule_2x.jpg", cdn + "asset-hash/library_capsule.jpg" }) &&
                !SteamCatalog.LibraryPortraitUrls(620, portraitMetadata).Any() &&
                !SteamCatalog.LibraryPortraitUrls(2380050, portraitMetadata.Replace("steam/apps/2380050/", "https://example.test/")).Any(),
                "Versioned portrait capsules prefer 2x and validate the game's Steam asset path");
            Check(SteamCatalog.LibraryLogoUrls(2380050).Take(2).SequenceEqual(new[] { cdn + "logo_2x.png", cdn + "logo.png" }) &&
                !SteamCatalog.LibraryLogoUrls(0).Any(), "Steam logo downloads prefer transparent Library Logo assets for the selected app");
            Check(SteamCatalog.LibraryLogoUrls(3357650).Any(url => url.EndsWith("/3357650/405574ce52d96aaad729fa0fe926db98078e8efb/logo_2x.png")),
                "PRAGMATA includes a versioned Steam logo fallback");
            var steamFolder = Path.Combine(root, "steam");
            var logoFolder = Directory.CreateDirectory(Path.Combine(steamFolder, "appcache", "librarycache", "2380050"));
            var hashedLogo = Path.Combine(logoFolder.CreateSubdirectory("950fa3491c3888408595568c1cfaf31568223c38").FullName, "logo.png");
            var flatLogo = Path.Combine(logoFolder.Parent.FullName, "2380050_logo.png");
            File.WriteAllText(hashedLogo, "versioned Steam logo fixture");
            File.WriteAllText(flatLogo, "legacy Steam logo fixture");
            Check(SteamCatalog.LocalLogoPaths(2380050, steamFolder).SequenceEqual(new[] { hashedLogo, flatLogo }) &&
                !SteamCatalog.LocalLogoPaths(620, steamFolder).Any(), "Steam logo lookup supports versioned and legacy client caches for the selected app");
            const string steamGenres = "{\"2380050\":{\"success\":true,\"data\":{\"genres\":[{\"description\":\" Action \"},{\"description\":\"action\"},{\"description\":\"Adventure\"},{\"description\":\"Racing\"},{\"description\":\"Simulation\"}]}}}";
            Check(SteamCatalog.ParseGenres(2380050, steamGenres).SequenceEqual(new[] { "Action", "Adventure", "Racing", "Simulation" }) &&
                SteamCatalog.ParseGenres(620, steamGenres) == null, "Steam genres retain all unique entries for the selected app");
            const string tagPage = "<meta content='https://store.steampowered.com/app/2380050/Star_Trucker/' property='og:url'>" +
                "<div class='glance_tags popular_tags'>" +
                "<a href='https://store.steampowered.com/tags/en/Space%20Sim/?snr=1' class='app_tag'>Space Sim</a>" +
                "<a class='app_tag' href='/tags/en/Truck%20%26%20Trailer/'>Truck &amp; Trailer</a>" +
                "<a class='app_tag' href='/tags/en/space%20sim/'>space sim</a>" +
                "<a class='app_tag' href='https://example.test/tags/en/Other/'>Other</a></div>";
            Check(SteamCatalog.ParseStoreTags(2380050, tagPage).SequenceEqual(new[] { "Space Sim", "Truck & Trailer" }) &&
                SteamCatalog.ParseStoreTags(620, tagPage) == null &&
                SteamCatalog.ParseStoreTags(2380050, "<title>Age check</title>") == null,
                "Steam store tags retain display order and reject unrelated pages and links");
            const string screenshots = "{\"2380050\":{\"success\":true,\"data\":{\"screenshots\":[{\"path_full\":\"https://example.test/bad.jpg\"},{\"path_full\":\"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/2380050/ss_example.jpg\",\"path_thumbnail\":\"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/2380050/ss_example.600x338.jpg\"}]}}}";
            Check(SteamCatalog.ScreenshotUrls(2380050, screenshots).Single() == cdn + "ss_example.jpg",
                "The screenshot fallback uses full-size game artwork from Steam");
            VerifySteamArtworkRefresh(root);

            var store = new LibraryStore(Path.Combine(root, "data"));
            var library = store.Load();
            var oldDirectory = Directory.CreateDirectory(Path.Combine(root, "old-data")).FullName;
            File.WriteAllText(Path.Combine(oldDirectory, "library.json"),
                "{\"UsageNoticeAccepted\":true,\"Preferences\":{\"UnlimitedGenres\":false},\"Games\":[{\"ArtworkPosition\":{\"X\":0.7,\"Y\":0.5}}]}");
            var oldLibrary = new LibraryStore(oldDirectory).Load();
            Check(oldLibrary.UsageNoticeAccepted && oldLibrary.Preferences.InterfaceFont == null && oldLibrary.Preferences.ShowRecentRss,
                "Existing libraries enable Home RSS by default without resetting their acknowledgment");
            Check(oldLibrary.Preferences.ShowStoreTags, "Old settings keep store tags visible by default");
            Check(!oldLibrary.Preferences.DisableArtworkAccent && !library.Preferences.DisableArtworkAccent,
                "Existing and new profiles enable artwork accents by default");
            Check(!oldLibrary.Preferences.SetProcessPriority && !library.Preferences.SetProcessPriority,
                "Existing and new profiles leave game process priority unchanged by default");
            Check(!oldLibrary.Preferences.PlayThemeMusic && !library.Preferences.PlayThemeMusic,
                "Existing and new profiles leave theme music off by default");
            Check(!oldLibrary.Preferences.UsePreviewUpdates && !library.Preferences.UsePreviewUpdates,
                "Existing and new profiles use Stable updates by default");
            Check(oldLibrary.Preferences.DefaultAccentColour == null && library.Preferences.DefaultAccentColour == null,
                "Existing and new profiles retain the built-in accent palette");
            Check(oldLibrary.Games.Single().ArtworkPosition.Zoom == 1, "Artwork positions saved before zoom use the original framing scale");
            Check(!library.UsageNoticeAccepted, "A fresh library requires acknowledgement of the usage notice");
            var collection = new GameCollection { Name = "Weekend" };
            var launched = new DateTime(2026, 11, 19, 12, 30, 0, DateTimeKind.Utc);
            library.Folder = root;
            library.UsageNoticeAccepted = true;
            library.Collections.Add(collection);
            library.Games.Add(new Game { Title = "Star Voyage", Path = executable, SteamId = 19, PlaySeconds = 271, LastPlayedUtc = launched, CollectionIds = new List<string> { collection.Id } });
            var replacement = Guid.NewGuid().ToString("N");
            library.Games[0].CustomArtworkId = replacement;
            library.Games[0].ArtworkPosition = new ArtworkPosition { X = .72, Y = .39, Zoom = 1.65 };
            library.Games[0].Genres = SteamCatalog.ParseGenres(2380050, steamGenres);
            library.Games[0].GenreSteamId = library.Games[0].SteamId;
            library.Games[0].SteamGenresComplete = true;
            library.Games[0].SteamTags = SteamCatalog.ParseStoreTags(2380050, tagPage);
            library.Games[0].TagSteamId = library.Games[0].SteamId;
            library.Games[0].ManualTags = new List<string> { "Co-op" };
            library.Games[0].Developers = new List<string> { "Rockstar North" };
            library.Games[0].Publishers = new List<string> { "Rockstar Games" };
            library.Games[0].DetailsSteamId = 19;
            library.Games[0].ReleaseDate = new DateTime(2026, 10, 21);
            library.Games[0].Scale = GameScale.AAA;
            library.Preferences.DisablePlaytimeTracking = true;
            library.Preferences.ShowStoreTags = false;
            library.Preferences.DisableArtworkAccent = true;
            library.Preferences.DefaultAccentColour = "#31415D";
            library.Preferences.UsePreviewUpdates = true;
            library.Collections.Add(new GameCollection
            {
                Name = "Space adventures", Rules = new List<CollectionRule>
                {
                    new CollectionRule { Attribute = CollectionAttribute.Genre, Operator = CollectionOperator.Is, Qualifier = "adventure" },
                    new CollectionRule { Attribute = CollectionAttribute.SteamTag, Operator = CollectionOperator.Contains, Qualifier = "space" },
                    new CollectionRule { Attribute = CollectionAttribute.SteamTag, Operator = CollectionOperator.IsNot, Qualifier = "action" }
                }
            });
            store.Save(library);
            var saved = store.Load();
            var savedGame = saved.Games[0];
            Check(savedGame.TagNames.SequenceEqual(new[] { "Co-op" }) && savedGame.SteamTagsFor().Contains("Space Sim"),
                "Manual tags override the displayed tag list while Steam tags remain available");
            var developerRule = new CollectionRule
            {
                Attribute = CollectionAttribute.Developer,
                Operator = CollectionOperator.Contains,
                Qualifier = "rockstar"
            };
            Check(developerRule.Matches(savedGame) &&
                new CollectionRule { Attribute = CollectionAttribute.Publisher, Qualifier = "Rockstar Games" }.Matches(savedGame),
                "Developer and publisher rules match persisted Steam details");
            Check(savedGame.ReleaseDate?.Year == 2026 && savedGame.Scale == GameScale.AAA && saved.Preferences.DisablePlaytimeTracking,
                "Game release date, scale and playtime preference survive reload");
            savedGame.IsModded = true;
            Check(new CollectionRule { Attribute = CollectionAttribute.Modded, Qualifier = "Yes" }.Matches(savedGame),
                "Modded rules use the current managed installation status");
            Check(saved.UsageNoticeAccepted, "The first-run acknowledgement is stored between launches");
            Check(saved.Preferences.UsePreviewUpdates, "Next preview opt-in survives reload");
            Check(saved.Folder == root && saved.Games[0].CollectionIds.Single() == saved.Collections[0].Id, "Watched folder and collection membership survive reload");
            Check(saved.Games[0].PlaySeconds == 271 && saved.Games[0].LastPlayedUtc == launched && saved.Games[0].SteamId == 19, "Play history and Steam ID survive reload");
            var simulation = new GameCollection { GenreFilters = new List<string> { "Simulation" } };
            var simulationRule = new CollectionRule { Attribute = CollectionAttribute.Genre, Qualifier = "Simulation" };
            Check(saved.Games[0].SteamGenresComplete && saved.Games[0].Genres.Count == 4 &&
                simulation.Includes(saved.Games[0]) && simulationRule.Matches(saved.Games[0]) &&
                simulation.Includes(new Game { Genres = saved.Games[0].Genres, ManualGenres = true }),
                "All saved genres are available to legacy filters and collection rules");
            var rules = saved.Collections.Single(item => item.IsDynamic);
            Check(!saved.Preferences.ShowStoreTags && rules.Includes(saved.Games[0]) && saved.Games[0].SteamTagsFor().Contains("Space Sim"),
                "Hidden store tags still work in mixed collection rules after reload");
            Check(saved.Preferences.DisableArtworkAccent, "The artwork accent opt-out survives reload");
            Check(saved.Preferences.DefaultAccentColour == "#31415D", "The default accent colour survives reload");
            saved.Games[0].SteamId = 620;
            Check(!saved.Games[0].SteamTagsFor().Any() && !rules.Includes(saved.Games[0]) &&
                !rules.Rules[2].Matches(saved.Games[0]),
                "A new Steam ID hides stale tags and an unknown tag source does not satisfy exclusion");
            rules.MatchAnyGenre = true;
            Check(rules.Includes(saved.Games[0]), "Match any includes a game when one of its rules matches");
            saved.Games[0].SteamId = 19;
            Check(saved.Games[0].CustomArtworkId == replacement && saved.Games[0].ArtworkPosition.X == .72 &&
                saved.Games[0].ArtworkPosition.Y == .39 && saved.Games[0].ArtworkPosition.Zoom == 1.65,
                "Artwork replacement, position and zoom are saved per game");
            Check(store.CustomArtworkPath("../other") == null &&
                store.CustomArtworkPath(replacement) == Path.Combine(store.ArtworkDirectory, "custom", replacement + ".img"),
                "Replacement artwork is confined to the launcher-managed cache");
            Check(store.ThemeMusicPath("../" + replacement + ".mp3") == null &&
                store.ThemeMusicPath(replacement + ".exe") == null &&
                store.ThemeMusicPath(replacement + ".wav") == Path.Combine(root, "data", "music", replacement + ".wav"),
                "Imported music is confined to the profile music folder and supported extensions");
            saved.Games[0].Title = "Renamed";
            store.Save(saved);
            var libraryPath = Path.Combine(root, "data", "library.json");
            var backupPath = libraryPath + ".bak";
            var backup = File.ReadAllText(backupPath);
            File.WriteAllText(libraryPath, "broken");
            var recovered = store.Load();
            Check(recovered.Games.Single().Title == "Star Voyage", "Previous library is recovered when the latest save is unreadable");
            recovered.Games[0].Title = "Recovered and edited";
            store.Save(recovered);
            Check(File.ReadAllText(backupPath) == backup, "Saving a recovered library preserves the last readable backup");
            Check(store.Load().Games.Single().Title == "Recovered and edited", "A recovered library can save new edits");
            recovered.Games[0].Title = "Edited again";
            store.Save(recovered);
            Check(File.ReadAllText(backupPath) != backup, "Later saves resume rotating the recovery backup");
            foreach (var invalidLibrary in new[] { "{\"Games\":[null]}", "{\"Collections\":[null]}", "{\"Preferences\":{\"RssFeeds\":[null]}}" })
            {
                File.WriteAllText(libraryPath, invalidLibrary);
                Check(store.Load().Games.Single().Title == "Recovered and edited",
                    "A null game, collection, or RSS record falls back to the last readable backup");
            }

            var watched = Directory.CreateDirectory(Path.Combine(root, "watch"));
            using (var changed = new ManualResetEventSlim())
            using (var watcher = new LibraryWatcher(watched.FullName))
            {
                watcher.Changed += () => changed.Set();
                watcher.Start();
                File.WriteAllText(Path.Combine(watched.FullName, "NewGame.exe"), "new");
                Check(changed.Wait(TimeSpan.FromSeconds(8)), "New executables trigger a watcher notification");
            }

            using (var finished = new ManualResetEventSlim())
            using (var sessions = new GameSessions())
            {
                long seconds = -1;
                sessions.Ended += (id, elapsed) => { if (id == "session") { seconds = elapsed; finished.Set(); } };
                var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                sessions.Launch("session", command, "/d /c exit 0", setProcessPriority: true);
                Check(finished.Wait(TimeSpan.FromSeconds(8)) && seconds >= 0, "Exited executable completes its tracked session");
            }

            foreach (var setProcessPriority in new[] { false, true })
            {
                using (var finished = new ManualResetEventSlim())
                using (var sessions = new GameSessions())
                {
                    sessions.Ended += (id, elapsed) => { if (id == "priority") finished.Set(); };
                    var resultFile = Path.Combine(root, "priority-" + setProcessPriority + ".txt");
                    var arguments = "--write-priority \"" + resultFile + "\"";
                    var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the fixture executable.");
                    sessions.Launch("priority", executablePath, arguments, setProcessPriority);
                    // Let the child read its own priority after Launch has returned.
                    File.WriteAllText(resultFile + ".ready", "");
                    Check(finished.Wait(TimeSpan.FromSeconds(8)) && File.Exists(resultFile), "The launch priority probe completed");
                    Check((File.ReadAllText(resultFile) == "High") == setProcessPriority,
                        setProcessPriority ? "The opted-in executable receives High process priority" :
                            "The opted-out executable does not receive High process priority");
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
