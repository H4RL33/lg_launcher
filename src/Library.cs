using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace LGLauncher
{
    [DataContract]
    internal sealed partial class Game
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Title { get; set; }
        [DataMember] public string Path;
        [DataMember] public string Arguments = "";
        [DataMember] public int SteamId;
        [DataMember] public long PlaySeconds;
        [DataMember] public DateTime? LastPlayedUtc;
        [DataMember] public List<string> CollectionIds = new List<string>();
        [DataMember(EmitDefaultValue = false)] public string CustomArtworkId;
        [DataMember(EmitDefaultValue = false)] public string CustomArtworkFileName { get; set; }
        [DataMember(EmitDefaultValue = false)] public string CustomPortraitId { get; set; }
        [DataMember(EmitDefaultValue = false)] public string CustomPortraitFileName { get; set; }
        [DataMember(EmitDefaultValue = false)] public string CustomLogoId { get; set; }
        [DataMember(EmitDefaultValue = false)] public string CustomLogoFileName { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ThemeMusicFile { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ThemeMusicFileName { get; set; }
        [DataMember(EmitDefaultValue = false)] public ArtworkPosition ArtworkPosition;
        [DataMember(EmitDefaultValue = false)] public List<string> Genres { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool ManualGenres { get; set; }
        [DataMember(EmitDefaultValue = false)] public int GenreSteamId { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SteamGenresComplete { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<string> SteamTags { get; set; }
        [DataMember(EmitDefaultValue = false)] public int TagSteamId { get; set; }

        internal IEnumerable<string> GenreNames
        {
            get { return Genres ?? Enumerable.Empty<string>(); }
        }

        internal bool HasSteamTags { get { return SteamId > 0 && TagSteamId == SteamId && SteamTags != null; } }

        internal IEnumerable<string> SteamTagsFor()
        {
            return HasSteamTags ? SteamTags : Enumerable.Empty<string>();
        }
    }

    [DataContract]
    internal sealed class ArtworkPosition
    {
        public const double MinimumZoom = 1;
        public const double MaximumZoom = 4;
        [DataMember] public double X;
        [DataMember] public double Y;
        [DataMember(EmitDefaultValue = false)] public double Zoom = MinimumZoom;

        public static double ClampZoom(double zoom)
        {
            return Double.IsNaN(zoom) ? MinimumZoom : Math.Max(MinimumZoom, Math.Min(MaximumZoom, zoom));
        }
    }

    [DataContract]
    internal sealed class GameCollection
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name;
        [DataMember(EmitDefaultValue = false)] public List<string> GenreFilters { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool MatchAnyGenre { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<CollectionRule> Rules { get; set; }
        [DataMember(EmitDefaultValue = false)] public string IconName { get; set; }

        public bool IsDynamic { get { return (Rules != null ? Rules.Count : GenreFilters == null ? 0 : GenreFilters.Count) > 0; } }

        public IEnumerable<CollectionRule> EffectiveRules()
        {
            return Rules ?? (GenreFilters ?? Enumerable.Empty<string>())
                .Select(genre => new CollectionRule
                {
                    Attribute = CollectionAttribute.Genre, Operator = CollectionOperator.Is, Qualifier = genre
                });
        }

        public bool Includes(Game game)
        {
            if (!IsDynamic) return game.CollectionIds.Contains(Id);
            if (Rules != null)
                return MatchAnyGenre
                    ? Rules.Any(rule => rule != null && rule.Matches(game))
                    : Rules.All(rule => rule != null && rule.Matches(game));
            var genres = game.GenreNames;
            return MatchAnyGenre
                ? GenreFilters.Any(genre => genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                : GenreFilters.All(genre => genres.Contains(genre, StringComparer.OrdinalIgnoreCase));
        }
    }

    internal enum CollectionAttribute { Genre, SteamTag }
    internal enum CollectionOperator { Is, IsNot, Contains }

    [DataContract]
    internal sealed class CollectionRule
    {
        [DataMember] public CollectionAttribute Attribute { get; set; }
        [DataMember] public CollectionOperator Operator { get; set; }
        [DataMember] public string Qualifier { get; set; }

        internal bool Matches(Game game)
        {
            if (String.IsNullOrWhiteSpace(Qualifier)) return false;
            IEnumerable<string> values;
            switch (Attribute)
            {
                case CollectionAttribute.Genre:
                    if (game.Genres == null) return false;
                    values = game.GenreNames;
                    break;
                case CollectionAttribute.SteamTag:
                    if (!game.HasSteamTags) return false;
                    values = game.SteamTagsFor();
                    break;
                default:
                    return false;
            }

            var qualifier = Qualifier.Trim();
            switch (Operator)
            {
                case CollectionOperator.Is: return values.Contains(qualifier, StringComparer.OrdinalIgnoreCase);
                case CollectionOperator.IsNot: return !values.Contains(qualifier, StringComparer.OrdinalIgnoreCase);
                case CollectionOperator.Contains:
                    return values.Any(value => value != null && value.IndexOf(qualifier, StringComparison.OrdinalIgnoreCase) >= 0);
                default: return false;
            }
        }
    }

    [DataContract]
    internal sealed class LauncherPreferences
    {
        [DataMember] public string InterfaceFont { get; set; }
        [DataMember(EmitDefaultValue = false)] public int InterfaceFontSize { get; set; }
        [DataMember(EmitDefaultValue = false)] public int InterfaceScalePercent { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool DisableTranslucency { get; set; }
        [DataMember(EmitDefaultValue = false)] public string DefaultAccentColour { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool DisableArtworkAccent { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool ReduceMotion { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool DisableArtworkShift { get; set; }
        [DataMember] public bool UseGameLogos { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool PlayThemeMusic { get; set; }
        [DataMember] public bool ShowStoreTags { get; set; }
        [DataMember] public bool ShowRecentRss { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool StartWithSystem { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool StartMinimised { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool CloseToTray { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool CloseWhenGameLaunched { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool DisableHardwareRendering { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SetProcessPriority { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool AutomaticUpdates { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool DisableContributionReminders { get; set; }
        [DataMember(EmitDefaultValue = false)] public int RssRefreshMinutes { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<RssSubscription> RssFeeds { get; set; }

        public LauncherPreferences() { UseGameLogos = ShowStoreTags = ShowRecentRss = true; }

        [OnDeserializing]
        private void SetDefaults(StreamingContext context) { UseGameLogos = ShowStoreTags = ShowRecentRss = true; }
    }

    [DataContract]
    internal sealed class RssSubscription
    {
        [DataMember] public Guid Id { get; set; }
        [DataMember] public string Location { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool IsFile { get; set; }
        [DataMember] public bool Enabled { get; set; }
    }

    [DataContract]
    internal sealed class Library
    {
        [DataMember] public bool UsageNoticeAccepted;
        [DataMember(EmitDefaultValue = false)] public long GameReturns { get; set; }
        [DataMember] public LauncherPreferences Preferences = new LauncherPreferences();
        [DataMember] public string Folder;
        [DataMember] public List<Game> Games = new List<Game>();
        [DataMember] public List<GameCollection> Collections = new List<GameCollection>();
        [DataMember] public List<string> IgnoredPaths = new List<string>();
    }

    internal sealed class LibraryStore
    {
        private readonly string file;
        private readonly DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Library));
        public string ArtworkDirectory { get; private set; }

        public LibraryStore(string directory)
        {
            file = Path.Combine(directory, "library.json");
            ArtworkDirectory = Path.Combine(directory, "artwork");
        }

        public string CustomArtworkPath(string artworkId)
        {
            Guid parsed;
            return Guid.TryParseExact(artworkId, "N", out parsed)
                ? Path.Combine(ArtworkDirectory, "custom", parsed.ToString("N") + ".img")
                : null;
        }

        public string ThemeMusicPath(string fileName)
        {
            if (fileName == null || fileName.Length != 36) return null;
            var extension = fileName.Substring(32);
            Guid id;
            return (extension == ".mp3" || extension == ".wav") &&
                Guid.TryParseExact(fileName.Substring(0, 32), "N", out id) &&
                fileName == id.ToString("N") + extension
                ? Path.Combine(Path.GetDirectoryName(file), "music", fileName)
                : null;
        }

        public Library Load()
        {
            var library = Read(file) ?? Read(file + ".bak");
            if (library == null)
            {
                if (File.Exists(file) || File.Exists(file + ".bak"))
                    throw new IOException("The library and its backup could not be read. Your files are still in " + Path.GetDirectoryName(file) + ".");
                return new Library();
            }
            library.Games = library.Games ?? new List<Game>();
            library.Collections = library.Collections ?? new List<GameCollection>();
            library.IgnoredPaths = library.IgnoredPaths ?? new List<string>();
            library.Preferences = library.Preferences ?? new LauncherPreferences();
            foreach (var game in library.Games)
            {
                game.CollectionIds = game.CollectionIds ?? new List<string>();
                var position = game.ArtworkPosition;
                if (position != null && !(position.X >= 0 && position.X <= 1 && position.Y >= 0 && position.Y <= 1))
                    game.ArtworkPosition = null;
                else if (position != null)
                    position.Zoom = ArtworkPosition.ClampZoom(position.Zoom);
            }
            return library;
        }

        private Library Read(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = File.OpenRead(path))
                    return (Library)serializer.ReadObject(stream);
            }
            catch (SerializationException) { return null; }
        }

        public void Save(Library library)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temporary = file + ".tmp";
            using (var stream = File.Create(temporary))
            {
                serializer.WriteObject(stream, library);
                stream.Flush(true);
            }
            if (File.Exists(file)) File.Replace(temporary, file, file + ".bak");
            else File.Move(temporary, file);
        }
    }

    internal sealed class Discovery
    {
        public string Title { get; private set; }
        public string Path { get; private set; }

        private Discovery(string title, string path)
        {
            Title = title;
            Path = path;
        }

        public static List<Discovery> Scan(string root, CancellationToken token)
        {
            var games = new List<Discovery>();
            root = new DirectoryInfo(root).FullName.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var folders = new Stack<Tuple<string, string>>();
            folders.Push(Tuple.Create(root + System.IO.Path.DirectorySeparatorChar, (string)null));
            while (folders.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var folder = folders.Pop();
                try
                {
                    foreach (var path in Directory.EnumerateFiles(folder.Item1, "*.exe"))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!String.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var name = System.IO.Path.GetFileNameWithoutExtension(path);
                        if (Regex.IsMatch(name, @"^(unins|unitycrashhandler|crashreport|crashpad|vc_redist|vcredist|dxsetup|easyanticheat).*|^(setup|install|uninstall|updater|repair)([-_. ].*|\d*)$", RegexOptions.IgnoreCase)) continue;
                        games.Add(new Discovery(folder.Item2 ?? FriendlyName(name), path));
                    }
                    foreach (var path in Directory.EnumerateDirectories(folder.Item1))
                    {
                        token.ThrowIfCancellationRequested();
                        var name = System.IO.Path.GetFileName(path);
                        if (name.StartsWith(".") || Regex.IsMatch(name, @"^(\$Recycle\.Bin|System Volume Information|_CommonRedist|Redist|__Installer|EasyAntiCheat)$", RegexOptions.IgnoreCase)) continue;
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        folders.Push(Tuple.Create(path, folder.Item2 ?? FriendlyName(name)));
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return games.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(game => game.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string FriendlyName(string name)
        {
            return Regex.Replace(Regex.Replace(name.Replace('_', ' '), @"[- ]Win(32|64)(-Shipping)?$|\.exe$", "", RegexOptions.IgnoreCase),
                                 @"(?<=\p{Ll})(?=\p{Lu})", " ").Trim();
        }
    }

    internal sealed class LibraryWatcher : IDisposable
    {
        private readonly FileSystemWatcher watcher;
        private readonly Timer debounce;
        private volatile bool disposed;
        public event Action Changed;

        public LibraryWatcher(string folder)
        {
            debounce = new Timer(_ => { var handler = Changed; if (!disposed && handler != null) handler(); });
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += OnChange;
            watcher.Error += (sender, args) => Schedule();
        }

        public void Start() { watcher.EnableRaisingEvents = true; }

        private void OnChange(object sender, FileSystemEventArgs args)
        {
            // Dotted directory names can contain newly added games too.
            var renamed = args as RenamedEventArgs;
            if (args.ChangeType == WatcherChangeTypes.Deleted || Directory.Exists(args.FullPath) ||
                args.FullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                (renamed != null && renamed.OldFullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) Schedule();
        }

        private void Schedule()
        {
            if (disposed) return;
            try { debounce.Change(750, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            disposed = true;
            watcher.Dispose();
            debounce.Dispose();
        }
    }
}
