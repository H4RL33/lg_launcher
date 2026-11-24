using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private const string ModRequestTemplate = """
        ## Mod name

        <!-- What is the mod called? -->

        ## Official project link

        <!-- Link to the project's website, source, or official downloads. -->

        ## Games and dependencies

        <!-- Which games does it support and what other mods or loaders does it require? -->

        ## Why include it?

        <!-- What would launcher-managed installs help you do? -->
        """;
    private static readonly Uri ModRequestPage = new("https://github.com/H4RL33/lg_launcher/issues/new" +
        $"?title={Uri.EscapeDataString("[Mod request] ")}&body={Uri.EscapeDataString(ModRequestTemplate)}");
    private static readonly Uri OptiScalerSpoofingGuide = new("https://github.com/optiscaler/OptiScaler/blob/master/Spoofing.md");
    private static readonly ModDxgiSpoofing[] DxgiSpoofingModes = Enum.GetValues<ModDxgiSpoofing>();
    private static readonly ModProviderId[] AvailableModProviders =
        [ModProviderId.OptiScaler, ModProviderId.ReFramework, ModProviderId.ReShade, ModProviderId.RenoDx];

    private readonly ModCompatibility _modsCompatibility = ModCompatibility.Load();
    private readonly ModCatalogStore _modCatalog = new(Program.DataDirectory);
    private readonly ModRenoDxCatalog _renoDxCatalog = new(Program.DataDirectory);
    private readonly DispatcherTimer _modCatalogTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly ModPackageStore _modPackages = new(Program.DataDirectory);
    private readonly ModTransactions _modTransactions = new(Program.DataDirectory);
    private readonly CancellationTokenSource _modLifetime = new();
    private readonly Dictionary<ModProviderId, string> _modPackageMessages = [];
    private readonly Dictionary<ModProviderId, ModPackageDescriptor> _checkedModDownloads = [];
    private readonly Dictionary<ModProviderId, ModUpstreamRelease> _checkedModReleases = [];
    private static readonly FilePickerFileType RenoDxAddonFile = new("RenoDX add-on") { Patterns = ["*.addon64"] };
    private readonly Dictionary<ModProviderId, ModPackageTransfer?> _modPackageOperations = [];
    private readonly Dictionary<Guid, ModInstallInspection> _modInspections = [];
    private readonly Dictionary<Guid, Task<ModInstallInspection>> _modInspectionTasks = [];
    private StackPanel? _modsSettings;
    private bool _modFileOperation;
    private Game? _modListGame;
    private Guid? _modListOrphan;
    private StackPanel? _gameModRows;
    private readonly Dictionary<ModProviderId, (Button Row, SymbolIcon Tick)> _gameModEntries = [];
    private ModAssessment? _selectedModAssessment;
    private Dictionary<ModProviderId, TextBlock>? _gameModStatuses;
    private StackPanel? _legacyGameModRows;
    private (Expander Panel, TextBlock Count, StackPanel Messages)? _gameModWarnings;
    private (Guid Installation, ModPackageKey Package)[] _visibleLegacyGameMods = [];
    private Button? _optiScalerUpdateCheck;
    private DateTimeOffset? _lastModCatalogAttempt;
    private bool _modCatalogBusy;
    private string? _modCatalogMessage;
    private DateTimeOffset? _lastRenoDxCatalogAttempt;
    private Task<bool>? _renoDxCatalogRefresh;

    private Control BuildMods()
    {
        _modsSettings = new StackPanel { Spacing = 12 };
        RefreshModsSettings();
        return _modsSettings;
    }

    private void StartModCatalogRefresh()
    {
        if (_verification || !OperatingSystem.IsWindows()) return;
        _modCatalogTimer.Tick += (_, _) =>
        {
            _ = RefreshModCatalog();
            _ = RefreshRenoDxCatalog();
        };
        _modCatalogTimer.Start();
        _ = RefreshModCatalog();
        _ = RefreshRenoDxCatalog();
    }

    private Task<bool> RefreshRenoDxCatalog(bool force = false)
    {
        if (_renoDxCatalogRefresh is { IsCompleted: false } refresh) return refresh;
        var now = DateTimeOffset.UtcNow;
        if (_closed || !force && (!CanFetch || _updateHandoff ||
            _lastRenoDxCatalogAttempt is { } recent && now - recent < TimeSpan.FromDays(1)))
            return Task.FromResult(_renoDxCatalog.Available);
        _lastRenoDxCatalogAttempt = now;
        return _renoDxCatalogRefresh = Refresh();

        async Task<bool> Refresh()
        {
            try
            {
                var updated = await _renoDxCatalog.RefreshAsync(_modLifetime.Token);
                if (!_closed && updated && _game is { } game && _page == Page.Game) RefreshGameModList(game);
                return updated;
            }
            catch (OperationCanceledException) when (_modLifetime.IsCancellationRequested) { return false; }
        }
    }

    private async Task<string?> RefreshModCatalog(bool force = false, HttpClient? client = null)
    {
        if (_closed || _modCatalogBusy) return null;
        var now = DateTimeOffset.UtcNow;
        var lastCheck = _lastModCatalogAttempt ?? _modCatalog.LastChecked;
        if (!force && (!CanFetch || _updateHandoff || lastCheck is { } recent &&
            recent <= now && now - recent < TimeSpan.FromDays(1))) return null;
        _lastModCatalogAttempt = now;
        _modCatalogBusy = true;
        _modCatalogMessage = "Checking for compatibility updates…";
        RenderOptiScalerUpdateCheck();
        try
        {
            var result = await _modCatalog.RefreshAsync(_modLifetime.Token, client);
            _modCatalogMessage = result == ModCatalogRefresh.Unavailable ? "Could not refresh compatibility. Using the current list." : null;
            if (!_closed && result == ModCatalogRefresh.Updated && _game is { } game && _page == Page.Game) RenderGameModAction(game);
            return result switch
            {
                ModCatalogRefresh.Updated => "refreshed",
                ModCatalogRefresh.Unavailable => "refresh failed (using current list)",
                _ => "up-to-date"
            };
        }
        catch (OperationCanceledException) when (_modLifetime.IsCancellationRequested) { return null; }
        finally
        {
            _modCatalogBusy = false;
            RenderOptiScalerUpdateCheck();
        }
    }

    private void RenderOptiScalerUpdateCheck()
    {
        if (_closed || _page != Page.Mods || _optiScalerUpdateCheck == null) return;
        var source = _modCatalog.Current is { } catalog && _modCatalog.LastChecked is { } checkedAt
            ? $"Catalog {catalog.Version} · Last checked {checkedAt.ToLocalTime():g}" : "Bundled compatibility list";
        var description = "Check the OptiScaler package and compatibility list for updates." + Environment.NewLine + source;
        if (_modCatalogMessage != null) description += Environment.NewLine + _modCatalogMessage;
        ToolTip.SetTip(_optiScalerUpdateCheck, description);
        AutomationProperties.SetHelpText(_optiScalerUpdateCheck, description);
        _optiScalerUpdateCheck.IsEnabled = !_modCatalogBusy && !_modPackageOperations.ContainsKey(ModProviderId.OptiScaler);
    }

    private Task<bool> CheckModUpdates(ModProviderId provider, Func<CancellationToken, Task<ModPackageCheck>>? checkPackage = null,
        HttpClient? catalogClient = null) => ChangeModPackage(provider, "Checking for updates…", async token =>
    {
        if (provider == ModProviderId.RenoDx)
            return await RefreshRenoDxCatalog(force: true) ? "Game catalog refreshed." : "Could not refresh the game catalog.";
        if (provider != ModProviderId.OptiScaler) return $"Mod: {await CheckPackage()}";

        var catalog = RefreshModCatalog(force: true, catalogClient);
        string packageStatus;
        try { packageStatus = await CheckPackage(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { packageStatus = "The package check timed out."; }
        catch (Exception error) when (ModUiFailure(error)) { packageStatus = $"Could not check the package. {error.Message}"; }
        return $"Mod: {packageStatus}, Compat List: {await catalog ?? "not checked"}";

        async Task<string> CheckPackage()
        {
            var result = await (checkPackage == null ? _modPackages.CheckAsync(provider, token) : checkPackage(token));
            if (result.Download is { } download) _checkedModDownloads[provider] = download;
            if (result.Release is { } release) _checkedModReleases[provider] = release;
            else _checkedModReleases.Remove(provider);
            return result.Status;
        }
    });

    private void RefreshModsSettings()
    {
        if (_closed || _page != Page.Mods || _modsSettings == null) return;
        var body = _modsSettings;
        body.Children.Clear();
        try
        {
            var available = new StackPanel { Spacing = 12 };
            var records = _modTransactions.FindInstallations();
            foreach (var provider in AvailableModProviders)
            {
                var gameSpecific = provider == ModProviderId.RenoDx;
                var packages = gameSpecific ? [] : _modPackages.List(provider);
                var checkedDownload = _checkedModDownloads.GetValueOrDefault(provider);
                var preferred = gameSpecific ? null : _modPackages.Preferred(provider, checkedDownload, packages);
                var cached = preferred == null ? null : packages.FirstOrDefault(package => package.Key == preferred.Key);
                var busy = _modPackageOperations.TryGetValue(provider, out var transfer);
                var check = ModSymbolButton($"Check {ModPackageStore.Name(provider)} for updates", "LG.Icon.Refresh",
                    () => _ = CheckModUpdates(provider));
                check.IsEnabled = !busy;
                var info = ModPackageInfo(provider);
                var actions = gameSpecific ? Actions(check, info) : Actions(info, check);
                if (preferred != null && cached == null)
                {
                    var updating = checkedDownload != null && (packages.Count > 0 || preferred.Key != ModPackageStore.Approved(provider).Key);
                    var modName = ModPackageStore.Name(provider);
                    void Download() => _ = DownloadModPackage(provider, (progress, token) => _modPackages.AcquireAsync(preferred, token, progress));
                    var download = updating ? ModUpdateButton($"Update {modName}", Download) :
                        ModSymbolButton($"Download {modName}", "LG.Icon.Download", Download, "primary");
                    download.IsEnabled = !busy;
                    actions.Children.Add(download);
                }
                if (preferred != null && _checkedModReleases.GetValueOrDefault(provider) is { } release &&
                    ModPackageStore.IsNewer(provider, release.Version, preferred.Key.Version))
                    actions.Children.Add(ModSymbolButton($"View {ModPackageStore.Name(provider)} {release.Version} release",
                        "LG.Icon.ExternalLink", () => OpenModNotes(release.Page)));
                if (packages.Count > 0)
                {
                    var delete = ModCacheDeleteButton(provider, packages);
                    delete.IsEnabled = !busy;
                    actions.Children.Add(delete);
                }
                if (provider == ModProviderId.OptiScaler)
                {
                    _optiScalerUpdateCheck = check;
                    RenderOptiScalerUpdateCheck();
                }
                var availability = gameSpecific ? "Game-specific add-ons"
                    : cached != null ? $"Cached {cached.Key.Version} · {cached.Key.Variant}"
                    : checkedDownload != null && packages.Count > 0 ? "An earlier package is cached."
                    : provider == ModProviderId.ReShade ? "Full add-on package is not downloaded."
                    : "Approved package is not downloaded.";
                if (_modPackageMessages.TryGetValue(provider, out var message)) availability += $" · {message}";
                var name = Label(ModPackageStore.Name(provider));
                name.FontWeight = FontWeight.SemiBold;
                var labels = new StackPanel { Spacing = 4, Children = { name, Label(availability, muted: true) } };
                var details = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("22,*"), ColumnSpacing = 11, Children = { ModIcon(provider), labels }
                };
                Grid.SetColumn(labels, 1);
                var row = new StackPanel { Spacing = 8, Children = { ModSettingsRow(details, actions) } };
                if (transfer != null)
                {
                    var bar = ModProgressBar($"{ModPackageStore.Name(provider)} download progress", transfer.Percent);
                    transfer.Bar = bar;
                    row.Children.Add(bar);
                }
                AddModSettingsRow(available, row);
            }
            body.Children.Add(Section("Available mods", available));
            var management = new StackPanel { Spacing = 12 };
            foreach (var record in records) AddModSettingsRow(management, ManagedModSettingsRow(record));
            if (_modTransactions.HasAnyPending())
            {
                var recover = ActionButton("Retry recovery", () => _ = RecoverModChanges());
                recover.IsEnabled = !_modFileOperation;
                management.Children.Add(Actions(recover));
            }
            if (management.Children.Count == 0) management.Children.Add(Label("No managed installations.", muted: true));
            body.Children.Add(Section("Managed installations", management));
        }
        catch (Exception error) when (ModUiFailure(error)) { body.Children.Add(Label(error.Message, muted: true)); }
    }

    private static Grid ModSettingsRow(Control details, Control actions)
    {
        details.VerticalAlignment = VerticalAlignment.Center;
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 1);
        return new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 12, Children = { details, actions } };
    }

    private SymbolIcon ModIcon(ModProviderId provider)
    {
        var name = provider switch
        {
            ModProviderId.OptiScaler => "scaling",
            ModProviderId.ReFramework => "construction",
            ModProviderId.SpecialK => "gauge",
            ModProviderId.ReShade => "palette",
            ModProviderId.RenoDx => "lightbulb",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var icon = Symbol("LG.Icon.Wrench", 19);
        icon.Data = LucideIconCatalog.Find(name)?.Geometry ?? icon.Data;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    private void AddModSettingsRow(StackPanel section, Control row)
    {
        if (section.Children.Count > 0)
            section.Children.Add(new Border { Height = 1, Background = this.FindResource("LG.Line") as IBrush });
        section.Children.Add(row);
    }

    private Grid ManagedModSettingsRow(ModInstallationRecord record)
    {
        var root = record.Target.Root;
        var executable = Path.Combine(root, record.Target.Executable);
        var game = _library.Games.Find(item => String.Equals(item.Path, executable, StringComparison.OrdinalIgnoreCase));
        if (game == null)
        {
            var matches = _library.Games.Where(item => ModTransactions.Contains(root, item.Path)).Take(2).ToArray();
            if (matches.Length == 1) game = matches[0];
        }
        var title = game?.Title ?? Path.GetFileName(root);
        var name = Label(title);
        name.FontWeight = FontWeight.SemiBold;
        var labels = new StackPanel { Spacing = 4, Children = { name, Label(executable, muted: true) } };
        Grid.SetColumn(labels, 1);
        var fallback = Symbol("LG.Icon.Game", 19);
        var icon = new Panel { Children = { fallback } };
        if (game != null)
        {
            var image = new Image { Stretch = Stretch.Uniform };
            image.PropertyChanged += (_, args) =>
            {
                if (args.Property == Image.SourceProperty) fallback.IsVisible = image.Source == null;
            };
            image.Bind(Image.SourceProperty, new Binding(nameof(LibraryEntry.SidebarIcon)) { Source = GetEntry(game) });
            icon.Children.Add(image);
        }
        var iconFrame = new Border
        {
            Classes = { "lg-game-icon" },
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        };
        var details = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"), ColumnSpacing = 10, Children = { iconFrame, labels } };
        var manage = ActionButton("Manage", () => _ = ShowGameMods(game ?? new Game { Title = title, Path = executable },
            game == null ? record.Id : null));
        AutomationProperties.SetName(manage, $"Manage mods in {title}");
        return ModSettingsRow(details, manage);
    }

    private Button ModPackageInfo(ModProviderId provider)
    {
        var name = ModPackageStore.Name(provider);
        if (provider == ModProviderId.RenoDx)
            return ModSymbolButton($"About {name}", "LG.Icon.Info", () => _ = Notice($"About {name}",
                "RenoDX needs different add-on files for each game. Open the game’s Mods list and select RenoDX to check compatibility " +
                "and install the matching add-on. RenoDX requires ReShade with full add-on support."), "primary");
        var description = provider switch
        {
            ModProviderId.OptiScaler => "OptiScaler can swap a game's built-in upscaler for another one and add or replace frame generation in supported games.",
            ModProviderId.ReFramework => "REFramework is a modding framework for Capcom RE Engine games. Some of these games need it to run OptiScaler.",
            ModProviderId.ReShade => "ReShade adds post-processing effects such as colour grading, sharpening, and ambient occlusion.",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var info = new Button { Classes = { "lg", "quiet" }, Content = Symbol("LG.Icon.Info") };
        AutomationProperties.SetName(info, $"About {name}");
        AutomationProperties.SetHelpText(info, description);
        var tip = Label(description);
        tip.MaxWidth = 260;
        ToolTip.SetTip(info, tip);
        ToolTip.SetPlacement(info, PlacementMode.BottomEdgeAlignedRight);
        Click(info, () => ToolTip.SetIsOpen(info, true));
        return info;
    }

    private Task<bool> DownloadModPackage(ModProviderId provider, ProgressBar? dialogProgress = null) =>
        DownloadModPackage(provider, (progress, token) => _modPackages.AcquireAsync(ModPackageStore.Approved(provider), token, progress), dialogProgress);

    private Task<bool> DownloadRenoDxAddon(ModPackageDescriptor selected, ProgressBar progressBar, Action<byte[]> receive) =>
        ChangeModPackage(ModProviderId.RenoDx, "Downloading and checking the add-on…", async token =>
        {
            var progress = new Progress<int>(percent => progressBar.Value = percent);
            var bytes = await _modPackages.FetchRenoDxAsync(selected, token, progress);
            receive(bytes);
            return "Add-on ready for preview.";
        });

    private Task<bool> DownloadModPackage(ModProviderId provider, Func<IProgress<int>, CancellationToken, Task<CachedModPackage>> acquire,
        ProgressBar? dialogProgress = null)
    {
        var transfer = new ModPackageTransfer();
        return ChangeModPackage(provider, "Downloading and checking the package…", async token =>
        {
            var progress = new Progress<int>(percent =>
            {
                if (_closed || _modPackageOperations.GetValueOrDefault(provider) != transfer) return;
                transfer.Percent = percent;
                if (transfer.Bar is { } bar) bar.Value = percent;
                if (dialogProgress != null) dialogProgress.Value = percent;
            });
            var cache = await acquire(progress, token);
            return $"Cached {cache.Key.Version}.";
        }, transfer);
    }

    private Button ModSymbolButton(string name, string icon, Action action, string? variant = null)
    {
        var button = ActionButton(name, action, variant);
        button.Content = Symbol(icon, 17);
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private Button ModUpdateButton(string name, Action action)
    {
        var button = ModSymbolButton(name, "LG.Icon.Download", action, "primary");
        if (button.Content is SymbolIcon icon && LucideIconCatalog.Find("circle-arrow-up") is { } update)
            icon.Data = update.Geometry;
        return button;
    }

    private Button ModDeleteButton(string name, Action action)
    {
        var button = ModSymbolButton(name, "LG.Icon.Delete", action);
        button.Classes.Add("destructive");
        return button;
    }

    private Button ModCacheDeleteButton(ModProviderId provider, IReadOnlyList<CachedModPackage> packages)
    {
        var name = ModPackageStore.Name(provider);
        if (packages.Count == 1)
        {
            var package = packages[0];
            return ModDeleteButton($"Delete cached {name} {package.Key.Version}", () => _ = DeleteModPackage(package));
        }

        var delete = ModDeleteButton($"Delete cached {name} download…", () => _ = ChooseCachedModPackage(provider));
        AutomationProperties.SetHelpText(delete, "Choose a cached version and variant to delete.");
        return delete;
    }

    private async Task ChooseCachedModPackage(ModProviderId provider)
    {
        var name = ModPackageStore.Name(provider);
        var dialog = new ModalDialog($"Delete cached {name} downloads", 480);
        dialog.Body.Children.Add(Label("Deleting a cached copy leaves installed mods in their game folders.", muted: true));
        var installed = _modTransactions.FindInstallations();
        var downloads = _modPackages.List(provider);
        var duplicateLabels = downloads.GroupBy(ModCachePackageLabel, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CachedModPackage? selected = null;
        string? selectedLabel = null;
        foreach (var package in downloads.OrderByDescending(package => package.Key.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Key.Variant, StringComparer.OrdinalIgnoreCase))
        {
            var label = ModCachePackageLabel(package);
            if (duplicateLabels.Contains(label)) label += $" · {package.Key.Digest[..Math.Min(8, package.Key.Digest.Length)]}";
            var count = installed.Count(record => record.Packages.Contains(package.Key) && record.Files.Any(file => file.Owner == provider));
            var usage = count == 1 ? "1 game uses this version" : $"{count} games use this version";
            dialog.Body.Children.Add(SettingsRow(label, "LG.Icon.Download", () =>
            {
                selected = package;
                selectedLabel = label;
                dialog.Close(true);
            }, Label(usage, muted: true)));
        }
        AddModClose(dialog);
        if (await ShowModal(dialog) && selected != null) await DeleteModPackage(selected, selectedLabel);
    }

    private static string ModCachePackageLabel(CachedModPackage package) => $"{package.Key.Version} · {package.Key.Variant}";

    private static ProgressBar ModProgressBar(string name, int percent = 0)
    {
        var bar = new ProgressBar { Classes = { "lg-progress" }, Value = percent };
        AutomationProperties.SetName(bar, name);
        return bar;
    }

    private sealed class ModPackageTransfer
    {
        internal int Percent { get; set; }
        internal ProgressBar? Bar { get; set; }
    }

    private async Task DeleteModPackage(CachedModPackage package, string? selectedLabel = null)
    {
        var provider = package.Key.Provider;
        if (_closed || _modPackageOperations.ContainsKey(provider)) return;
        var name = ModPackageStore.Name(provider);
        var message = $"Games that already have {name} installed will keep it. " +
            $"This deletes the cached download ({selectedLabel ?? ModCachePackageLabel(package)}) used to set up {name} in other games. " +
            "You can download the approved package from Mods.";
        if (!await Confirm($"Delete cached {name}?", message, "Delete cached copy", destructive: true)) return;
        await ChangeModPackage(provider, "Deleting cached copy…", async token =>
        {
            await _modPackages.DeleteAsync(package.Key, token);
            return "Cached copy deleted.";
        });
    }

    private async Task<bool> ChangeModPackage(ModProviderId provider, string progress, Func<CancellationToken, Task<string>> action,
        ModPackageTransfer? transfer = null)
    {
        if (_closed || !_modPackageOperations.TryAdd(provider, transfer)) return false;
        var name = ModPackageStore.Name(provider);
        _modPackageMessages[provider] = progress;
        Status($"{name} · {progress}");
        RefreshModsSettings();
        try
        {
            _modPackageMessages[provider] = await action(_modLifetime.Token);
            if (!_closed) Status($"{name} · {_modPackageMessages[provider]}");
            return true;
        }
        catch (OperationCanceledException) { _modPackageMessages[provider] = "Package operation cancelled or timed out."; }
        catch (Exception error) when (ModUiFailure(error)) { _modPackageMessages[provider] = error.Message; }
        finally
        {
            _modPackageOperations.Remove(provider);
            RefreshModsSettings();
            if (_modListGame is { } game) RefreshGameModList(game);
        }
        if (!_closed) Status($"{name} · {_modPackageMessages[provider]}");
        return false;
    }

    private bool CanPlayWithMods(Game game)
    {
        if (_modFileOperation) return false;
        try { return !_modTransactions.HasPending(game.Path); }
        catch (Exception error) when (ModUiFailure(error)) { return false; }
    }

    private void RenderGameModAction(Game game)
    {
        GameModsButton.IsVisible = OperatingSystem.IsWindows() && !_editingArtwork;
        GameModsButton.IsEnabled = !_modFileOperation;
        RefreshGameModList(game);
    }

    private void RefreshGameModList(Game game)
    {
        if (_modListGame != game || _gameModStatuses is not { } statuses || _legacyGameModRows is not { } legacyRows ||
            _gameModRows is not { } rows) return;
        var advisory = _selectedModAssessment ?? _modsCompatibility.Advisory(game);
        var status = DisplayCompatibility(advisory.Status, _modCatalog.Find(game));
        ModInstallationRecord? record = null;
        try
        {
            record = ModListRecord(game);
            if (_modTransactions.HasPending(game.Path)) status = "Recovery needed";
            else if (record?.Files.Any(file => file.Owner == ModProviderId.OptiScaler) == true)
                status = !_modInspections.TryGetValue(record.Id, out var inspection) ? "Managed installation"
                    : NeedsReview(InspectionFor(inspection, ModProviderId.OptiScaler)) ? "Review changes"
                    : $"Installed {record.Packages.FirstOrDefault(package => package.Provider == ModProviderId.OptiScaler)?.Version}".TrimEnd();
            else if (ExternalOptiScaler(Path.GetDirectoryName(advisory.Target?.ExePath ?? game.Path))) status = "External install detected";
        }
        catch (Exception error) when (ModUiFailure(error)) { status = "Review changes"; }
        statuses[ModProviderId.OptiScaler].Text = status;

        var inspected = record == null ? null : _modInspections.GetValueOrDefault(record.Id);
        if (record?.Packages.FirstOrDefault(package => package.Provider == ModProviderId.OptiScaler) is { } opti &&
            status is not ("Recovery needed" or "Review changes"))
        {
            if (UpdateFor(record, opti) != null) statuses[ModProviderId.OptiScaler].Text = $"Installed {opti.Version} · Update available";
            else if (NewerModRelease(opti) != null) statuses[ModProviderId.OptiScaler].Text = $"Installed {opti.Version} · Newer release upstream";
        }
        bool Installed(ModProviderId provider) => record?.Files.Any(file => file.Owner == provider) == true;
        var ordered = AvailableModProviders.Where(Installed).Select(provider => (Control)_gameModEntries[provider].Row)
            .Append(legacyRows).Concat(AvailableModProviders.Where(provider => !Installed(provider))
                .Select(provider => _gameModEntries[provider].Row)).ToArray();
        foreach (var (provider, entry) in _gameModEntries)
        {
            entry.Tick.IsVisible = Installed(provider);
            entry.Row.IsEnabled = _modListOrphan == null || Installed(provider);
        }
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = rows.Children.IndexOf(ordered[index]);
            if (current < 0) rows.Children.Insert(index, ordered[index]);
            else if (current != index) rows.Children.Move(current, index);
        }
        var renoDx = _renoDxCatalog.Find(game);
        statuses[ModProviderId.ReShade].Text = InstalledStatus(ModProviderId.ReShade, "Set up ReShade");
        statuses[ModProviderId.ReFramework].Text = InstalledStatus(ModProviderId.ReFramework, "Check game support and set up");
        statuses[ModProviderId.RenoDx].Text = InstalledStatus(ModProviderId.RenoDx, renoDx switch
        {
            { Options.Length: > 0 } => "Game add-on available",
            not null => "See game add-ons",
            _ => "Import a downloaded add-on"
        });
        var legacyMods = record?.Packages.Where(package => !AvailableModProviders.Contains(package.Provider) &&
                record.Files.Any(file => file.Owner == package.Provider))
            .Select(package => (record.Id, package)).ToArray() ?? [];
        var warnings = new List<string>();
        if (record?.Files.Any(file => file.Owner == ModProviderId.OptiScaler) == true &&
            record.Files.Any(file => file.Owner == ModProviderId.ReFramework))
            warnings.Add("OptiScaler and REFramework both use Insert by default. Change REFramework’s menu key under Configuration → Menu key.");
        var requirements = ModCompatibility.OptiScalerRequirements(advisory, _modCatalog.Find(game));
        if (renoDx is { Options.Length: > 0 }) requirements = [.. requirements, ModRenoDxOption.ReShadeRequirement];
        var installedLinks = record?.KnownDependencies().Where(link => record.Files.Any(file => file.Owner == link.Dependent)) ?? [];
        foreach (var requirement in requirements.Concat(installedLinks).DistinctBy(link => (link.Dependent, link.Required)))
        {
            if (record?.HasDependency(requirement, inspected) == true) continue;
            var dependent = ModPackageStore.Name(requirement.Dependent);
            var required = ModPackageStore.Name(requirement.Required);
            warnings.Add(requirement.Variant switch
            {
                "pd-upscaler" => $"OptiScaler needs REFramework’s pd-upscaler branch and a companion upscaler plugin for this game. See the OptiScaler compatibility notes.",
                "full add-on support" => $"{dependent} needs ReShade with full add-on support for this game.",
                _ => $"{dependent} needs {required} to work with this setup."
            });
        }
        RenderGameModWarnings([.. warnings]);
        if (legacyMods.SequenceEqual(_visibleLegacyGameMods)) return;
        _visibleLegacyGameMods = legacyMods;
        legacyRows.Children.Clear();
        legacyRows.IsVisible = legacyMods.Length > 0;
        if (legacyMods.Length > 0) legacyRows.Children.Add(Label("Legacy installations", muted: true));
        foreach (var (installation, package) in legacyMods)
        {
            var description = Label($"Installed {package.Version}", muted: true);
            var row = SettingsRow(ModPackageStore.Name(package.Provider), ModIcon(package.Provider),
                () => _ = ShowGameManagedMod(game, installation, package.Provider), description, InstalledModTick());
            legacyRows.Children.Add(row);
        }

        string InstalledStatus(ModProviderId provider, string available)
        {
            var package = record?.Packages.FirstOrDefault(package => package.Provider == provider);
            if (package == null) return available;
            if (inspected != null && NeedsReview(InspectionFor(inspected, provider))) return "Review changes";
            if (UpdateFor(record!, package) != null) return $"Installed {package.Version} · Update available";
            return NewerModRelease(package) != null ? $"Installed {package.Version} · Newer release upstream" : $"Installed {package.Version}";
        }
    }

    private ModUpstreamRelease? NewerModRelease(ModPackageKey installed) =>
        _checkedModReleases.GetValueOrDefault(installed.Provider) is { } release &&
        ModPackageStore.IsNewer(installed.Provider, release.Version, installed.Version) ? release : null;

    private SymbolIcon InstalledModTick()
    {
        var tick = Symbol("LG.Icon.Check", 16);
        tick.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("LG.Success"));
        tick.VerticalAlignment = VerticalAlignment.Center;
        return tick;
    }

    private ModPackageDescriptor? UpdateFor(ModInstallationRecord record, ModPackageKey installed)
    {
        var provider = installed.Provider;
        if (provider == ModProviderId.RenoDx)
        {
            var game = _library.Games.FirstOrDefault(game => ModTransactions.Contains(record.Target.Root, game.Path));
            if (game == null) return null;
            if (_modInspections.TryGetValue(record.Id, out var inspection) &&
                !record.HasDependency(ModRenoDxOption.ReShadeRequirement, inspection)) return null;
            return _renoDxCatalog.Find(game)?.Options.FirstOrDefault(option =>
                RenoDxReplaces(option, installed) && option.Supports(record))?.Package;
        }
        if (provider == ModProviderId.SpecialK || provider == ModProviderId.ReShade && record.ReShadeVulkan ||
            record.RecipeId != null && provider is ModProviderId.ReFramework or ModProviderId.OptiScaler) return null;
        var preferred = _modPackages.Preferred(provider, _checkedModDownloads.GetValueOrDefault(provider));
        return preferred.Key.Variant == installed.Variant && ModPackageStore.IsNewer(provider, preferred.Key.Version, installed.Version)
            ? preferred : null;
    }

    private static bool RenoDxReplaces(ModRenoDxOption option, ModPackageKey current) =>
        option.File.Equals(current.Variant, StringComparison.OrdinalIgnoreCase) &&
        !option.Digest.Equals(current.Digest, StringComparison.OrdinalIgnoreCase) &&
        (current.Version == "Imported" || String.CompareOrdinal(option.Version, current.Version) >= 0);

    private ModInstallationRecord? ModListRecord(Game game) => _modListOrphan is { } id
        ? _modTransactions.FindInstallations().FirstOrDefault(record => record.Id == id)
        : _modTransactions.FindForGame(game.Path).FirstOrDefault();

    private void RefreshManagedGameStatus()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var records = _modTransactions.FindInstallations();
            foreach (var game in _library.Games)
                game.IsModded = records.Any(record => record.Files.Length > 0 && ModTransactions.Contains(record.Target.Root, game.Path));
        }
        catch (Exception error) when (ModUiFailure(error)) { Status("Could not read managed mods. " + error.Message); }
    }

    private void RenderGameModWarnings(string[] warnings)
    {
        if (_gameModWarnings is not { } state) return;
        if (warnings.SequenceEqual(state.Messages.Children.OfType<TextBlock>().Select(label => label.Text))) return;
        state.Panel.IsVisible = warnings.Length > 0;
        if (!state.Panel.IsVisible) state.Panel.IsExpanded = false;
        state.Count.Text = warnings.Length.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var noun = warnings.Length == 1 ? "warning" : "warnings";
        AutomationProperties.SetName(state.Panel, $"Warnings, {state.Count.Text} {noun}");
        state.Messages.Children.Clear();
        foreach (var warning in warnings) state.Messages.Children.Add(Label(warning));
    }

    private async Task RefreshSelectedModStatus()
    {
        _selectedModAssessment = null;
        if (_closed || !OperatingSystem.IsWindows() || _page != Page.Game || _game is not { } game) return;
        var launch = game.Path;
        var snapshot = new Game { Title = game.Title, Path = launch, SteamId = game.SteamId };
        try
        {
            if (_modTransactions.FindForGame(launch).FirstOrDefault() is { } record)
            {
                await RefreshModInspection(record.Id);
            }
            else
            {
                var assessment = await Task.Run(() => _modsCompatibility.Assess(snapshot), _modLifetime.Token);
                if (StillSelected(game, launch) && game.SteamId == snapshot.SteamId && game.Title == snapshot.Title)
                    _selectedModAssessment = assessment;
            }
            if (StillSelected(game, launch)) RenderGameModAction(game);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ModUiFailure(error))
        {
            if (StillSelected(game, launch) && _gameModStatuses is { } statuses)
                statuses[ModProviderId.OptiScaler].Text = "Review changes";
        }
    }

    private async Task ShowGameMods(Game game, Guid? orphan = null)
    {
        if (_modListGame != null || _closed) return;
        var list = new ModalDialog("Mods", 440);
        var opening = false;
        var rows = list.Body;
        var statuses = new Dictionary<ModProviderId, TextBlock>();
        foreach (var provider in AvailableModProviders)
        {
            var status = Label("", muted: true);
            statuses.Add(provider, status);
            var tick = InstalledModTick();
            var row = SettingsRow(ModPackageStore.Name(provider), ModIcon(provider), () => _ = OpenMod(() => provider switch
            {
                ModProviderId.OptiScaler => ShowGameOptiScaler(game, list),
                ModProviderId.ReShade => ShowGameReShade(game, list),
                _ => ShowGameAddon(game, list, provider)
            }), status, tick);
            _gameModEntries.Add(provider, (row, tick));
        }
        var warningTitle = Label("Warnings");
        var count = Label("");
        foreach (var label in new[] { warningTitle, count })
        {
            label.Foreground = this.FindResource("LG.Warning") as IBrush;
            label.FontWeight = FontWeight.SemiBold;
            label.VerticalAlignment = VerticalAlignment.Center;
        }
        var warningHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Children = { warningTitle, count } };
        Grid.SetColumn(count, 1);
        var messages = new StackPanel { Spacing = 8 };
        var warnings = new WarningExpander { Classes = { "lg-warning-panel" }, Header = warningHeader, Content = messages, IsVisible = false };
        list.Body.Children.Add(warnings);
        var legacyRows = new StackPanel { Spacing = 12, IsVisible = false };
        list.Body.Children.Add(legacyRows);
        AddModClose(list);
        _modListGame = game;
        _modListOrphan = orphan;
        _gameModRows = rows;
        _gameModStatuses = statuses;
        _legacyGameModRows = legacyRows;
        _gameModWarnings = (warnings, count, messages);
        list.Closed += (_, _) =>
        {
            _gameModStatuses = null;
            _modListGame = null;
            _modListOrphan = null;
            _gameModRows = null;
            _gameModEntries.Clear();
            _legacyGameModRows = null;
            _gameModWarnings = null;
            _visibleLegacyGameMods = [];
        };
        RefreshGameModList(game);
        list.InitialFocus = rows.Children.SelectMany(row => row == legacyRows ? legacyRows.Children : [row])
            .OfType<Button>().FirstOrDefault(row => row.IsEnabled) ?? list.InitialFocus;
        var closed = ShowModal(list);
        if (_page == Page.Game) _ = RefreshSelectedModStatus();
        else _ = InspectManagedList();
        await closed;

        async Task InspectManagedList()
        {
            try
            {
                if (ModListRecord(game) is { } record) await RefreshModInspection(record.Id);
                if (_modListGame == game) RefreshGameModList(game);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (ModUiFailure(error))
            {
                if (_modListGame == game) Status(error.Message);
            }
        }

        async Task OpenMod(Func<Task> open)
        {
            if (opening || _modFileOperation) return;
            opening = true;
            try { await open(); }
            finally { opening = false; }
        }
    }

    private async Task ShowGameReShade(Game game, ModalDialog list)
    {
        try
        {
            if (_modTransactions.HasPending(game.Path))
            {
                if (await Confirm("ReShade recovery", "A mod operation for this game was interrupted. Retry recovery now?", "Retry recovery"))
                    await RecoverModChanges();
                return;
            }
            var existing = ModListRecord(game);
            if (existing?.Files.Any(file => file.Owner == ModProviderId.ReShade) == true)
            {
                await ShowManagedMod(existing, ModProviderId.ReShade);
                return;
            }
            var snapshot = new Game { Title = game.Title, Path = game.Path, SteamId = game.SteamId };
            var root = existing?.Target.Root ?? Path.GetDirectoryName(snapshot.Path) ?? "";
            var executable = existing?.Target.ExePath ?? snapshot.Path;
            var api = ModReShadeApi.Dxgi;
            var setup = new ModalDialog("Set up ReShade", 490);
            var folderText = Label(root, muted: true);
            var executableText = Label(executable, muted: true);
            Field(setup.Body, "Game folder", folderText);
            Field(setup.Body, "Rendering executable", executableText);
            if (existing == null)
            {
                setup.Body.Children.Add(Actions(ActionButton("Choose game folder", () => _ = ChooseRoot()),
                    ActionButton("Choose executable", () => _ = ChooseExecutable())));
            }
            var apiNote = Label("", muted: true);
            var chained = existing?.Files.Any(file => file.Owner == ModProviderId.OptiScaler) == true;
            var choices = chained ? [ModReShadeApi.Dxgi, ModReShadeApi.Vulkan] : Enum.GetValues<ModReShadeApi>();
            var picker = BuildChoicePicker("Graphics API", choices, api, ReShadeApiName, choice =>
            {
                api = choice;
                UpdateApiNote();
                return true;
            });
            setup.Body.Children.Add(PreferenceRow("Graphics API", picker));
            setup.Body.Children.Add(apiNote);
            UpdateApiNote();
            AddModUsage(setup.Body, [ModProviderId.ReShade]);
            var offline = ModToggle("I will use ReShade only for offline play", out var offlineRow);
            setup.Body.Children.Add(offlineRow);
            var planner = new ModInstallPlanner(_modsCompatibility, _modPackages, _modTransactions.Vulkan);
            await ShowModSetupPreview(setup, list, game, snapshot, offline, "ReShade setup",
                token => planner.PreviewReShadeAsync(snapshot, root, executable, api, offline.IsChecked == true, existing, token));

            bool CanChoose() => DialogHost.Children.Contains(setup) && StillSelected(game, snapshot) && setup.Body.IsEnabled;

            void UpdateApiNote()
            {
                apiNote.Text = api == ModReShadeApi.Vulkan
                    ? "Vulkan uses a shared layer. A layer registered for your Windows account is available to games started without administrator privileges."
                    : chained ? "OptiScaler will load ReShade for DirectX 10, 11 and 12." : "";
                apiNote.IsVisible = !String.IsNullOrEmpty(apiNote.Text);
            }

            async Task ChooseRoot()
            {
                if (await PickModRoot(root) is { } selected && CanChoose()) root = folderText.Text = selected;
            }

            async Task ChooseExecutable()
            {
                if (await PickFile(this, "Choose the rendering executable", Executables, root) is { } selected && CanChoose())
                    executable = executableText.Text = selected;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ModUiFailure(error)) { if (!_closed && DialogHost.Children.Contains(list)) await Notice("ReShade", error.Message); }
    }

    private async Task ShowGameAddon(Game game, ModalDialog list, ModProviderId provider)
    {
        var name = ModPackageStore.Name(provider);
        try
        {
            if (_modTransactions.HasPending(game.Path))
            {
                if (await Confirm($"{name} recovery", "A mod operation for this game was interrupted. Retry recovery now?", "Retry recovery"))
                    await RecoverModChanges();
                return;
            }
            var existing = ModListRecord(game);
            if (existing?.Files.Any(file => file.Owner == provider) == true)
            {
                await ShowManagedMod(existing, provider);
                return;
            }

            var snapshot = new Game { Title = game.Title, Path = game.Path, SteamId = game.SteamId };
            var setup = new ModalDialog($"Set up {name}", 490);
            AddModUsage(setup.Body, [provider]);
            var root = existing?.Target.Root ?? Path.GetDirectoryName(snapshot.Path) ?? "";
            var executable = existing?.Target.ExePath ?? snapshot.Path;
            ModRenoDxOption? option = null;
            MediaFileField? importedAddon = null;
            if (provider == ModProviderId.RenoDx)
            {
                if (!_renoDxCatalog.Available && !_verification) await RefreshRenoDxCatalog();
                if (!DialogHost.Children.Contains(list) || !StillSelected(game, snapshot)) return;
                var match = _renoDxCatalog.Find(snapshot);
                var options = match?.Options.Where(choice => choice.Supports(existing)).ToArray() ?? [];
                if (match is not { Options.Length: > 0 } || options.Length == 0 &&
                    existing?.HasDependency(ModRenoDxOption.ReShadeRequirement) == true)
                {
                    setup.Body.Children.Add(Label("Download the .addon64 file for this game from its RenoDX mod page, then browse for it here.", muted: true));
                    importedAddon = new MediaFileField("RenoDX add-on", "No file selected", false, null, null,
                        () => PickFile(this, "Choose a RenoDX add-on", RenoDxAddonFile, null));
                    setup.Body.Children.Add(importedAddon);
                }
                if (existing?.HasDependency(ModRenoDxOption.ReShadeRequirement) != true)
                {
                    setup.Body.Children.Add(Label("Install ReShade with full add-on support for this game first. A standard managed install can be upgraded from Manage ReShade.", muted: true));
                    AddModClose(setup);
                    await ShowModal(setup);
                    return;
                }
                if (options.Length > 0)
                {
                    option = options[0];
                    var choiceControl = options.Length == 1 ? (Control)Label(option.Label) :
                        BuildChoicePicker("RenoDX add-on", options, options[0], choice => choice.Label, choice =>
                        {
                            option = choice;
                            return true;
                        });
                    Field(setup.Body, "RenoDX add-on", choiceControl);
                }
            }

            var folderText = Label(root, muted: true);
            var executableText = Label(executable, muted: true);
            Field(setup.Body, "Game folder", folderText);
            Field(setup.Body, "Rendering executable", executableText);
            if (existing == null)
            {
                setup.Body.Children.Add(Actions(ActionButton("Choose game folder", () => _ = ChooseRoot()),
                    ActionButton("Choose executable", () => _ = ChooseExecutable())));
            }
            if (provider == ModProviderId.ReFramework)
                setup.Body.Children.Add(Label("The reviewed unified build supports selected 64-bit RE Engine games.", muted: true));
            var offline = ModToggle($"I will use {name} only for offline play", out var offlineRow);
            setup.Body.Children.Add(offlineRow);
            var planner = new ModInstallPlanner(_modsCompatibility, _modPackages, _modTransactions.Vulkan);
            var addonPackage = provider == ModProviderId.ReFramework
                ? _modPackages.Preferred(provider, _checkedModDownloads.GetValueOrDefault(provider)) : null;
            (ModPackageKey Key, byte[] Bytes)? downloaded = null;
            await ShowModSetupPreview(setup, list, game, snapshot, offline, $"{name} setup",
                token => planner.PreviewAddonAsync(snapshot, root, executable, offline.IsChecked == true, existing,
                    option?.Package ?? addonPackage, option, token, importedAddon?.SelectedPath,
                    downloaded is { } payload && payload.Key == option?.Package.Key ? payload.Bytes : null),
                (id, bar) => option?.Package is { } selected
                    ? DownloadRenoDxAddon(selected, bar, bytes => downloaded = (selected.Key, bytes))
                    : addonPackage != null
                        ? DownloadModPackage(id, (progress, token) => _modPackages.AcquireAsync(addonPackage, token, progress), bar)
                        : DownloadModPackage(id, bar));

            bool CanChoose() => DialogHost.Children.Contains(setup) && StillSelected(game, snapshot) && setup.Body.IsEnabled;

            async Task ChooseRoot()
            {
                if (await PickModRoot(root) is { } selected && CanChoose()) root = folderText.Text = selected;
            }

            async Task ChooseExecutable()
            {
                if (await PickFile(this, "Choose the rendering executable", Executables, root) is { } selected && CanChoose())
                    executable = executableText.Text = selected;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ModUiFailure(error)) { if (!_closed && DialogHost.Children.Contains(list)) await Notice(name, error.Message); }
    }

    private static string ReShadeApiName(ModReShadeApi api) => api switch
    {
        ModReShadeApi.Dxgi => "DirectX 10, 11 or 12",
        ModReShadeApi.Direct3D9 => "DirectX 9",
        ModReShadeApi.OpenGl => "OpenGL",
        ModReShadeApi.Vulkan => "Vulkan",
        _ => throw new ArgumentOutOfRangeException(nameof(api))
    };

    private async Task<ModInstallInspection> RefreshModInspection(Guid installation)
    {
        if (_modInspectionTasks.TryGetValue(installation, out var pending)) return await pending;
        var work = _modTransactions.InspectAsync(installation, _modLifetime.Token);
        _modInspectionTasks[installation] = work;
        try
        {
            var result = await work;
            if (!_closed && _modInspectionTasks.GetValueOrDefault(installation) == work) _modInspections[installation] = result;
            return result;
        }
        finally
        {
            if (_modInspectionTasks.GetValueOrDefault(installation) == work) _modInspectionTasks.Remove(installation);
        }
    }

    private async Task ShowGameManagedMod(Game game, Guid installation, ModProviderId provider)
    {
        if (_modFileOperation) return;
        try
        {
            if (_modTransactions.FindForGame(game.Path).FirstOrDefault(record => record.Id == installation) is { } record)
                await ShowManagedMod(record, provider);
        }
        catch (Exception error) when (ModUiFailure(error)) { await Notice(ModPackageStore.Name(provider), error.Message); }
    }

    private async Task ShowGameOptiScaler(Game game, ModalDialog list)
    {
        try
        {
            if (_modTransactions.HasPending(game.Path))
            {
                if (await Confirm("OptiScaler recovery", "A mod operation for this game was interrupted. Retry recovery now?", "Retry recovery"))
                    await RecoverModChanges();
                return;
            }
            var record = ModListRecord(game);
            if (record?.Files.Any(file => file.Owner == ModProviderId.OptiScaler) == true)
            {
                await ShowManagedMod(record, ModProviderId.OptiScaler);
                return;
            }
            if (record?.Files.Any(file => file.Owner == ModProviderId.ReShade) == true)
            {
                await Notice("OptiScaler", "Uninstall ReShade first, then install OptiScaler and add ReShade alongside it. Your ReShade presets will stay in the game folder.");
                return;
            }

            var launch = game.Path;
            var snapshot = new Game { Title = game.Title, Path = launch, SteamId = game.SteamId };
            var assessment = await Task.Run(() => _modsCompatibility.Assess(snapshot), _modLifetime.Token);
            if (!DialogHost.Children.Contains(list) || !StillSelected(game, snapshot)) return;
            var external = ExternalOptiScaler(Path.GetDirectoryName(assessment.Target?.ExePath ?? launch));
            var dialog = new ModalDialog("OptiScaler", 465);
            var report = _modCatalog.Find(snapshot);
            dialog.Body.Children.Add(Label(external ? "External install detected" : DisplayCompatibility(assessment.Status, report)));
            var evidence = _modCatalog.Current == null ||
                assessment.Status is ModCompatibilityStatus.Blocked or ModCompatibilityStatus.Dependency or ModCompatibilityStatus.Verified
                ? assessment.Entry : null;
            var page = String.IsNullOrEmpty(report?.Page) ? evidence?.Page ?? report?.Page : report.Page;
            var inputs = report?.Inputs ?? evidence?.Inputs;
            var note = report?.Note ?? evidence?.Note;
            if (page != null)
            {
                if (!String.IsNullOrWhiteSpace(inputs)) dialog.Body.Children.Add(Label($"Reported inputs · {inputs}", muted: true));
                if (!String.IsNullOrWhiteSpace(note)) dialog.Body.Children.Add(Label(note, muted: true));
                dialog.Body.Children.Add(Actions(ActionButton("View compatibility notes", () => OpenModNotes(ModCompatibility.WikiPage(page)))));
            }
            else dialog.Body.Children.Add(Actions(ActionButton("View compatibility list", () =>
                OpenModNotes(ModCompatibility.WikiPage("")))));

            var requirements = ModCompatibility.OptiScalerRequirements(assessment, report);
            var canGuide = ModCompatibility.CanGuideOptiScaler(assessment, requirements);
            var missing = assessment.Recipe == null && requirements.Any(requirement => record?.HasDependency(requirement) != true);
            var allowSetup = !external && !String.IsNullOrWhiteSpace(launch) && File.Exists(launch) && canGuide && !missing;
            if (external)
            {
                dialog.Body.Children.Add(Label("Use the tool that installed this copy to manage or remove it.", muted: true));
                dialog.Body.Children.Add(Actions(ActionButton("OptiScaler spoofing guide", () => OpenModNotes(OptiScalerSpoofingGuide))));
            }
            if (canGuide && missing)
                dialog.Body.Children.Add(Label("Install REFramework for this game first, then return to set up OptiScaler.", muted: true));
            else if (!canGuide && assessment.Status != ModCompatibilityStatus.Blocked)
                dialog.Body.Children.Add(Label("This game needs a separate dependency setup. See the compatibility notes.", muted: true));
            if (allowSetup)
                DialogActions(dialog, assessment.Recipe == null ? "Guided setup" : "Set up", () => dialog.Close(true), defaultAction: false);
            else AddModClose(dialog);
            if (await ShowModal(dialog) && allowSetup && StillSelected(game, snapshot))
            {
                var planner = new ModInstallPlanner(_modsCompatibility, _modPackages);
                await ShowModSetup(game, snapshot, assessment, list, planner.PreviewAsync, record, report);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ModUiFailure(error)) { if (!_closed && DialogHost.Children.Contains(list)) await Notice("OptiScaler", error.Message); }
    }

    private async Task ShowModSetup(Game game, Game snapshot, ModAssessment assessment, ModalDialog list,
        Func<ModSetupRequest, CancellationToken, Task<ModPlanPreview>> buildPreview, ModInstallationRecord? existing = null,
        ModCatalogReport? report = null)
    {
        var verified = assessment.Target != null;
        var root = assessment.Target?.Root ?? existing?.Target.Root ?? Path.GetDirectoryName(snapshot.Path)!;
        var executable = assessment.Target?.ExePath ?? existing?.Target.ExePath ?? snapshot.Path;
        var setup = new ModalDialog(verified ? "Review verified setup" : "Guided OptiScaler setup", 490);
        var folderText = Label(root, muted: true);
        var executableText = Label(executable, muted: true);
        var loader = ModInstallPlanner.Loaders[0];
        var loaderPicker = BuildChoicePicker("OptiScaler DLL name", ModInstallPlanner.Loaders, loader, name => name, name =>
        {
            loader = name;
            return true;
        });
        var offline = ModToggle("I will use OptiScaler only for offline play", out var offlineRow);
        ModDxgiSpoofing? spoofing = assessment.Recipe?.Omit.Contains("OptiScaler.ini", StringComparer.OrdinalIgnoreCase) == true
            ? null : ModInstallPlanner.DefaultDxgiSpoofing(assessment.Recipe, assessment.Entry);
        var browseFolder = ActionButton("Choose game folder", () => _ = ChooseRoot());
        var browseExe = ActionButton("Choose executable", () => _ = ChooseExecutable());
        Field(setup.Body, "Game folder", folderText);
        Field(setup.Body, "Rendering executable", executableText);
        if (!verified)
        {
            if (existing == null) setup.Body.Children.Add(Actions(browseFolder, browseExe));
            Field(setup.Body, "DLL name", loaderPicker);
        }
        if (spoofing is { } mode) setup.Body.Children.Add(DxgiSpoofingRow(mode, value => spoofing = value));
        setup.Body.Children.Add(Label("Local checks can miss anti-cheat. Follow the OptiScaler guidance for your game.", muted: true));
        setup.Body.Children.Add(offlineRow);
        var optiScalerPackage = verified ? ModPackageStore.Approved(ModProviderId.OptiScaler)
            : _modPackages.Preferred(ModProviderId.OptiScaler, _checkedModDownloads.GetValueOrDefault(ModProviderId.OptiScaler));
        await ShowModSetupPreview(setup, list, game, snapshot, offline, "OptiScaler setup", token =>
            buildPreview(new(snapshot, root, executable, loader, verified,
                offline.IsChecked == true, spoofing, existing, report, optiScalerPackage), token),
            (provider, bar) => provider == ModProviderId.OptiScaler
                ? DownloadModPackage(provider, (progress, token) => _modPackages.AcquireAsync(optiScalerPackage, token, progress), bar)
                : DownloadModPackage(provider, bar));

        bool CanChoose() => DialogHost.Children.Contains(setup) && StillSelected(game, snapshot) && setup.Body.IsEnabled;

        async Task ChooseRoot()
        {
            if (await PickModRoot(root) is { } selected && CanChoose()) root = folderText.Text = selected;
        }

        async Task ChooseExecutable()
        {
            if (await PickFile(this, "Choose the rendering executable", Executables, root) is { } selected && CanChoose())
                executable = executableText.Text = selected;
        }
    }

    private async Task ShowModSetupPreview(ModalDialog setup, ModalDialog list, Game game, Game snapshot, ToggleSwitch offline,
        string title, Func<CancellationToken, Task<ModPlanPreview>> buildPreview, Func<ModProviderId, ProgressBar, Task<bool>>? download = null)
    {
        var preparing = false;
        using var previewLifetime = CancellationTokenSource.CreateLinkedTokenSource(_modLifetime.Token);
        setup.Closed += (_, _) => previewLifetime.Cancel();
        Task? preparation = null;
        var cancel = ActionButton("Cancel", () => setup.Close(), "quiet");
        var next = new Button { Content = "Preview install", Classes = { "lg", "primary" }, IsEnabled = false };
        Click(next, () =>
        {
            if (!preparing && offline.IsChecked == true && SetupOpen()) preparation = PreparePreview();
        });
        setup.Footer.Children.Add(cancel);
        setup.Footer.Children.Add(next);
        setup.InitialFocus = cancel;
        offline.IsCheckedChanged += (_, _) => next.IsEnabled = !preparing && offline.IsChecked == true;
        await ShowModal(setup);
        if (preparation != null) await preparation;

        bool Current() => DialogHost.Children.Contains(list) && StillSelected(game, snapshot);

        bool SetupOpen() => Current() && DialogHost.Children.Contains(setup);

        async Task PreparePreview()
        {
            preparing = true;
            setup.Body.IsEnabled = false;
            next.Content = "Preparing preview…";
            next.Classes.Remove("primary");
            next.IsEnabled = false;
            setup.InitialFocus?.Focus();
            try
            {
                var preview = await buildPreview(previewLifetime.Token);
                if (!SetupOpen()) return;
                if (preview.Missing.Length > 0)
                {
                    if (!await ShowModDownloads(preview.Missing, SetupOpen, download ?? DownloadModPackage) || !SetupOpen()) return;
                    preview = await buildPreview(previewLifetime.Token);
                }
                if (!SetupOpen()) return;
                if (preview.Plan is not { } plan)
                {
                    await Notice(title, preview.Message);
                    return;
                }
                // Push the preview first so the Mods list never becomes the active dialog during the handoff.
                var install = ShowModPlan(plan, Current, (progress, token) => _modTransactions.InstallAsync(plan, token, progress));
                if (!install.IsCompleted) setup.Close(true);
                await install;
            }
            catch (OperationCanceledException) when (previewLifetime.IsCancellationRequested) { }
            catch (Exception error) when (ModUiFailure(error))
            {
                if (SetupOpen()) await Notice(title, error.Message);
            }
            finally
            {
                preparing = false;
                if (DialogHost.Children.Contains(setup))
                {
                    setup.Body.IsEnabled = true;
                    next.Content = "Preview install";
                    next.Classes.Add("primary");
                    next.IsEnabled = offline.IsChecked == true;
                }
            }
        }
    }

    private Task<bool> ShowModDownloads(ModProviderId[] providers, Func<bool> current, Func<ModProviderId, ProgressBar, Task<bool>> download)
    {
        var dialog = new ModalDialog("Download mod packages", 460);
        var names = String.Join(", ", providers.Select(ModPackageStore.Name));
        dialog.Body.Children.Add(Label($"Download and verify {names} before previewing the install?", muted: true));
        var started = false;
        DialogActions(dialog, "Download", () => _ = Download(), defaultAction: false);
        return ShowModal(dialog);

        async Task Download()
        {
            if (started) return;
            if (!current()) { dialog.Close(); return; }
            started = true;
            var status = Label("");
            var bar = ShowModProgress(dialog, "Mod package download progress", status);
            for (var index = 0; index < providers.Length; index++)
            {
                if (!DialogHost.Children.Contains(dialog)) return;
                if (!current()) { dialog.Close(); return; }
                var provider = providers[index];
                var name = ModPackageStore.Name(provider);
                status.Text = $"Downloading and verifying {name} ({index + 1} of {providers.Length})…";
                AutomationProperties.SetName(bar, $"{name} download progress");
                bar.Value = 0;
                if (await download(provider, bar)) continue;
                if (!DialogHost.Children.Contains(dialog)) return;
                status.Text = _modPackageMessages.GetValueOrDefault(provider) ?? "A download is already in progress.";
                bar.IsVisible = false;
                AddModClose(dialog);
                dialog.InitialFocus?.Focus();
                return;
            }
            dialog.Close(true);
        }
    }

    private async Task ShowModPlan(ModInstallPlan plan, Func<bool> current,
        Func<IProgress<int>, CancellationToken, Task<ModOperationResult>> install)
    {
        var title = ModPackageStore.Name(plan.Packages.FirstOrDefault()?.Provider ?? ModProviderId.OptiScaler);
        var updating = plan.ReplacingPackage != null;
        var dialog = new ModalDialog($"{(updating ? "Update" : "Install")} {title}", 500);
        Field(dialog.Body, "Rendering executable", Label(plan.Target.ExePath));
        Field(dialog.Body, "Packages", Label(String.Join('\n', plan.Packages.Select(package => package.Provider == ModProviderId.ReShade
            ? $"ReShade {package.Version} · {package.Variant}"
            : $"{ModPackageStore.Name(package.Provider)} {package.Version}"))));
        if (plan.DxgiSpoofing is { } spoofing) Field(dialog.Body, "DXGI hardware spoofing", Label(spoofing.ToString()));
        if (plan.ReShadeChain != null) dialog.Body.Children.Add(Label("OptiScaler will load ReShade beside its own DLL.", muted: true));
        if (plan.ReShadeVulkan is { } layer)
            Field(dialog.Body, "Vulkan layer", Label(layer.Existing switch
            {
                null => "Register a shared ReShade layer for this Windows user.",
                { CanUpgrade: true } => "Upgrade the shared ReShade layer to full add-on support for all games using it.",
                _ => $"Reuse the enabled ReShade layer.\n{layer.Existing.Manifest}"
            }, muted: true));
        AddModUsage(dialog.Body, plan.Packages.Select(package => package.Provider));
        var changes = new StackPanel { Spacing = 5 };
        changes.Children.Add(Label(updating ? "Your existing settings and uninstall backups are kept."
            : "Existing files changed by this install will be backed up for uninstall.", muted: true));
        foreach (var change in plan.Changes)
        {
            if (change.ExpectedHash == null)
                changes.Children.Add(ModFileRow("Add", change.Relative, "LG.Icon.Plus", "LG.Success"));
            else if (String.Equals(change.Relative, plan.ReShadeChain?.Configuration, StringComparison.OrdinalIgnoreCase))
                changes.Children.Add(ModFileRow("Modified", change.Relative, "LG.Icon.Edit", "LG.Modified"));
            else
                changes.Children.Add(ModFileRow("Replace", change.Relative, "LG.Icon.Replace", "LG.Warning"));
        }
        Field(dialog.Body, "Game files", changes);
        var installing = false;
        dialog.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (installing && args.Key == Key.Escape) args.Handled = true;
        }, RoutingStrategies.Tunnel);
        DialogActions(dialog, updating ? "Update" : "Install", () => _ = Install(), defaultAction: false);
        await ShowModal(dialog);

        async Task Install()
        {
            if (installing) return;
            if (!current()) { dialog.Close(); return; }
            installing = true;
            var status = Label(updating ? "Updating mod files…" : "Backing up originals and installing mod files…");
            var bar = ShowModProgress(dialog, "Mod installation progress", status);
            ModOperationResult? completed = null;
            var progress = new Progress<int>(percent => bar.Value = percent);
            await RunModFileOperation(async token =>
            {
                completed = await install(progress, token);
                return completed;
            });
            installing = false;
            if (_closed || !DialogHost.Children.Contains(dialog)) return;
            if (completed == null)
            {
                dialog.Close();
                return;
            }
            status.Text = completed.Message;
            AddModClose(dialog);
            dialog.InitialFocus?.Focus();
        }
    }

    private async Task UpdateInstalledMod(ModInstallationRecord record, ModPackageKey installed, string? replacementPath = null)
    {
        bool Current() => !_closed && _modListGame != null && ModListRecord(_modListGame)?.Id == record.Id;
        if (!Current()) return;
        var planner = new ModInstallPlanner(_modsCompatibility, _modPackages, _modTransactions.Vulkan);
        ModInstallPlan plan;
        if (replacementPath != null)
            plan = await planner.PreviewRenoDxReplacementAsync(record, installed, replacementPath, _modLifetime.Token);
        else
        {
            if (UpdateFor(record, installed) is not { } update) return;
            var direct = installed.Provider == ModProviderId.RenoDx;
            byte[]? downloaded = null;
            if ((direct || !_modPackages.List(installed.Provider).Any(package => package.Key == update.Key)) &&
                !await ShowModDownloads([installed.Provider], Current,
                    (provider, bar) => direct ? DownloadRenoDxAddon(update, bar, bytes => downloaded = bytes)
                        : DownloadModPackage(provider, (progress, token) => _modPackages.AcquireAsync(update, token, progress), bar)))
                return;
            if (!Current()) return;
            plan = await planner.PreviewUpdateAsync(record, installed, update, _modLifetime.Token, downloaded);
        }
        if (Current()) await ShowModPlan(plan, Current, (progress, token) => _modTransactions.InstallAsync(plan, token, progress));
    }

    private static ProgressBar ShowModProgress(ModalDialog dialog, string name, TextBlock status)
    {
        var bar = ModProgressBar(name);
        dialog.Body.Children.Clear();
        dialog.Footer.Children.Clear();
        dialog.Body.Children.Add(status);
        dialog.Body.Children.Add(bar);
        dialog.Focus();
        return bar;
    }

    private Grid ModFileRow(string action, string path, string icon, string brush)
    {
        var colour = this.FindResource(brush) as IBrush;
        var symbol = Symbol(icon, 15);
        symbol.Foreground = colour;
        symbol.VerticalAlignment = VerticalAlignment.Top;
        var label = Label($"{action} {path}");
        label.Foreground = colour;
        var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("17,*"), ColumnSpacing = 8, Children = { symbol, label } };
        Grid.SetColumn(label, 1);
        return row;
    }

    private void AddModUsage(StackPanel body, IEnumerable<ModProviderId> providers)
    {
        var mods = providers.Distinct().ToArray();
        var guides = Actions();
        foreach (var provider in mods)
        {
            var name = ModPackageStore.Name(provider);
            var guide = provider switch
            {
                ModProviderId.OptiScaler => new Uri("https://github.com/optiscaler/OptiScaler#how-it-works"),
                ModProviderId.ReFramework => new Uri("https://refdocs.praydog.com/"),
                ModProviderId.SpecialK => new Uri("https://wiki.special-k.info/SpecialK/Local"),
                ModProviderId.ReShade => new Uri("https://reshade.me/"),
                ModProviderId.RenoDx => new Uri("https://renodx.com/mods.html"),
                _ => throw new ArgumentOutOfRangeException(nameof(providers))
            };
            guides.Children.Add(ActionButton($"{name} usage guide", () => OpenModNotes(guide)));
        }
        body.Children.Add(guides);
    }

    private async Task ShowManagedMod(ModInstallationRecord record, ModProviderId provider)
    {
        var package = record.Packages.FirstOrDefault(package => package.Provider == provider);
        if (_closed || package == null || !record.Files.Any(file => file.Owner == provider)) return;
        var name = ModPackageStore.Name(provider);
        try
        {
            var hasInspection = _modInspections.TryGetValue(record.Id, out var cached) && cached.Installation != null;
            var inspection = hasInspection && cached != null ? InspectionFor(cached, provider) : new ModInstallInspection(record, [], [], false, false);
            var version = Label(package.Version, muted: true);
            version.FontWeight = FontWeight.SemiBold;
            if (this.FindResource("LG.Font.13") is double size) version.FontSize = size;
            var dialog = new ModalDialog($"Manage {name}", 480, footerLeading: version);
            Field(dialog.Body, "Game folder", Label(record.Target.Root, muted: true));
            if (provider == ModProviderId.ReShade && record.ReShadeChain != null)
                dialog.Body.Children.Add(Label("This installation uses OptiScaler to load ReShade.", muted: true));
            if (provider == ModProviderId.ReShade && record.ReShadeVulkan)
                dialog.Body.Children.Add(Label("This game uses the shared ReShade Vulkan layer.", muted: true));
            AddModUsage(dialog.Body, [provider]);
            var notes = new StackPanel { Spacing = 8 };
            var spoofing = new StackPanel { Spacing = 7, IsVisible = false };
            ModSpoofingSetting? currentSpoofing = null;
            ModDxgiSpoofing draftSpoofing = default;
            var configRead = 0;
            var archive = ModToggle("Save a copy of edited mod settings", out var archiveRow);
            var action = "";
            var actions = Actions();
            dialog.Body.Children.Add(notes);
            dialog.Body.Children.Add(spoofing);
            dialog.Body.Children.Add(archiveRow);
            dialog.Body.Children.Add(actions);
            var uninstall = ModDeleteButton($"Uninstall {name}", () => SelectAction("uninstall"));
            var affected = ModSymbolButton("View affected files", "LG.Icon.Info", () => _ = ShowAffectedModFiles(inspection));
            if (affected.Content is SymbolIcon info) info.Foreground = this.FindResource("LG.Text") as IBrush;
            var relink = ActionButton("Relink game folder", () => SelectAction("relink"));
            var recover = ActionButton("Retry recovery", () => SelectAction("recover"));
            var upgrade = ActionButton("Enable full add-on support", () => SelectAction("upgrade"));
            var update = ActionButton("Update mod", () => SelectAction("update"));
            string? replacementPath = null;
            var replace = new Button { Content = "Update from file", Classes = { "lg" } };
            var check = new Button { Content = "Check for updates", Classes = { "lg", "quiet" } };
            var viewRelease = IconButton("View release", "LG.Icon.ExternalLink", () =>
            {
                if (NewerModRelease(package) is { } release) OpenModNotes(release.Page);
            }, "quiet");
            var updateInfo = Label("", muted: true);
            string? updateStatus = null;
            dialog.Body.Children.Add(updateInfo);
            Click(replace, () => _ = PickReplacement());
            Click(check, () => _ = CheckInstalledUpdate());
            var verify = ModSymbolButton($"Verify {name} installation", "LG.Icon.Refresh", () => _ = Verify());
            foreach (var button in new[] { uninstall, verify, affected, relink, recover, upgrade, update, replace, check, viewRelease })
                actions.Children.Add(button);
            RenderInspection();
            AddModClose(dialog);
            var completion = ShowModal(dialog);
            _ = LoadSpoofing();
            if (!hasInspection) _ = LoadInspection();
            if (!await completion || _closed) return;
            switch (action)
            {
                case "uninstall":
                    var archiveSettings = archive.IsChecked == true;
                    var beforeRemoval = await _modTransactions.InspectAsync(record.Id, _modLifetime.Token);
                    if (!_closed && beforeRemoval.Installation is { } installed && !beforeRemoval.RecoveryNeeded &&
                        installed.Files.Any(file => file.Owner == provider) &&
                        await ConfirmModUninstall(installed, InspectionFor(beforeRemoval, provider), provider, archiveSettings))
                        await RunModFileOperation(token => _modTransactions.UninstallAsync(installed.Id, archiveSettings, token, provider));
                    break;
                case "recover": await RecoverModChanges(); break;
                case "upgrade": await UpgradeReShade(record); break;
                case "update": await UpdateInstalledMod(record, package); break;
                case "replace": await UpdateInstalledMod(record, package, replacementPath); break;
                case "relink":
                    if (record.Files.Any(file => file.Owner != provider) && !await Confirm("Relink managed mods?",
                        "This folder is shared with other managed mods for this game. Relinking updates their folder too.", "Relink")) break;
                    if (await PickModRoot(null) is { } root)
                        await RunModFileOperation(token => Task.Run(() => _modTransactions.RelinkAsync(record.Id,
                            GameInstallTarget.Capture(root, ModPaths.Within(root, record.Target.Executable)), token), token));
                    break;
            }

            void SelectAction(string requested)
            {
                action = requested;
                dialog.Close(true);
            }

            bool Open() => !_closed && DialogHost.Children.Contains(dialog);

            void RenderInspection()
            {
                notes.Children.Clear();
                if (!hasInspection) notes.Children.Add(Label("Checking installed files…", muted: true));
                if (inspection.Installation == null) notes.Children.Add(Label("Installation record missing.", muted: true));
                AddModInspectionNotes(notes, inspection);
                var limitedLocalReShade = provider == ModProviderId.ReShade && !record.ReShadeVulkan &&
                    record.Files.Any(file => file.Owner == provider && file.DeployedHash == ModPackageStore.StandardReShadeModuleDigest);
                if (limitedLocalReShade) notes.Children.Add(Label("This ReShade install has limited add-on support.", muted: true));
                notes.IsVisible = notes.Children.Count > 0;
                var count = inspection.Changed.Length + inspection.Missing.Length;
                ToolTip.SetTip(affected, count == 0 ? "View affected files" : $"View affected files ({count})");
                affected.IsEnabled = hasInspection;
                var installed = inspection.Installation?.Files.Any(file => file.Owner == provider) == true;
                var exists = installed && Directory.Exists(record.Target.Root);
                uninstall.IsVisible = exists && !inspection.RecoveryNeeded;
                archiveRow.IsVisible = uninstall.IsVisible && inspection.Changed.Any(file => file.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
                relink.IsVisible = installed && !exists;
                recover.IsVisible = inspection.RecoveryNeeded;
                upgrade.IsVisible = provider == ModProviderId.ReShade && hasInspection && exists &&
                    !inspection.RecoveryNeeded && !inspection.BuildChanged && inspection.Missing.Length == 0 && inspection.Changed.Length == 0 &&
                    (limitedLocalReShade || inspection.VulkanCanUpgrade);
                var target = UpdateFor(record, package);
                var canUpdate = hasInspection && exists && !inspection.BuildChanged && !inspection.RecoveryNeeded &&
                    inspection.Missing.Length == 0 && inspection.Changed.All(file => file.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
                update.IsVisible = canUpdate && target != null;
                update.Content = target == null ? "Update mod" : $"Update to {target.Key.Version}";
                replace.IsVisible = canUpdate && provider == ModProviderId.RenoDx && target == null &&
                    record.HasDependency(ModRenoDxOption.ReShadeRequirement, _modInspections.GetValueOrDefault(record.Id));
                var upstream = NewerModRelease(package);
                viewRelease.IsVisible = upstream != null && target == null;
                check.IsVisible = provider != ModProviderId.SpecialK;
                updateInfo.Text = viewRelease.IsVisible && upstream != null
                    ? $"{upstream.Version} is available upstream. This managed setup still uses its reviewed package."
                    : updateStatus;
                updateInfo.IsVisible = !String.IsNullOrWhiteSpace(updateInfo.Text);
                if (inspection.BuildChanged || inspection.RecoveryNeeded || !exists) spoofing.IsVisible = false;
            }

            async Task CheckInstalledUpdate()
            {
                check.IsEnabled = false;
                updateInfo.Text = "Checking for updates…";
                updateInfo.IsVisible = true;
                await CheckModUpdates(provider);
                if (!Open()) return;
                updateStatus = _modPackageMessages.GetValueOrDefault(provider);
                check.IsEnabled = true;
                RenderInspection();
            }

            async Task PickReplacement()
            {
                replace.IsEnabled = false;
                try
                {
                    var path = await PickFile(this, "Choose the updated RenoDX add-on", RenoDxAddonFile, null);
                    if (path == null || !Open()) return;
                    replacementPath = path;
                    SelectAction("replace");
                }
                finally { replace.IsEnabled = true; }
            }

            void ApplyInspection(ModInstallInspection result)
            {
                hasInspection = true;
                inspection = InspectionFor(result, provider);
                if (result.Installation is { } installation) record = installation;
                RenderInspection();
                RefreshGameMods();
            }

            async Task LoadInspection()
            {
                try
                {
                    var result = await RefreshModInspection(record.Id);
                    if (Open()) ApplyInspection(result);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (ModUiFailure(error))
                {
                    if (!Open() || hasInspection) return;
                    notes.Children.Clear();
                    notes.Children.Add(Label("Could not check the installation. " + error.Message, muted: true));
                    notes.IsVisible = true;
                }
            }

            async Task Verify()
            {
                if (!Open() || !dialog.IsEffectivelyEnabled) return;
                if (await ShowModVerification(record.Id, provider) is not { } verified || !Open()) return;
                _modInspectionTasks.Remove(record.Id);
                _modInspections[record.Id] = verified;
                ApplyInspection(verified);
                await LoadSpoofing();
            }

            async Task LoadSpoofing()
            {
                var request = ++configRead;
                spoofing.IsVisible = false;
                spoofing.Children.Clear();
                if (provider != ModProviderId.OptiScaler || !Open() || inspection.RecoveryNeeded || inspection.BuildChanged ||
                    inspection.Installation?.Files.Any(file => file.Owner == ModProviderId.OptiScaler &&
                        Path.GetFileName(file.Relative).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)) != true) return;
                spoofing.IsVisible = true;
                spoofing.Children.Add(Label("Reading DXGI hardware spoofing…", muted: true));
                try
                {
                    var current = await _modTransactions.ReadDxgiSpoofingAsync(record.Id, _modLifetime.Token);
                    if (!Open() || request != configRead || !spoofing.IsVisible) return;
                    if (currentSpoofing?.Hash != current.Hash) draftSpoofing = current.Mode;
                    currentSpoofing = current;
                    var save = ModSymbolButton("Save DXGI setting", "LG.Icon.Check", () => _ = SaveSpoofing());
                    save.IsEnabled = draftSpoofing != current.Mode;
                    var choice = DxgiSpoofingRow(draftSpoofing, value =>
                    {
                        draftSpoofing = value;
                        save.IsEnabled = value != current.Mode;
                    }, save);
                    spoofing.Children.Clear();
                    spoofing.Children.Add(choice);
                    spoofing.Children.Add(Label("Changes apply the next time the game starts.", muted: true));
                    spoofing.Children.Add(Actions(ActionButton("OptiScaler spoofing guide", () => OpenModNotes(OptiScalerSpoofingGuide))));
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (ModUiFailure(error))
                {
                    if (!Open() || request != configRead) return;
                    currentSpoofing = null;
                    spoofing.Children.Clear();
                    Field(spoofing, "DXGI hardware spoofing", Label(error.Message, muted: true));
                }
            }

            async Task SaveSpoofing()
            {
                if (!Open() || currentSpoofing is not { } current || draftSpoofing == current.Mode) return;
                var saved = false;
                var requested = draftSpoofing;
                dialog.Body.IsEnabled = false;
                try
                {
                    await RunModFileOperation(async token =>
                    {
                        var result = await _modTransactions.SetDxgiSpoofingAsync(record.Id, current.Hash, requested, token);
                        saved = true;
                        return result;
                    });
                    if (!saved || !Open()) return;
                    currentSpoofing = null;
                    var refreshed = await RefreshModInspection(record.Id);
                    if (!Open()) return;
                    ApplyInspection(refreshed);
                    await LoadSpoofing();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (ModUiFailure(error)) { if (Open()) await Notice($"Manage {name}", error.Message); }
                finally { dialog.Body.IsEnabled = true; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ModUiFailure(error))
        {
            if (!_closed) await Notice($"Manage {name}", error.Message);
        }
    }

    private async Task<ModInstallInspection?> ShowModVerification(Guid installation, ModProviderId provider)
    {
        var dialog = new ModalDialog($"Verify {ModPackageStore.Name(provider)} installation", 480);
        var status = Label("Checking installed files and the recorded game build…");
        var bar = ModProgressBar("Installation verification progress");
        dialog.Body.Children.Add(status);
        dialog.Body.Children.Add(bar);
        AddModClose(dialog);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_modLifetime.Token);
        dialog.Closed += (_, _) => lifetime.Cancel();
        var completion = ShowModal(dialog);
        ModInstallInspection? inspection = null;
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (DialogHost.Children.Contains(dialog)) bar.Value = Math.Max(bar.Value, value);
            });
            inspection = await _modTransactions.InspectAsync(installation, lifetime.Token, progress);
            if (lifetime.IsCancellationRequested) return null;
            var selected = InspectionFor(inspection, provider);
            bar.Value = 100;
            status.Text = selected.Installation == null ? "Installation record missing." :
                NeedsReview(selected) ? "Verification complete. Some items need review." : "Verification complete. All recorded files for this mod match.";
            AddModInspectionNotes(dialog.Body, selected);
            if (selected.Changed.Length + selected.Missing.Length > 0)
                dialog.Body.Children.Add(Label($"{selected.Changed.Length} changed · {selected.Missing.Length} missing. Open Affected files for details.", muted: true));
            dialog.Body.Children.Add(Label("Verification did not change any files.", muted: true));
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (ModUiFailure(error))
        {
            if (lifetime.IsCancellationRequested) return null;
            status.Text = "Verification could not finish.";
            dialog.Body.Children.Add(Label(error.Message, muted: true));
        }
        await completion;
        return inspection;
    }

    private Task<bool> ShowAffectedModFiles(ModInstallInspection inspection)
    {
        var dialog = new ModalDialog("Affected files", 480);
        if (inspection.Changed.Length + inspection.Missing.Length == 0)
            dialog.Body.Children.Add(Label(inspection.Installation == null ? "The installation record could not be found." : "No changed or missing mod files."));
        if (inspection.Changed.Length > 0) AddFiles("Changed files", inspection.Changed, "Changed", "LG.Icon.Replace", "LG.Warning");
        if (inspection.Missing.Length > 0) AddFiles("Missing files", inspection.Missing, "Missing", "LG.Icon.Close", "LG.Error");
        AddModClose(dialog);
        return ShowModal(dialog);

        void AddFiles(string title, IEnumerable<string> files, string action, string icon, string brush)
        {
            var rows = new StackPanel { Spacing = 5 };
            foreach (var file in files) rows.Children.Add(ModFileRow(action, file, icon, brush));
            Field(dialog.Body, title, rows);
        }
    }

    private static void AddModInspectionNotes(StackPanel body, ModInstallInspection inspection)
    {
        if (inspection.RecoveryNeeded) body.Children.Add(Label("An interrupted mod operation needs recovery.", muted: true));
        if (inspection.BuildChanged) body.Children.Add(Label("The recorded game build could not be verified. Original game files may require review or repair through the game store.", muted: true));
        if (inspection.VulkanLayerIssue is { } issue) body.Children.Add(Label(issue, muted: true));
    }

    private async Task UpgradeReShade(ModInstallationRecord record)
    {
        var detail = record.ReShadeVulkan
            ? "This upgrades the shared ReShade Vulkan layer for all games using it. Game settings and presets are kept."
            : "This replaces the managed ReShade DLL. Your ReShade settings and presets are kept.";
        if (!await Confirm("Enable full add-on support?", detail, "Enable")) return;
        var approved = ModPackageStore.Approved(ModProviderId.ReShade).Key;
        if (!_modPackages.List(ModProviderId.ReShade).Any(package => package.Key == approved) &&
            !await ShowModDownloads([ModProviderId.ReShade], () => !_closed, DownloadModPackage)) return;
        var planner = new ModInstallPlanner(_modsCompatibility, _modPackages, _modTransactions.Vulkan);
        await RunModFileOperation(async token =>
        {
            var module = await planner.ReadReShadeModuleAsync(token);
            return await _modTransactions.UpgradeReShadeAsync(record.Id, module, token);
        });
    }

    private static ModInstallInspection InspectionFor(ModInstallInspection inspection, ModProviderId provider)
    {
        var owned = inspection.Installation?.Files.Where(file => file.Owner == provider)
            .Select(file => file.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return inspection with
        {
            Changed = inspection.Changed.Where(owned.Contains).ToArray(),
            Missing = inspection.Missing.Where(owned.Contains).ToArray(),
            VulkanLayerIssue = provider == ModProviderId.ReShade ? inspection.VulkanLayerIssue : null,
            VulkanCanUpgrade = provider == ModProviderId.ReShade && inspection.VulkanCanUpgrade
        };
    }

    private Grid DxgiSpoofingRow(ModDxgiSpoofing mode, Action<ModDxgiSpoofing> select, Button? save = null)
    {
        var choice = BuildChoicePicker("DXGI hardware spoofing", DxgiSpoofingModes, mode, option => option.ToString(), value =>
        {
            select(value);
            return true;
        });
        Control input = choice;
        if (save != null)
        {
            Grid.SetColumn(save, 1);
            input = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8, Children = { choice, save } };
        }
        return PreferenceRow("DXGI hardware spoofing", input, "Automatic lets OptiScaler choose based on your GPU.");
    }

    private Task<bool> ConfirmModUninstall(ModInstallationRecord record, ModInstallInspection inspection, ModProviderId provider, bool archiveSettings)
    {
        var name = ModPackageStore.Name(provider);
        var dialog = new ModalDialog($"Uninstall {name}?", 500);
        var others = record.Files.Where(file => file.Owner != provider).Select(file => file.Owner).Distinct().ToArray();
        var dependents = record.InstalledDependentsOf(provider);
        if (dependents.Length > 0)
            dialog.Body.Children.Add(Label($"Removing {name} may break these installed mods: {String.Join(", ", dependents.Select(ModPackageStore.Name))}."));
        if (others.Length > 0)
            dialog.Body.Children.Add(Label($"These mods will stay installed: {String.Join(", ", others.Select(ModPackageStore.Name))}.", muted: true));
        dialog.Body.Children.Add(Label("The launcher will restore verified originals from their backups. Changed or ambiguous files may be left for review.", muted: true));
        if (provider == ModProviderId.ReShade && record.ReShadeChain != null)
            dialog.Body.Children.Add(Label("The launcher will restore OptiScaler’s previous ReShade setting if it is still enabled.", muted: true));
        if (provider == ModProviderId.ReShade && record.ReShadeVulkan)
            dialog.Body.Children.Add(Label("This removes the game’s Vulkan activation file and saves any edits to it. The shared layer stays registered for other games.", muted: true));
        var changes = new StackPanel { Spacing = 5 };
        foreach (var file in record.Files.Where(file => file.Owner == provider))
        {
            var missing = inspection.Missing.Contains(file.Relative, StringComparer.OrdinalIgnoreCase);
            if (missing && file.Original == null) continue;
            var saveEditedIni = archiveSettings && file.Relative.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
            var changed = inspection.Changed.Contains(file.Relative, StringComparer.OrdinalIgnoreCase);
            if ((changed && !saveEditedIni) || (inspection.BuildChanged && file.Original != null))
                changes.Children.Add(ModFileRow("Review", file.Relative, "LG.Icon.Info", "LG.Warning"));
            else
                changes.Children.Add(file.Original == null
                    ? ModFileRow("Remove", file.Relative, "LG.Icon.Close", "LG.Error")
                    : ModFileRow("Restore", file.Relative, "LG.Icon.Replace", "LG.Warning"));
        }
        if (changes.Children.Count > 0) Field(dialog.Body, "Game files", changes);
        DialogActions(dialog, "Uninstall", () => dialog.Close(true), defaultAction: false);
        return ShowModal(dialog);
    }

    private Task RecoverModChanges() => RunModFileOperation(async token =>
    {
        var results = await _modTransactions.RecoverAsync(token);
        return new(results.Count == 0 ? "No interrupted mod operations." : String.Join('\n', results.Select(result => result.Message)),
            results.Any(result => result.RecoveryNeeded));
    });

    private async Task RunModFileOperation(Func<CancellationToken, Task<ModOperationResult>> operation)
    {
        if (_closed || _modFileOperation || _updateHandoff) return;
        if (_playing.Count > 0) { await Notice("Close running games", "Close games launched from LG Launcher before changing mod files."); return; }
        _modFileOperation = true;
        RefreshGameMods();
        RefreshModsSettings();
        Status("Updating game mod files…");
        try
        {
            var result = await operation(_modLifetime.Token);
            if (_closed) return;
            Status(result.Message);
            if (result.RecoveryNeeded || result.Message.StartsWith("Review changes", StringComparison.Ordinal))
                await Notice("Mod files need review", result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ModUiFailure(error)) { if (!_closed) await Notice("Mod operation interrupted", error.Message); }
        finally
        {
            _modFileOperation = false;
            _modInspectionTasks.Clear();
            _modInspections.Clear();
            RefreshManagedGameStatus();
            if (_page is Page.Home or Page.Library or Page.Collections) Render();
            RefreshGameMods();
            _ = RefreshSelectedModStatus();
            RefreshModsSettings();
            TryAutomaticUpdate();
        }
    }

    private void RecoverPendingModsOnStartup()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { if (_modTransactions.HasAnyPending()) _ = RecoverModChanges(); }
        catch (Exception error) when (ModUiFailure(error)) { Status("Mod recovery needs review. " + error.Message); }
    }

    private void RefreshGameMods()
    {
        if (!_closed && _page == Page.Game && _game is { } game) RenderDetails(game);
        else if (!_closed && _modListGame is { } managed) RefreshGameModList(managed);
    }

    private bool StillSelected(Game game, string launch) => !_closed && _page == Page.Game && _game == game && game.Path == launch;

    private bool StillSelected(Game game, Game snapshot) => !_closed && _modListGame == game && game.Path == snapshot.Path &&
        game.SteamId == snapshot.SteamId && game.Title == snapshot.Title;

    private async Task<string?> PickModRoot(string? directory)
    {
        var start = Directory.Exists(directory) ? await StorageProvider.TryGetFolderFromPathAsync(directory!) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose the game folder", SuggestedStartLocation = start });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private static bool ExternalOptiScaler(string? directory) => directory != null &&
        (File.Exists(Path.Combine(directory, "OptiScaler.ini")) || File.Exists(Path.Combine(directory, "OptiScaler.dll")) ||
            File.Exists(Path.Combine(directory, "OptiScaler.asi")));

    private static bool NeedsReview(ModInstallInspection inspection) => inspection.BuildChanged || inspection.RecoveryNeeded || inspection.VulkanLayerIssue != null ||
        inspection.Changed.Length != 0 || inspection.Missing.Length != 0;

    private string DisplayCompatibility(ModCompatibilityStatus status, ModCatalogReport? report) => status switch
    {
        ModCompatibilityStatus.Verified => "Verified setup available",
        ModCompatibilityStatus.Dependency => "Dependency setup needed",
        ModCompatibilityStatus.Blocked => "Install unavailable",
        _ => report?.Label ?? (_modCatalog.Current == null && status == ModCompatibilityStatus.Reported
            ? "Reported compatible" : "Compatibility unknown")
    };

    private void OpenModNotes(Uri address)
    {
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception error) { Status(error.Message); }
    }

    private static void AddModClose(ModalDialog dialog)
    {
        var close = ActionButton("Close", () => dialog.Close(), "quiet");
        dialog.InitialFocus = close;
        dialog.Footer.Children.Add(close);
    }

    private static ToggleSwitch ModToggle(string title, out Control row)
    {
        var toggle = new ToggleSwitch { OnContent = "", OffContent = "" };
        AutomationProperties.SetName(toggle, title);
        row = PreferenceRow(title, toggle);
        return toggle;
    }

    private static bool ModUiFailure(Exception error) => ModTransactions.ModFailure(error) ||
        error is HttpRequestException or KeyNotFoundException or SharpCompress.Common.SharpCompressException;
}
