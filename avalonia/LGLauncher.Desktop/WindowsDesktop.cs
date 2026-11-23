using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Win32;

namespace LGLauncher;

internal static class WindowsDesktop
{
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "LG Launcher";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string? GetStartupCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        return key?.GetValue(StartupName) as string;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void SetStartupCommand(string? command)
    {
        if (command == null)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
            existing?.DeleteValue(StartupName, throwOnMissingValue: false);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
        key.SetValue(StartupName, command, RegistryValueKind.String);
    }

    public static string LauncherStartupCommand()
    {
        var executable = Environment.ProcessPath;
        if (executable == null || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open the LG Launcher executable to enable Windows startup.");
        var command = "\"" + executable + "\"";
        if (command.Length > 260) throw new InvalidOperationException("Move LG Launcher.exe to a shorter path to enable Windows startup.");
        return command;
    }

    public static bool AllowAnimations()
    {
        const uint getClientAreaAnimation = 0x1042;
        return !OperatingSystem.IsWindows() || !SystemParametersInfo(getClientAreaAnimation, 0, out var enabled, 0) || enabled != 0;
    }

    public static void ApplySystemCorners(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle) return;
        const int windowCornerPreference = 33;
        var roundWhenAppropriate = 2;
        DwmSetWindowAttribute(handle, windowCornerPreference, ref roundWhenAppropriate, sizeof(int));
    }

    public static void SetWindowMotion(Window window, bool reduceMotion)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle) return;
        const int transitionsForceDisabled = 3;
        var disabled = reduceMotion ? 1 : 0;
        DwmSetWindowAttribute(handle, transitionsForceDisabled, ref disabled, sizeof(int));
    }

    public static void UseCompositorFrame(Window window, double titleBarHeight)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Keep the native caption style for DWM while the app draws its own chrome.
        window.Resources[typeof(WindowDrawnDecorations)] = Application.Current!.FindResource("LG.CustomWindowDecorations");
        window.WindowDecorations = WindowDecorations.Full;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = titleBarHeight;
    }

    public static void TrackFramePointer(Window window, Action<PixelPoint> moved, Action left)
    {
        if (!OperatingSystem.IsWindows()) return;
        const uint nonClientMouseMove = 0x00A0, nonClientMouseLeave = 0x02A2;
        const uint trackNonClientLeave = 0x12;
        var tracking = false;
        Win32Properties.AddWndProcHookCallback(window,
            (IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == nonClientMouseMove)
                {
                    if (!tracking)
                    {
                        var request = new MouseTracking
                        {
                            Size = (uint)Marshal.SizeOf<MouseTracking>(), Flags = trackNonClientLeave, Window = handle
                        };
                        tracking = TrackMouseEvent(ref request);
                    }
                    var coordinates = lParam.ToInt64();
                    moved(new PixelPoint((short)coordinates, (short)(coordinates >> 16)));
                }
                else if (message == nonClientMouseLeave)
                {
                    tracking = false;
                    left();
                }
                return IntPtr.Zero;
            });
    }

    public static bool IsPointerWithin(Window window) =>
        OperatingSystem.IsWindows() && GetCursorPos(out var cursor)
            ? new Rect(window.ClientSize).Contains(window.PointToClient(new PixelPoint(cursor.X, cursor.Y)))
            : window.IsPointerOver;

    internal static bool HasNativeCaption(Window window)
    {
        const int styleIndex = -16;
        const int caption = 0x00C00000;
        return OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is { } handle &&
            (GetWindowLong(handle, styleIndex) & caption) == caption;
    }

    public static Bitmap? ExecutableIcon(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        if (ExtractIconEx(path, 0, out var icon, IntPtr.Zero, 1) == 0 || icon == IntPtr.Zero) return null;
        const int size = 32;
        const int stride = size * 4;
        try
        {
            var dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = size, Height = -size, Planes = 1, BitCount = 32 };
                var dib = CreateDIBSection(dc, ref info, 0, out var pixels, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return null;
                var previous = SelectObject(dc, dib);
                try
                {
                    if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 3)) return null;
                    var rgba = new byte[stride * size];
                    Marshal.Copy(pixels, rgba, 0, rgba.Length);
                    var hasAlpha = false;
                    for (var i = 3; i < rgba.Length; i += 4) hasAlpha |= rgba[i] != 0;
                    if (!hasAlpha)
                    {
                        if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 1)) return null;
                        var mask = new byte[rgba.Length];
                        Marshal.Copy(pixels, mask, 0, mask.Length);
                        for (var i = 0; i < rgba.Length; i += 4) rgba[i + 3] = (byte)(255 - mask[i]);
                        Marshal.Copy(rgba, 0, pixels, rgba.Length);
                    }
                    return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pixels, new PixelSize(size, size), new Vector(96, 96), stride);
                }
                finally
                {
                    SelectObject(dc, previous);
                    DeleteObject(dib);
                }
            }
            finally { DeleteDC(dc); }
        }
        finally { DestroyIcon(icon); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking
    {
        public uint Size, Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref MouseTracking track);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPosition position);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out IntPtr largeIcon, IntPtr smallIcons, uint count);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
