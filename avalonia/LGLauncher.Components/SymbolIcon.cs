using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace LGLauncher.Components;

public sealed class SymbolIcon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<SymbolIcon, Geometry?>(nameof(Data));

    static SymbolIcon() => AffectsRender<SymbolIcon>(DataProperty, ForegroundProperty);

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Data == null || Foreground == null) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        using var position = context.PushTransform(Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2));
        using var scale = context.PushTransform(Matrix.CreateScale(size / 24, size / 24));
        var stroke = new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, stroke, Data);
    }
}
