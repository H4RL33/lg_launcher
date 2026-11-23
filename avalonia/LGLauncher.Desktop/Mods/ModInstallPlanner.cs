using System.Diagnostics;

namespace LGLauncher;

internal sealed record ModSetupRequest(Game Game, string Root, string Executable, string Loader, bool Verified, bool Offline,
    ModDxgiSpoofing? DxgiSpoofing = null, bool IncludeSpecialK = false);
internal sealed record ModPlanPreview(ModInstallPlan? Plan, ModProviderId[] Missing, string Message);
internal enum ModReShadeApi { Dxgi, Direct3D9, OpenGl, Vulkan }

internal sealed class ModInstallPlanner(ModCompatibility compatibility, ModPackageStore store, ModReShadeVulkan? vulkan = null)
{
    internal static readonly string[] Loaders = ["dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll"];

    internal static ModDxgiSpoofing DefaultDxgiSpoofing(ModRecipe? recipe) =>
        recipe?.DisableDxgiSpoofing == true ? ModDxgiSpoofing.Disabled : ModDxgiSpoofing.Automatic;

    internal async Task<ModPlanPreview> PreviewAsync(ModSetupRequest request, CancellationToken token)
    {
        using var lease = await store.LeaseAsync(token).ConfigureAwait(false);
        return await Task.Run(() => Preview(request, token), token).ConfigureAwait(false);
    }

    internal async Task<ModPlanPreview> PreviewReShadeAsync(Game game, string root, string executable, ModReShadeApi api,
        bool offline, ModInstallationRecord? existing, CancellationToken token)
    {
        using var lease = await store.LeaseAsync(token).ConfigureAwait(false);
        return await Task.Run(() => PreviewReShade(game, root, executable, api, offline, existing, token), token).ConfigureAwait(false);
    }

    private ModPlanPreview PreviewReShade(Game game, string root, string executable, ModReShadeApi api, bool offline,
        ModInstallationRecord? existing, CancellationToken token)
    {
        if (!offline) return new(null, [], "Confirm that you will use the mod only for offline play.");
        if (compatibility.Advisory(game).Status == ModCompatibilityStatus.Blocked)
            return new(null, [], "The compatibility list flags this game for incompatibility or anti-cheat.");
        var target = GameInstallTarget.Capture(root, executable);
        var launch = ModPaths.Relative(Path.GetRelativePath(target.Root, Path.GetFullPath(game.Path)));
        ModPaths.RejectReparsePoints(ModPaths.Within(target.Root, launch));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (target.Root.Equals(windows, StringComparison.OrdinalIgnoreCase) || ModTransactions.Contains(windows, target.Root))
            return new(null, [], "Choose a game folder outside the Windows system folder.");
        if (ModCompatibility.HasAntiCheat(target)) return new(null, [], "Anti-cheat files were detected in the selected game folder.");

        var renderer = Path.GetDirectoryName(target.ExePath)!;
        var name = api switch
        {
            ModReShadeApi.Dxgi => "dxgi.dll",
            ModReShadeApi.Direct3D9 => "d3d9.dll",
            ModReShadeApi.OpenGl => "opengl32.dll",
            ModReShadeApi.Vulkan => ModReShadeVulkan.ActivationFile,
            _ => throw new ArgumentOutOfRangeException(nameof(api))
        };
        var changes = new List<ModFileChange>();
        ModReShadeChain? chain = null;
        if (existing != null)
        {
            if (!existing.Target.Matches(target) || existing.Files.Any(file => file.Owner == ModProviderId.ReShade))
                return new(null, [], "Review the existing managed installation for this game before installing ReShade.");
            if (api != ModReShadeApi.Vulkan)
            {
                if (api != ModReShadeApi.Dxgi) return new(null, [], "Choose DirectX 10, 11 or 12, or Vulkan to install alongside OptiScaler.");
                var (configuration, original) = ReadChainingConfiguration(existing);
                var before = ModFileSnapshot.For(original).Hash;
                var previousValue = ModDxgiConfiguration.ReadReShade(original);
                chain = new(configuration, previousValue, before);
                var edited = ModDxgiConfiguration.WithReShade(original, "true");
                if (!ReferenceEquals(original, edited)) changes.Add(new(configuration, before, edited, ModProviderId.OptiScaler));
                name = ModPackageStore.ReShadeModule;
            }
        }
        else if (api != ModReShadeApi.Vulkan &&
            (File.Exists(Path.Combine(renderer, "OptiScaler.ini")) || File.Exists(Path.Combine(renderer, "OptiScaler.dll"))))
        {
            return new(null, [], "An external OptiScaler installation was detected. Use its coexistence guide to configure ReShade.");
        }

        foreach (var proxy in new[] { name, ModPackageStore.ReShadeModule, "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "ddraw.dll", "opengl32.dll" }.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(renderer, proxy);
            ModPaths.RejectReparsePoints(path);
            if (ModPaths.Attributes(path) == null) continue;
            if (proxy == name || proxy == ModPackageStore.ReShadeModule ||
                (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductName == "ReShade"))
                return new(null, [], $"{proxy} is already present in the rendering folder. Review the existing installation before continuing.");
        }

        var destination = ModPaths.Relative(Path.GetRelativePath(target.Root, Path.Combine(renderer, name)));
        var registered = api == ModReShadeApi.Vulkan ? vulkan?.Find() : null;
        if (registered is { CanUpgrade: false })
            return VulkanPreview(registered.Package, new(registered));

        var approved = ModPackageStore.Approved(ModProviderId.ReShade);
        var package = store.List(ModProviderId.ReShade).FirstOrDefault(candidate => candidate.Key == approved.Key);
        if (package == null) return new(null, [ModProviderId.ReShade], "Download the reviewed ReShade package before previewing the install.");
        var module = ReadReviewedReShade(package, ModPackageStore.ReShadeModule, ModPackageStore.ReShadeModuleLength, ModPackageStore.ReShadeModuleDigest);
        if (api == ModReShadeApi.Vulkan)
        {
            if (vulkan == null) throw new InvalidDataException("The Windows Vulkan registration service is unavailable.");
            var manifest = ReadReviewedReShade(package, ModPackageStore.ReShadeVulkanManifest, 526, ModPackageStore.ReShadeVulkanManifestDigest);
            return VulkanPreview(package.Key, new(registered, module, manifest));
        }

        changes.Insert(0, new(destination, null, module, ModProviderId.ReShade));
        var links = chain == null ? null : new[] { new ModDependencyLink(ModProviderId.ReShade, ModProviderId.OptiScaler) };
        return new(new(target, changes, [package.Key], Dependencies: links, ExistingInstallation: existing?.Id, ReShadeChain: chain),
            [], "Review the files and versions below.");

        ModPlanPreview VulkanPreview(ModPackageKey key, ModReShadeVulkanInstall layer) => new(
            new(target, [new(destination, null, "[GENERAL]\r\n"u8.ToArray(), ModProviderId.ReShade)], [key],
                ExistingInstallation: existing?.Id, ReShadeVulkan: layer), [], "Review the Vulkan layer and per-game activation file.");

    }

    internal async Task<byte[]> ReadReShadeModuleAsync(CancellationToken token)
    {
        using var lease = await store.LeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var approved = ModPackageStore.Approved(ModProviderId.ReShade).Key;
        var package = store.List(ModProviderId.ReShade).FirstOrDefault(candidate => candidate.Key == approved) ??
            throw new InvalidDataException("Download ReShade with full add-on support first.");
        return await Task.Run(() => ReadReviewedReShade(package, ModPackageStore.ReShadeModule, ModPackageStore.ReShadeModuleLength,
            ModPackageStore.ReShadeModuleDigest), token).ConfigureAwait(false);
    }

    private static byte[] ReadReviewedReShade(CachedModPackage package, string member, int length, string digest)
    {
        var bytes = ReadBytes(ModPaths.Within(package.ContentDirectory, member), length);
        if (bytes.Length != length || ModFileSnapshot.For(bytes).Hash != digest)
            throw new InvalidDataException($"The cached {member} changed. Delete and download the ReShade package again.");
        return bytes;
    }

    internal static (string Relative, byte[] Bytes) ReadChainingConfiguration(ModInstallationRecord record)
    {
        if (!record.Packages.Contains(ModPackageStore.Approved(ModProviderId.OptiScaler).Key))
            throw new InvalidDataException("ReShade needs the reviewed managed OptiScaler version to load alongside it.");
        var folder = Path.GetDirectoryName(record.Target.Executable) ?? "";
        var owned = record.Files.Where(file => file.Owner == ModProviderId.OptiScaler).ToArray();
        var loaders = owned.Where(file => Loaders.Any(name =>
            file.Relative.Equals(ModPaths.Relative(Path.Combine(folder, name)), StringComparison.OrdinalIgnoreCase))).ToArray();
        if (loaders.Length != 1 ||
            ModFileSnapshot.Read(ModPaths.Within(record.Target.Root, loaders[0].Relative))?.Hash != loaders[0].DeployedHash)
            throw new InvalidDataException("The managed OptiScaler loader is missing or changed.");
        var ini = ModPaths.Relative(Path.Combine(folder, "OptiScaler.ini"));
        if (!owned.Any(file => file.Relative.Equals(ini, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The OptiScaler setup does not have a managed INI beside the rendering executable.");
        return (ini, ReadBytes(ModPaths.Within(record.Target.Root, ini), ModDxgiConfiguration.MaximumSize));
    }

    private static byte[] ReadBytes(string path, int maximum)
    {
        ModPaths.RejectReparsePoints(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the reviewed size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private ModPlanPreview Preview(ModSetupRequest request, CancellationToken token)
    {
        var assessment = compatibility.Assess(request.Game);
        if (assessment.Status == ModCompatibilityStatus.Blocked) return new(null, [], "The compatibility list flags this game for incompatibility or anti-cheat.");
        if (request.Verified && assessment.Recipe == null) return new(null, [], "The selected game build has no verified recipe.");
        var recipe = request.Verified ? assessment.Recipe : null;
        if (!request.Offline) return new(null, [], "Confirm that you will use the mod only for offline play.");
        var target = recipe != null ? assessment.Target! : GameInstallTarget.Capture(request.Root, request.Executable);
        var launch = ModPaths.Relative(Path.GetRelativePath(target.Root, Path.GetFullPath(request.Game.Path)));
        ModPaths.RejectReparsePoints(ModPaths.Within(target.Root, launch));
        if (ModCompatibility.HasAntiCheat(target)) return new(null, [], "Anti-cheat files were detected in the selected game folder.");
        if (recipe == null && (assessment.Status == ModCompatibilityStatus.Dependency || ModCompatibility.RequiresSpecialSetup(target)))
            return new(null, [], "This game needs a separate dependency setup. See the compatibility notes.");

        var loader = recipe?.Loader ?? request.Loader;
        if (!Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) return new(null, [], "Choose a supported OptiScaler DLL name.");
        var extra = recipe?.Dependencies.Where(dependency => !dependency.Optional ||
            (dependency.Provider == ModProviderId.SpecialK && request.IncludeSpecialK)).ToArray() ?? [];
        var providers = extra.Select(dependency => dependency.Provider).Prepend(ModProviderId.OptiScaler).Distinct().ToArray();
        var packages = new Dictionary<ModProviderId, CachedModPackage>();
        var missing = new List<ModProviderId>();
        foreach (var provider in providers)
        {
            var approved = ModPackageStore.Approved(provider);
            var dependency = extra.FirstOrDefault(item => item.Provider == provider);
            var version = provider == ModProviderId.OptiScaler ? recipe?.OptiScalerVersion : dependency?.Version;
            var digest = provider == ModProviderId.OptiScaler ? recipe?.OptiScalerDigest : dependency?.Digest;
            if (version != null && (version != approved.Key.Version || !String.Equals(digest, approved.Key.Digest, StringComparison.OrdinalIgnoreCase)))
                return new(null, [], $"The recipe needs a different reviewed build of {ModPackageStore.Name(provider)}.");
            var cached = store.List(provider).FirstOrDefault(package => package.Key == approved.Key);
            if (cached == null) missing.Add(provider);
            else packages.Add(provider, cached);
        }
        if (missing.Count > 0) return new(null, [.. missing], "Download the reviewed packages before previewing the install.");

        var files = new List<ModFileChange>();
        var occupied = new List<string>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executableFolder = Path.GetDirectoryName(target.Executable);
        var opti = packages[ModProviderId.OptiScaler];
        ModDxgiSpoofing? dxgiSpoofing = recipe?.Omit.Contains("OptiScaler.ini", StringComparer.OrdinalIgnoreCase) == true
            ? null : request.DxgiSpoofing ?? DefaultDxgiSpoofing(recipe);
        foreach (var member in ModPackageStore.Approved(ModProviderId.OptiScaler).Files)
        {
            if (recipe?.Omit.Contains(member, StringComparer.OrdinalIgnoreCase) == true) continue;
            var destination = member == "OptiScaler.dll" ? loader : member;
            Add(opti, member, destination, member == "OptiScaler.ini" ? dxgiSpoofing : null);
        }
        foreach (var dependency in extra)
        {
            var package = packages[dependency.Provider];
            Add(package, ModPackageStore.Approved(dependency.Provider).Files.Single(), dependency.Destination);
        }
        if (occupied.Count > 0) return new(null, [], "Existing files need review before installing:\n" + String.Join('\n', occupied));
        var recipeId = recipe == null ? null : $"{recipe.SteamId}/{recipe.Build}/{loader}";
        var dependencies = extra.Select(dependency => new ModDependencyLink(ModProviderId.OptiScaler, dependency.Provider)).ToArray();
        return new(new(target, files, packages.Values.Select(package => package.Key).ToArray(), recipeId, dxgiSpoofing, dependencies),
            [], "Review the files and versions below.");

        void Add(CachedModPackage package, string member, string destination, ModDxgiSpoofing? spoofing = null)
        {
            token.ThrowIfCancellationRequested();
            var relative = ModPaths.Relative(Path.Combine(executableFolder ?? "", destination));
            if (!destinations.Add(relative)) throw new InvalidDataException($"The recipe assigns two files to {relative}.");
            var current = ModFileSnapshot.Read(ModPaths.Within(target.Root, relative));
            if (current != null && (recipe == null || !recipe.OriginalHashes.TryGetValue(relative, out var expected) || current.Hash != expected))
            {
                occupied.Add(relative);
                return;
            }
            var source = ModPaths.Within(package.ContentDirectory, member);
            ModPaths.RejectReparsePoints(source);
            var bytes = File.ReadAllBytes(source);
            if (!package.Files.TryGetValue(member, out var hash) || ModFileSnapshot.For(bytes).Hash != hash)
                throw new InvalidDataException($"The cached {ModPackageStore.Name(package.Key.Provider)} file changed. Delete and download that package again.");
            if (spoofing is { } mode) bytes = ModDxgiConfiguration.WithMode(bytes, mode);
            files.Add(new(relative, current?.Hash, bytes, package.Key.Provider));
        }
    }
}
