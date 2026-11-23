using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace LGLauncher;

internal sealed record GameInstallTarget(string Root, string Executable, string Build, DateTime RootCreatedUtc)
{
    internal string ExePath => ModPaths.Within(Root, Executable);

    internal static GameInstallTarget Capture(string root, string executable)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        executable = Path.GetFullPath(executable);
        var volume = Path.GetPathRoot(root);
        if (!OperatingSystem.IsWindows() || volume is not { Length: 3 } || volume[1] != ':' ||
            String.Equals(volume, root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            throw new InvalidDataException("Choose a local game folder on Windows.");
        if (new DriveInfo(volume).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("Choose a game folder on a local drive.");
        var relative = ModPaths.Relative(Path.GetRelativePath(root, executable));
        var driveRelative = Path.GetRelativePath(volume, root);
        ModPaths.Relative(driveRelative);
        ModPaths.RejectReparsePoints(executable);
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose the game's 64-bit Windows executable.");
        using var input = File.OpenRead(executable);
        using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.IsDll || pe.PEHeaders.PEHeader == null)
            throw new InvalidDataException("The selected file is not a 64-bit Windows executable.");
        input.Position = 0;
        return new(root, relative, Convert.ToHexString(SHA256.HashData(input)), Directory.GetCreationTimeUtc(root));
    }

    internal bool Matches(GameInstallTarget target) => SameFolder(target) &&
        String.Equals(Executable, target.Executable, StringComparison.OrdinalIgnoreCase) && Build == target.Build;

    internal bool SameFolder(GameInstallTarget target) => Root.Equals(target.Root, StringComparison.OrdinalIgnoreCase) &&
        RootCreatedUtc == target.RootCreatedUtc;
}

internal sealed record ModFileSnapshot(string Hash, long Length, DateTime Created, DateTime Modified, FileAttributes Attributes)
{
    internal static ModFileSnapshot? Read(string path)
    {
        ModPaths.RejectReparsePoints(path);
        if (ModPaths.Attributes(path) is not { } attributes) return null;
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException($"A folder occupies the file path {Path.GetFileName(path)}.");
        using var source = File.OpenRead(path);
        return new(Convert.ToHexString(SHA256.HashData(source)), source.Length, File.GetCreationTimeUtc(path),
            File.GetLastWriteTimeUtc(path), attributes);
    }

    internal static ModFileSnapshot For(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength,
        DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal);

    internal void ApplyMetadata(string path)
    {
        File.SetCreationTimeUtc(path, Created);
        File.SetLastWriteTimeUtc(path, Modified);
        File.SetAttributes(path, Attributes & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly |
            FileAttributes.System | FileAttributes.NotContentIndexed));
    }
}

internal sealed record ModFileChange(string Relative, string? ExpectedHash, byte[] Bytes, ModProviderId Owner);
internal sealed record ModDependencyLink(ModProviderId Dependent, ModProviderId Required, string? Variant = null)
{
    internal bool Matches(ModPackageKey package) => package.Provider == Required &&
        (Variant == null || Variant.Equals(package.Variant, StringComparison.OrdinalIgnoreCase));
}
internal sealed record ModReShadeChain(string Configuration, string PreviousValue, string ExpectedHash);
internal sealed record ModInstallPlan(GameInstallTarget Target, IReadOnlyList<ModFileChange> Changes, IReadOnlyList<ModPackageKey> Packages,
    string? RecipeId = null, ModDxgiSpoofing? DxgiSpoofing = null, ModDependencyLink[]? Dependencies = null,
    Guid? ExistingInstallation = null, ModReShadeChain? ReShadeChain = null, ModReShadeVulkanInstall? ReShadeVulkan = null);
internal sealed record ModOwnedFile(string Relative, string DeployedHash, ModProviderId Owner, ModFileSnapshot? Original, string? OriginalBackup);
internal sealed record ModInstallationRecord(Guid Id, GameInstallTarget Target, ModPackageKey[] Packages, ModOwnedFile[] Files, string[] CreatedDirectories,
    string? RecipeId = null, ModDependencyLink[]? Dependencies = null, ModReShadeChain? ReShadeChain = null, bool ReShadeVulkan = false)
{
    internal ModDependencyLink[] KnownDependencies()
    {
        if (Dependencies != null) return Dependencies;
        // Older records stored an OptiScaler setup and its selected companion packages together.
        if (!Packages.Any(package => package.Provider == ModProviderId.OptiScaler)) return [];
        return Packages.Where(package => package.Provider != ModProviderId.OptiScaler)
            .Select(package => new ModDependencyLink(ModProviderId.OptiScaler, package.Provider)).ToArray();
    }

    internal bool HasDependency(ModDependencyLink requirement, ModInstallInspection? inspection = null)
    {
        if (inspection is { BuildChanged: true } or { RecoveryNeeded: true }) return false;
        foreach (var link in DependencyChain(requirement))
        {
            if (!Packages.Any(link.Matches)) return false;
            var files = Files.Where(file => file.Owner == link.Required).ToArray();
            if (files.Length == 0) return false;
            if (inspection != null && (files.Any(file => inspection.Missing.Contains(file.Relative, StringComparer.OrdinalIgnoreCase) ||
                !file.Relative.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                inspection.Changed.Contains(file.Relative, StringComparer.OrdinalIgnoreCase)) ||
                link.Required == ModProviderId.ReShade && inspection.VulkanLayerIssue != null))
                return false;
        }
        return true;
    }

    internal IEnumerable<ModDependencyLink> DependencyChain(ModDependencyLink requirement)
    {
        var links = KnownDependencies();
        var visited = new HashSet<ModDependencyLink>();
        var pending = new Queue<ModDependencyLink>([requirement]);
        while (pending.TryDequeue(out var link))
        {
            if (!visited.Add(link)) continue;
            yield return link;
            foreach (var next in links.Where(next => next.Dependent == link.Required)) pending.Enqueue(next);
        }
    }

    internal ModProviderId[] InstalledDependentsOf(ModProviderId provider)
    {
        var links = KnownDependencies();
        var visited = new HashSet<ModProviderId> { provider };
        var pending = new Queue<ModProviderId>();
        pending.Enqueue(provider);
        while (pending.TryDequeue(out var required))
        {
            foreach (var link in links.Where(link => link.Required == required))
                if (visited.Add(link.Dependent)) pending.Enqueue(link.Dependent);
        }
        return Files.Select(file => file.Owner).Distinct()
            .Where(owner => owner != provider && visited.Contains(owner)).ToArray();
    }
}
internal sealed record ModInstallInspection(ModInstallationRecord? Installation, string[] Missing, string[] Changed, bool RecoveryNeeded, bool BuildChanged,
    string? VulkanLayerIssue = null, bool VulkanCanUpgrade = false);
internal sealed record ModOperationResult(string Message, bool RecoveryNeeded = false);
internal enum ModTransactionStep { JournalWritten, BeforeMutation, FileApplied, Committed }

internal static class ModInstallState
{
    internal static T? Read<T>(string path) where T : class
    {
        ModPaths.RejectReparsePoints(path);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("An installation record could not be read.");
    }

    internal static void Write<T>(string path, T data)
    {
        ModPaths.RejectReparsePoints(path);
        var temporary = path + ".tmp";
        ModPaths.RejectReparsePoints(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, data);
            stream.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
