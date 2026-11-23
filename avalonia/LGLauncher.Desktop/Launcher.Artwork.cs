using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using LGLauncher.Components;
using SkiaSharp;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private const int MaximumArtworkDimension = 16384;
    private const long MaximumArtworkPixels = 40_000_000;
    private const int PortraitBitmapWidth = 420;
    private const int PortraitBitmapHeight = 630;
    private readonly ArtworkLayer _artwork = new();
    private readonly ArtworkLayer _chromeArtwork = new();
    private readonly ArtworkLayer _searchArtwork = new() { Radius = 8 };
    private readonly ArtworkLayer _resultsArtwork = new() { Radius = 8 };
    private readonly Transitions _artworkParallaxMotion = new()
    {
        new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(145), Easing = new CubicEaseOut() }
    };
    private readonly Dictionary<(int Id, bool Portrait), WeakReference<Bitmap>> _cardImages = [];
    private readonly Dictionary<string, WeakReference<Bitmap>> _customPortraits = [];
    private readonly Dictionary<string, Task<Bitmap?>> _customPortraitRequests = [];
    private readonly Dictionary<(int Id, bool Portrait), Task<Bitmap?>> _cardImageRequests = [];
    private readonly HashSet<(int Id, bool Portrait)> _attemptedCardImages = [];
    private readonly HashSet<int> _attemptedBackdrops = [];
    private ArtworkFraming? _framing;
    private Color? _artworkAccent;
    private (Color? Colour, bool FromArtwork) _targetAccent;
    private int _accentRevision;
    private CancellationTokenSource? _detailsRequest;
    private Game? _artworkGame;
    private int _artworkSteamId;
    private string? _artworkOverride;
    private bool _requestedOnline;
    private bool _editingArtwork;
    private bool _replacingArtwork;
    private Point _draftCenter;
    private double _draftZoom;
    private Point _dragAnchor;
    private Point _dragCenter;
    private Rect _dragCrop;
    private IPointer? _dragPointer;

    static Launcher()
    {
        PointerMovedEvent.AddClassHandler<PopupRoot>((popup, args) =>
        {
            if (popup.ParentTopLevel is Launcher owner) owner.TrackArtworkPointer(args);
        }, RoutingStrategies.Tunnel);
        ContextMenu.ClosedEvent.AddClassHandler<FrostedContextMenu>((menu, _) =>
        {
            if ((menu.Parent as Popup)?.PlacementTarget is { } target && TopLevel.GetTopLevel(target) is Launcher owner)
                owner.ResetArtworkPointerOutsideWindow();
        });
    }

    private void InitializeArtwork()
    {
        ChromeArtworkHost.Content = _chromeArtwork;
        SearchArtworkHost.Content = _searchArtwork;
        ResultsArtworkHost.Content = _resultsArtwork;
        ShellSurface.LayoutUpdated += (_, _) => UpdateGlassCrop();
        ArtworkHost.PropertyChanged += (_, args) =>
        {
            if (args.Property == RenderTransformProperty) UpdateGlassCrop();
        };
        ArtworkTools.SaveCommand = new LauncherCommand(() => FinishArtworkMove(true));
        ArtworkTools.CancelCommand = new LauncherCommand(() => FinishArtworkMove(false));
        var menu = LauncherMenus.Create();
        var replace = menu.AddAction("Replace Artwork…", ChooseArtwork, "LG.Icon.Artwork");
        var move = menu.AddAction("Move Artwork", BeginArtworkMove, "LG.Icon.Move");
        var reset = menu.AddAction("Reset Artwork", ResetArtwork, "LG.Icon.Refresh");
        menu.Opening += (_, args) =>
        {
            if (_editingArtwork || _replacingArtwork || _game == null) { args.Cancel = true; return; }
            replace.IsEnabled = true;
            move.IsEnabled = _artwork.Picture != null;
            reset.IsEnabled = _game.ArtworkPosition != null || _game.CustomArtworkId != null;
        };
        Details.ContextMenu = menu;
        AddHandler(PointerMovedEvent, (_, args) => TrackArtworkPointer(args), RoutingStrategies.Tunnel);
        PointerExited += (_, _) => ResetArtworkPointerOutsideWindow();
        Deactivated += (_, _) =>
        {
            if (!IsContextMenuOpen()) ResetArtworkParallax();
        };
        if (!_verification)
            WindowsDesktop.TrackFramePointer(this, point => MoveArtworkParallax(ShellSurface.PointToClient(point)),
                ResetArtworkPointerOutsideWindow);
        ResetArtworkParallax(immediate: true);
        _artwork.SizeChanged += (_, _) => UpdateCrop();
        PanSurface.PointerPressed += (_, args) =>
        {
            if (!_editingArtwork || !args.GetCurrentPoint(PanSurface).Properties.IsLeftButtonPressed) return;
            _dragPointer = args.Pointer;
            _dragPointer.Capture(PanSurface);
            RememberDrag(args.GetPosition(PanSurface));
            args.Handled = true;
        };
        PanSurface.PointerMoved += (_, args) =>
        {
            if (args.Pointer != _dragPointer) return;
            _draftCenter = ArtworkFraming.DragPosition(_dragCrop, _artwork.Bounds.Size, _dragCenter, args.GetPosition(PanSurface) - _dragAnchor);
            UpdateCrop();
        };
        PanSurface.PointerReleased += (_, args) =>
        {
            if (args.Pointer == _dragPointer) args.Pointer.Capture(null);
        };
        PanSurface.PointerCaptureLost += (_, _) => _dragPointer = null;
        PanSurface.PointerWheelChanged += (_, args) =>
        {
            var point = args.GetPosition(PanSurface);
            ZoomArtwork(args.Delta.Y, point);
            if (_dragPointer != null) RememberDrag(point);
            args.Handled = true;
        };
    }

    private void TrackArtworkPointer(PointerEventArgs args)
    {
        // The smoke fixture sends its own pointer events with a zero timestamp.
        if (args.Pointer.Type == PointerType.Mouse && (!_verification || args.Timestamp == 0))
            MoveArtworkParallax(args.GetPosition(ShellSurface));
    }

    private bool IsContextMenuOpen() => OpenedPopups.Any(popup => popup.Child is FrostedContextMenu);

    private void ResetArtworkPointerOutsideWindow()
    {
        if (!IsContextMenuOpen() && !WindowsDesktop.IsPointerWithin(this)) ResetArtworkParallax();
    }

    private void MoveArtworkParallax(Point pointer)
    {
        var size = ArtworkHost.Bounds.Size;
        var viewport = ShellSurface.Bounds.Size;
        if (!_motion || _library.Preferences.DisableArtworkShift || _editingArtwork || _page != Page.Game || _artwork.Picture == null ||
            _playing.Count != 0 || size.Width <= 0 || size.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return;
        var x = Math.Clamp(pointer.X / viewport.Width * 2 - 1, -1, 1);
        var y = Math.Clamp(pointer.Y / viewport.Height * 2 - 1, -1, 1);
        var maxShiftX = Math.Min(8, size.Width * .008);
        var maxShiftY = Math.Min(6, size.Height * .008);
        // Keep the same amount of overscan across the cursor's full range of motion.
        var scale = 1 + 2.15 * Math.Max(maxShiftX / size.Width, maxShiftY / size.Height);
        var motion = TransformOperations.CreateBuilder(2);
        motion.AppendScale(scale, scale);
        motion.AppendTranslate(-x * maxShiftX, -y * maxShiftY);
        ArtworkHost.Transitions ??= _artworkParallaxMotion;
        ArtworkHost.RenderTransform = motion.Build();
    }

    private void ResetArtworkParallax(bool immediate = false)
    {
        if (immediate || !_motion) ArtworkHost.Transitions = null;
        ArtworkHost.RenderTransform = TransformOperations.Identity;
    }

    private void RememberDrag(Point point)
    {
        _dragAnchor = point;
        _dragCrop = _artwork.Crop;
        _dragCenter = _draftCenter;
    }

    private void ZoomArtwork(double detents, Point pointer)
    {
        var viewport = _artwork.Bounds.Size;
        if (!_editingArtwork || viewport.Width <= 0 || viewport.Height <= 0) return;
        var next = ArtworkPosition.ClampZoom(_draftZoom * Math.Pow(1.12, detents));
        if (next == _draftZoom) return;
        _draftCenter = ArtworkFraming.ZoomPosition(_artwork.Crop, new Point(pointer.X / viewport.Width, pointer.Y / viewport.Height), _draftZoom, next);
        _draftZoom = next;
        ArtworkTools.ZoomLabel = next.ToString("P0");
        UpdateCrop();
    }

    private void BeginArtworkMove()
    {
        if (_editingArtwork || _artwork.Picture == null || _game == null || _replacingArtwork) return;
        _detailsRequest?.Cancel();
        _editingArtwork = true;
        ResetArtworkParallax(immediate: true);
        var position = _game.ArtworkPosition;
        _draftCenter = position == null ? _artwork.Crop.Center : new Point(position.X, position.Y);
        _draftZoom = position?.Zoom ?? ArtworkPosition.MinimumZoom;
        ArtworkTools.ZoomLabel = _draftZoom.ToString("P0");
        GameInfo.IsEnabled = false;
        PanSurface.IsVisible = ArtworkTools.IsVisible = true;
        CloseSearch();
        SearchSurface.IsVisible = false;
        PanSurface.Focus();
        UpdateCrop();
    }

    private void FinishArtworkMove(bool commit)
    {
        if (!_editingArtwork || _game == null) return;
        if (commit)
        {
            var previous = _game.ArtworkPosition;
            var previouslyUnsaved = _saveFailed;
            _game.ArtworkPosition = new ArtworkPosition { X = _draftCenter.X, Y = _draftCenter.Y, Zoom = _draftZoom };
            if (!Save())
            {
                _game.ArtworkPosition = previous;
                _saveFailed = previouslyUnsaved;
                return;
            }
            Status("Artwork position saved.");
        }
        _editingArtwork = false;
        _dragPointer?.Capture(null);
        PanSurface.IsVisible = ArtworkTools.IsVisible = false;
        GameInfo.IsEnabled = SearchSurface.IsVisible = true;
        UpdateCrop();
    }

    private bool HandleArtworkKey(Key key)
    {
        if (!_editingArtwork) return false;
        if (key == Key.Escape) { FinishArtworkMove(false); return true; }
        var direction = key switch
        {
            Key.Left => new Vector(-32, 0),
            Key.Right => new Vector(32, 0),
            Key.Up => new Vector(0, -32),
            Key.Down => new Vector(0, 32),
            _ => default
        };
        if (direction == default) return false;
        _draftCenter = ArtworkFraming.DragPosition(_artwork.Crop, _artwork.Bounds.Size, _draftCenter, direction);
        UpdateCrop();
        return true;
    }

    private void UpdateCrop()
    {
        if (_artwork.Picture is not { } picture) return;
        var size = _artwork.Bounds.Size;
        var aspect = picture.Size.AspectRatio;
        var crop = _editingArtwork
            ? ArtworkFraming.CenteredViewbox(aspect, size, _draftCenter, _draftZoom)
            : _game?.ArtworkPosition is { } saved
                ? ArtworkFraming.CenteredViewbox(aspect, size, new Point(saved.X, saved.Y), saved.Zoom)
                : _framing?.Viewbox(size) ?? ArtworkFraming.CenteredViewbox(aspect, size, new Point(.5, .5));
        _artwork.UpdateCrop(crop);
        UpdateGlassCrop();
    }

    private void RefreshArtworkGlass()
    {
        var show = !_library.Preferences.DisableTranslucency && _page == Page.Game && _chromeArtwork.Picture != null;
        Classes.Set("artwork-glass", show);
        _chromeArtwork.IsVisible = _searchArtwork.IsVisible = _resultsArtwork.IsVisible = show;
        if (show) UpdateGlassCrop();
    }

    private void UpdateGlassCrop()
    {
        var size = _artwork.Bounds.Size;
        if (!_chromeArtwork.IsVisible || size.Width <= 0 || size.Height <= 0) return;
        var crop = _artwork.Crop;
        var transform = ArtworkHost.RenderTransform?.Value ?? Matrix.Identity;
        var center = ArtworkHost.RenderTransformOrigin.ToPixels(size);
        // Map each fixed glass surface back through the artwork's shift and overscan.
        var offsetX = center.X * (transform.M11 - 1) - transform.M31;
        var offsetY = center.Y * (transform.M22 - 1) - transform.M32;
        // The blur texture has a half-image of mirrored padding on each edge.
        var x = crop.Width / size.Width / 2 / transform.M11;
        var y = crop.Height / size.Height / 2 / transform.M22;
        SampleGlass(_chromeArtwork);
        if (SearchSurface.IsVisible) SampleGlass(_searchArtwork);
        if (SearchDropdown.IsVisible) SampleGlass(_resultsArtwork);

        void SampleGlass(ArtworkLayer layer)
        {
            if (layer.Bounds.Width <= 0 || layer.Bounds.Height <= 0 ||
                layer.TranslatePoint(default, Details) is not { } origin) return;
            layer.UpdateCrop(new Rect((crop.X + .5) / 2 + (origin.X + offsetX) * x,
                (crop.Y + .5) / 2 + (origin.Y + offsetY) * y,
                layer.Bounds.Width * x, layer.Bounds.Height * y));
        }
    }

    private void ApplyArtworkAccent()
    {
        Color? artworkAccent = null;
        if (_page == Page.Game && _game is { } game && !_library.Preferences.DisableArtworkAccent)
        {
            // Keep the previous palette through the gap between game selection and artwork loading.
            if (!MatchesDetailsArtwork(game) || (_detailsRequest != null && _artworkAccent == null)) return;
            artworkAccent = _artworkAccent;
        }
        var accent = artworkAccent ?? DefaultAccentColour;
        var next = (accent, artworkAccent.HasValue);
        if (_targetAccent == next) return;
        _targetAccent = next;
        var revision = ++_accentRevision;

        if (artworkAccent is not { } target || !CanAnimateArtworkAccent() || !Resources.TryGetValue("LG.AccentFill", out var current) ||
            current is not SolidColorBrush fill || fill.Color == target)
        {
            SetArtworkPalette(accent);
            return;
        }

        var start = fill.Color;
        var started = Stopwatch.GetTimestamp();
        var easing = new CubicEaseOut();
        RequestAnimationFrame(ApplyFrame);

        void ApplyFrame(TimeSpan _)
        {
            if (_closed || revision != _accentRevision) return;
            var progress = CanAnimateArtworkAccent() ? Math.Min(1, Stopwatch.GetElapsedTime(started) / ViewMotion.FadeDuration) : 1;
            SetArtworkPalette(ArtworkAccent.Mix(start, target, easing.Ease(progress)));
            if (progress < 1) RequestAnimationFrame(ApplyFrame);
        }
    }

    private bool CanAnimateArtworkAccent() => _motion && IsEffectivelyVisible && WindowState != WindowState.Minimized;

    private void SetArtworkPalette(Color? accent)
    {
        const string listSelection = "SystemControlHighlightListAccentLowBrush";
        const string listHover = "SystemControlHighlightListAccentMediumBrush";
        const string listPressed = "SystemControlHighlightListAccentHighBrush";
        const string textSelection = "TextControlSelectionHighlightColor";
        const string textFocus = "TextControlBorderBrushFocused";
        if (accent is not { } color)
        {
            foreach (var key in new[]
            {
                "LG.Accent", "LG.AccentFill", "LG.OnAccent", "LG.Selection", listSelection, listHover, listPressed, textSelection, textFocus,
                "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
                "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed"
            })
            {
                Resources.Remove(key);
            }
            return;
        }

        var surface = ((ISolidColorBrush)this.FindResource("LG.Surface")!).Color;
        var text = ((ISolidColorBrush)this.FindResource("LG.Text")!).Color;
        var hover = ArtworkAccent.Mix(surface, color, .8);
        while (ArtworkAccent.Contrast(hover, text) < 4.5)
            hover = ArtworkAccent.Mix(hover, surface, .1);
        var selection = ArtworkAccent.Mix(surface, hover, .85);
        var indicator = ArtworkAccent.ForDarkSurface(color);
        SetBrush("LG.Accent", indicator);
        SetBrush("LG.Selection", selection);
        SetBrush(listSelection, selection);
        SetBrush(listHover, hover);
        SetBrush(listPressed, ArtworkAccent.Mix(surface, hover, .72));
        SetBrush(textSelection, selection);
        SetBrush(textFocus, indicator);
        SetSwitchBrushes("", ArtworkAccent.Mix(surface, indicator, .8));
        SetSwitchBrushes("PointerOver", ArtworkAccent.Mix(surface, indicator, .92));
        SetSwitchBrushes("Pressed", ArtworkAccent.Mix(surface, indicator, .72));
        // Resolve contrast from the same fill that will be painted in this frame.
        SetBrush("LG.OnAccent", ArtworkAccent.TextOn(color));
        SetBrush("LG.AccentFill", color);

        void SetSwitchBrushes(string state, Color track)
        {
            SetBrush($"ToggleSwitchKnobFillOn{state}", ArtworkAccent.TextOn(track));
            SetBrush($"ToggleSwitchFillOn{state}", track);
        }

        void SetBrush(string key, Color value)
        {
            if (Resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush) brush.Color = value;
            else Resources[key] = new SolidColorBrush(value);
        }
    }

    private void SetArtwork(Bitmap? picture, ArtworkFraming? framing, Bitmap? glass = null, Color? accent = null)
    {
        ResetArtworkParallax(immediate: true);
        _framing = framing;
        _artworkAccent = accent;
        var oldPicture = _artwork.Picture;
        var oldGlass = _chromeArtwork.Picture;
        _artwork.SetPicture(picture);
        _chromeArtwork.SetPicture(glass);
        _searchArtwork.SetPicture(glass);
        _resultsArtwork.SetPicture(glass);
        oldPicture?.Dispose();
        oldGlass?.Dispose();
        ApplyArtworkAccent();
        RefreshArtworkGlass();
        UpdateCrop();
        if (picture != null) AnimateView(_artwork, fadeOnly: true);
        else CancelMotion(_artwork);
    }

    private async void ChooseArtwork()
    {
        var game = _game;
        if (game == null || _editingArtwork || _replacingArtwork) return;
        var source = await PickFile(this, "Choose artwork for " + game.Title, Images, null);
        if (source != null) await ReplaceArtwork(game, source);
    }

    private async Task<bool> ReplaceArtwork(Game game, string source)
    {
        if (_replacingArtwork) return false;
        _replacingArtwork = true;
        var id = Guid.NewGuid().ToString("N");
        var destination = _store.CustomArtworkPath(id);
        var committed = false;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ImportGameMediaFile(source, destination, 2560, CancellationToken.None);
            });
            if (_closed || !_library.Games.Contains(game)) return false;
            committed = SaveArtworkOverride(game, id, Path.GetFileName(source));
            if (committed) Status("Artwork replaced.");
            return committed;
        }
        catch (Exception error) when (IsArtworkError(error))
        {
            if (!_closed) await Notice("Could not replace artwork", error.Message);
            return false;
        }
        finally
        {
            _replacingArtwork = false;
            if (!committed)
            {
                try { File.Delete(destination); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void ResetArtwork()
    {
        if (_game == null || _editingArtwork || _replacingArtwork) return;
        if (SaveArtworkOverride(_game, null)) Status("Original artwork and automatic framing restored.");
    }

    private bool SaveArtworkOverride(Game game, string? newId, string? fileName = null)
    {
        var oldId = game.CustomArtworkId;
        var oldFileName = game.CustomArtworkFileName;
        var position = game.ArtworkPosition;
        var previouslyUnsaved = _saveFailed;
        game.CustomArtworkId = newId;
        game.CustomArtworkFileName = fileName;
        game.ArtworkPosition = null;
        if (!Save())
        {
            game.CustomArtworkId = oldId;
            game.CustomArtworkFileName = oldFileName;
            game.ArtworkPosition = position;
            _saveFailed = previouslyUnsaved;
            return false;
        }
        if (_page == Page.Game && _game == game)
        {
            _artworkGame = null;
            LoadDetailsArtwork(game);
        }
        return true;
    }

    private bool MatchesDetailsArtwork(Game game) =>
        _artworkGame == game && _artworkSteamId == game.SteamId && _artworkOverride == game.CustomArtworkId;

    private async void LoadDetailsArtwork(Game game, bool reload = false)
    {
        var online = CanFetch;
        var same = MatchesDetailsArtwork(game);
        if (!reload && same && (!online || _requestedOnline)) return;
        _detailsRequest?.Cancel();
        var work = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
        _detailsRequest = work;
        var token = work.Token;
        _artworkGame = game;
        _artworkSteamId = game.SteamId;
        _artworkOverride = game.CustomArtworkId;
        _requestedOnline = online;
        if (!same) SetArtwork(null, null);
        var id = game.SteamId;
        try
        {
            var custom = _store.CustomArtworkPath(game.CustomArtworkId);
            if (custom != null && await PresentArtwork(game, custom, token)) return;
            if (id <= 0) return;
            var backdrop = Path.Combine(_store.ArtworkDirectory, "backdrops", id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg");
            if (await PresentArtwork(game, backdrop, token)) return;
            var header = _steam.CachedArtwork(id);
            var headerShown = header != null && await PresentArtwork(game, header, token);
            if (!online || token.IsCancellationRequested) return;
            if (_attemptedBackdrops.Add(id))
            {
                var found = await _steam.GetBackdrop(id, token, refresh: File.Exists(backdrop));
                if (found != null && await PresentArtwork(game, found, token)) return;
            }
            if (!headerShown && await EnsureHeader(id, token) != null)
            {
                header = _steam.CachedArtwork(id);
                if (header != null) await PresentArtwork(game, header, token);
            }
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested)
            {
                _attemptedBackdrops.Remove(id);
                if (_detailsRequest == work) _requestedOnline = false;
            }
        }
        catch (Exception error) when (IsArtworkError(error)) { }
        finally
        {
            if (_detailsRequest == work)
            {
                _detailsRequest = null;
                if (!_closed) ApplyArtworkAccent();
            }
            work.Dispose();
        }
    }

    private async Task<bool> PresentArtwork(Game game, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return false;
        try
        {
            var (decoded, framing, glass, accent) = await Task.Run(() =>
            {
                var picture = ReadBitmap(path, 2560);
                var sample = SampleArtwork(path, picture.Size.AspectRatio);
                return (picture, sample.Framing, sample.Glass, sample.Accent);
            }, token);
            if (token.IsCancellationRequested || _closed || _page != Page.Game || _game != game || _editingArtwork)
            {
                decoded.Dispose();
                glass?.Dispose();
                return false;
            }
            SetArtwork(decoded, framing, glass, accent);
            return true;
        }
        catch (Exception error) when (IsArtworkError(error)) { return false; }
    }

    private async void LoadCardArtwork(GameCard card, Game game)
    {
        var (picture, fromHeader) = await ResolveCardArtwork(game, _background.Token);
        if (picture == null) return;
        if (fromHeader) card.ArtworkStretch = Stretch.Uniform;
        card.Artwork = picture;
    }

    private async Task<(Bitmap? Picture, bool FromHeader)> ResolveCardArtwork(Game game, CancellationToken token)
    {
        var customId = game.CustomPortraitId;
        var steamId = game.SteamId;
        var picture = customId != null ? await GetCustomPortrait(customId, token) : null;
        if (!StillCurrent()) return default;
        if (picture != null) return (picture, false);
        if (steamId <= 0) return default;
        picture = await EnsureCardImage((steamId, true), token);
        if (!StillCurrent()) return default;
        if (picture != null) return (picture, false);
        picture = await EnsureHeader(steamId, token);
        return StillCurrent() ? (picture, true) : default;

        bool StillCurrent() => !_closed && !token.IsCancellationRequested && game.CustomPortraitId == customId && game.SteamId == steamId;
    }

    private Task<Bitmap?> GetCustomPortrait(string id, CancellationToken token)
    {
        if (_customPortraits.TryGetValue(id, out var cached) && cached.TryGetTarget(out var image)) return Task.FromResult<Bitmap?>(image);
        if (_customPortraitRequests.TryGetValue(id, out var request)) return request;
        if (_store.CustomArtworkPath(id) is not { } path || !File.Exists(path)) return Task.FromResult<Bitmap?>(null);
        var current = Load();
        if (!current.IsCompleted) _customPortraitRequests[id] = current;
        return current;

        async Task<Bitmap?> Load()
        {
            try
            {
                var picture = await Task.Run(() => ReadBitmap(path, PortraitBitmapWidth, PortraitBitmapHeight), token);
                if (_closed || token.IsCancellationRequested)
                {
                    picture.Dispose();
                    return null;
                }
                _customPortraits[id] = new WeakReference<Bitmap>(picture);
                return picture;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
            catch (Exception error) when (IsArtworkError(error)) { return null; }
            finally { _customPortraitRequests.Remove(id); }
        }
    }

    private Task<Bitmap?> EnsureHeader(int id, CancellationToken token) => EnsureCardImage((id, false), token);

    private async Task<Bitmap?> EnsureCardImage((int Id, bool Portrait) key, CancellationToken token)
    {
        if (_closed || token.IsCancellationRequested) return null;
        if (_cardImages.TryGetValue(key, out var cached) && cached.TryGetTarget(out var image)) return image;
        if (!_cardImageRequests.TryGetValue(key, out var request))
        {
            // Details and cards share the fetch, so only the launcher can cancel it.
            request = LoadCardImage(key, _background.Token);
            if (!request.IsCompleted) _cardImageRequests[key] = request;
        }

        try { return await request.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
    }

    private async Task<Bitmap?> LoadCardImage((int Id, bool Portrait) key, CancellationToken token)
    {
        try
        {
            var (id, portrait) = key;
            var path = portrait ? _steam.CachedPortrait(id) : _steam.CachedArtwork(id);
            var image = await Decode(path);
            if (image == null && CanFetch && _attemptedCardImages.Add(key))
            {
                var replacement = portrait ? await _steam.GetPortrait(id, token, refresh: path != null)
                    : await _steam.DownloadArtwork(id, token, refresh: path != null);
                image = await Decode(replacement);
            }
            if (_closed || token.IsCancellationRequested) image?.Dispose();
            token.ThrowIfCancellationRequested();
            if (_closed || image == null) return null;
            _cardImages[key] = new WeakReference<Bitmap>(image);
            return image;

            Task<Bitmap?> Decode(string? candidate) => candidate == null ? Task.FromResult<Bitmap?>(null) : Task.Run(() => portrait
                ? TryReadBitmap(candidate, PortraitBitmapWidth, PortraitBitmapHeight)
                : TryReadBitmap(candidate, 470), token);
        }
        catch (OperationCanceledException) { if (token.IsCancellationRequested) _attemptedCardImages.Remove(key); return null; }
        catch (Exception error) when (IsArtworkError(error)) { return null; }
        finally { _cardImageRequests.Remove(key); }
    }

    private static bool IsArtworkError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or HttpRequestException;

    private static Bitmap? TryReadBitmap(string path, int maximumWidth, int? maximumHeight = null)
    {
        try { return ReadBitmap(path, maximumWidth, maximumHeight); }
        catch (Exception error) when (IsArtworkError(error)) { return null; }
    }

    private static bool CanDecodeSteamArtwork(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return false;
            var source = ReadArtworkDimensions(codec);
            var dimensions = codec.GetScaledDimensions(Math.Min(1f, 420f / Math.Max(source.Width, source.Height)));
            var sample = new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var image = new SKBitmap(sample);
            var pixels = image.GetPixels();
            return pixels != IntPtr.Zero && codec.GetPixels(sample, pixels) == SKCodecResult.Success;
        }
        catch (Exception error) when (IsArtworkError(error)) { return false; }
    }

    private static Bitmap ReadBitmap(string path, int maximumWidth, int? maximumHeight = null)
    {
        using var stream = File.OpenRead(path);
        SKImageInfo pixels;
        using (var source = new SKManagedStream(stream, disposeManagedStream: false))
        using (var codec = SKCodec.Create(source))
            pixels = ReadArtworkDimensions(codec);

        stream.Position = 0;
        var heightLimit = maximumHeight ?? maximumWidth;
        var widthScale = (double)maximumWidth / pixels.Width;
        var heightScale = (double)heightLimit / pixels.Height;
        var scale = Math.Min(widthScale, heightScale);
        if (scale >= 1) return new Bitmap(stream);
        var limitByWidth = widthScale <= heightScale;
        if (pixels.Width * scale < 1 || pixels.Height * scale < 1)
        {
            using var artwork = new Bitmap(stream);
            var size = limitByWidth ? new PixelSize(maximumWidth, 1) : new PixelSize(1, heightLimit);
            return artwork.CreateScaledBitmap(size, BitmapInterpolationMode.HighQuality);
        }
        return limitByWidth ? Bitmap.DecodeToWidth(stream, maximumWidth) : Bitmap.DecodeToHeight(stream, heightLimit);
    }

    private static SKImageInfo ReadArtworkDimensions(SKCodec? codec)
    {
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            throw new InvalidDataException("The artwork could not be decoded.");
        var pixels = codec.Info;
        if (pixels.Width > MaximumArtworkDimension || pixels.Height > MaximumArtworkDimension ||
            (long)pixels.Width * pixels.Height > MaximumArtworkPixels)
            throw new InvalidDataException("Artwork must be at most 16,384 pixels on each side and 40 megapixels in total.");
        return pixels;
    }

    private static (ArtworkFraming? Framing, Bitmap? Glass, Color? Accent) SampleArtwork(string path, double aspect)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return default;
            var pixels = ReadArtworkDimensions(codec);
            var scale = Math.Min(1f, (float)ArtworkFraming.SampleSize / Math.Max(pixels.Width, pixels.Height));
            var dimensions = codec.GetScaledDimensions(scale);
            using var source = SKBitmap.Decode(codec, new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (source == null) return default;
            if (scale == 1) return BuildArtworkSample(source, aspect);
            var width = Math.Clamp((int)Math.Round(pixels.Width * scale), 1, ArtworkFraming.SampleSize);
            var height = Math.Clamp((int)Math.Round(pixels.Height * scale), 1, ArtworkFraming.SampleSize);
            using var resized = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
            return resized == null ? default : BuildArtworkSample(resized, aspect);
        }
        catch (Exception error) when (IsArtworkError(error)) { return default; }
    }

    private static (ArtworkFraming, Bitmap, Color?) BuildArtworkSample(SKBitmap sample, double aspect)
    {
        var pixels = sample.Bytes;
        var framing = ArtworkFraming.Analyze(pixels, sample.Width, sample.Height, aspect);
        var accent = ArtworkAccent.FromPixels(pixels);
        using var blurred = new SKBitmap(sample.Width * 2, sample.Height * 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(blurred);
        using var shader = sample.ToShader(SKShaderTileMode.Mirror, SKShaderTileMode.Mirror,
            SKMatrix.CreateTranslation(sample.Width / 2f, sample.Height / 2f));
        using var blur = SKImageFilter.CreateBlur(2.4f, 2.4f, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, ImageFilter = blur };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawRect(new SKRect(0, 0, blurred.Width, blurred.Height), paint);
        return (framing, new WriteableBitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, blurred.GetPixels(),
            new PixelSize(blurred.Width, blurred.Height), new Vector(96, 96), blurred.RowBytes), accent);
    }

    private LibraryEntry GetEntry(Game game)
    {
        if (!_entries.TryGetValue(game, out var entry)) _entries[game] = entry = new LibraryEntry(game, LoadIcon);
        return entry;
    }

    private void InvalidateEntry(Game game) => _entries.Remove(game);

    private async void LoadIcon(LibraryEntry entry)
    {
        var game = entry.Game;
        var path = game.Path;
        var id = game.SteamId;
        var token = _background.Token;
        var online = CanFetch;
        entry.LoadedOnline = online;
        entry.Loading = true;
        try
        {
            var cached = id > 0 ? Path.Combine(_store.ArtworkDirectory, "icons", id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg") : null;
            var cachedExists = File.Exists(cached);
            if (cachedExists)
            {
                var picture = await Task.Run(() => TryReadBitmap(cached!, 40), token);
                if (!StillCurrent()) { picture?.Dispose(); return; }
                if (picture != null)
                {
                    entry.SetIcon(picture);
                    return;
                }
            }
            if (entry.Image == null)
            {
                var local = await Task.Run(() => WindowsDesktop.ExecutableIcon(path), token);
                if (!StillCurrent()) { local?.Dispose(); return; }
                entry.SetIcon(local);
            }
            if (id <= 0 || !online) return;
            var steamIcon = await _steam.GetIcon(id, token, refresh: cachedExists);
            if (steamIcon == null) return;
            var downloaded = await Task.Run(() => ReadBitmap(steamIcon, 40), token);
            if (StillCurrent()) entry.SetIcon(downloaded); else downloaded.Dispose();
        }
        catch (OperationCanceledException) { if (token.IsCancellationRequested) entry.LoadedOnline = false; }
        catch (Exception error) when (IsArtworkError(error)) { }
        finally { entry.Loading = false; }

        bool StillCurrent() => !_closed && _entries.TryGetValue(game, out var current) && current == entry;
    }

    private void RefreshVisibleIcons()
    {
        if (!CanFetch) return;
        foreach (var entry in _shownEntries)
            if (entry.Requested && !entry.Loading && !entry.LoadedOnline && entry.Game.SteamId > 0)
                LoadIcon(entry);
    }

    private void DisposeImages()
    {
        _detailsRequest?.Cancel();
        SetArtwork(null, null);
        foreach (var entry in _entries.Values) entry.Image?.Dispose();
        foreach (var weak in _cardImages.Values) if (weak.TryGetTarget(out var bitmap)) bitmap.Dispose();
        foreach (var weak in _customPortraits.Values) if (weak.TryGetTarget(out var bitmap)) bitmap.Dispose();
    }

    internal sealed class LibraryEntry(Game game, Action<LibraryEntry> load) : INotifyPropertyChanged
    {
        internal Game Game { get; } = game;
        internal Bitmap? Image { get; private set; }
        internal bool Loading { get; set; }
        internal bool LoadedOnline { get; set; }
        internal bool Requested { get; private set; }
        public string Title => Game.Title;
        public IImage? SidebarIcon
        {
            get
            {
                if (!Requested) { Requested = true; load(this); }
                return Image;
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetIcon(Bitmap? icon)
        {
            var old = Image;
            Image = icon;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarIcon)));
            old?.Dispose();
        }
    }

    private sealed class ArtworkLayer : Control
    {
        public double Radius { get; init; }
        public Bitmap? Picture { get; private set; }
        public Rect Crop { get; private set; } = new(0, 0, 1, 1);

        public void SetPicture(Bitmap? picture)
        {
            Picture = picture;
            InvalidateVisual();
        }

        public void UpdateCrop(Rect crop)
        {
            if (Crop == crop) return;
            Crop = crop;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (Picture == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var size = Picture.Size;
            var source = new Rect(Crop.X * size.Width, Crop.Y * size.Height, Crop.Width * size.Width, Crop.Height * size.Height);
            var destination = new Rect(Bounds.Size);
            using var clip = Radius > 0 ? context.PushClip(new RoundedRect(destination, Radius)) : default;
            context.DrawImage(Picture, source, destination);
        }
    }
}
