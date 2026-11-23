using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LGLauncher.Components;

public sealed class MediaFileField : StackPanel
{
    private readonly string _defaultLabel;
    private readonly bool _hasCustomFile;
    private readonly string _customLabel;
    private readonly TextBlock _status;
    private readonly Button _reset;

    public MediaFileField(string label, string defaultLabel, bool hasCustomFile, string? fileName, string? advice, Func<Task<string?>> browse)
    {
        FileLabel = label;
        _defaultLabel = defaultLabel;
        _hasCustomFile = hasCustomFile;
        _customLabel = String.IsNullOrWhiteSpace(fileName) ? "Custom file" : fileName;
        Spacing = 5;
        var heading = new WrapPanel { ItemSpacing = 8, LineSpacing = 3 };
        heading.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (advice != null)
            heading.Children.Add(new TextBlock
            {
                Text = advice, Classes = { "lg-muted" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            });
        Children.Add(heading);
        _status = new TextBlock
        {
            Classes = { "lg-muted" }, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var choose = new Button { Classes = { "lg" }, Content = "Browse…" };
        _reset = new Button { Classes = { "lg", "quiet" }, Content = "Clear" };
        AutomationProperties.SetName(choose, "Browse for " + label.ToLowerInvariant());
        AutomationProperties.SetName(_reset, "Clear " + label.ToLowerInvariant());
        var row = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), ColumnSpacing = 8,
            Children = { _status, choose, _reset }
        };
        Grid.SetColumn(choose, 1);
        Grid.SetColumn(_reset, 2);
        Children.Add(row);
        choose.Click += async (_, _) =>
        {
            choose.IsEnabled = false;
            try
            {
                if (await browse() is not { } path) return;
                SelectFile(path);
            }
            finally { choose.IsEnabled = true; }
        };
        _reset.Click += (_, _) =>
        {
            SelectedPath = null;
            ResetRequested = _hasCustomFile;
            if (_reset.IsFocused) choose.Focus();
            UpdateStatus();
        };
        UpdateStatus();
    }

    public string FileLabel { get; }
    public string? SelectedPath { get; private set; }
    public bool ResetRequested { get; private set; }

    public void SelectFile(string path)
    {
        SelectedPath = path;
        ResetRequested = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _status.Text = SelectedPath != null ? Path.GetFileName(SelectedPath) :
            _hasCustomFile && !ResetRequested ? _customLabel : _defaultLabel;
        ToolTip.SetTip(_status, _status.Text);
        _reset.IsEnabled = SelectedPath != null || _hasCustomFile && !ResetRequested;
    }
}
