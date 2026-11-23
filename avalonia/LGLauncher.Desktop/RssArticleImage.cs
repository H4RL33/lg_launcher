using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SkiaSharp;

namespace LGLauncher;

internal sealed class RssArticleImage : Border
{
    private const int MaximumBytes = 6 * 1024 * 1024;
    private const int MaximumDimension = 8192;
    private const int DecodedDimension = 1280;
    private static readonly SemaphoreSlim DownloadSlots = new(3);
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    });

    static RssArticleImage() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("LGLauncher/" + UpdateFeed.CurrentVersion.ToString(3));

    private readonly TextBlock _placeholder;
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
        MaxHeight = 650, HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly Uri _address;
    private readonly bool _load;
    private readonly CancellationToken _background;
    private readonly HttpClient _client;
    private CancellationTokenSource? _request;

    protected override Type StyleKeyOverride => typeof(Border);

    internal RssArticleImage(Uri address, string? alt, bool load, CancellationToken background, HttpClient? client = null)
    {
        _address = address;
        _load = load;
        _background = background;
        _client = client ?? Client;
        var description = String.IsNullOrWhiteSpace(alt) ? "Article image" : alt;
        _placeholder = new TextBlock
        {
            Text = description, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 8), Classes = { "lg-muted" }
        };
        Classes.Add("lg-panel");
        MinHeight = 54;
        Child = _placeholder;
        AutomationProperties.SetName(this, description);
        AutomationProperties.SetName(_image, description);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_load) EffectiveViewportChanged += CheckViewport;
    }

    private async void CheckViewport(object? sender, EffectiveViewportChangedEventArgs args)
    {
        var viewport = args.EffectiveViewport;
        if (VisualRoot == null || _background.IsCancellationRequested || _request != null ||
            _image.Source != null || viewport.Width <= 0 || viewport.Height <= 0 ||
            viewport.Bottom < -120 || viewport.Top > Bounds.Height + 120 ||
            viewport.Right <= 0 || viewport.Left >= Bounds.Width) return;

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_background);
        _request = request;
        Bitmap? picture;
        try
        {
            picture = await Download(_address, request.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            picture = null;
        }

        if (_request != request || request.IsCancellationRequested)
        {
            picture?.Dispose();
            if (_request == request) _request = null;
            return;
        }
        EffectiveViewportChanged -= CheckViewport;
        _request = null;
        if (picture == null)
        {
            if (_placeholder.Text == "Article image") _placeholder.Text = "Image unavailable";
            return;
        }
        _image.Source = picture;
        MinHeight = 0;
        Classes.Remove("lg-panel");
        Child = _image;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= CheckViewport;
        _request?.Cancel();
        _request = null;
        var picture = _image.Source as Bitmap;
        _image.Source = null;
        picture?.Dispose();
        if (Child == _image)
        {
            MinHeight = 54;
            Classes.Add("lg-panel");
            Child = _placeholder;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private async Task<Bitmap?> Download(Uri address, CancellationToken cancellationToken)
    {
        await DownloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        try
        {
            for (var hop = 0; hop <= 4; hop++)
            {
                using var response = await _client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (RssReader.ResolveWebAddress(response.Headers.Location?.OriginalString, address) is not { } next) return null;
                    address = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes) return null;
                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + count > MaximumBytes) return null;
                    bytes.Write(buffer, 0, count);
                }
                return await Task.Run(() => Decode(bytes), token).ConfigureAwait(false);
            }
            return null;
        }
        finally { DownloadSlots.Release(); }
    }

    private static Bitmap? Decode(MemoryStream bytes)
    {
        bytes.Position = 0;
        int width, height;
        using (var stream = new SKManagedStream(bytes, disposeManagedStream: false))
        using (var codec = SKCodec.Create(stream))
        {
            if (codec == null) return null;
            width = codec.Info.Width;
            height = codec.Info.Height;
        }
        if (width is <= 0 or > MaximumDimension || height is <= 0 or > MaximumDimension ||
            (long)width * height > 32_000_000) return null;

        bytes.Position = 0;
        if (width >= height && width > DecodedDimension) return Bitmap.DecodeToWidth(bytes, DecodedDimension);
        if (height > DecodedDimension) return Bitmap.DecodeToHeight(bytes, DecodedDimension);
        return new Bitmap(bytes);
    }
}
