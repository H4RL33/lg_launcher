using System.Diagnostics;
using Avalonia;

namespace LGLauncher;

internal static class Program
{
    internal const string InstanceMutexName = @"Local\LGLauncher.Desktop";
    internal static string DataDirectory { get; private set; } = "";
    internal static string? VerificationMode { get; private set; }
    internal static string? PreviewPath { get; private set; }
    internal static EventWaitHandle? ActivationSignal { get; private set; }
    internal static bool IsUpdateRestart { get; private set; }
    private static string? _restartId;
    private static int _updateWorkerPid;
    private static bool _setupUpdateRestart;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--verify-mods", var fixtureDirectory])
            return ModVerification.Run(fixtureDirectory);
        if (args is ["--verify-mods", var assetsFixture, var archives])
            return ModVerification.Run(assetsFixture, archives);
        if (args is ["--apply-update" or "--apply-setup-update", var transaction, var name, var parent, var started, var digest, var length, var version])
            return Int32.TryParse(parent, out var pid) && Int64.TryParse(started, out var ticks) &&
                Int64.TryParse(length, out var bytes) && Version.TryParse(version, out var release)
                ? UpdateInstaller.Apply(transaction, name, pid, ticks, digest, bytes, release, args[0] == "--apply-setup-update") : 1;
        if (args is ["--verify-update-install", var candidate, var result])
            return UpdateInstallVerification.Begin(candidate, result);
        if (args is ["--update-restart" or "--setup-update-restart", var restart, var worker] && Guid.TryParseExact(restart, "N", out _) &&
            Int32.TryParse(worker, out _updateWorkerPid))
        {
            _restartId = restart;
            _setupUpdateRestart = args[0] == "--setup-update-restart";
            IsUpdateRestart = true;
            if (UpdateInstallVerification.IsRunning) return UpdateInstallVerification.Complete(restart, _updateWorkerPid, _setupUpdateRestart);
        }
        if (args is ["--verify-migration" or "--verify-existing" or "--verify-mods-ui" or "--verify-setup", var data, var preview])
        {
            VerificationMode = args[0];
            DataDirectory = Path.GetFullPath(data);
            PreviewPath = Path.GetFullPath(preview);
            return Run();
        }

        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LG Launcher");
        using var singleInstance = new Mutex(false, OperatingSystem.IsWindows() ? InstanceMutexName : "LGLauncher.Desktop");
        using var activation = OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LGLauncher.ShowWindow") : null;
        var restarting = args is ["--restart-launcher"];
        bool acquired;
        try { acquired = singleInstance.WaitOne(restarting ? TimeSpan.FromSeconds(30) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            if (activation == null) return Run("LG Launcher is already open.");
            activation.Set();
            return _restartId == null ? 0 : 2;
        }
        ActivationSignal = activation;
        try { return Run(); }
        finally { singleInstance.ReleaseMutex(); }
    }

    internal static void StartRestart()
    {
        if (VerificationMode != null) throw new InvalidOperationException("Restart is unavailable during verification.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The launcher executable could not be located.");
        var start = new ProcessStartInfo(executable);
        if (String.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--restart-launcher");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The launcher could not be started.");
    }

    internal static string? CompleteUpdateRestart()
    {
        var id = _restartId;
        _restartId = null;
        return id == null ? null : UpdateInstaller.ReportRestart(id, _updateWorkerPid, _setupUpdateRestart);
    }

    private static int Run(string? notice = null)
    {
        (LibraryStore Store, Library Library)? profile = null;
        if (notice == null)
        {
            try
            {
                var store = new LibraryStore(DataDirectory);
                profile = (store, store.Load());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                notice = error.Message;
            }
        }

        var builder = AppBuilder.Configure(() => new App { StartupProfile = profile, StartupNotice = notice }).UsePlatformDetect();
        if (OperatingSystem.IsWindows() && profile?.Library.Preferences.DisableHardwareRendering == true)
        {
            builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            });
        }
        return builder.StartWithClassicDesktopLifetime([]);
    }
}
