using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace LGLauncher.Components;

public sealed class ColourPicker : StackPanel
{
    private readonly List<(Button Button, Color Colour)> _presets = [];
    private readonly Border _preview = Swatch(Colors.Transparent);
    private readonly TextBlock _error = new()
    {
        Text = "Enter six hex digits, such as #52B8AA.",
        Classes = { "lg-muted" },
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    public ColourPicker(Color defaultColour, Color currentColour)
    {
        Spacing = 10;
        var presets = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        AddPreset("Default", defaultColour);
        AddPreset("Rose", Color.FromRgb(234, 141, 160));
        AddPreset("Amber", Color.FromRgb(240, 188, 121));
        AddPreset("Green", Color.FromRgb(150, 199, 129));
        AddPreset("Teal", Color.FromRgb(82, 184, 170));
        AddPreset("Blue", Color.FromRgb(123, 162, 241));
        AddPreset("Pink", Color.FromRgb(220, 150, 224));
        Children.Add(presets);

        var hexLabel = new TextBlock { Text = "Hex colour", Classes = { "lg-muted" } };
        AutomationProperties.SetLabeledBy(HexField, hexLabel);
        AutomationProperties.SetName(HexField, "Hex colour");
        _preview.Name = "HexColourPreview";
        HexField.SizeChanged += (_, args) => _preview.Width = _preview.Height = args.NewSize.Height;
        Grid.SetColumn(_preview, 1);
        Children.Add(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                hexLabel,
                new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8, Children = { HexField, _preview } },
                _error
            }
        });
        HexField.Text = FormatHex(currentColour);
        HexField.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            _error.IsVisible = false;
            UpdatePreview();
        };
        UpdatePreview();

        void AddPreset(string name, Color colour)
        {
            var button = new Button
            {
                Classes = { "lg", "colour-preset" },
                Content = Swatch(colour)
            };
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
            button.Click += (_, _) => HexField.Text = FormatHex(colour);
            _presets.Add((button, colour));
            presets.Children.Add(button);
        }
    }

    public TextBox HexField { get; } = new() { Classes = { "lg" } };

    public bool TryGetColour(out Color colour)
    {
        if (TryParseHex(HexField.Text, out colour)) return true;
        _error.IsVisible = true;
        HexField.Focus();
        return false;
    }

    public static Border Swatch(Color colour) => new()
    {
        Classes = { "lg-colour-swatch" },
        Background = new SolidColorBrush(colour)
    };

    public static string FormatHex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    public static bool TryParseHex(string? text, out Color colour)
    {
        var digits = text.AsSpan().Trim();
        if (digits.StartsWith("#", StringComparison.Ordinal)) digits = digits[1..];
        if (digits.Length == 6 && Int32.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            colour = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        colour = default;
        return false;
    }

    private void UpdatePreview()
    {
        var valid = TryParseHex(HexField.Text, out var colour);
        _preview.Background = new SolidColorBrush(valid ? colour : Colors.Transparent);
        foreach (var preset in _presets)
        {
            preset.Button.Classes.Set("selected", valid && preset.Colour == colour);
        }
    }
}
