using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace LGLauncher;

internal static class UpdateInstaller
{
    private const string FolderPrefix = ".lg-launcher-update-";
    private const string SetupFolderPrefix = "lg-launcher-setup-update-";
    private const string WorkerName = "update-worker.exe";
    private const string PackageName = "download.exe";
    private const string SetupPackageName = "LG-Launcher-Setup.exe";
    private const string BackupName = "previous.exe";
    private const string MessageName = "result.txt";
    private const string SetupAppId = "LGLauncher.Windows.Stable";
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Downloads = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly bool IsReleaseBundle = typeof(UpdateInstaller).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(attribute => attribute.Key == "SupportsInAppUpdates" && attribute.Value == "true");

    internal static bool IsSetupInstallation { get; } = OperatingSystem.IsWindows() && HasSetupMarker(Environment.ProcessPath);
    internal static bool IsUserSetupInstallation { get; } = IsSetupInstallation && HasUserSetupMarker(Environment.ProcessPath);

    internal sealed record Staging(string Target, string Folder, string Id, bool Setup)
    {
        internal string DownloadPath => Path.Combine(Folder, Setup ? SetupPackageName : PackageName);
        internal string WorkerPath => Path.Combine(Folder, WorkerName);
        internal string BackupPath => Path.Combine(Folder, BackupName);
        internal string MessagePath => Path.Combine(Folder, MessageName);
    }

    // A bundled assembly has an empty Location. A DLL run through dotnet must never replace its host.
#pragma warning disable IL3000
    private static bool IsWindowsReleaseBundle => OperatingSystem.IsWindows() && IsReleaseBundle &&
        String.IsNullOrEmpty(typeof(UpdateInstaller).Assembly.Location) && Environment.ProcessPath != null;
#pragma warning restore IL3000

    internal static bool IsSupported => IsWindowsReleaseBundle && !IsSetupInstallation;
    internal static bool SupportsSetupUpdates => IsWindowsReleaseBundle && IsUserSetupInstallation;
    internal static bool SupportsAutomaticUpdates => IsSupported || SupportsSetupUpdates;

    internal static Staging CreateStage(bool setup = false)
    {
        if (!(setup ? SupportsSetupUpdates : IsSupported))
            throw new InvalidOperationException("In-app updates require the matching Windows release executable.");
        var target = Environment.ProcessPath!;
        if (setup) RequireRegisteredSetupDirectory(target);
        var id = Guid.NewGuid().ToString("N");
        var stage = GetStage(target, id, setup);
        Directory.CreateDirectory(stage.Folder);
        return stage;
    }

    internal static async Task Download(UpdateFeed.Package package, Staging stage, IProgress<int>? progress,
        CancellationToken token, HttpClient? client = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var downloadToken = deadline.Token;
        var address = package.Url;
        using var response = await Request(address, client ?? Downloads, downloadToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("The release download was unavailable.", null, response.StatusCode);
        await using (var output = new FileStream(stage.DownloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            await VerifiedDownload.CopyAsync(response.Content, output, package.Length, package.Sha256, progress, downloadToken).ConfigureAwait(false);
            await output.FlushAsync(downloadToken).ConfigureAwait(false);
        }
        RequireVersion(stage.DownloadPath, package.Version);
    }

    private static async Task<HttpResponseMessage> Request(Uri address, HttpClient client, CancellationToken token)
    {
        for (var hop = 0; hop < 3; hop++)
        {
            var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                var location = response.Headers.Location;
                if (location == null) break;
                var next = location.IsAbsoluteUri ? location : new Uri(address, location);
                if (next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                    next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com")) break;
                address = next;
            }
        }
        throw new HttpRequestException("The release download redirected to an unexpected address.");
    }

    internal static async Task StartWorker(Staging stage, UpdateFeed.Package package, CancellationToken token)
    {
        File.Copy(stage.Target, stage.WorkerPath);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(stage.WorkerPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { stage.Setup ? "--apply-setup-update" : "--apply-update", stage.Id, Path.GetFileName(stage.Target),
            current.Id.ToString(CultureInfo.InvariantCulture), current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            package.Sha256, package.Length.ToString(CultureInfo.InvariantCulture), package.Version.ToString() })
            start.ArgumentList.Add(argument);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("The update helper could not be started.");
        using var handoff = CancellationTokenSource.CreateLinkedTokenSource(token);
        handoff.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            if (await worker.StandardOutput.ReadLineAsync(handoff.Token).ConfigureAwait(false) != "ready")
                throw new IOException("The update helper could not verify the staged update.");
        }
        catch
        {
            try
            {
                if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(3000); }
            }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    internal static int Apply(string id, string fileName, int pid, long startTime, string digest, long length, Version version, bool setup)
    {
        if (!IsSupported || !Guid.TryParseExact(id, "N", out _) ||
            Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !VerifiedDownload.IsSha256(digest) || length <= 0 || length > UpdateFeed.MaximumPackageSize) return 1;
        var comparison = UpdateFeed.Normalize(version).CompareTo(UpdateFeed.Normalize(UpdateFeed.CurrentVersion));
        if (comparison < 0 || (setup && comparison == 0)) return 1;

        Staging stage;
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != startTime || parent.MainModule?.FileName is not { } target ||
                !SamePath(Path.GetFileName(target), fileName)) return 1;
            if (setup ? !HasUserSetupMarker(target) : HasSetupMarker(target)) return 1;
            if (setup) RequireRegisteredSetupDirectory(target);
            stage = GetStage(target, id, setup);
            if (!SamePath(Environment.ProcessPath, stage.WorkerPath) || !RegularDirectory(stage.Folder)) return 1;
            VerifyStagedPackage(stage.DownloadPath, digest, length, version);
            Console.WriteLine("ready");
            Console.Out.Flush();
            if (!parent.WaitForExit(30000)) return 1;
        }
        catch (Exception error) when (ExpectedFailure(error)) { return 1; }

        if (setup) return ApplySetup(stage, digest, length, version);

        try
        {
            VerifyStagedPackage(stage.DownloadPath, digest, length, version);
            Replace(stage.Target, stage.DownloadPath, stage.BackupPath);
            WriteResult(stage, $"LG Launcher updated to {version}.");
            if (RestartAndWait(stage)) return 0;
        }
        catch (Exception error) when (ExpectedFailure(error)) { }

        try
        {
            if (File.Exists(stage.BackupPath))
            {
                if (File.Exists(stage.Target)) Replace(stage.Target, stage.BackupPath, null);
                else File.Move(stage.BackupPath, stage.Target);
            }
            WriteResult(stage, "The update could not be installed. The previous launcher was reopened.");
            using var restored = StartLauncher(stage);
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            // Keep the previous executable in the staging folder if Windows still holds a file open.
        }
        return 1;
    }

    private static int ApplySetup(Staging stage, string digest, long length, Version version)
    {
        string message;
        var installed = false;
        try
        {
            var exitCode = RunSetup(stage, digest, length, version);
            if (exitCode == 2) message = "Windows Setup was cancelled.";
            else if (exitCode != 0) message = $"Windows Setup could not install the update (exit code {exitCode}).";
            else
            {
                RequireVersion(stage.Target, version);
                installed = true;
                message = $"LG Launcher updated to {version}.";
            }
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            message = "Could not install the Windows Setup update. " + error.Message;
        }

        try
        {
            WriteResult(stage, message);
            return RestartAndWait(stage) && installed ? 0 : 1;
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            Discard(stage);
            return 1;
        }
    }

    private static int RunSetup(Staging stage, string digest, long length, Version version)
    {
        if (!RegularDirectory(stage.Folder)) throw new InvalidDataException("The staged update folder is not a regular directory.");
        // Keep the verified file open for reading only until Setup has finished.
        using var payload = File.OpenRead(stage.DownloadPath);
        VerifyStagedPackage(stage.DownloadPath, digest, length, version);
        var directory = RequireRegisteredSetupDirectory(stage.Target);
        var start = new ProcessStartInfo(stage.DownloadPath)
        {
            UseShellExecute = false,
            WorkingDirectory = stage.Folder
        };
        // Setup shows progress and skips its post-install launch. The helper restarts
        // the launcher under the same Windows account that requested the update.
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/SP-");
        start.ArgumentList.Add("/DIR=" + directory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Setup could not be started.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void Replace(string target, string source, string? backup)
    {
        using var gate = new Mutex(false, Program.InstanceMutexName);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(6)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("LG Launcher is still open.");
        try { File.Replace(source, target, backup); }
        finally { gate.ReleaseMutex(); }
    }

    private static bool RestartAndWait(Staging stage)
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent(stage.Id));
        using var child = StartLauncher(stage);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < RestartTimeout)
        {
            if (ready.WaitOne(250)) return true;
            if (child.HasExited) return ready.WaitOne(0);
        }
        if (ready.WaitOne(0)) return true;

        // Release the portable executable before rollback. Windows Setup owns its installed files.
        if (!stage.Setup && !child.HasExited)
        {
            try { child.Kill(); }
            catch (InvalidOperationException) when (child.HasExited) { }
            if (!child.WaitForExit(5000)) throw new IOException("The replacement launcher did not stop for rollback.");
        }
        return false;
    }

    private static Process StartLauncher(Staging stage)
    {
        var start = new ProcessStartInfo(stage.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(stage.Target)! };
        start.ArgumentList.Add(stage.Setup ? "--setup-update-restart" : "--update-restart");
        start.ArgumentList.Add(stage.Id);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("The updated launcher could not be started.");
    }

    internal static string? ReportRestart(string id, int workerPid, bool setup = false)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(id, "N", out _) || Environment.ProcessPath is not { } target) return null;
        var stage = GetStage(target, id, setup);
        if (!RegularDirectory(stage.Folder)) return null;
        try
        {
            var message = ReadResult(stage);
            if (EventWaitHandle.TryOpenExisting(ReadyEvent(id), out var ready))
            {
                using (ready) ready.Set();
            }
            _ = Task.Run(() =>
            {
                try
                {
                    using var worker = Process.GetProcessById(workerPid);
                    worker.WaitForExit(15000);
                }
                catch (Exception error) when (ExpectedFailure(error)) { }

                // Readiness lets the helper exit immediately. Windows may briefly retain its image mapping.
                for (var attempt = 0; attempt < 12 && Directory.Exists(stage.Folder); attempt++)
                {
                    Discard(stage);
                    if (Directory.Exists(stage.Folder)) Thread.Sleep(100);
                }
            });
            return message;
        }
        catch (Exception error) when (ExpectedFailure(error)) { return null; }
    }

    private static void WriteResult(Staging stage, string message)
    {
        try { File.WriteAllText(stage.MessagePath, message); }
        catch (Exception error) when (ExpectedFailure(error)) { }
    }

    private static string? ReadResult(Staging stage)
    {
        try { return File.Exists(stage.MessagePath) ? File.ReadAllText(stage.MessagePath) : null; }
        catch (Exception error) when (ExpectedFailure(error)) { return null; }
    }

    internal static void Discard(Staging stage)
    {
        try
        {
            if (!RegularDirectory(stage.Folder)) return;
            foreach (var path in new[] { stage.DownloadPath, stage.BackupPath, stage.WorkerPath })
                File.Delete(path);
            if (RegularDirectory(stage.MessagePath)) Directory.Delete(stage.MessagePath);
            else File.Delete(stage.MessagePath);
            Directory.Delete(stage.Folder);
        }
        catch (Exception error) when (ExpectedFailure(error)) { }
    }

    private static bool RegularDirectory(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    internal static Staging GetStage(string target, string id, bool setup)
    {
        var root = setup ? Path.GetTempPath() : Path.GetDirectoryName(target)!;
        var prefix = setup ? SetupFolderPrefix : FolderPrefix;
        return new(target, Path.Combine(root, prefix + id), id, setup);
    }

    private static bool HasSetupMarker(string? executable) =>
        Path.GetDirectoryName(executable) is { } folder && File.Exists(Path.Combine(folder, "LG-Launcher.install.ini"));

    private static string RequireRegisteredSetupDirectory(string target)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows Setup updates require Windows.");
        var directory = Path.GetDirectoryName(target)!;
        var appId = UpdateInstallVerification.SetupFixtureAppId ?? SetupAppId;
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var registration = currentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{appId}_is1");
        if (registration?.GetValue("InstallLocation") is not string registered || !Path.IsPathFullyQualified(registered) ||
            !SamePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)), directory))
            throw new IOException("Open LG Launcher from its registered Windows Setup folder to install this update.");
        return directory;
    }

    private static bool HasUserSetupMarker(string? executable)
    {
        if (Path.GetDirectoryName(executable) is not { } folder) return false;
        try
        {
            using var marker = File.OpenText(Path.Combine(folder, "LG-Launcher.install.ini"));
            return String.Equals(marker.ReadLine(), "[Installation]", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(marker.ReadLine(), "Scope=User", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (ExpectedFailure(error)) { return false; }
    }

    private static string ReadyEvent(string id) => @"Local\LGLauncher.Update." + id;

    private static bool SamePath(string? first, string second) =>
        String.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void RequireVersion(string path, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var version = info.ProductVersion?.Split('+', 2)[0];
        if (!Version.TryParse(version, out var actual) || UpdateFeed.Normalize(actual) != UpdateFeed.Normalize(expected))
            throw new InvalidDataException("The update executable does not match the release version.");
    }

    private static void VerifyStagedPackage(string path, string digest, long length, Version version)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The staged update is not a regular file.");
        using var binary = File.OpenRead(path);
        if (binary.Length != length || !VerifiedDownload.MatchesSha256(SHA256.HashData(binary), digest))
            throw new InvalidDataException("The staged update did not match the official checksum.");
        RequireVersion(path, version);
    }

    internal static bool ExpectedFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or
            ArgumentException or System.Security.SecurityException;
}
