using System.Diagnostics;

namespace LGLauncher;

internal sealed record ModSpoofingSetting(ModDxgiSpoofing Mode, string Hash);

internal sealed class ModTransactions(string profile, Action<ModTransactionStep>? checkpoint = null, ModReShadeVulkan? vulkan = null)
{
    private sealed record State(Guid Transaction, ModInstallationRecord? Installation);
    private sealed record Operation(string Relative, ModFileSnapshot? Before, string? BeforeBackup, ModFileSnapshot? After, string? Payload);
    private sealed record Journal(Guid Id, GameInstallTarget Target, Operation[] Operations, string[] Directories, ModInstallationRecord? Result);
    private readonly string _root = Path.Combine(profile, "mods", "installations");
    internal ModReShadeVulkan Vulkan { get; } = vulkan ?? new(profile);
    private readonly SemaphoreSlim _operations = new(1);

    internal IReadOnlyList<ModInstallationRecord> FindInstallations() => Folders().Select(folder => ReadState(folder)?.Installation)
        .OfType<ModInstallationRecord>().ToArray();

    internal bool HasPending(string executable) => Folders().Any(folder =>
    {
        var journal = ReadJournal(folder);
        return journal != null && Contains(journal.Target.Root, executable);
    });

    internal bool HasAnyPending() => Folders().Any(folder => ReadJournal(folder) != null);

    internal IReadOnlyList<ModInstallationRecord> FindForGame(string executable) => FindInstallations()
        .Where(record => Contains(record.Target.Root, executable)).ToArray();

    internal static bool Contains(string root, string? path) => !String.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        (Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    internal Task<ModOperationResult> InstallAsync(ModInstallPlan plan, CancellationToken token, IProgress<int>? progress = null) =>
        Run(() => Install(plan, token, progress), token);

    internal Task<ModOperationResult> UninstallAsync(Guid id, bool archiveEditedConfiguration, CancellationToken token, ModProviderId? provider = null) =>
        Run(() => Uninstall(id, archiveEditedConfiguration, token, provider), token);

    internal Task<ModSpoofingSetting> ReadDxgiSpoofingAsync(Guid id, CancellationToken token) => Run(() =>
    {
        var (_, _, bytes, snapshot) = ReadManagedConfiguration(id, token, verifyBuild: false);
        return new ModSpoofingSetting(ModDxgiConfiguration.Read(bytes), snapshot.Hash);
    }, token);

    internal Task<ModOperationResult> SetDxgiSpoofingAsync(Guid id, string expectedHash, ModDxgiSpoofing mode, CancellationToken token) =>
        Run(() => SetDxgiSpoofing(id, expectedHash, mode, token), token);

    internal Task<ModOperationResult> UpgradeReShadeAsync(Guid id, byte[] module, CancellationToken token) =>
        Run(() => UpgradeReShade(id, module, token), token);

    internal Task<IReadOnlyList<ModOperationResult>> RecoverAsync(CancellationToken token) => Run<IReadOnlyList<ModOperationResult>>(() =>
    {
        var results = new List<ModOperationResult>();
        foreach (var folder in Folders())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (ReadJournal(folder) is { } journal)
                {
                    Recover(folder, journal);
                    results.Add(new($"Recovered the mod operation for {Path.GetFileName(journal.Target.Root)}."));
                }
            }
            catch (Exception error) when (ModFailure(error)) { results.Add(new(error.Message, true)); }
        }
        return results;
    }, token);

    internal Task<ModInstallInspection> InspectAsync(Guid id, CancellationToken token, IProgress<int>? progress = null) => Run(() =>
    {
        var folder = Folder(id);
        var record = ReadState(folder)?.Installation;
        var missing = new List<string>();
        var changed = new List<string>();
        if (record != null)
        {
            for (var index = 0; index < record.Files.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var file = record.Files[index];
                var current = ModFileSnapshot.Read(ModPaths.Within(record.Target.Root, file.Relative));
                if (current == null) missing.Add(file.Relative);
                else if (current.Hash != file.DeployedHash && !IsVulkanActivation(record, file)) changed.Add(file.Relative);
                progress?.Report((int)((index + 1L) * 90 / record.Files.Length));
            }
        }
        token.ThrowIfCancellationRequested();
        var (layerIssue, layerCanUpgrade) = record?.ReShadeVulkan == true ? CheckVulkanLayer() : (null, false);
        return new ModInstallInspection(record, [.. missing], [.. changed], ReadJournal(folder) != null,
            record != null && !BuildMatches(record.Target), layerIssue, layerCanUpgrade);
    }, token);

    private (string? Issue, bool CanUpgrade) CheckVulkanLayer()
    {
        try
        {
            return Vulkan.Find() switch
            {
                null => ("No enabled ReShade Vulkan layer was found for this installation.", false),
                { CanUpgrade: true } => ("The shared ReShade Vulkan layer has limited add-on support.", true),
                _ => (null, false)
            };
        }
        catch (Exception error) when (ModFailure(error)) { return (error.Message, false); }
    }

    internal Task<ModOperationResult> RelinkAsync(Guid id, GameInstallTarget target, CancellationToken token) => Run(() =>
    {
        var folder = Folder(id);
        var state = ReadState(folder);
        if (state?.Installation is not { } record || ReadJournal(folder) != null)
            throw new InvalidDataException("Resolve pending recovery before relinking this installation.");
        if (Directory.Exists(record.Target.Root) || record.Target.Build != target.Build ||
            !record.Target.Executable.Equals(target.Executable, StringComparison.OrdinalIgnoreCase) ||
            !BuildMatches(target) || Occupied(target.Root, id) || record.Files.Any(file =>
                ModFileSnapshot.Read(ModPaths.Within(target.Root, file.Relative))?.Hash != file.DeployedHash))
            throw new InvalidDataException("The selected folder does not match the recorded install.");
        ModInstallState.Write(StatePath(folder), state with { Installation = record with { Target = target } });
        return new ModOperationResult("Mod installation relinked.");
    }, token);

    private ModOperationResult Install(ModInstallPlan plan, CancellationToken token, IProgress<int>? progress)
    {
        // Each file is prepared, checked before deployment, applied, and verified. Finalization is the last step.
        var totalSteps = plan.Changes.Count * 4L + 1;
        var completedSteps = 0L;
        var lastProgress = -1;
        ReportProgress(0);
        if (!BuildMatches(plan.Target) || ModCompatibility.HasAntiCheat(plan.Target) || Occupied(plan.Target.Root, plan.ExistingInstallation))
            throw new InvalidDataException("The game files changed or already have a managed installation.");
        RequireIdle(plan.Target);
        var operationId = Guid.NewGuid();
        var id = plan.ExistingInstallation ?? operationId;
        var folder = Folder(id);
        ModPaths.RejectReparsePoints(folder);
        var existing = plan.ExistingInstallation is { } ? ReadState(folder)?.Installation : null;
        if (plan.ReShadeVulkan != null && (plan.ReShadeChain != null || plan.Packages.Count != 1 ||
            plan.Packages[0].Provider != ModProviderId.ReShade || plan.Changes.Count != 1 ||
            plan.Changes[0].Owner != ModProviderId.ReShade || plan.Changes[0].ExpectedHash != null ||
            !plan.Changes[0].Relative.Equals(ModPaths.Relative(Path.Combine(Path.GetDirectoryName(plan.Target.Executable) ?? "",
                ModReShadeVulkan.ActivationFile)), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The ReShade Vulkan plan does not match the rendering executable.");
        if (plan.ExistingInstallation != null)
        {
            if (existing == null || !existing.Target.Matches(plan.Target) || (plan.ReShadeChain == null && plan.ReShadeVulkan == null) ||
                existing.Files.Any(file => file.Owner == ModProviderId.ReShade) ||
                plan.Packages.Count != 1 || plan.Packages[0].Provider != ModProviderId.ReShade)
                throw new InvalidDataException("The managed OptiScaler installation changed after preview.");
            if (plan.ReShadeChain is { } link)
            {
                var (configuration, bytes) = ModInstallPlanner.ReadChainingConfiguration(existing);
                if (!configuration.Equals(link.Configuration, StringComparison.OrdinalIgnoreCase) ||
                    ModFileSnapshot.For(bytes).Hash != link.ExpectedHash || ModDxgiConfiguration.ReadReShade(bytes) != link.PreviousValue)
                    throw new InvalidDataException("The OptiScaler INI changed after preview. Preview the ReShade installation again.");
            }
        }
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operations = new List<Operation>();
        var files = existing?.Files.ToList() ?? [];
        if (plan.Changes.Count == 0) throw new InvalidDataException("The install plan has no files.");
        foreach (var change in plan.Changes)
        {
            token.ThrowIfCancellationRequested();
            var relative = ModPaths.Relative(change.Relative);
            if (!allPaths.Add(relative) || relative.Equals(plan.Target.Executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The plan has an overlapping file target.");
            var destination = ModPaths.Within(plan.Target.Root, relative);
            var prior = ModFileSnapshot.Read(destination);
            if (prior?.Hash != change.ExpectedHash) throw new InvalidDataException($"The file changed after preview: {relative}");
            var trackedIndex = files.FindIndex(file => file.Relative.Equals(relative, StringComparison.OrdinalIgnoreCase));
            if (existing != null && (trackedIndex >= 0
                ? prior == null || !relative.Equals(plan.ReShadeChain?.Configuration, StringComparison.OrdinalIgnoreCase) ||
                    files[trackedIndex].Owner != ModProviderId.OptiScaler || change.Owner != ModProviderId.OptiScaler
                : prior != null || change.Owner != ModProviderId.ReShade))
                throw new InvalidDataException($"The ReShade plan overlaps a managed file: {relative}.");
            for (var parent = Path.GetDirectoryName(destination)!; parent != plan.Target.Root && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)!)
            {
                if (File.Exists(parent)) throw new InvalidDataException("A file occupies a planned folder.");
                directories.Add(Path.GetRelativePath(plan.Target.Root, parent));
            }
            var after = ModFileSnapshot.For(change.Bytes);
            if (prior?.Hash == after.Hash) throw new InvalidDataException($"An existing copy occupies {relative}.");
            var payload = $"payload/{operationId:N}/{operations.Count}";
            var backup = prior == null ? null : trackedIndex >= 0 ? $"undo/{operationId:N}/{operations.Count}" : $"original/{operations.Count}";
            if (prior != null) CopyVerified(destination, ModPaths.Within(folder, backup!), prior);
            if (trackedIndex >= 0 && prior != null) after = after with { Created = prior.Created, Attributes = prior.Attributes };
            WritePayload(ModPaths.Within(folder, payload), change.Bytes);
            operations.Add(new(relative, prior, backup, after, payload));
            if (trackedIndex < 0) files.Add(new(relative, after.Hash, change.Owner, prior, backup));
            else if (files[trackedIndex].DeployedHash == prior?.Hash)
                files[trackedIndex] = files[trackedIndex] with { DeployedHash = after.Hash };
            AdvanceProgress();
        }
        var orderedDirectories = directories.OrderBy(path => path.Length).ToArray();
        var record = new ModInstallationRecord(id, plan.Target, [.. existing?.Packages ?? [], .. plan.Packages], [.. files],
            [.. existing?.CreatedDirectories ?? [], .. orderedDirectories], existing?.RecipeId ?? plan.RecipeId,
            existing == null ? plan.Dependencies : existing.KnownDependencies().Concat(plan.Dependencies ?? []).Distinct().ToArray(),
            plan.ReShadeChain, plan.ReShadeVulkan != null);
        Apply(folder, new(operationId, plan.Target, [.. operations], orderedDirectories, record), token, progress == null ? null : AdvanceProgress,
            plan.ReShadeVulkan is { } layer ? () => Vulkan.Ensure(layer) : null);
        ReportProgress(100);
        var name = plan.Packages.FirstOrDefault()?.Provider ?? ModProviderId.OptiScaler;
        return new($"{ModPackageStore.Name(name)} installed.");

        void AdvanceProgress()
        {
            completedSteps++;
            ReportProgress((int)(100 * completedSteps / totalSteps));
        }

        void ReportProgress(int percent)
        {
            if (progress == null || percent <= lastProgress) return;
            try { progress.Report(percent); }
            catch (Exception error)
            {
                // A progress observer can disappear while the file transaction is still active.
                Debug.WriteLine(error);
                progress = null;
            }
            lastProgress = percent;
        }
    }

    private ModOperationResult Uninstall(Guid id, bool archiveEditedConfiguration, CancellationToken token, ModProviderId? provider)
    {
        var folder = Folder(id);
        var record = ReadState(folder)?.Installation ?? throw new InvalidDataException("The installation record is unavailable.");
        if (ReadJournal(folder) != null) throw new InvalidDataException("Recover the previous operation before uninstalling.");
        var observed = GameInstallTarget.Capture(record.Target.Root, record.Target.ExePath);
        if (!record.Target.SameFolder(observed)) throw new InvalidDataException("The original game folder could not be verified.");
        RequireIdle(observed);
        var operationId = Guid.NewGuid();
        var operations = new List<Operation>();
        var unresolved = new List<ModOwnedFile>();
        var remaining = new List<ModOwnedFile>();
        var archivedConfiguration = false;
        foreach (var file in record.Files)
        {
            token.ThrowIfCancellationRequested();
            if (provider is { } selected && file.Owner != selected)
            {
                remaining.Add(file);
                continue;
            }
            var path = ModPaths.Within(observed.Root, file.Relative);
            var current = ModFileSnapshot.Read(path);
            if (current?.Hash == file.Original?.Hash) continue;
            var editedIni = file.Original == null && current != null && file.Relative.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                (archiveEditedConfiguration || (IsVulkanActivation(record, file) && current.Hash != file.DeployedHash));
            if ((!editedIni && current?.Hash != file.DeployedHash) ||
                (file.Original != null && (!record.Target.Matches(observed) ||
                    !MatchesBackup(folder, file.OriginalBackup, file.Original))))
            {
                unresolved.Add(file);
                continue;
            }
            var beforeBackup = editedIni ? $"saved-configuration/{operationId:N}/{file.Relative}" : $"undo/{operationId:N}/{operations.Count}";
            CopyVerified(path, ModPaths.Within(folder, beforeBackup), current!);
            operations.Add(new(file.Relative, current, beforeBackup, file.Original, file.OriginalBackup));
            archivedConfiguration |= editedIni;
        }
        if (provider == ModProviderId.ReShade && unresolved.Count == 0 && record.ReShadeChain is { } chain)
        {
            var configuration = remaining.FindIndex(file => file.Owner == ModProviderId.OptiScaler &&
                file.Relative.Equals(chain.Configuration, StringComparison.OrdinalIgnoreCase));
            if (configuration >= 0 && File.Exists(ModPaths.Within(observed.Root, chain.Configuration)))
            {
                var file = remaining[configuration];
                var (bytes, before) = ReadConfiguration(ModPaths.Within(observed.Root, file.Relative), token);
                if (ModDxgiConfiguration.ReadReShade(bytes).Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    var restored = ModDxgiConfiguration.WithReShade(bytes, chain.PreviousValue);
                    if (!ReferenceEquals(restored, bytes))
                    {
                        var rollback = $"undo/{operationId:N}/{operations.Count}";
                        var payload = $"payload/{operationId:N}/{operations.Count}";
                        WritePayload(ModPaths.Within(folder, rollback), bytes);
                        WritePayload(ModPaths.Within(folder, payload), restored);
                        var after = ModFileSnapshot.For(restored) with { Created = before.Created, Attributes = before.Attributes };
                        operations.Add(new(file.Relative, before, rollback, after, payload));
                        if (file.DeployedHash == before.Hash) remaining[configuration] = file with { DeployedHash = after.Hash };
                    }
                }
            }
        }
        remaining.AddRange(unresolved);
        var remainder = remaining.Count == 0 ? null : record with
        {
            Files = [.. remaining],
            Packages = record.Packages.Where(package => remaining.Any(file => file.Owner == package.Provider)).ToArray(),
            Dependencies = record.KnownDependencies(),
            ReShadeChain = remaining.Any(file => file.Owner == ModProviderId.ReShade) ? record.ReShadeChain : null,
            ReShadeVulkan = remaining.Any(file => file.Owner == ModProviderId.ReShade) && record.ReShadeVulkan
        };
        Apply(folder, new(operationId, observed, [.. operations], [], remainder), token);
        if (provider == null || remainder == null) RemoveEmpty(record.Target.Root, record.CreatedDirectories);
        if (unresolved.Count > 0)
            return new("Review changes before restoring these files: " + String.Join(", ", unresolved.Select(file => file.Relative)));
        var name = provider is { } removed ? ModPackageStore.Name(removed) : "OptiScaler";
        return new(archiveEditedConfiguration || archivedConfiguration
            ? $"{name} uninstalled. Saved configuration, if any, is under {folder}." : $"{name} uninstalled.");
    }

    private static bool IsVulkanActivation(ModInstallationRecord record, ModOwnedFile file) =>
        record.ReShadeVulkan && file.Owner == ModProviderId.ReShade &&
        Path.GetFileName(file.Relative).Equals(ModReShadeVulkan.ActivationFile, StringComparison.OrdinalIgnoreCase);

    private (ModInstallationRecord Record, ModOwnedFile File, byte[] Bytes, ModFileSnapshot Snapshot) ReadManagedConfiguration(Guid id,
        CancellationToken token, bool verifyBuild = true)
    {
        var record = ReadState(Folder(id))?.Installation ?? throw new InvalidDataException("The installation record is unavailable.");
        if (Occupied(record.Target.Root, id))
            throw new InvalidDataException("Resolve any overlapping installation or pending recovery before changing OptiScaler settings.");
        if (verifyBuild && !BuildMatches(record.Target)) throw new InvalidDataException("The recorded game build could not be verified.");
        if (ModCompatibility.HasAntiCheat(record.Target)) throw new InvalidDataException("Anti-cheat files were detected in the game folder.");

        var renderingFolder = Path.GetDirectoryName(record.Target.Executable) ?? "";
        var relative = ModPaths.Relative(Path.Combine(renderingFolder, "OptiScaler.ini"));
        var matches = record.Files.Where(file => ModPaths.Relative(file.Relative).Equals(relative, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Owner != ModProviderId.OptiScaler)
            throw new InvalidDataException("The installation does not have a single managed OptiScaler INI beside the rendering executable.");
        var (bytes, snapshot) = ReadConfiguration(ModPaths.Within(record.Target.Root, matches[0].Relative), token);
        return (record, matches[0], bytes, snapshot);
    }

    private static (byte[] Bytes, ModFileSnapshot Snapshot) ReadConfiguration(string path, CancellationToken token)
    {
        ModPaths.RejectReparsePoints(path);
        if (ModPaths.Attributes(path) is not { } attributes || (attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException("The managed OptiScaler INI is missing or its path is occupied by a folder.");

        token.ThrowIfCancellationRequested();
        using var source = File.OpenRead(path);
        if (source.Length > ModDxgiConfiguration.MaximumSize) throw new InvalidDataException("The managed OptiScaler INI is too large.");
        var bytes = new byte[(int)source.Length];
        source.ReadExactly(bytes);
        var handle = source.SafeFileHandle;
        var snapshot = ModFileSnapshot.For(bytes) with
        {
            Created = File.GetCreationTimeUtc(handle),
            Modified = File.GetLastWriteTimeUtc(handle),
            Attributes = File.GetAttributes(handle)
        };
        return (bytes, snapshot);
    }

    private ModOperationResult SetDxgiSpoofing(Guid id, string expectedHash, ModDxgiSpoofing mode, CancellationToken token)
    {
        var (record, file, bytes, before) = ReadManagedConfiguration(id, token);
        if (before.Hash != expectedHash)
            throw new InvalidDataException("The OptiScaler INI changed after opening settings. Open settings again to review it.");
        RequireIdle(record.Target);
        var changed = ModDxgiConfiguration.WithMode(bytes, mode);
        if (changed.AsSpan().SequenceEqual(bytes)) return new("DXGI spoofing is already set to the selected value.");

        var operationId = Guid.NewGuid();
        var folder = Folder(id);
        var rollback = $"undo/{operationId:N}";
        var payload = $"payload/{operationId:N}";
        var rollbackPath = ModPaths.Within(folder, rollback);
        var payloadPath = ModPaths.Within(folder, payload);
        var after = ModFileSnapshot.For(changed) with { Created = before.Created, Attributes = before.Attributes };
        // Keep the deployed hash for configurations already edited outside the launcher so uninstall still offers to archive them.
        var result = before.Hash == file.DeployedHash
            ? record with { Files = record.Files.Select(item => item == file ? file with { DeployedHash = after.Hash } : item).ToArray() }
            : record;
        try
        {
            WritePayload(rollbackPath, bytes);
            WritePayload(payloadPath, changed);
            Apply(folder, new(operationId, record.Target, [new(file.Relative, before, rollback, after, payload)], [], result), token);
        }
        catch (Exception error) when (ModFailure(error) || error is OperationCanceledException)
        {
            if (ReadJournal(folder) == null)
            {
                ModPaths.RejectReparsePoints(rollbackPath);
                ModPaths.RejectReparsePoints(payloadPath);
                File.Delete(rollbackPath);
                File.Delete(payloadPath);
            }
            throw;
        }
        return new("DXGI spoofing saved.");
    }

    private ModOperationResult UpgradeReShade(Guid id, byte[] module, CancellationToken token)
    {
        const string complete = "ReShade with full add-on support is installed.";
        var folder = Folder(id);
        var record = ReadState(folder)?.Installation;
        if (record == null || ReadJournal(folder) != null || !BuildMatches(record.Target) || ModCompatibility.HasAntiCheat(record.Target))
            throw new InvalidDataException("Review the game and its managed installation before upgrading ReShade.");
        var file = record.Files.SingleOrDefault(item => item.Owner == ModProviderId.ReShade) ??
            throw new InvalidDataException("The managed ReShade install could not be found.");
        RequireIdle(record.Target);
        var after = ModFileSnapshot.For(module);
        if (after.Hash != ModPackageStore.ReShadeModuleDigest)
            throw new InvalidDataException("The updated DLL does not match the reviewed ReShade release.");

        var destination = ModPaths.Within(record.Target.Root, file.Relative);
        var before = ModFileSnapshot.Read(destination);
        token.ThrowIfCancellationRequested();
        if (record.ReShadeVulkan)
        {
            if (before == null) throw new InvalidDataException("The ReShade Vulkan activation file is missing.");
            Vulkan.UpgradeEnabled(module);
            return new(complete);
        }

        if (before == null || before.Hash != file.DeployedHash)
            throw new InvalidDataException("The managed ReShade DLL is missing or has changed. Review the installation before upgrading.");
        if (before.Hash == after.Hash) return new(complete);
        if (before.Hash != ModPackageStore.StandardReShadeModuleDigest)
            throw new InvalidDataException("The managed DLL does not match the reviewed standard ReShade build.");

        var operationId = Guid.NewGuid();
        var rollback = $"undo/{operationId:N}/0";
        var payload = $"payload/{operationId:N}/0";
        var rollbackPath = ModPaths.Within(folder, rollback);
        var payloadPath = ModPaths.Within(folder, payload);
        var approved = ModPackageStore.Approved(ModProviderId.ReShade).Key;
        var result = record with
        {
            Files = record.Files.Select(item => item == file ? file with { DeployedHash = after.Hash } : item).ToArray(),
            Packages = record.Packages.Select(key => key.Provider == ModProviderId.ReShade ? approved : key).ToArray()
        };
        try
        {
            CopyVerified(destination, rollbackPath, before);
            WritePayload(payloadPath, module);
            var update = new Operation(file.Relative, before, rollback, after with { Created = before.Created, Attributes = before.Attributes }, payload);
            Apply(folder, new(operationId, record.Target, [update], [], result), token);
        }
        finally
        {
            if (ReadJournal(folder) == null)
            {
                File.Delete(rollbackPath);
                File.Delete(payloadPath);
            }
        }
        return new(complete);
    }

    private void Apply(string folder, Journal journal, CancellationToken token, Action? advanceProgress = null, Action? prepareSharedLayer = null)
    {
        if (!BuildMatches(journal.Target)) throw new InvalidDataException("The game build changed before deployment.");
        var needed = journal.Operations.Sum(operation => operation.After?.Length ?? 0) +
            (journal.Operations.MaxBy(operation => operation.Before?.Length ?? 0)?.Before?.Length ?? 0);
        if (new DriveInfo(Path.GetPathRoot(journal.Target.Root)!).AvailableFreeSpace < needed)
            throw new IOException("There is insufficient space to stage the next game file.");
        foreach (var operation in journal.Operations)
        {
            var path = ModPaths.Within(journal.Target.Root, operation.Relative);
            if (ModFileSnapshot.Read(path)?.Hash != operation.Before?.Hash)
                throw new InvalidDataException($"The file changed before deployment: {operation.Relative}");
            if (operation.Before != null)
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            advanceProgress?.Invoke();
        }
        token.ThrowIfCancellationRequested();
        ModInstallState.Write(JournalPath(folder), journal);
        checkpoint?.Invoke(ModTransactionStep.JournalWritten);
        try
        {
            foreach (var relative in journal.Directories)
            {
                token.ThrowIfCancellationRequested();
                var path = ModPaths.Within(journal.Target.Root, relative);
                ModPaths.RejectReparsePoints(path);
                if (ModPaths.Attributes(path) != null) throw new IOException($"A folder appeared after preview: {relative}");
                Directory.CreateDirectory(path);
                using var marker = new FileStream(Path.Combine(path, $".lg-mod-{journal.Id:N}"), FileMode.CreateNew, FileAccess.Write);
                marker.Flush(true);
            }
            RequireIdle(journal.Target);
            token.ThrowIfCancellationRequested();
            prepareSharedLayer?.Invoke();
            foreach (var operation in journal.Operations)
            {
                token.ThrowIfCancellationRequested();
                WriteGameFile(folder, journal, operation.Relative, operation.Before?.Hash, operation.After, operation.Payload,
                    forward: true, checkpoint);
                checkpoint?.Invoke(ModTransactionStep.FileApplied);
                advanceProgress?.Invoke();
            }
            foreach (var operation in journal.Operations)
            {
                var path = ModPaths.Within(journal.Target.Root, operation.Relative);
                if (ModFileSnapshot.Read(path)?.Hash != operation.After?.Hash) throw new IOException($"Could not verify {operation.Relative}.");
                advanceProgress?.Invoke();
            }
            RequireIdle(journal.Target);
            if (!BuildMatches(journal.Target)) throw new IOException("The game build changed during deployment.");
            VerifyDisplacedFiles(journal);
            ModInstallState.Write(StatePath(folder), new State(journal.Id, journal.Result));
            checkpoint?.Invoke(ModTransactionStep.Committed);
            Finish(folder, journal, committed: true);
        }
        catch (Exception error) when (ModFailure(error) || error is OperationCanceledException)
        {
            try { Recover(folder, journal); }
            catch (Exception recovery) when (ModFailure(recovery))
            {
                throw new IOException($"Recovery needed. {recovery.Message}", error);
            }
            throw;
        }
    }

    private static void Recover(string folder, Journal journal)
    {
        var committed = ReadState(folder)?.Transaction == journal.Id;
        if (!BuildMatches(journal.Target)) throw new IOException("The game build or drive changed. Keep the recovery record for review.");
        VerifyDisplacedFiles(journal);
        RequireIdle(journal.Target);
        if (!committed)
        {
            foreach (var operation in journal.Operations.Reverse())
            {
                var path = ModPaths.Within(journal.Target.Root, operation.Relative);
                var current = ModFileSnapshot.Read(path)?.Hash;
                if (current == operation.Before?.Hash) continue;
                if (current != operation.After?.Hash)
                    throw new IOException($"A file changed outside LG Launcher: {operation.Relative}");
                WriteGameFile(folder, journal, operation.Relative, current, operation.Before, operation.BeforeBackup, forward: false);
            }
        }
        Finish(folder, journal, committed);
    }

    private static void WriteGameFile(string folder, Journal journal, string relative, string? expected, ModFileSnapshot? after, string? source,
        bool forward, Action<ModTransactionStep>? checkpoint = null)
    {
        var destination = ModPaths.Within(journal.Target.Root, relative);
        ModPaths.RejectReparsePoints(destination);
        var stage = StagePath(journal);
        ModPaths.RejectReparsePoints(stage);
        var stagedFile = Path.Combine(stage, "staged");
        if (after != null || expected != null) Directory.CreateDirectory(stage);
        if (after != null)
        {
            File.Delete(stagedFile);
            CopyVerified(ModPaths.Within(folder, source!), stagedFile, after);
        }
        if (ModFileSnapshot.Read(destination)?.Hash != expected)
            throw new IOException($"The file changed while applying the plan: {relative}");
        checkpoint?.Invoke(ModTransactionStep.BeforeMutation);
        var displaced = DisplacedPath(journal, relative, forward);
        if (ModPaths.Attributes(displaced) != null) throw new IOException($"A previous file needs review: {displaced}");
        if (after == null) File.Move(destination, displaced);
        else
        {
            if (expected == null) File.Move(stagedFile, destination);
            else File.Replace(stagedFile, destination, displaced);
            after.ApplyMetadata(destination);
        }
        if (expected != null && ModFileSnapshot.Read(displaced)?.Hash != expected)
            throw new IOException($"A file changed during replacement. Its preserved copy is here: {displaced}");
    }

    private static string DisplacedPath(Journal journal, string relative, bool forward)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(relative);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        return Path.Combine(StagePath(journal), $"displaced-{(forward ? "apply" : "undo")}-{hash}");
    }

    private static void VerifyDisplacedFiles(Journal journal)
    {
        foreach (var operation in journal.Operations)
        {
            Verify(DisplacedPath(journal, operation.Relative, true), operation.Before?.Hash);
            Verify(DisplacedPath(journal, operation.Relative, false), operation.After?.Hash);
        }

        static void Verify(string path, string? expected)
        {
            if (ModFileSnapshot.Read(path) is { } current && current.Hash != expected)
                throw new IOException($"An external change was preserved here for review: {path}");
        }
    }

    private static void Finish(string folder, Journal journal, bool committed)
    {
        var stage = StagePath(journal);
        ModPaths.RejectReparsePoints(stage);
        if (Directory.Exists(stage))
        {
            VerifyDisplacedFiles(journal);
            foreach (var operation in journal.Operations)
            {
                File.Delete(DisplacedPath(journal, operation.Relative, true));
                File.Delete(DisplacedPath(journal, operation.Relative, false));
            }
            File.Delete(Path.Combine(stage, "staged"));
            Directory.Delete(stage);
        }
        foreach (var relative in journal.Directories.Reverse())
        {
            var path = ModPaths.Within(journal.Target.Root, relative);
            ModPaths.RejectReparsePoints(path);
            var marker = Path.Combine(path, $".lg-mod-{journal.Id:N}");
            if (!File.Exists(marker)) continue;
            File.Delete(marker);
            if (!committed) RemoveEmpty(journal.Target.Root, [relative]);
        }
        var installation = committed ? journal.Result : ReadState(folder)?.Installation;
        var retained = installation?.Files.Select(file => file.OriginalBackup).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in journal.Operations.SelectMany(operation => new[] { operation.BeforeBackup, operation.Payload }).OfType<string>().Distinct())
        {
            if (relative.StartsWith("saved-configuration/", StringComparison.Ordinal) || retained?.Contains(relative) == true) continue;
            var path = ModPaths.Within(folder, relative);
            ModPaths.RejectReparsePoints(path);
            File.Delete(path);
        }
        File.Delete(JournalPath(folder));
    }

    private static void RemoveEmpty(string root, IEnumerable<string> directories)
    {
        foreach (var relative in directories.OrderByDescending(path => path.Length))
        {
            var path = ModPaths.Within(root, relative);
            ModPaths.RejectReparsePoints(path);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
    }

    private bool Occupied(string root, Guid? except) => Folders().Any(folder =>
    {
        if (ReadState(folder)?.Installation is { } record && record.Id != except && Overlap(root, record.Target.Root)) return true;
        return ReadJournal(folder) is { } journal && Overlap(root, journal.Target.Root);

        static bool Overlap(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase) || Contains(left, right) || Contains(right, left);
    });

    private static bool BuildMatches(GameInstallTarget target)
    {
        try { return target.Matches(GameInstallTarget.Capture(target.Root, target.ExePath)); }
        catch (Exception error) when (ModFailure(error)) { return false; }
    }

    private static void RequireIdle(GameInstallTarget target)
    {
        var executable = target.ExePath;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (InvalidOperationException) { continue; }
                catch (System.ComponentModel.Win32Exception) { path = null; }
                if (path == null || path.Equals(executable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Close the game before changing its mod files.");
            }
        }
    }

    private static bool MatchesBackup(string folder, string? relative, ModFileSnapshot expected) =>
        relative != null && ModFileSnapshot.Read(ModPaths.Within(folder, relative))?.Hash == expected.Hash;

    private static void CopyVerified(string source, string destination, ModFileSnapshot expected)
    {
        ModPaths.RejectReparsePoints(source);
        ModPaths.RejectReparsePoints(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        if (ModFileSnapshot.Read(destination)?.Hash != expected.Hash || !File.Exists(destination))
            throw new IOException($"The backup or staged file failed verification: {Path.GetFileName(source)}");
    }

    private static void WritePayload(string path, byte[] bytes)
    {
        ModPaths.RejectReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }

    private async Task<T> Run<T>(Func<T> operation, CancellationToken token)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(operation, token).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    internal static bool ModFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException or
        BadImageFormatException or ArgumentException or System.Text.Json.JsonException or System.Security.SecurityException;

    private IEnumerable<string> Folders()
    {
        ModPaths.RejectReparsePoints(_root);
        return Directory.Exists(_root) ? Directory.EnumerateDirectories(_root).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)) : [];
    }

    private string Folder(Guid id) => Path.Combine(_root, id.ToString("N"));
    private static string StagePath(Journal journal) => Path.Combine(journal.Target.Root, $".lg-mod-stage-{journal.Id:N}");
    private static string StatePath(string folder) => Path.Combine(folder, "state.json");
    private static string JournalPath(string folder) => Path.Combine(folder, "journal.json");
    private static State? ReadState(string folder) => ModInstallState.Read<State>(StatePath(folder));
    private static Journal? ReadJournal(string folder) => ModInstallState.Read<Journal>(JournalPath(folder));
}
