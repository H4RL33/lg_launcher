using Avalonia.Media;
using SkiaSharp;

namespace LGLauncher;

internal static class ArtworkAccent
{
    internal static Color? FromPixels(ReadOnlySpan<byte> bgra)
    {
        const int hueBins = 24;
        var bins = new (double Weight, double Red, double Green, double Blue)[hueBins];
        for (var offset = 0; offset + 3 < bgra.Length; offset += 4)
        {
            var alpha = bgra[offset + 3];
            if (alpha < 224) continue;
            var blue = bgra[offset];
            var green = bgra[offset + 1];
            var red = bgra[offset + 2];
            var lightest = Math.Max(red, Math.Max(green, blue));
            var chroma = lightest - Math.Min(red, Math.Min(green, blue));
            if (lightest < 38 || chroma < 24) continue;

            new SKColor(red, green, blue).ToHsv(out var hue, out _, out _);
            var weight = Math.Min(chroma, 110) * alpha / 255d;
            ref var bin = ref bins[(int)(hue / (360f / hueBins)) % hueBins];
            bin.Weight += weight;
            bin.Red += weight * red;
            bin.Green += weight * green;
            bin.Blue += weight * blue;
        }

        var strongest = 0d;
        var selected = default(SKColor);
        // Group adjacent hues to keep colour variations within the same part of the artwork together.
        for (var index = 0; index < bins.Length; index++)
        {
            var previous = bins[(index + bins.Length - 1) % bins.Length];
            var current = bins[index];
            var next = bins[(index + 1) % bins.Length];
            var weight = previous.Weight + current.Weight + next.Weight;
            if (weight <= strongest) continue;
            strongest = weight;
            selected = new SKColor(
                (byte)Math.Round((previous.Red + current.Red + next.Red) / weight),
                (byte)Math.Round((previous.Green + current.Green + next.Green) / weight),
                (byte)Math.Round((previous.Blue + current.Blue + next.Blue) / weight));
        }
        if (strongest == 0) return null;

        selected.ToHsl(out var primaryHue, out var saturation, out var lightness);
        var tint = SKColor.FromHsl(primaryHue, Math.Clamp(saturation, 48, 78), Math.Clamp(lightness, 42, 76));
        return LightenTo(Color.FromRgb(tint.Red, tint.Green, tint.Blue), .15);
    }

    internal static Color ForDarkSurface(Color accent) => LightenTo(accent, .32);

    internal static Color TextOn(Color fill) => Contrast(fill, Colors.White) > Contrast(fill, Colors.Black) ? Colors.White : Colors.Black;

    internal static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    internal static Color Mix(Color start, Color end, double amount)
    {
        return Color.FromRgb(Channel(start.R, end.R), Channel(start.G, end.G), Channel(start.B, end.B));

        byte Channel(byte first, byte last) => (byte)Math.Round(first + (last - first) * amount);
    }

    private static Color LightenTo(Color color, double minimumLuminance)
    {
        while (Luminance(color) < minimumLuminance) color = Mix(color, Colors.White, .06);
        return color;
    }

    // The channels are linearised to measure contrast against the launcher's dark surfaces.
    private static double Luminance(Color color)
    {
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);

        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
    }
}
