using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace LGLauncher;

internal static class UpdateInstallVerification
{
    private const string ReportVariable = "LG_LAUNCHER_INSTALL_SMOKE_REPORT";
    private const string RejectDigestVariable = "LG_LAUNCHER_INSTALL_SMOKE_REJECT";
    private static bool IsTestExecutable =>
        String.Equals(Path.GetFileName(Environment.ProcessPath), "update-test.exe", StringComparison.OrdinalIgnoreCase);

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
            var package = new UpdateFeed.Package(new Uri("https://github.com/test-fixture"), digest, input.Length, UpdateFeed.CurrentVersion);
            using var client = new HttpClient(new BinaryFixture(fixture));
            stage = UpdateInstaller.CreateStage();
            try
            {
                UpdateInstaller.Download(package with { Sha256 = new string('0', 64) }, stage, null, CancellationToken.None, client)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("An invalid checksum was accepted.");
            }
            catch (InvalidDataException) { }
            UpdateInstaller.Discard(stage);
            stage = UpdateInstaller.CreateStage();
            UpdateInstaller.Download(package, stage, null, CancellationToken.None, client).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable(ReportVariable, report);
            if (Path.GetFileName(report) == "rollback.txt")
                Environment.SetEnvironmentVariable(RejectDigestVariable, digest);
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
        Path.GetFileName(report) is "install.txt" or "rollback.txt" &&
        String.Equals(Path.GetDirectoryName(Path.GetFullPath(report)), Path.GetDirectoryName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase);

    internal static int Complete(string id, int workerPid)
    {
        var report = Environment.GetEnvironmentVariable(ReportVariable)!;
        if (Environment.GetEnvironmentVariable(RejectDigestVariable) is { } reject)
        {
            using var running = File.OpenRead(Environment.ProcessPath!);
            if (Convert.ToHexString(SHA256.HashData(running)) == reject) return 3;
        }
        var message = UpdateInstaller.ReportRestart(id, workerPid);
        var folder = Path.GetDirectoryName(Environment.ProcessPath)!;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Directory.EnumerateDirectories(folder, ".lg-launcher-update-*").Any() && DateTime.UtcNow < deadline)
            Thread.Sleep(40);
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
