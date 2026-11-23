using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.Win32;

namespace LGLauncher;

internal sealed record ModReShadeVulkanRegistration(string Manifest, ModPackageKey Package, bool CanUpgrade = false);
internal sealed record ModReShadeVulkanInstall(ModReShadeVulkanRegistration? Existing, byte[]? Library = null, byte[]? Manifest = null);

internal sealed class ModReShadeVulkan(string profile, Func<string[]>? enabledManifests = null, Action<string>? register = null)
{
    internal const string ActivationFile = "ReShade.ini";
    private const string RegistryPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
    private const string LayerName = "VK_LAYER_reshade";
    private readonly string _root = Path.Combine(profile, "mods", "reshade-vulkan");
    private string ManagedDirectory => ModPaths.Within(_root, ModPackageStore.Approved(ModProviderId.ReShade).Key.Version);
    private string ManagedManifest => Path.Combine(ManagedDirectory, ModPackageStore.ReShadeVulkanManifest);

    internal ModReShadeVulkanRegistration? Find()
    {
        var found = (enabledManifests ?? ReadRegisteredManifests)().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ReadLayer).OfType<ModReShadeVulkanRegistration>().Take(2).ToArray();
        if (found.Length > 1) throw new InvalidDataException("More than one ReShade Vulkan layer is enabled. Keep one enabled before installing.");
        return found.FirstOrDefault();
    }

    internal void Ensure(ModReShadeVulkanInstall plan)
    {
        var current = Find();
        if (current != plan.Existing) throw ChangedRegistration();
        if (current is { CanUpgrade: false }) return;
        if (plan.Library is not { } library || plan.Manifest is not { } manifest ||
            ModFileSnapshot.For(library).Hash != ModPackageStore.ReShadeModuleDigest ||
            ModFileSnapshot.For(manifest).Hash != ModPackageStore.ReShadeVulkanManifestDigest)
            throw new InvalidDataException("The Vulkan layer files do not match the reviewed ReShade release.");

        var directory = ManagedDirectory;
        ModPaths.RejectReparsePoints(directory);
        if (current is { CanUpgrade: true })
        {
            Upgrade(current, library);
            return;
        }
        if (!Directory.Exists(directory))
        {
            var staging = ModPaths.Within(_root, $".stage-{Guid.NewGuid():N}");
            ModPaths.RejectReparsePoints(staging);
            Directory.CreateDirectory(staging);
            try
            {
                WriteLayerFile(Path.Combine(staging, ModPackageStore.ReShadeModule), library);
                WriteLayerFile(Path.Combine(staging, ModPackageStore.ReShadeVulkanManifest), manifest);
                Directory.Move(staging, directory);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    File.Delete(Path.Combine(staging, ModPackageStore.ReShadeModule));
                    File.Delete(Path.Combine(staging, ModPackageStore.ReShadeVulkanManifest));
                    Directory.Delete(staging);
                }
            }
        }

        var managed = ReadLayer(ManagedManifest) ??
            throw new InvalidDataException("The shared ReShade Vulkan layer files could not be verified.");
        if (Find() != null) throw ChangedRegistration();
        if (managed.CanUpgrade) Upgrade(managed, library);
        // The global registration is independent of per-game files and is shared by subsequent installs.
        (register ?? RegisterForCurrentUser)(ManagedManifest);
    }

    internal void UpgradeEnabled(byte[] library)
    {
        var current = Find();
        if (current == null) throw new InvalidDataException("No enabled ReShade Vulkan layer was found for this installation.");
        if (current.CanUpgrade) Upgrade(current, library);
    }

    private void Upgrade(ModReShadeVulkanRegistration expected, byte[] library)
    {
        if (ModFileSnapshot.For(library).Hash != ModPackageStore.ReShadeModuleDigest)
            throw new InvalidDataException("The updated Vulkan DLL does not match the reviewed ReShade release.");
        // Both editions ship the same manifest. Replacing just the DLL preserves the registration and each game's configuration.
        var destination = ModPaths.Within(ManagedDirectory, ModPackageStore.ReShadeModule);
        var operation = Guid.NewGuid();
        var stage = ModPaths.Within(ManagedDirectory, $".stage-{operation:N}");
        var displaced = ModPaths.Within(ManagedDirectory, $".previous-{operation:N}");
        try
        {
            WriteLayerFile(stage, library);
            if (ReadLayer(ManagedManifest) != expected) throw ChangedRegistration();
            File.Replace(stage, destination, displaced);
            if (ModFileSnapshot.Read(displaced)?.Hash != ModPackageStore.StandardReShadeModuleDigest)
                throw new IOException($"The previous Vulkan DLL changed during replacement. Its preserved copy is here: {displaced}");
            File.Delete(displaced);
        }
        finally { File.Delete(stage); }
    }

    private ModReShadeVulkanRegistration? ReadLayer(string manifest)
    {
        if (!IsLocalPath(manifest)) return null;
        var isReShade = Path.GetFileName(manifest).StartsWith("ReShade", StringComparison.OrdinalIgnoreCase);
        try
        {
            ModPaths.RejectReparsePoints(manifest);
            using var input = File.OpenRead(manifest);
            if (input.Length > 256 * 1024) throw new InvalidDataException("The Vulkan manifest exceeds its size limit.");
            using var json = JsonDocument.Parse(input);
            if (!json.RootElement.TryGetProperty("layer", out var layer) || layer.ValueKind != JsonValueKind.Object ||
                !layer.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != LayerName)
                return null;
            isReShade = true;

            if (!layer.TryGetProperty("library_path", out var library) || library.ValueKind != JsonValueKind.String ||
                library.GetString() is not { Length: > 0 } libraryPath)
                throw new InvalidDataException("The ReShade Vulkan manifest has no library path.");
            var dll = Path.GetFullPath(libraryPath, Path.GetDirectoryName(manifest)!);
            if (!IsLocalPath(dll) || !Path.GetFileName(dll).Equals(ModPackageStore.ReShadeModule, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The registered ReShade layer does not point to ReShade64.dll.");
            ModPaths.RejectReparsePoints(dll);
            using var binary = File.OpenRead(dll);
            using var pe = new PEReader(binary, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || !pe.PEHeaders.IsDll)
                throw new InvalidDataException("The registered ReShade layer is not a 64-bit Windows DLL.");
            var version = FileVersionInfo.GetVersionInfo(dll);
            if (!String.Equals(version.ProductName, "ReShade", StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrWhiteSpace(version.ProductVersion))
                throw new InvalidDataException("The registered Vulkan DLL could not be identified as ReShade.");
            binary.Position = 0;
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binary));
            var approved = ModPackageStore.Approved(ModProviderId.ReShade).Key;
            if (manifest.Equals(ManagedManifest, StringComparison.OrdinalIgnoreCase))
            {
                if (!dll.Equals(Path.Combine(ManagedDirectory, ModPackageStore.ReShadeModule), StringComparison.OrdinalIgnoreCase) ||
                    ModFileSnapshot.Read(manifest)?.Hash != ModPackageStore.ReShadeVulkanManifestDigest ||
                    digest != ModPackageStore.ReShadeModuleDigest && digest != ModPackageStore.StandardReShadeModuleDigest)
                    throw new InvalidDataException("The launcher’s shared ReShade Vulkan files have changed.");
                return new(manifest, approved, CanUpgrade: digest == ModPackageStore.StandardReShadeModuleDigest);
            }
            if (digest != ModPackageStore.ReShadeModuleDigest)
                throw new InvalidDataException($"Use the tool that installed this layer to switch to the official ReShade {approved.Version} build with full add-on support, or disable the layer before continuing.");
            return new(manifest, approved);
        }
        catch (Exception error) when (ModTransactions.ModFailure(error) || error is InvalidOperationException)
        {
            if (!isReShade) return null;
            throw new InvalidDataException($"The enabled ReShade Vulkan registration needs review: {manifest}. {error.Message}", error);
        }
    }

    private static bool IsLocalPath(string path) => Path.IsPathFullyQualified(path) && Path.GetPathRoot(path) is { Length: 3 } root && root[1] == ':';

    private static void WriteLayerFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static InvalidDataException ChangedRegistration() =>
        new("The ReShade Vulkan registration changed after preview. Preview the installation again.");

    private static string[] ReadRegisteredManifests()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var paths = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath);
            if (key == null) continue;
            foreach (var value in key.GetValueNames())
                if (key.GetValue(value) is int enabled && enabled == 0 && IsLocalPath(value)) paths.Add(value);
        }
        return [.. paths];
    }

    private static void RegisterForCurrentUser(string manifest)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("ReShade Vulkan registration is available on Windows.");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.CreateSubKey(RegistryPath);
        key.SetValue(manifest, 0, RegistryValueKind.DWord);
    }
}
