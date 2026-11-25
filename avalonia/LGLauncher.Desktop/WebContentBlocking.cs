using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32.SafeHandles;

namespace LGLauncher;

[SupportedOSPlatform("windows")]
internal static class WebContentBlocking
{
    private const string Version = "2026.1124.1811";
    private const string Archive = "LGLauncher.uBOLite.edge.zip";

    public static Task<EnvironmentHandle> CreateEnvironment(string runtimeDirectory)
    {
        var completion = new EnvironmentCompletion();
        var result = CreateCoreWebView2EnvironmentWithOptions(null, runtimeDirectory, new EnvironmentOptions(), completion);
        Marshal.ThrowExceptionForHR(result);
        return completion.Task;
    }

    public static async Task SetEnabled(CoreWebView2Profile profile, string webDirectory, bool enabled)
    {
        var root = Path.Combine(webDirectory, "uBO-Lite");
        if (!enabled && !Directory.Exists(root)) return;

        var folder = Path.Combine(root, Version);
        if (enabled) await Task.Run(() => PrepareExtension(root, folder));

        var installed = (await profile.GetBrowserExtensionsAsync()).ToDictionary(extension => extension.Id, StringComparer.Ordinal);
        var bundled = installed.GetValueOrDefault(ExtensionId(folder));
        if (enabled) bundled ??= await profile.AddBrowserExtensionAsync(folder);
        if (bundled != null && bundled.IsEnabled != enabled) await bundled.EnableAsync(enabled);

        foreach (var previous in Directory.EnumerateDirectories(root))
        {
            if (previous == folder || !System.Version.TryParse(Path.GetFileName(previous), out _)) continue;

            if (!installed.TryGetValue(ExtensionId(previous), out var extension)) continue;
            if (enabled) await extension.RemoveAsync();
            else if (extension.IsEnabled) await extension.EnableAsync(false);
        }
    }

    private static void PrepareExtension(string root, string folder)
    {
        if (File.Exists(Path.Combine(folder, "manifest.json"))) return;

        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, Guid.NewGuid().ToString("N"));
        using var archive = typeof(WebContentBlocking).Assembly.GetManifestResourceStream(Archive)
            ?? throw new InvalidOperationException("The bundled uBO Lite archive is missing.");
        try
        {
            ZipFile.ExtractToDirectory(archive, staging);
            try
            {
                Directory.Move(staging, folder);
            }
            catch (IOException) when (File.Exists(Path.Combine(folder, "manifest.json")))
            {
                // Another launcher instance installed the same version.
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static string ExtensionId(string folder)
    {
        // Chromium hashes the Windows wide-character path with an uppercase drive letter.
        if (folder.Length > 1 && folder[1] == ':') folder = Char.ToUpperInvariant(folder[0]) + folder[1..];
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(folder));
        return String.Concat(Convert.ToHexString(hash.AsSpan(0, 16)).Select(digit =>
            (char)('a' + (digit <= '9' ? digit - '0' : digit - 'A' + 10))));
    }

    internal sealed class EnvironmentHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public EnvironmentHandle(IntPtr environment) : base(ownsHandle: true)
        {
            if (environment == IntPtr.Zero) throw new InvalidOperationException("WebView2 returned no environment.");
            Marshal.AddRef(environment);
            SetHandle(environment);
        }

        protected override bool ReleaseHandle()
        {
            Marshal.Release(handle);
            return true;
        }
    }

    // Native WebView2 interfaces used to hand Avalonia an extension-enabled COM environment.
    [ComImport, Guid("2FDE08A8-1E9A-4766-8C05-95A9CEB9D1C5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICoreWebView2EnvironmentOptions
    {
        string? AdditionalBrowserArguments { [return: MarshalAs(UnmanagedType.LPWStr)] get; [param: MarshalAs(UnmanagedType.LPWStr)] set; }
        string? Language { [return: MarshalAs(UnmanagedType.LPWStr)] get; [param: MarshalAs(UnmanagedType.LPWStr)] set; }
        string? TargetCompatibleBrowserVersion { [return: MarshalAs(UnmanagedType.LPWStr)] get; [param: MarshalAs(UnmanagedType.LPWStr)] set; }
        int AllowSingleSignOnUsingOSPrimaryAccount { get; set; }
    }

    [ComImport, Guid("57D29CC3-C84F-42A0-B0E2-EFFBD5E179DE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICoreWebView2EnvironmentOptions6
    {
        int AreBrowserExtensionsEnabled { get; set; }
    }

    [ComImport, Guid("4E8A3389-C9D8-4BD2-B6B5-124FEE6CC14D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
    {
        void Invoke(int errorCode, IntPtr environment);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EnvironmentOptions : ICoreWebView2EnvironmentOptions, ICoreWebView2EnvironmentOptions6
    {
        public string? AdditionalBrowserArguments { get; set; }
        public string? Language { get; set; }
        public string? TargetCompatibleBrowserVersion { get; set; } = new CoreWebView2EnvironmentOptions().TargetCompatibleBrowserVersion;
        public int AllowSingleSignOnUsingOSPrimaryAccount { get; set; }
        public int AreBrowserExtensionsEnabled { get; set; } = 1;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EnvironmentCompletion : ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
    {
        private readonly TaskCompletionSource<EnvironmentHandle> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EnvironmentHandle> Task => _completion.Task;

        public void Invoke(int errorCode, IntPtr environment)
        {
            try
            {
                Marshal.ThrowExceptionForHR(errorCode);
                _completion.SetResult(new EnvironmentHandle(environment));
            }
            catch (Exception error)
            {
                _completion.SetException(error);
            }
        }
    }

    [DllImport("WebView2Loader.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int CreateCoreWebView2EnvironmentWithOptions(string? browserExecutableFolder, string userDataFolder,
        ICoreWebView2EnvironmentOptions options, ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler completion);
}
