using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher.Gallery;

public sealed record DemoGame(string Title, string Playtime, IImage? Artwork = null);

public sealed partial class MainWindow : Window
{
    private readonly List<DemoGame> _games;
    private readonly Bitmap _sampleArtwork;
    private int _addedGames;
    private double _zoom = 1;
    private double _savedZoom = 1;
    private Point? _dragOrigin;
    private Vector _pan;
    private Vector _savedPan;
    private Vector _panOrigin;

    public MainWindow()
    {
        InitializeComponent();
        using var imageStream = typeof(MainWindow).Assembly.GetManifestResourceStream("Gallery.Welcome.jpg")
            ?? throw new InvalidOperationException("The gallery artwork resource is missing.");
        _sampleArtwork = new Bitmap(imageStream);
        LibraryHero.Source = EditorImage.Source = _sampleArtwork;
        Closed += (_, _) => _sampleArtwork.Dispose();
        _games =
        [
            new("Aurora", "14.2 hr", _sampleArtwork),
            new("Star Voyage", "47 min"),
            new("Drift Circuit", "2.5 hr")
        ];

        LibraryNav.Click += (_, _) => ShowPage(LibraryPage, LibraryNav, "Library");
        ControlsNav.Click += (_, _) => OpenControls();
        ShowControls.Click += (_, _) => OpenControls();
        ArtworkNav.Click += (_, _) => ShowPage(ArtworkPage, ArtworkNav, "Artwork");
        LibraryList.SelectionChanged += (_, _) =>
        {
            if (LibraryList.SelectedItem is DemoGame game) SelectGame(game);
        };
        Search.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty) RefreshLibrary();
        };
        ClearSearch.Click += (_, _) => Search.Clear();
        AddSample.Click += (_, _) => AddGame();
        FillEmpty.Click += (_, _) => AddGame();
        PrimaryExample.Click += (_, _) => Status.Text = "Primary action selected.";
        SecondaryExample.Click += (_, _) => Status.Text = "Secondary action selected.";
        QuietExample.Click += (_, _) => Status.Text = "Quiet action selected.";
        var examples = LauncherMenus.Create(PlacementMode.Bottom);
        examples.AddAction("Open artwork", () => ShowPage(ArtworkPage, ArtworkNav, "Artwork"), "LG.Icon.Artwork");
        examples.AddAction("Restore framing", RestoreArtwork, "LG.Icon.Refresh");
        MenuExample.ContextMenu = examples;
        MenuExample.Click += (_, _) => examples.Open(MenuExample);
        TextSize.SelectionChanged += (_, _) => TextSample.FontSize = TextSize.SelectedIndex == 1 ? 20 : 12;
        Editor.SaveCommand = new PreviewCommand(SaveArtwork);
        Editor.CancelCommand = new PreviewCommand(RestoreArtwork);
        ArtworkCanvas.PointerWheelChanged += (_, args) =>
        {
            UpdateArtwork(_zoom * Math.Pow(1.12, args.Delta.Y), _pan);
            args.Handled = true;
        };
        ArtworkCanvas.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(ArtworkCanvas).Properties.IsLeftButtonPressed) return;
            _dragOrigin = args.GetPosition(ArtworkCanvas);
            _panOrigin = _pan;
            args.Pointer.Capture(ArtworkCanvas);
        };
        ArtworkCanvas.PointerMoved += (_, args) =>
        {
            if (_dragOrigin is Point origin && args.Pointer.Captured == ArtworkCanvas)
                UpdateArtwork(_zoom, _panOrigin + (args.GetPosition(ArtworkCanvas) - origin));
        };
        ArtworkCanvas.PointerReleased += (_, args) => args.Pointer.Capture(null);
        ArtworkCanvas.PointerCaptureLost += (_, _) => _dragOrigin = null;
        ArtworkCanvas.SizeChanged += (_, _) => UpdateArtwork(_zoom, _pan);
        RefreshLibrary();
    }

    private void OpenControls() => ShowPage(ControlsPage, ControlsNav, "Controls");

    private void ShowPage(Control page, Button tab, string caption)
    {
        LibraryPage.IsVisible = Search.IsVisible = page == LibraryPage;
        ControlsPage.IsVisible = page == ControlsPage;
        ArtworkPage.IsVisible = page == ArtworkPage;
        foreach (var button in new[] { LibraryNav, ControlsNav, ArtworkNav })
            button.Classes.Set("selected", button == tab);
        PageCaption.Text = caption;
        ContentScroll.Offset = default;
    }

    private void RefreshLibrary()
    {
        var query = Search.Text?.Trim() ?? "";
        var matches = _games.Where(game => game.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        LibraryList.ItemsSource = matches.OrderBy(game => game.Title).ToArray();
        RecentCards.Children.Clear();
        foreach (var game in matches.Take(3))
        {
            RecentCards.Children.Add(new GameCard
            {
                Title = game.Title,
                Playtime = game.Playtime,
                ArtworkStretch = Stretch.Uniform,
                Artwork = game.Artwork,
                Margin = new Thickness(0, 0, 9, 0),
                OpenCommand = new PreviewCommand(() => SelectGame(game))
            });
        }
        RecentCards.IsVisible = matches.Length > 0;
        EmptyLibrary.IsVisible = matches.Length == 0;
    }

    private void AddGame()
    {
        _addedGames++;
        var game = new DemoGame($"Sample game {_addedGames}", "0 min");
        _games.Insert(0, game);
        ShowPage(LibraryPage, LibraryNav, "Library");
        Search.Text = "";
        RefreshLibrary();
        Status.Text = $"Added {game.Title}.";
    }

    private void SelectGame(DemoGame game)
    {
        EditorImage.Source = game.Artwork ?? _sampleArtwork;
        UpdateArtwork(_savedZoom, _savedPan);
        ShowPage(ArtworkPage, ArtworkNav, "Artwork");
        Status.Text = $"Selected {game.Title}.";
    }

    private void UpdateArtwork(double zoom, Vector pan)
    {
        _zoom = Math.Clamp(zoom, 1, 4);
        var bounds = ArtworkCanvas.Bounds;
        var horizontal = bounds.Width * (_zoom - 1) / 2;
        var vertical = bounds.Height * (_zoom - 1) / 2;
        _pan = new Vector(Math.Clamp(pan.X, -horizontal, horizontal), Math.Clamp(pan.Y, -vertical, vertical));
        EditorImage.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(_zoom, _zoom), new TranslateTransform(_pan.X, _pan.Y) }
        };
        Editor.ZoomLabel = _zoom.ToString("P0");
    }

    private void SaveArtwork()
    {
        _savedZoom = _zoom;
        _savedPan = _pan;
        Status.Text = "Saved the sample framing.";
    }

    private void RestoreArtwork()
    {
        UpdateArtwork(_savedZoom, _savedPan);
        Status.Text = "Restored the sample framing.";
    }

    internal void VerifyAndCapture(string previewPath)
    {
        UpdateLayout();
        Require(LibraryList.ItemCount == 3 && RecentCards.Children.Count == 3, "The sample library did not populate.");
        var first = (GameCard)RecentCards.Children[0];
        var open = first.GetVisualDescendants().OfType<Button>().Single();
        Require(open.Command != null, "The game card template did not bind its open command.");
        open.Command!.Execute(null);
        Require(ArtworkPage.IsVisible && Status.Text == "Selected Aurora.", "The portrait tile did not open the artwork page.");
        ShowPage(LibraryPage, LibraryNav, "Library");
        Search.Text = "unknown";
        Require(LibraryList.ItemCount == 0 && EmptyLibrary.IsVisible, "The empty search state did not render.");
        Search.Text = "";
        OpenControls();
        UpdateLayout();
        Require(PrimaryExample.IsVisible && ControlsPage.Bounds.Width > 0 && ControlsPage.Bounds.Height > 0,
            "The controls page did not render.");
        ShowPage(ArtworkPage, ArtworkNav, "Artwork");
        UpdateLayout();
        var savedPan = new Vector(40, -28);
        UpdateArtwork(2, savedPan);
        Editor.SaveCommand!.Execute(null);
        UpdateArtwork(3, default);
        Editor.CancelCommand!.Execute(null);
        Require(_zoom == 2 && _pan == savedPan && Editor.ZoomLabel == 2d.ToString("P0"),
            "The artwork toolbar did not restore the saved framing.");
        ShowPage(LibraryPage, LibraryNav, "Library");
        Status.Text = "Ready";
        UpdateLayout();

        var size = PixelSize.FromSize(Bounds.Size, 1);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(this);
        bitmap.Save(previewPath, PngBitmapEncoderOptions.Default);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PreviewCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
