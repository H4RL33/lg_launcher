using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;

namespace LGLauncher;

internal static class UpdateInstallVerification
{
    private const string ReportVariable = "LG_LAUNCHER_INSTALL_SMOKE_REPORT";
    private const string RejectDigestVariable = "LG_LAUNCHER_INSTALL_SMOKE_REJECT";
    private const string SetupFixturePrefix = "LGLauncher.SetupUpgrade.";
    internal static string? SetupFixtureAppId { get; } = GetSetupFixtureAppId();
    private static bool IsSetupFixture => SetupFixtureAppId != null &&
        String.Equals(Path.GetFileName(Environment.ProcessPath), "LG-Launcher.exe", StringComparison.OrdinalIgnoreCase);
    private static bool IsTestExecutable => IsSetupFixture ||
        String.Equals(Path.GetFileName(Environment.ProcessPath), "update-test.exe", StringComparison.OrdinalIgnoreCase);

    private static string? GetSetupFixtureAppId()
    {
        var appId = typeof(UpdateInstallVerification).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SetupFixtureAppId")?.Value;
        return appId != null && appId.StartsWith(SetupFixturePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(appId[SetupFixturePrefix.Length..], "N", out _) ? appId : null;
    }

    internal static bool IsRunning => IsTestExecutable &&
        Environment.GetEnvironmentVariable(ReportVariable) is { } report && IsReportPath(report);

    internal static int Begin(string fixture, string report)
    {
        var folder = Path.GetDirectoryName(Environment.ProcessPath);
        if (!IsTestExecutable || Path.GetFileName(fixture) != "fixture.exe" ||
            !String.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), folder, StringComparison.OrdinalIgnoreCase) ||
            !IsReportPath(report)) return 1;
        UpdateInstaller.Staging? stage = null;
        try
        {
            using var input = File.OpenRead(fixture);
            var digest = Convert.ToHexString(SHA256.HashData(input));
            var version = Version.Parse(FileVersionInfo.GetVersionInfo(fixture).ProductVersion!.Split('+', 2)[0]);
            var package = new UpdateFeed.Package(new Uri("https://github.com/test-fixture"), digest, input.Length, version);
            using var client = new HttpClient(new BinaryFixture(fixture));
            stage = UpdateInstaller.CreateStage(IsSetupFixture);
            try
            {
                UpdateInstaller.Download(package with { Sha256 = new string('0', 64) }, stage, null, CancellationToken.None, client)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("An invalid checksum was accepted.");
            }
            catch (InvalidDataException) { }
            UpdateInstaller.Discard(stage);
            stage = UpdateInstaller.CreateStage(IsSetupFixture);
            UpdateInstaller.Download(package, stage, null, CancellationToken.None, client).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable(ReportVariable, report);
            if (Path.GetFileName(report) is "rollback.txt" or "activation.txt" or "timeout.txt")
                Environment.SetEnvironmentVariable(RejectDigestVariable, digest);
            if (Path.GetFileName(report) == "missing-message.txt")
                Directory.CreateDirectory(stage.MessagePath);
            UpdateInstaller.StartWorker(stage, package, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            if (stage != null) UpdateInstaller.Discard(stage);
            File.WriteAllText(report, error.ToString());
            return 1;
        }
    }

    private static bool IsReportPath(string report) =>
        (IsSetupFixture ? Path.GetFileName(report) == "setup.txt" :
            Path.GetFileName(report) is "install.txt" or "rollback.txt" or "activation.txt" or "timeout.txt" or "missing-message.txt") &&
        String.Equals(Path.GetDirectoryName(Path.GetFullPath(report)), Path.GetDirectoryName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase);

    internal static int Complete(string id, int workerPid, bool setup)
    {
        if (setup != IsSetupFixture) return 1;
        var report = Environment.GetEnvironmentVariable(ReportVariable)!;
        if (Environment.GetEnvironmentVariable(RejectDigestVariable) is { } reject)
        {
            using var running = File.OpenRead(Environment.ProcessPath!);
            if (Convert.ToHexString(SHA256.HashData(running)) == reject)
            {
                var scenario = Path.GetFileName(report);
                if (scenario == "timeout.txt") Thread.Sleep(TimeSpan.FromMinutes(1));
                return scenario == "activation.txt" ? 2 : 3;
            }
        }
        var stage = UpdateInstaller.GetStage(Environment.ProcessPath!, id, setup);
        var missingMessage = Path.GetFileName(report) == "missing-message.txt";
        var message = UpdateInstaller.ReportRestart(id, workerPid, setup);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Directory.Exists(stage.Folder) && DateTime.UtcNow < deadline)
            Thread.Sleep(40);
        if (missingMessage && message == null && !Directory.Exists(stage.Folder))
        {
            File.WriteAllText(report, "Restart was acknowledged without a result message.");
            return 0;
        }
        File.WriteAllText(report, message ?? "Restart was not acknowledged.");
        return message == null ? 1 : 0;
    }

    private sealed class BinaryFixture(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://release-assets.githubusercontent.com/test-fixture") }
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) });
        }
    }
}
