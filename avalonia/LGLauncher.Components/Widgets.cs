using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LGLauncher.Components;

public enum StatusTone
{
    Neutral,
    Active,
    Warning
}

public sealed class StatusIndicator : TemplatedControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StatusIndicator, string>(nameof(Label), "");
    public static readonly StyledProperty<StatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusIndicator, StatusTone>(nameof(Tone));

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public StatusTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
}

public sealed class LibraryRow : TemplatedControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<LibraryRow, string>(nameof(Title), "");
    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<LibraryRow, IImage?>(nameof(Icon));
    public static readonly StyledProperty<Geometry?> IconSymbolProperty =
        AvaloniaProperty.Register<LibraryRow, Geometry?>(nameof(IconSymbol));

    public static IValueConverter IconCornerRadius { get; } = new FuncValueConverter<Rect, CornerRadius>(
        bounds => new CornerRadius(Math.Min(bounds.Width, bounds.Height) * 0.2));

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public IImage? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Geometry? IconSymbol { get => GetValue(IconSymbolProperty); set => SetValue(IconSymbolProperty, value); }
}

public sealed class GameCard : TemplatedControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<GameCard, string>(nameof(Title), "");
    public static readonly StyledProperty<string> PlaytimeProperty =
        AvaloniaProperty.Register<GameCard, string>(nameof(Playtime), "");
    public static readonly StyledProperty<bool> ShowPlaytimeProperty =
        AvaloniaProperty.Register<GameCard, bool>(nameof(ShowPlaytime), true);
    public static readonly StyledProperty<bool> IsModdedProperty =
        AvaloniaProperty.Register<GameCard, bool>(nameof(IsModded));
    public static readonly StyledProperty<IBrush> ModIndicatorBrushProperty =
        AvaloniaProperty.Register<GameCard, IBrush>(nameof(ModIndicatorBrush), Brushes.White);
    public static readonly StyledProperty<IImage?> ArtworkProperty =
        AvaloniaProperty.Register<GameCard, IImage?>(nameof(Artwork));
    public static readonly StyledProperty<Stretch> ArtworkStretchProperty =
        AvaloniaProperty.Register<GameCard, Stretch>(nameof(ArtworkStretch), Stretch.UniformToFill);
    public static readonly StyledProperty<double> ArtworkHeightProperty =
        AvaloniaProperty.Register<GameCard, double>(nameof(ArtworkHeight), 267);
    public static readonly StyledProperty<IBrush?> ArtworkBackgroundProperty =
        AvaloniaProperty.Register<GameCard, IBrush?>(nameof(ArtworkBackground));
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<GameCard, ICommand?>(nameof(OpenCommand));

    public GameCard()
    {
        SizeChanged += (_, args) =>
        {
            if (args.WidthChanged && args.NewSize.Width > 2) ArtworkHeight = (args.NewSize.Width - 2) * 1.5;
        };
    }

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Playtime { get => GetValue(PlaytimeProperty); set => SetValue(PlaytimeProperty, value); }
    public bool ShowPlaytime { get => GetValue(ShowPlaytimeProperty); set => SetValue(ShowPlaytimeProperty, value); }
    public bool IsModded { get => GetValue(IsModdedProperty); set => SetValue(IsModdedProperty, value); }
    public IBrush ModIndicatorBrush { get => GetValue(ModIndicatorBrushProperty); set => SetValue(ModIndicatorBrushProperty, value); }
    public IImage? Artwork { get => GetValue(ArtworkProperty); set => SetValue(ArtworkProperty, value); }
    public Stretch ArtworkStretch { get => GetValue(ArtworkStretchProperty); set => SetValue(ArtworkStretchProperty, value); }
    public double ArtworkHeight { get => GetValue(ArtworkHeightProperty); private set => SetValue(ArtworkHeightProperty, value); }
    public IBrush? ArtworkBackground { get => GetValue(ArtworkBackgroundProperty); set => SetValue(ArtworkBackgroundProperty, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
}

public sealed class CollectionCard : Button
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<CollectionCard, string>(nameof(Title), "");
    public static readonly StyledProperty<string> GameCountProperty =
        AvaloniaProperty.Register<CollectionCard, string>(nameof(GameCount), "");
    public static readonly StyledProperty<bool> IsDynamicProperty =
        AvaloniaProperty.Register<CollectionCard, bool>(nameof(IsDynamic));

    protected override Type StyleKeyOverride => typeof(CollectionCard);

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string GameCount { get => GetValue(GameCountProperty); set => SetValue(GameCountProperty, value); }
    public bool IsDynamic { get => GetValue(IsDynamicProperty); set => SetValue(IsDynamicProperty, value); }
}

public sealed class ArtworkToolbar : TemplatedControl
{
    public static readonly StyledProperty<string> ZoomLabelProperty =
        AvaloniaProperty.Register<ArtworkToolbar, string>(nameof(ZoomLabel), "100%");
    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ArtworkToolbar, ICommand?>(nameof(CancelCommand));
    public static readonly StyledProperty<ICommand?> SaveCommandProperty =
        AvaloniaProperty.Register<ArtworkToolbar, ICommand?>(nameof(SaveCommand));

    public string ZoomLabel { get => GetValue(ZoomLabelProperty); set => SetValue(ZoomLabelProperty, value); }
    public ICommand? CancelCommand { get => GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
    public ICommand? SaveCommand { get => GetValue(SaveCommandProperty); set => SetValue(SaveCommandProperty, value); }
}
