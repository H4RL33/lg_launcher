using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace LGLauncher;

internal static class ModVerification
{
    internal static int Run(string directory, string? archives = null)
    {
        var root = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(root, "mod-fixture"))) return 1;
        var report = Path.Combine(root, "mod-results.txt");
        try
        {
            VerifyPackages(root).GetAwaiter().GetResult();
            VerifyTransactions(root).GetAwaiter().GetResult();
            VerifyProviderUninstall(root).GetAwaiter().GetResult();
            Reject(() => Task.FromResult(ModDxgiConfiguration.Read("[Spoofing]\nDxgi=true ; note"u8.ToArray()))).GetAwaiter().GetResult();
            Reject(() => Task.FromResult(ModDxgiConfiguration.Read("[Spoofing]\nDxgi=false # note"u8.ToArray()))).GetAwaiter().GetResult();
            VerifyManagedSpoofing(root).GetAwaiter().GetResult();
            VerifyPlanning(root).GetAwaiter().GetResult();
            VerifyCompatibilityCatalog(root).GetAwaiter().GetResult();
            VerifyRenoDx(root).GetAwaiter().GetResult();
            if (archives != null) VerifyOfficialArchives(root, archives).GetAwaiter().GetResult();
            File.WriteAllText(report, "Mod fixtures passed.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, error.ToString());
            return 1;
        }
    }

    private static async Task VerifyCompatibilityCatalog(string root)
    {
        var profile = Path.Combine(root, "catalog-fixture");
        var payload = CatalogFixture(2);
        var status = HttpStatusCode.OK;
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            Require(request.RequestUri == ModCatalogStore.Source, "The compatibility check used the wrong address.");
            return new(status) { Content = new ByteArrayContent(payload) };
        }));
        var store = new ModCatalogStore(profile);
        var game = new Game { Title = "Fixture™ Game", SteamId = 123 };
        Require(store.Current == null && store.Find(game) == null, "A new profile should use bundled compatibility.");
        Require(await store.RefreshAsync(CancellationToken.None, http) == ModCatalogRefresh.Updated, "A valid compatibility report did not download.");
        Require(store.Current?.Version == 2 && store.LastChecked != null &&
            store.Find(game) is { Status: "Reported", Note: "Catalog fixture note." }, "The updated report could not be read by Steam ID.");
        game.SteamId = 0;
        game.Title = "Different Fixture Title";
        Require(store.Find(game)?.Page == "Fixture-Game", "The report could not be found by normalized title.");

        store = new ModCatalogStore(profile);
        Require(store.Current?.Version == 2 && store.LastChecked != null, "The cached reports did not survive restart.");
        Require(await store.RefreshAsync(CancellationToken.None, http) == ModCatalogRefresh.Current, "An unchanged catalog was not accepted.");
        status = HttpStatusCode.ServiceUnavailable;
        await RejectRefresh("An offline response removed the cached reports.");
        status = HttpStatusCode.OK;
        foreach (var invalid in new[]
        {
            CatalogFixture(1), CatalogFixture(3, schema: 2), CatalogFixture(3, status: "Verified"),
            CatalogFixture(3, page: "https://example.com/"), CatalogFixture(3, page: ".."), new byte[2 * 1024 * 1024 + 1],
            "{"u8.ToArray()
        })
        {
            payload = invalid;
            await RejectRefresh("An invalid or older response replaced the cached reports.");
        }
        payload = CatalogFixture(3, status: "Notes");
        using (File.OpenRead(Path.Combine(profile, "mods", "optiscaler-catalog-v1.json")))
            await RejectRefresh("A cache write failure changed the active catalog.");
        Require(await store.RefreshAsync(CancellationToken.None, http) == ModCatalogRefresh.Updated &&
            store.Find(game)?.Status == "Notes", "The cache did not recover after a failed replacement.");
        File.WriteAllText(Path.Combine(profile, "mods", "optiscaler-catalog-v1.json"), "broken");
        Require(new ModCatalogStore(profile).Current == null, "An unreadable cache prevented bundled fallback.");

        var upstream = Path.Combine(root, "upstream-catalog.json");
        if (File.Exists(upstream))
        {
            payload = File.ReadAllBytes(upstream);
            var published = new ModCatalogStore(profile);
            Require(await published.RefreshAsync(CancellationToken.None, http) == ModCatalogRefresh.Updated &&
                published.Find(new Game { Title = "PRAGMATA" }) != null, "The generated wiki catalog did not load with current game reports.");
        }

        async Task RejectRefresh(string error) => Require(await store.RefreshAsync(CancellationToken.None, http) == ModCatalogRefresh.Unavailable &&
            store.Current?.Version == 2 && new ModCatalogStore(profile).Current?.Version == 2, error);
    }

    internal static byte[] CatalogFixture(long version, string status = "Reported", string page = "Fixture-Game", int schema = 1, int steamId = 123) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = schema,
            version,
            revision = new string('a', 40),
            entries = new[] { new { title = "Different Fixture Title", steamId, status, page, inputs = "DLSS", note = "Catalog fixture note." } }
        });

    private static async Task VerifyOfficialArchives(string root, string archives)
    {
        var reShadeDownloads = 0;
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            var address = request.RequestUri!;
            if (address.Host == "api.github.com")
            {
                if (address.AbsolutePath == "/repos/crosire/reshade/tags")
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""[{"name":"v6.9.0-beta"},{"name":"v6.8.0"},{"name":"v6.7.3"}]""")
                    };
                var provider = address.AbsolutePath.StartsWith("/repos/optiscaler/", StringComparison.Ordinal)
                    ? ModProviderId.OptiScaler : ModProviderId.ReFramework;
                var approved = ModPackageStore.Approved(provider);
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = approved.Key.Version,
                        assets = new[] { new { browser_download_url = approved.Address.AbsoluteUri, digest = $"sha256:{approved.Key.Digest}" } }
                    }))
                };
            }
            var asset = Path.GetFileName(address.AbsolutePath);
            if (address.Host == "reshade.me") reShadeDownloads++;
            return new(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(Path.Combine(archives, asset))) };
        }));
        var store = new ModPackageStore(root, client);
        var oldKey = new ModPackageKey(ModProviderId.ReShade, "standard x64 with Vulkan", "6.8.0",
            "207AEA16205FBF952BC8FE1879966672454CF04002E7AD34237C7990A5B3C0B4");
        var oldArchive = File.ReadAllBytes(Path.Combine(archives, "ReShade_Setup_6.8.0.exe"));
        Require(ModFileSnapshot.For(oldArchive).Hash == oldKey.Digest, "The standard ReShade fixture archive changed.");
        using var oldZip = new ZipArchive(new MemoryStream(oldArchive, 152576, oldArchive.Length - 152576));
        using var standardEntry = oldZip.GetEntry(ModPackageStore.ReShadeModule)!.Open();
        using var standardBytes = new MemoryStream();
        standardEntry.CopyTo(standardBytes);
        var standard = standardBytes.ToArray();
        Require(ModFileSnapshot.For(standard).Hash == ModPackageStore.StandardReShadeModuleDigest, "The standard ReShade fixture DLL changed.");
        var legacyCache = Path.Combine(root, "mods", "packages", oldKey.StoreId);
        Directory.CreateDirectory(Path.Combine(legacyCache, "files"));
        File.WriteAllBytes(Path.Combine(legacyCache, "files", ModPackageStore.ReShadeModule), standard);
        File.WriteAllText(Path.Combine(legacyCache, "package.json"), JsonSerializer.Serialize(new
        {
            Key = oldKey,
            Files = new Dictionary<string, string> { [ModPackageStore.ReShadeModule] = ModPackageStore.StandardReShadeModuleDigest }
        }));
        Require(store.List(ModProviderId.ReShade).Single().Key == oldKey, "The standard ReShade fixture cache was not recognized.");
        foreach (var provider in ModPackageStore.FixedPackageProviders)
        {
            var descriptor = ModPackageStore.Approved(provider);
            Require((await store.CheckAsync(provider, CancellationToken.None)).Status == $"version {descriptor.Key.Version} available",
                $"The {provider} update check treated an uncached package as current.");
            var cache = await store.AcquireAsync(descriptor, CancellationToken.None);
            if (provider == ModProviderId.ReShade)
                Require(cache.Files.Keys.SequenceEqual([ModPackageStore.ReShadeModule, ModPackageStore.ReShadeVulkanManifest]),
                    "The ReShade cache did not contain the extracted x64 DLL and Vulkan manifest.");
            Require(cache.Files.Count == descriptor.Files.Length && cache.Files.Keys.All(file =>
                File.Exists(ModPaths.Within(cache.ContentDirectory, file))), $"Could not extract {provider} from the reviewed archive.");
            Require((await store.CheckAsync(provider, CancellationToken.None)).Status == "up-to-date",
                $"The {provider} update check did not identify the cached package as current.");
        }
        var reShade = store.List(ModProviderId.ReShade).Single(package => package.Key == ModPackageStore.Approved(ModProviderId.ReShade).Key);
        Require(reShadeDownloads == 1 && reShade.Files[ModPackageStore.ReShadeModule] == ModPackageStore.ReShadeModuleDigest,
            "The older standard cache prevented downloading the full add-on package.");
        await VerifyReShadeStandalone(root, store);
        await VerifyReShadeVulkan(root, store, standard);
        await VerifyStandaloneReFramework(root, store);
        var gameRoot = Path.Combine(root, "stack-fixture");
        var executable = Path.Combine(gameRoot, "Project", "Binaries", "Win64", "Project-Win64-Shipping.exe");
        var launcher = Path.Combine(gameRoot, "Project.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        var fixtureExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        File.Copy(fixtureExe, launcher);
        File.Copy(fixtureExe, executable);
        var game = new Game { Title = "Stack fixture", SteamId = 42, Path = launcher };
        var target = GameInstallTarget.Capture(gameRoot, executable);
        var opti = ModPackageStore.Approved(ModProviderId.OptiScaler).Key;
        var re = ModPackageStore.Approved(ModProviderId.ReFramework).Key;
        var specialK = ModPackageStore.Approved(ModProviderId.SpecialK).Key;
        var recipe = new ModRecipe(42, ["Project.exe"], target.Executable, target.Build, opti.Version, opti.Digest, "dxgi.dll",
            [new(re.Provider, re.Version, re.Digest, "dinput8.dll"), new(specialK.Provider, specialK.Version, specialK.Digest, "plugins/dxgi.dll")],
            [], true);
        var catalog = new ModCompatibility([], [recipe]);
        var planner = new ModInstallPlanner(catalog, store);
        var request = new ModSetupRequest(game, gameRoot, executable, "", true, true);
        var preview = await planner.PreviewAsync(request, CancellationToken.None);
        var plan = preview.Plan ?? throw new InvalidOperationException(preview.Message);
        Require(plan.DxgiSpoofing == ModDxgiSpoofing.Disabled &&
            ModDxgiConfiguration.Read(plan.Changes.Single(change => Path.GetFileName(change.Relative) == "OptiScaler.ini").Bytes) == ModDxgiSpoofing.Disabled,
            "The verified recipe did not preserve its DXGI spoofing default.");

        preview = await planner.PreviewAsync(request with { DxgiSpoofing = ModDxgiSpoofing.Enabled }, CancellationToken.None);
        plan = preview.Plan ?? throw new InvalidOperationException(preview.Message);
        Require(plan.DxgiSpoofing == ModDxgiSpoofing.Enabled, "The explicit DXGI choice did not reach the install plan.");
        var service = new ModTransactions(root);
        await service.InstallAsync(plan, CancellationToken.None);
        var folder = Path.GetDirectoryName(executable)!;
        Require(ModDxgiConfiguration.Read(File.ReadAllBytes(Path.Combine(folder, "OptiScaler.ini"))) == ModDxgiSpoofing.Enabled,
            "The approved OptiScaler archive did not install with the selected DXGI mode.");
        Require(File.Exists(Path.Combine(folder, "dxgi.dll")) && File.Exists(Path.Combine(folder, "dinput8.dll")) &&
            File.Exists(Path.Combine(folder, "plugins", "dxgi.dll")), "The verified stack did not deploy at the rendering executable.");
        var record = service.FindForGame(launcher).Single();
        await VerifyReShadeCoexistence(root, game, planner, service, record, oldKey, standard);
        await File.WriteAllTextAsync(Path.Combine(reShade.ContentDirectory, ModPackageStore.ReShadeModule), "Changed cache");
        await Reject(() => planner.PreviewReShadeAsync(game, gameRoot, executable, ModReShadeApi.Dxgi, true,
            service.FindForGame(launcher).Single(), CancellationToken.None));
        foreach (var provider in ModPackageStore.FixedPackageProviders)
            await store.DeleteAsync(ModPackageStore.Approved(provider).Key, CancellationToken.None);
        await service.UninstallAsync(record.Id, archiveEditedConfiguration: false, CancellationToken.None);
        Require(Directory.GetFileSystemEntries(folder).SequenceEqual(new[] { executable }), "Uninstall left owned stack files or directories behind.");
    }

    private static async Task VerifyReShadeStandalone(string root, ModPackageStore store)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "ReShade target with spaces")).FullName;
        var executable = Path.Combine(folder, "Game with spaces.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var game = new Game { Title = "ReShade fixture", Path = executable };
        var planner = new ModInstallPlanner(new ModCompatibility([], []), store);
        var service = new ModTransactions(root);
        var loader = Path.Combine(folder, "dxgi.dll");
        var preset = Path.Combine(folder, "ReShadePreset.ini");
        File.WriteAllText(loader, "Original game library");
        File.WriteAllText(preset, "Personal preset");
        foreach (var (api, name) in new[] { (ModReShadeApi.Dxgi, "dxgi.dll"), (ModReShadeApi.Direct3D9, "d3d9.dll"),
            (ModReShadeApi.OpenGl, "opengl32.dll") })
        {
            var plan = (await Preview(api)).Plan ?? throw new InvalidOperationException("ReShade preview failed.");
            var change = plan.Changes.Single();
            Require(change.Relative == name && (change.ExpectedHash != null) == (api == ModReShadeApi.Dxgi),
                "The ReShade preview used the wrong loader or missed the existing game library.");
            Require((await service.InstallAsync(plan, CancellationToken.None)).Message == "ReShade installed.",
                "The standalone ReShade transaction did not report its provider.");
            var record = service.FindForGame(executable).Single();
            Require(record.Files.Single().DeployedHash == ModPackageStore.ReShadeModuleDigest, "The ReShade payload did not match upstream.");
            await service.UninstallAsync(record.Id, false, CancellationToken.None, ModProviderId.ReShade);
            var restored = api == ModReShadeApi.Dxgi ? File.ReadAllText(loader) == "Original game library" :
                !File.Exists(Path.Combine(folder, name));
            Require(restored && File.ReadAllText(preset) == "Personal preset" &&
                service.FindForGame(executable).Count == 0, "Standalone uninstall changed the user's preset or left a managed loader.");
        }

        Task<ModPlanPreview> Preview(ModReShadeApi api) => planner.PreviewReShadeAsync(game, folder, executable, api, true, null, CancellationToken.None);
    }

    private static async Task VerifyStandaloneReFramework(string root, ModPackageStore store)
    {
        var token = CancellationToken.None;
        var folder = Directory.CreateDirectory(Path.Combine(root, "standalone-reframework")).FullName;
        var executable = Path.Combine(folder, "re9.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var game = new Game { Title = "Resident Evil Requiem", Path = executable, SteamId = 3764200 };
        var loader = Path.Combine(folder, "dinput8.dll");
        var optiLoader = Path.Combine(folder, "dxgi.dll");
        File.WriteAllText(loader, "original dinput8");
        File.WriteAllText(optiLoader, "original dxgi");
        var service = new ModTransactions(root);
        var planner = new ModInstallPlanner(ModCompatibility.Load(), store);
        var request = new ModSetupRequest(game, folder, executable, "dxgi.dll", false, true);
        Require((await planner.PreviewAsync(request, token)).Plan == null, "OptiScaler ignored the game's REFramework requirement.");
        var standalone = await planner.PreviewAddonAsync(game, folder, executable, true, null,
            ModPackageStore.Approved(ModProviderId.ReFramework), null, token);
        await service.InstallAsync(standalone.Plan ?? throw new InvalidOperationException(standalone.Message), token);
        var existing = service.FindForGame(executable).Single();
        var opti = await planner.PreviewAsync(request with { Existing = existing }, token);
        await service.InstallAsync(opti.Plan ?? throw new InvalidOperationException(opti.Message), token);
        var stack = service.FindForGame(executable).Single();
        Require(stack.Id == existing.Id && stack.InstalledDependentsOf(ModProviderId.ReFramework).SequenceEqual([ModProviderId.OptiScaler]),
            "OptiScaler did not attach its dependency to the standalone installation.");
        await service.UninstallAsync(stack.Id, false, token);
        Require(File.ReadAllText(loader) == "original dinput8" && File.ReadAllText(optiLoader) == "original dxgi",
            "Adding a second mod did not preserve both original backups.");
    }

    private static async Task VerifyReShadeCoexistence(string root, Game game, ModInstallPlanner planner, ModTransactions service,
        ModInstallationRecord record, ModPackageKey oldKey, byte[] standard)
    {
        var target = record.Target;
        var folder = Path.GetDirectoryName(target.ExePath)!;
        var ini = Path.Combine(folder, "OptiScaler.ini");
        var proxy = Path.Combine(folder, "dxgi.dll");
        var module = Path.Combine(folder, ModPackageStore.ReShadeModule);
        var original = File.ReadAllBytes(ini);
        var originalProxy = ModFileSnapshot.Read(proxy);
        var preview = await planner.PreviewReShadeAsync(game, target.Root, target.ExePath, ModReShadeApi.Dxgi, true, record, CancellationToken.None);
        var plan = preview.Plan ?? throw new InvalidOperationException(preview.Message);
        Require(plan.Changes.Count == 2 && !File.Exists(module) && plan.Changes.Any(change => change.Owner == ModProviderId.OptiScaler &&
            change.ExpectedHash == ModFileSnapshot.For(original).Hash), "Coexistence did not preview the DLL and managed INI change.");
        var interrupted = new ModTransactions(root, step =>
        {
            if (step == ModTransactionStep.FileApplied) throw new InvalidDataException("Fixture interruption");
        });
        await Reject(() => interrupted.InstallAsync(plan, CancellationToken.None));
        Require(!File.Exists(module) && File.ReadAllBytes(ini).SequenceEqual(original), "Interrupted coexistence did not roll back.");
        File.AppendAllText(ini, "\n; External edit\n");
        await Reject(() => service.InstallAsync(plan, CancellationToken.None));
        Require(!File.Exists(module), "A stale coexistence preview was accepted.");
        File.WriteAllBytes(ini, original);
        await service.InstallAsync(plan with
        {
            Packages = [oldKey],
            Changes = plan.Changes.Select(file => file.Owner == ModProviderId.ReShade ? file with { Bytes = standard } : file).ToArray()
        }, CancellationToken.None);
        var combined = service.FindForGame(game.Path).Single();
        Require(combined.Id == record.Id && combined.InstalledDependentsOf(ModProviderId.OptiScaler).SequenceEqual([ModProviderId.ReShade]) &&
            File.Exists(module) && ModFileSnapshot.Read(proxy) == originalProxy &&
            ModDxgiConfiguration.ReadReShade(File.ReadAllBytes(ini)) == "true",
            "ReShade did not join the managed stack without replacing the OptiScaler loader.");
        var settings = await service.ReadDxgiSpoofingAsync(record.Id, CancellationToken.None);
        await service.SetDxgiSpoofingAsync(record.Id, settings.Hash, ModDxgiSpoofing.Disabled, CancellationToken.None);
        var updatedIni = File.ReadAllBytes(ini);
        var full = await planner.ReadReShadeModuleAsync(CancellationToken.None);
        await Reject(() => interrupted.UpgradeReShadeAsync(record.Id, full, CancellationToken.None));
        Require(File.ReadAllBytes(module).SequenceEqual(standard) && service.FindForGame(game.Path).Single().Packages.Contains(oldKey),
            "Interrupting a ReShade upgrade did not restore the old DLL and package record.");
        File.WriteAllText(module, "External edit");
        await Reject(() => service.UpgradeReShadeAsync(record.Id, full, CancellationToken.None));
        Require(File.ReadAllText(module) == "External edit", "A ReShade upgrade overwrote a DLL changed outside the launcher.");
        File.WriteAllBytes(module, standard);
        await service.UpgradeReShadeAsync(record.Id, full, CancellationToken.None);
        var upgraded = service.FindForGame(game.Path).Single();
        Require(upgraded.Packages.Contains(ModPackageStore.Approved(ModProviderId.ReShade).Key) &&
            ModFileSnapshot.Read(module)?.Hash == ModPackageStore.ReShadeModuleDigest && File.ReadAllBytes(ini).SequenceEqual(updatedIni),
            "A ReShade upgrade lost the managed loader configuration or did not update its recorded package.");
        await service.UninstallAsync(record.Id, false, CancellationToken.None, ModProviderId.ReShade);
        Require(!File.Exists(module) && ModFileSnapshot.Read(proxy) == originalProxy &&
            File.ReadAllBytes(ini).SequenceEqual(ModDxgiConfiguration.WithMode(original, ModDxgiSpoofing.Disabled)) &&
            service.FindForGame(game.Path).Single().Packages.All(package => package.Provider != ModProviderId.ReShade),
            "Removing ReShade did not undo only the loader setting and preserve other OptiScaler edits.");
    }

    private static async Task VerifyReShadeVulkan(string root, ModPackageStore store, byte[] standard)
    {
        var token = CancellationToken.None;
        var profile = Path.Combine(root, "Vulkan profile");
        var enabled = new List<string>();
        var vulkan = new ModReShadeVulkan(profile, () => [.. enabled], enabled.Add);
        var service = new ModTransactions(profile, vulkan: vulkan);
        var compatibility = new ModCompatibility([], []);
        var planner = new ModInstallPlanner(compatibility, store, vulkan);
        var first = GameFixture("Vulkan one");
        var second = GameFixture("Vulkan two");
        var firstIni = Path.Combine(Path.GetDirectoryName(first.Path)!, ModReShadeVulkan.ActivationFile);
        var secondIni = Path.Combine(Path.GetDirectoryName(second.Path)!, ModReShadeVulkan.ActivationFile);
        var fresh = (await Preview(planner, first)).Plan ?? throw new InvalidOperationException("Missing Vulkan preview.");
        Require(fresh.ReShadeVulkan is { Existing: null, Library: not null, Manifest: not null } &&
            fresh.Changes.Single().Relative == ModReShadeVulkan.ActivationFile && enabled.Count == 0 && !File.Exists(firstIni),
            "Vulkan preview did not separate the shared registration from the game activation.");
        File.WriteAllText(firstIni, "Existing configuration");
        await Reject(() => service.InstallAsync(fresh, token));
        Require(enabled.Count == 0 && File.ReadAllText(firstIni) == "Existing configuration" &&
            (await Preview(planner, first)).Plan == null, "Vulkan installation overwrote an existing activation or registered during a stale preview.");
        File.Delete(firstIni);

        var failingLayer = new ModReShadeVulkan(profile, () => [], _ => throw new InvalidDataException("Registration failed"));
        var failing = new ModTransactions(profile, vulkan: failingLayer);
        await Reject(() => failing.InstallAsync(fresh, token));
        Require(!File.Exists(firstIni) && service.FindForGame(first.Path).Count == 0 && !service.HasPending(first.Path),
            "A failed Vulkan registration left a game activation or unfinished operation.");
        await service.InstallAsync(fresh, token);
        var firstRecord = service.FindForGame(first.Path).Single();
        Require(enabled.Count == 1 && File.Exists(firstIni) && firstRecord.ReShadeVulkan &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(first.Path)!, ModPackageStore.ReShadeModule)),
            "Vulkan install did not register exactly one shared layer and one game activation.");

        var optiKey = ModPackageStore.Approved(ModProviderId.OptiScaler).Key;
        var target = GameInstallTarget.Capture(Path.GetDirectoryName(second.Path)!, second.Path);
        await service.InstallAsync(new(target, [new("dxgi.dll", null, "Existing managed mod"u8.ToArray(), ModProviderId.OptiScaler)],
            [optiKey]), token);
        var opti = service.FindForGame(second.Path).Single();
        var emptyStore = new ModPackageStore(profile);
        var reuse = new ModInstallPlanner(compatibility, emptyStore, vulkan);
        var secondPlan = (await Preview(reuse, second, opti)).Plan ?? throw new InvalidOperationException("Missing Vulkan reuse preview.");
        Require(secondPlan.ReShadeVulkan is { Existing: not null, Library: null, Manifest: null } && secondPlan.ReShadeChain == null,
            "An enabled Vulkan layer could not be reused without a package download.");
        await service.InstallAsync(secondPlan, token);
        Require(enabled.Count == 1 && File.Exists(secondIni) && service.FindForGame(second.Path).Single().Id == opti.Id,
            "Installing Vulkan ReShade for a second game duplicated its registration or separated its managed record.");

        File.AppendAllText(firstIni, "Example=User settings\r\n");
        var edited = File.ReadAllBytes(firstIni);
        Require((await service.InspectAsync(firstRecord.Id, token)).Changed.Length == 0, "A normal ReShade INI edit was reported as damaged.");
        var layerFile = Path.Combine(Path.GetDirectoryName(enabled.Single())!, ModPackageStore.ReShadeModule);
        File.WriteAllBytes(layerFile, standard);
        Require((await service.InspectAsync(firstRecord.Id, token)).VulkanCanUpgrade &&
            (await service.InspectAsync(opti.Id, token)).VulkanCanUpgrade, "The older Vulkan layer did not offer an upgrade for both games.");
        var inspectionTarget = GameFixture("Vulkan upgrade preview");
        Require((await Preview(reuse, inspectionTarget)).Missing.SequenceEqual([ModProviderId.ReShade]) &&
            (await Preview(planner, inspectionTarget)).Plan?.ReShadeVulkan?.Existing?.CanUpgrade == true,
            "A new install did not preview upgrading the existing limited Vulkan layer.");
        await service.UpgradeReShadeAsync(firstRecord.Id, await planner.ReadReShadeModuleAsync(token), token);
        Require(ModFileSnapshot.Read(layerFile)?.Hash == ModPackageStore.ReShadeModuleDigest && enabled.Count == 1 &&
            File.ReadAllBytes(firstIni).SequenceEqual(edited) && (await service.InspectAsync(opti.Id, token)).VulkanLayerIssue == null,
            "Upgrading the shared Vulkan layer changed its registration or settings, or did not upgrade the other game.");
        await service.UninstallAsync(firstRecord.Id, false, token, ModProviderId.ReShade);
        var archived = Path.Combine(profile, "mods", "installations", firstRecord.Id.ToString("N"), "saved-configuration");
        Require(!File.Exists(firstIni) && File.Exists(secondIni) && enabled.Count == 1 &&
            Directory.EnumerateFiles(archived, ModReShadeVulkan.ActivationFile, SearchOption.AllDirectories)
                .Any(file => File.ReadAllBytes(file).SequenceEqual(edited)),
            "Per-game removal did not archive edited activation or preserve the shared layer for the other game.");

        var managed = enabled.Single();
        enabled.Clear();
        Require((await service.InspectAsync(opti.Id, token)).VulkanLayerIssue != null,
            "Verification did not report a missing shared Vulkan registration.");
        await service.UninstallAsync(opti.Id, false, token, ModProviderId.ReShade);
        Require(!File.Exists(secondIni) && service.FindForGame(second.Path).Single().Files.Single().Owner == ModProviderId.OptiScaler,
            "Vulkan removal affected the other managed mod.");

        var external = Directory.CreateDirectory(Path.Combine(root, "External ReShade layer")).FullName;
        foreach (var member in new[] { ModPackageStore.ReShadeModule, ModPackageStore.ReShadeVulkanManifest })
            File.Copy(Path.Combine(Path.GetDirectoryName(managed)!, member), Path.Combine(external, member));
        var foreign = Path.Combine(external, ModPackageStore.ReShadeVulkanManifest);
        enabled.Add(foreign);
        var foreignModule = Path.Combine(external, ModPackageStore.ReShadeModule);
        var full = File.ReadAllBytes(foreignModule);
        File.WriteAllBytes(foreignModule, standard);
        await Reject(() => Preview(reuse, first));
        Require(enabled.SequenceEqual([foreign]) && File.ReadAllBytes(foreignModule).SequenceEqual(standard),
            "An externally installed standard layer was changed or used.");
        File.WriteAllBytes(foreignModule, full);
        var existingPlan = (await Preview(reuse, first)).Plan ?? throw new InvalidOperationException("Missing existing layer preview.");
        Require(existingPlan.ReShadeVulkan?.Existing?.Manifest == foreign, "An existing foreign registration could not be reused.");
        await service.InstallAsync(existingPlan, token);
        await service.UninstallAsync(service.FindForGame(first.Path).Single().Id, false, token, ModProviderId.ReShade);
        Require(enabled.SequenceEqual([foreign]) && File.Exists(foreign), "Installing or removing a game changed a foreign layer.");
        enabled.Add(managed);
        await Reject(() => Preview(reuse, first));
        Require(enabled.Count == 2 && !File.Exists(firstIni), "Conflicting layer registrations were silently changed.");

        Game GameFixture(string name)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var executable = Path.Combine(directory, "Fixture.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
            return new Game { Title = name, Path = executable };
        }

        Task<ModPlanPreview> Preview(ModInstallPlanner installPlanner, Game game, ModInstallationRecord? record = null) =>
            installPlanner.PreviewReShadeAsync(game, Path.GetDirectoryName(game.Path)!, game.Path, ModReShadeApi.Vulkan,
                true, record, token);
    }

    private static async Task VerifyPackages(string root)
    {
        var bytes = Zip(("dinput8.dll", "fixture"));
        var payload = bytes;
        using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedPackageStream(payload)) }));
        var store = new ModPackageStore(root, http);
        var package = Descriptor("approved", bytes);
        var progress = new ProgressRecorder();
        var cached = await store.AcquireAsync(package, CancellationToken.None, progress);
        Require(File.ReadAllText(Path.Combine(cached.ContentDirectory, "dinput8.dll")) == "fixture", "The package did not unpack.");
        Require(progress.Values.Count > 1 && progress.Values[0] is > 0 and < 100 && progress.Values[^1] == 100 &&
            progress.Values.SequenceEqual(progress.Values.Distinct().Order()), "The package did not report increasing download progress.");
        progress.Values.Clear();
        await store.AcquireAsync(package, CancellationToken.None, progress);
        Require(progress.Values.Count == 0, "An existing cached package reported a new download.");

        payload = Zip(("../escaped.dll", "outside"), ("dinput8.dll", "fixture"));
        await Reject(() => store.AcquireAsync(Descriptor("escape", payload), CancellationToken.None));
        Require(!Directory.GetFiles(root, "escaped.dll", SearchOption.AllDirectories).Any(), "An archive escaped staging.");

        payload = Zip(("dinput8.dll", "fixture"), ("DINPUT8.DLL", "duplicate"));
        await Reject(() => store.AcquireAsync(Descriptor("collision", payload), CancellationToken.None));
        payload = bytes[..^5];
        await Reject(() => store.AcquireAsync(Descriptor("truncated", payload), CancellationToken.None));
        await Reject(() => store.AcquireAsync(Descriptor("bad-digest", bytes), CancellationToken.None));
        Require(store.List(ModProviderId.ReFramework).Single().Key == package.Key, "An invalid refresh replaced the approved copy.");
    }

    private static ModPackageDescriptor Descriptor(string version, byte[] bytes) => new(
        new(ModProviderId.ReFramework, "non-vr", version, Convert.ToHexString(SHA256.HashData(bytes))),
        new("https://github.com/praydog/REFramework-nightly/releases/download/fixture/REFramework.zip"),
        bytes.LongLength, ["dinput8.dll"]);

    private static async Task VerifyTransactions(string root)
    {
        var gameRoot = Path.Combine(root, "game");
        Directory.CreateDirectory(gameRoot);
        var executable = Path.Combine(gameRoot, "fixture-game.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var original = Path.Combine(gameRoot, "replace.dll");
        var added = Path.Combine(gameRoot, "loader.dll");
        File.WriteAllText(original, "vendor");
        var timestamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(original, timestamp);
        File.WriteAllText(Path.Combine(gameRoot, "foreign.dll"), "user file");
        var deployed = System.Text.Encoding.UTF8.GetBytes("mod");
        var target = GameInstallTarget.Capture(gameRoot, executable);
        var plan = new ModInstallPlan(target,
            [new("replace.dll", ModFileSnapshot.Read(original)?.Hash, deployed, ModProviderId.OptiScaler),
                new("loader.dll", null, deployed, ModProviderId.OptiScaler)],
            [ModPackageStore.Approved(ModProviderId.OptiScaler).Key]);
        var token = CancellationToken.None;
        var interruptedProgress = new ProgressRecorder();
        var interrupted = new ModTransactions(root, point =>
        {
            if (point == ModTransactionStep.FileApplied) throw new SimulatedStop();
        });
        await Stop(() => interrupted.InstallAsync(plan, token, interruptedProgress));
        Require(File.ReadAllText(original) == "mod", "The interruption did not occur after the first write.");
        Require(interruptedProgress.Values.Count > 1 && interruptedProgress.Values.All(value => value < 100),
            "An interrupted install reported completion or skipped progress before the interruption.");
        var service = new ModTransactions(root);
        await service.RecoverAsync(token);
        Require(File.ReadAllText(original) == "vendor" && File.GetLastWriteTimeUtc(original) == timestamp && !File.Exists(added),
            "Recovery did not restore the original bytes and timestamp.");

        var verifiedCompletion = false;
        var installProgress = new ProgressRecorder(value =>
        {
            if (value == 100)
                verifiedCompletion = !service.HasAnyPending() && service.FindInstallations().Count == 1;
        });
        await service.InstallAsync(plan, token, installProgress);
        var values = installProgress.Values;
        Require(values.Count > 2 && values[0] == 0 && values[^1] == 100 && values.SequenceEqual(values.Distinct().Order()),
            "Install did not report increasing progress from start to completion.");
        Require(verifiedCompletion, "Install reported completion before finalizing the transaction.");
        var record = service.FindInstallations().Single();
        await Stop(() => interrupted.UninstallAsync(record.Id, archiveEditedConfiguration: false, token));
        await service.RecoverAsync(token);
        Require(File.ReadAllText(original) == "mod" && File.ReadAllText(added) == "mod" && service.FindInstallations().Count == 1,
            "An interrupted uninstall did not restore the installed state.");

        File.AppendAllText(executable, "updated");
        await service.UninstallAsync(record.Id, archiveEditedConfiguration: false, token);
        Require(File.ReadAllText(original) == "mod" && !File.Exists(added) && service.FindInstallations().Count == 1,
            "A moved game build was restored from a stale backup or lost its recovery record.");
        using (var stream = new FileStream(executable, FileMode.Open, FileAccess.Write)) stream.SetLength(stream.Length - "updated".Length);
        await service.UninstallAsync(record.Id, archiveEditedConfiguration: false, token);
        Require(File.ReadAllText(original) == "vendor" && !File.Exists(added) && service.FindInstallations().Count == 0 &&
            File.ReadAllText(Path.Combine(gameRoot, "foreign.dll")) == "user file", "Uninstall did not restore the vendor file or touched unrelated files.");

        File.WriteAllText(original, "changed after preview");
        await Reject(() => service.InstallAsync(plan, token));
        Require(File.ReadAllText(original) == "changed after preview" && !File.Exists(added), "A stale plan made changes.");
        File.WriteAllText(original, "vendor");
        var raced = false;
        var duringReplacement = new ModTransactions(root, point =>
        {
            if (point != ModTransactionStep.BeforeMutation || raced) return;
            raced = true;
            File.WriteAllText(original, "store repair");
        });
        try { await duringReplacement.InstallAsync(plan, token); }
        catch (IOException) { }
        Require(raced && (File.ReadAllText(original) == "store repair" ||
            Directory.GetFiles(gameRoot, "displaced-*", SearchOption.AllDirectories).Any(path => File.ReadAllText(path) == "store repair")),
            "A replacement racing preflight lost the store repair's bytes.");
    }

    private static async Task Stop(Func<Task> attempt)
    {
        try { await attempt(); }
        catch (SimulatedStop) { return; }
        throw new InvalidOperationException("The fixture did not reach the requested interruption.");
    }

    private static async Task VerifyProviderUninstall(string root)
    {
        var token = CancellationToken.None;
        var game = Directory.CreateDirectory(Path.Combine(root, "provider-removal-game")).FullName;
        var exe = Path.Combine(game, "game.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        var service = new ModTransactions(Path.Combine(root, "provider-removal-profile"));
        var dependant = ModProviderId.ReFramework;
        var required = ModProviderId.SpecialK;
        var third = ModProviderId.OptiScaler;
        var original = Path.Combine(game, "required.dll");
        File.WriteAllText(original, "original");
        var plan = new ModInstallPlan(GameInstallTarget.Capture(game, exe),
            [new("required.dll", ModFileSnapshot.Read(original)?.Hash, "dependency"u8.ToArray(), required),
                new("dependent.dll", null, "dependent"u8.ToArray(), dependant),
                new("third.dll", null, "indirect"u8.ToArray(), third)],
            [ModPackageStore.Approved(required).Key, ModPackageStore.Approved(dependant).Key, ModPackageStore.Approved(third).Key],
            Dependencies: [new(dependant, required), new(third, dependant)]);
        await service.InstallAsync(plan, token);
        var record = service.FindInstallations().Single();
        Require(record.InstalledDependentsOf(required).ToHashSet().SetEquals([dependant, third]),
            "The dependency query did not include both direct and indirect dependents of a non-OptiScaler mod.");
        await service.UninstallAsync(record.Id, false, token, required);
        var remaining = service.FindInstallations().Single();
        Require(remaining.Id == record.Id && remaining.Packages.Select(package => package.Provider).ToHashSet().SetEquals([dependant, third]) &&
            remaining.KnownDependencies().SequenceEqual(plan.Dependencies!) &&
            File.ReadAllText(original) == "original" &&
            File.ReadAllText(Path.Combine(game, "dependent.dll")) == "dependent" &&
            File.ReadAllText(Path.Combine(game, "third.dll")) == "indirect",
            "Removing one mod did not restore its original and retain the other mods and their dependency links.");
        await service.UninstallAsync(record.Id, false, token, dependant);
        Require(!File.Exists(Path.Combine(game, "dependent.dll")) && File.Exists(Path.Combine(game, "third.dll")),
            "Removing a second provider touched another provider's files.");
        await service.UninstallAsync(record.Id, false, token, third);
        Require(service.FindInstallations().Count == 0, "Removing the last provider left a managed installation.");
    }

    private static async Task VerifyManagedSpoofing(string root)
    {
        var gameRoot = Path.Combine(root, "spoofing-game");
        var renderingFolder = Path.Combine(gameRoot, "bin", "x64");
        Directory.CreateDirectory(renderingFolder);
        var executable = Path.Combine(renderingFolder, "render-fixture.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var target = GameInstallTarget.Capture(gameRoot, executable);
        const string relative = "bin/x64/OptiScaler.ini";
        var path = Path.Combine(renderingFolder, "OptiScaler.ini");
        var otherIni = Path.Combine(gameRoot, "OptiScaler.ini");
        File.WriteAllText(otherIni, "unmanaged root configuration");
        File.WriteAllText(path, "original configuration");
        const string automatic = "[Spoofing]\r\nDxgi=auto\r\n\r\n[Other]\r\nDxgi=ignored\r\n";
        const string disabled = "[Spoofing]\r\nDxgi=false\r\n\r\n[Other]\r\nDxgi=ignored\r\n";
        const string edited = "[Spoofing]\r\nDxgi=false\r\n\r\n[Other]\r\nDxgi=ignored\r\nNote=hand edited\r\n";
        const string editedEnabled = "[Spoofing]\r\nDxgi=true\r\n\r\n[Other]\r\nDxgi=ignored\r\nNote=hand edited\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(automatic);
        var plan = new ModInstallPlan(target,
            [new(relative, ModFileSnapshot.Read(path)?.Hash, bytes, ModProviderId.OptiScaler),
                new("bin/x64/dinput8.dll", null, "dependency"u8.ToArray(), ModProviderId.ReFramework)],
            [ModPackageStore.Approved(ModProviderId.OptiScaler).Key, ModPackageStore.Approved(ModProviderId.ReFramework).Key]);
        var token = CancellationToken.None;
        var profile = Path.Combine(root, "spoofing-profile");
        var service = new ModTransactions(profile);
        await service.InstallAsync(plan, token);
        var record = service.FindInstallations().Single();
        var (mode, hash) = await ReadSetting();
        Require(mode == ModDxgiSpoofing.Automatic, "The managed configuration beside the rendering executable was not read.");

        var interrupted = new ModTransactions(profile, step =>
        {
            if (step == ModTransactionStep.FileApplied) throw new SimulatedStop();
        });
        await Stop(() => SetSetting(interrupted, hash, ModDxgiSpoofing.Disabled));
        Require(File.ReadAllText(path) == disabled && service.HasPending(executable),
            "An interrupted settings change did not reach deployment or leave recovery state.");
        await Reject(() => ReadSetting());
        await Reject(() => SetSetting(service, hash, ModDxgiSpoofing.Disabled));
        await service.RecoverAsync(token);
        Require(File.ReadAllText(path) == automatic && !service.HasPending(executable),
            "Settings recovery did not restore the preceding configuration.");

        var created = File.GetCreationTimeUtc(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        await SetSetting(service, hash, ModDxgiSpoofing.Disabled);
        var inspection = await service.InspectAsync(record.Id, token);
        Require(File.ReadAllText(path) == disabled && (await ReadSetting()).Mode == ModDxgiSpoofing.Disabled && inspection.Changed.Length == 0 &&
            File.ReadAllText(otherIni) == "unmanaged root configuration" &&
            File.ReadAllText(Path.Combine(renderingFolder, "dinput8.dll")) == "dependency",
            "Saving spoofing changed something outside the owned setting or failed to update the active file hash.");
        Require(File.GetCreationTimeUtc(path) == created && (File.GetAttributes(path) & FileAttributes.Hidden) != 0,
            "Saving spoofing changed the configuration's creation time or dropped its file attributes.");
        await Reject(() => SetSetting(service, hash, ModDxgiSpoofing.Enabled));

        var (_, updatedHash) = await ReadSetting();
        var antiCheatMarker = Path.Combine(gameRoot, "EasyAntiCheat");
        Directory.CreateDirectory(antiCheatMarker);
        await Reject(() => SetSetting(service, updatedHash, ModDxgiSpoofing.Enabled));
        Directory.Delete(antiCheatMarker);
        File.AppendAllText(executable, "updated");
        await Reject(() => SetSetting(service, updatedHash, ModDxgiSpoofing.Enabled));
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable, overwrite: true);
        File.Move(path, path + ".held");
        await Reject(() => ReadSetting());
        File.Move(path + ".held", path);
        await service.UninstallAsync(record.Id, archiveEditedConfiguration: false, token);
        Require(File.ReadAllText(path) == "original configuration" && service.FindInstallations().Count == 0,
            "Uninstalling after a settings change lost the original configuration backup.");

        File.Delete(path);
        var freshPlan = plan with { Changes = [plan.Changes[0] with { ExpectedHash = null }, plan.Changes[1]] };
        await service.InstallAsync(freshPlan, token);
        record = service.FindInstallations().Single();
        File.WriteAllText(path, edited);
        var (_, editedHash) = await ReadSetting();
        var committed = new ModTransactions(profile, step =>
        {
            if (step == ModTransactionStep.Committed) throw new SimulatedStop();
        });
        await Stop(() => SetSetting(committed, editedHash, ModDxgiSpoofing.Enabled));
        Require(service.HasPending(executable), "The settings fixture did not stop after committing.");
        await service.RecoverAsync(token);
        Require(File.ReadAllText(path) == editedEnabled && (await service.InspectAsync(record.Id, token)).Changed.SequenceEqual([relative]),
            "Recovering a committed settings change overwrote a manual edit or stopped tracking it for review.");
        await service.UninstallAsync(record.Id, archiveEditedConfiguration: false, token);
        Require(File.ReadAllText(path) == editedEnabled && service.FindInstallations().Count == 1,
            "Uninstall removed a manually edited configuration without archiving it.");
        await service.UninstallAsync(record.Id, archiveEditedConfiguration: true, token);
        var saved = Directory.GetFiles(Path.Combine(profile, "mods", "installations", record.Id.ToString("N"), "saved-configuration"),
            "OptiScaler.ini", SearchOption.AllDirectories).Single();
        Require(!File.Exists(path) && File.ReadAllText(saved) == editedEnabled && service.FindInstallations().Count == 0,
            "Uninstall did not archive the complete edited configuration.");

        // The running check must follow operations that require an idle, writable executable.
        await service.InstallAsync(freshPlan, token);
        record = service.FindInstallations().Single();
        var (_, runningHash) = await ReadSetting();
        await VerifySpoofingWhileRunning(executable, () => SetSetting(service, runningHash, ModDxgiSpoofing.Disabled));

        Task<ModSpoofingSetting> ReadSetting() => service.ReadDxgiSpoofingAsync(record.Id, token);

        Task SetSetting(ModTransactions transactions, string expectedHash, ModDxgiSpoofing requested) =>
            transactions.SetDxgiSpoofingAsync(record.Id, expectedHash, requested, token);
    }

    private static async Task VerifySpoofingWhileRunning(string executable, Func<Task> save)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "/d /q /c set /p lg_fixture_wait=")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        }) ?? throw new InvalidOperationException("Could not start the disposable rendering executable.");
        try
        {
            await save();
        }
        catch (IOException error) when (error.Message.Contains("Close the game", StringComparison.Ordinal)) { return; }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
        throw new InvalidOperationException("The launcher changed the spoofing setting while the game was running.");
    }

    private sealed class SimulatedStop : Exception;

    private static async Task VerifyPlanning(string root)
    {
        var gameRoot = Path.Combine(root, "game");
        var executable = Path.Combine(gameRoot, "fixture-game.exe");
        var target = GameInstallTarget.Capture(gameRoot, executable);
        var game = new Game { Title = "Cyberpunk 2077", Path = executable, SteamId = 1091500 };
        var catalog = ModCompatibility.Load();
        Require(catalog.Assess(game).Status == ModCompatibilityStatus.Reported, "An upstream report should be advisory.");
        game.Title = "Resident Evil 4 (2023)";
        game.SteamId = 0;
        Require(catalog.Assess(game).Status == ModCompatibilityStatus.Dependency, "A third-party dependency was missed.");
        Require(ModCompatibility.OptiScalerRequirements(catalog.Advisory(game), null)
            .Any(requirement => requirement.Required == ModProviderId.ReFramework && requirement.Variant == "pd-upscaler"),
            "The compatibility entry lost the required REFramework variant.");
        game.Title = "EA Sports WRC";
        Require(catalog.Assess(game).Status == ModCompatibilityStatus.Blocked, "An unsupported game was missed.");
        game.Title = "EA SPORTS™ WRC";
        Require(catalog.Assess(game).Status == ModCompatibilityStatus.Blocked, "A trademark symbol bypassed a blocked game match.");
        var planner = new ModInstallPlanner(catalog, new(root));
        var preview = await planner.PreviewAsync(new(game, target.Root, target.ExePath, "dxgi.dll", false, true), CancellationToken.None);
        Require(preview.Plan == null && preview.Missing.Length == 0, "A blocked game could use guided setup.");
        game.Title = "Fixture standalone game";
        preview = await planner.PreviewAsync(new(game, target.Root, target.ExePath, "dxgi.dll", true, true), CancellationToken.None);
        Require(preview.Plan == null && preview.Missing.Length == 0, "A game without an exact recipe offered verified setup.");
        preview = await planner.PreviewAsync(new(game, target.Root, target.ExePath, "dxgi.dll", false, true), CancellationToken.None);
        Require(preview.Plan == null && preview.Missing.SequenceEqual(new[] { ModProviderId.OptiScaler }), "Guided setup did not request the approved master package.");

        var profile = Path.Combine(root, "guided-replacement");
        var approved = ModPackageStore.Approved(ModProviderId.OptiScaler);
        var cache = Path.Combine(profile, "mods", "packages", approved.Key.StoreId);
        var hashes = new Dictionary<string, string>();
        foreach (var member in approved.Files)
        {
            var bytes = member == "OptiScaler.ini" ? "[Spoofing]\nDxgi=auto\n"u8.ToArray() : "fixture payload"u8.ToArray();
            var path = ModPaths.Within(Path.Combine(cache, "files"), member);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            hashes.Add(member, ModFileSnapshot.For(bytes).Hash);
        }
        ModInstallState.Write(Path.Combine(cache, "package.json"), new { approved.Key, Files = hashes });
        var replacementRoot = Directory.CreateDirectory(Path.Combine(profile, "game")).FullName;
        var replacementExe = Path.Combine(replacementRoot, "game.exe");
        File.Copy(executable, replacementExe);
        game.Path = replacementExe;
        var original = Path.Combine(replacementRoot, "libxess.dll");
        var config = Path.Combine(replacementRoot, "OptiScaler.ini");
        File.WriteAllText(original, "original game library");
        File.WriteAllText(config, "original game configuration");
        var before = ModFileSnapshot.Read(original);
        var replacementPlanner = new ModInstallPlanner(catalog, new(profile));
        var request = new ModSetupRequest(game, replacementRoot, replacementExe, "dxgi.dll", false, true);
        preview = await replacementPlanner.PreviewAsync(request, CancellationToken.None);
        var plan = preview.Plan ?? throw new InvalidOperationException(preview.Message);
        Require(plan.Changes.Single(change => change.Relative == "libxess.dll").ExpectedHash == before?.Hash,
            "The original library did not appear as a replacement.");
        var service = new ModTransactions(profile);
        File.WriteAllText(original, "changed since preview");
        await Reject(() => service.InstallAsync(plan, CancellationToken.None));
        File.WriteAllText(original, "original game library");
        await service.InstallAsync(plan, CancellationToken.None);
        Require(File.ReadAllText(original) == "fixture payload", "The game library was not replaced.");
        var installation = service.FindForGame(replacementExe).Single();
        File.Delete(original);
        File.AppendAllText(config, "; fixture settings edit");
        await service.UninstallAsync(installation.Id, true, CancellationToken.None);
        Require(ModFileSnapshot.Read(original)?.Hash == before?.Hash, "Uninstall did not restore the missing original game library.");
        var archived = Path.Combine(profile, "mods", "installations", installation.Id.ToString("N"), "saved-configuration");
        Require(File.ReadAllText(config) == "original game configuration" &&
            File.ReadAllText(Directory.GetFiles(archived, "OptiScaler.ini", SearchOption.AllDirectories).Single()).Contains("fixture settings edit", StringComparison.Ordinal),
            "Uninstall did not restore the original configuration and save the edited mod settings.");
    }

    internal static (byte[] Index, byte[] Release) RenoDxFixture(string title, int appId, byte[] binary)
    {
        var filenames = new[] { "renodx-fixture-hdr.addon64", "renodx-fixture-sdr.addon64" };
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            games = new[]
            {
                new
                {
                    title, steam_appid = appId, aliases = new[] { "RenoDX fixture alias" },
                    mods = filenames.Select((name, number) => new
                    {
                        title = "Fixture", variant = number == 0 ? "HDR" : "SDR", category = "game", status = "stable",
                        deploy = new { api = "d3d12" },
                        artifacts = new[] { new { name, arch = "x64", size = binary.Length, url = $"./{name}" } }
                    })
                }
            }
        });
        var release = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = "snapshot",
            assets = filenames.Select(name => Asset(name, binary)).Prepend(Asset("games-index.json", index))
        });
        return (index, release);

        static object Asset(string name, byte[] bytes) => new
        {
            name, size = bytes.Length, digest = "sha256:" + ModFileSnapshot.For(bytes).Hash,
            browser_download_url = new Uri(ModRenoDxCatalog.ReleaseFiles, name).AbsoluteUri, updated_at = "2026-11-12T04:58:28Z"
        };
    }

    private static async Task VerifyRenoDx(string root)
    {
        var token = CancellationToken.None;
        var profile = Path.Combine(root, "renodx-fixture");
        var binary = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "version.dll"));
        var (index, release) = RenoDxFixture("RenoDX fixture", 1675830, binary);
        var catalog = new ModRenoDxCatalog(profile);
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            if (request.RequestUri == ModRenoDxCatalog.Source) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(index) };
            if (request.RequestUri == ModRenoDxCatalog.Release) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(release) };
            Require(request.RequestUri == new Uri(ModRenoDxCatalog.ReleaseFiles, "renodx-fixture-hdr.addon64"),
                "The add-on did not download from the official snapshot.");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(binary) };
        }));
        Require(await catalog.RefreshAsync(token, http), "The RenoDX catalog did not accept the matching official index and release.");
        var game = new Game { Title = "Different library title", SteamId = 1675830 };
        var option = catalog.Find(game)?.Options.FirstOrDefault() ?? throw new InvalidOperationException("The RenoDX Steam match was missed.");
        index = "{}"u8.ToArray();
        Require(!await catalog.RefreshAsync(token, http) && catalog.Find(game)?.Options.Length == 2,
            "A mismatched RenoDX index replaced the verified one.");
        var alias = new Game { Title = "RenoDX fixture alias" };
        Require(new ModRenoDxCatalog(profile).Find(alias)?.Options.Length == 2 &&
            catalog.Find(new Game { Title = alias.Title, SteamId = 1 }) == null, "RenoDX offline matching returned an incorrect title or Steam ID.");

        var folder = Directory.CreateDirectory(Path.Combine(profile, "game")).FullName;
        var exe = Path.Combine(folder, "game.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        game.Path = exe;
        var store = new ModPackageStore(profile, http);
        var planner = new ModInstallPlanner(new ModCompatibility([], []), store);
        var target = GameInstallTarget.Capture(folder, exe);
        var service = new ModTransactions(profile);
        var reShade = ModPackageStore.Approved(ModProviderId.ReShade).Key;
        Require((await Preview(null)).Missing.Length == 0, "RenoDX offered a download without full add-on ReShade.");
        var limited = new ModInstallationRecord(Guid.NewGuid(), target, [reShade with { Variant = "standard x64" }],
            [new("dxgi.dll", "", ModProviderId.ReShade, null, null)], []);
        Require((await Preview(limited)).Missing.Length == 0, "Standard ReShade satisfied the RenoDX requirement.");
        await service.InstallAsync(new(target, [new("dxgi.dll", null, "fixture ReShade"u8.ToArray(), reShade.Provider)], [reShade]), token);
        var existing = service.FindInstallations().Single();
        Require((await Preview(existing)).Missing.SequenceEqual([ModProviderId.RenoDx]), "RenoDX did not offer to download its matched add-on.");
        await store.AcquireAsync(option.Package, token);
        var addon = Path.Combine(folder, option.File);
        File.WriteAllText(addon, "original game add-on");
        var preview = await Preview(existing);
        var plan = preview.Plan ?? throw new InvalidOperationException(preview.Message);
        File.WriteAllText(Path.Combine(folder, "dxgi.dll"), "changed after preview");
        await Reject(() => service.InstallAsync(plan, token));
        File.WriteAllText(Path.Combine(folder, "dxgi.dll"), "fixture ReShade");
        await service.InstallAsync(plan, token);
        existing = service.FindInstallations().Single();
        Require(existing.InstalledDependentsOf(ModProviderId.ReShade).SequenceEqual([ModProviderId.RenoDx]) &&
            ModFileSnapshot.Read(addon)?.Hash == option.Digest, "RenoDX deployment did not retain its ReShade dependency.");
        await service.UninstallAsync(existing.Id, false, token, ModProviderId.RenoDx);
        Require(File.ReadAllText(addon) == "original game add-on" && service.FindInstallations().Single().Files.Single().Owner == ModProviderId.ReShade,
            "Removing RenoDX did not restore its original and preserve the ReShade installation.");

        Task<ModPlanPreview> Preview(ModInstallationRecord? record) =>
            planner.PreviewAddonAsync(game, folder, exe, true, record, option.Package, option, token);
    }

    private static byte[] Zip(params (string Name, string Value)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open());
                writer.Write(entry.Value);
            }
        }
        return stream.ToArray();
    }

    private static async Task Reject(Func<Task> attempt)
    {
        try { await attempt(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("An invalid package was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ChunkedPackageStream(byte[] payload) : MemoryStream(payload)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 64)], cancellationToken);
    }

    private sealed class ProgressRecorder(Action<int>? reported = null) : IProgress<int>
    {
        internal List<int> Values { get; } = [];
        public void Report(int value)
        {
            Values.Add(value);
            reported?.Invoke(value);
        }
    }
}
