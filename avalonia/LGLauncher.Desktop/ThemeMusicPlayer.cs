using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace LGLauncher;

internal sealed class ThemeMusicPlayer
{
    private const uint FreeThreadedCallbacks = 1;
    private const float PlaybackVolume = .3f;
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(350);
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private NativeMediaPlayer? _player;
    private PlaybackEvents? _events;
    private long _fadeStarted;
    private float _fadeStartVolume;
    private float _fadeTargetVolume;

    internal bool IsPlaying => _player != null;
    internal float Volume { get; private set; }

    internal ThemeMusicPlayer() => _fadeTimer.Tick += (_, _) => UpdateFade();

    internal bool Play(string path)
    {
        Stop();
        if (!StartPlayback(path)) return false;
        FadeTo(PlaybackVolume);
        return true;
    }

    internal void FadeOut()
    {
        if (!IsPlaying || (_fadeTimer.IsEnabled && _fadeTargetVolume == 0)) return;
        FadeTo(0);
    }

    internal void Stop()
    {
        _fadeTimer.Stop();
        Volume = 0;
        ReleasePlayback();
    }

    private void FadeTo(float volume)
    {
        _fadeStarted = Stopwatch.GetTimestamp();
        _fadeStartVolume = Volume;
        _fadeTargetVolume = volume;
        _fadeTimer.Start();
    }

    private void UpdateFade()
    {
        if (_player is not { } player) return;

        var progress = (float)Math.Min(1, Stopwatch.GetElapsedTime(_fadeStarted) / FadeDuration);
        var volume = _fadeStartVolume + (_fadeTargetVolume - _fadeStartVolume) * progress;
        if (player.SetVolume(volume) < 0)
        {
            Stop();
            return;
        }

        Volume = volume;
        if (progress < 1) return;
        if (_fadeTargetVolume == 0) Stop();
        else _fadeTimer.Stop();
    }

    private bool StartPlayback(string path)
    {
        ReleasePlayback();
        _events = new PlaybackEvents((sender, failed) => Dispatcher.UIThread.Post(() =>
        {
            if (_events != sender) return;
            // Repeat at the current volume while preserving any fade already in progress.
            if (failed || !StartPlayback(path)) Stop();
        }));
        _player = Open(path, _events);
        if (_player is { } player && player.SetVolume(Volume) >= 0 && player.Play() >= 0) return true;
        ReleasePlayback();
        return false;
    }

    private void ReleasePlayback()
    {
        _events = null;
        var player = _player;
        _player = null;
        if (player != null) Close(player);
    }

    internal static bool CanOpen(string path)
    {
        if (Open(path, null) is not { } player) return false;
        Close(player);
        return true;
    }

    private static NativeMediaPlayer? Open(string path, NativeMediaEvents? callback)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            return MFPCreateMediaPlayer(path, false, FreeThreadedCallbacks, callback, IntPtr.Zero, out var player) >= 0 ? player : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or COMException) { return null; }
    }

    private static void Close(NativeMediaPlayer player)
    {
        player.Shutdown();
        if (OperatingSystem.IsWindows()) Marshal.FinalReleaseComObject(player);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class PlaybackEvents(Action<PlaybackEvents, bool> completed) : NativeMediaEvents
    {
        public void OnMediaPlayerEvent(IntPtr header)
        {
            const int errorEvent = 10;
            const int endedEvent = 11;
            var kind = Marshal.ReadInt32(header);
            var result = Marshal.ReadInt32(header, sizeof(int));
            if (kind == errorEvent || result < 0) completed(this, true);
            else if (kind == endedEvent) completed(this, false);
        }
    }

    [DllImport("mfplay.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFPCreateMediaPlayer(string path, [MarshalAs(UnmanagedType.Bool)] bool startPlayback,
        uint options, [MarshalAs(UnmanagedType.Interface)] NativeMediaEvents? callback, IntPtr window,
        [MarshalAs(UnmanagedType.Interface)] out NativeMediaPlayer? player);

    // The gaps correspond to unused IMFPMediaPlayer slots in the Windows SDK's mfplay.h.
    [ComImport, Guid("A714590A-58AF-430A-85BF-44F5EC838D85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeMediaPlayer
    {
        [PreserveSig] int Play();
        void _VtblGap1_16();
        [PreserveSig] int SetVolume(float volume);
        void _VtblGap2_17();
        [PreserveSig] int Shutdown();
    }

    [ComVisible(true), Guid("766C8FFB-5FDB-4FEA-A28D-B912996F51BD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface NativeMediaEvents
    {
        [PreserveSig] void OnMediaPlayerEvent(IntPtr header);
    }
}
