using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private Task<RssBlock[]>? _releaseNotesRequest;
    private HeaderedContentControl? _releaseNotesSection;

    private Control BuildReleaseNotes()
    {
        var (header, content) = BuildReleaseNotesContent(0);
        var section = Section("Release notes", content);
        section.Header = header;
        return _releaseNotesSection = section;
    }

    private void RefreshReleaseNotes()
    {
        if (_page != Page.About || _releaseNotesSection is not { } section) return;
        var selectedIndex = (section.Content as TabControl)?.SelectedIndex ?? 0;
        var (header, content) = BuildReleaseNotesContent(Math.Max(0, selectedIndex));
        section.Header = header;
        section.Content = content;
    }

    private (Control Header, Control Content) BuildReleaseNotesContent(int selectedIndex)
    {
        var installedVersion = UpdateFeed.CurrentVersion;
        var selectedVersion = installedVersion;
        var title = Label("");
        title.VerticalAlignment = VerticalAlignment.Center;
        var open = new Button { Classes = { "lg", "quiet" }, Content = Symbol("LG.Icon.ExternalLink") };
        ToolTip.SetPlacement(open, PlacementMode.BottomEdgeAlignedRight);
        Click(open, () => OpenReleasePage(selectedVersion));
        Grid.SetColumn(open, 1);
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8, Children = { title, open } };

        var current = CreateReleaseNotesBody();
        current.Children.Add(Label("Loading release notes…", muted: true));
        current.AttachedToVisualTree += (_, _) => _ = ShowCurrentReleaseNotes(current);

        if (_updateResult?.AvailableRelease is not { } release)
        {
            SelectRelease(installedVersion);
            return (header, current);
        }

        var available = CreateReleaseNotesBody();
        RenderReleaseNotes(available, ReadReleaseNotes(release.Markdown));
        var tabs = new TabControl
        {
            Classes = { "lg-tabs" },
            Items =
            {
                new TabItem { Header = "Current release", Classes = { "lg-tab" }, Content = current },
                new TabItem { Header = "Available release", Classes = { "lg-tab" }, Content = available }
            },
            SelectedIndex = selectedIndex
        };
        tabs.SelectionChanged += (_, _) => SelectRelease(tabs.SelectedIndex == 1 ? release.Version : installedVersion);
        SelectRelease(tabs.SelectedIndex == 1 ? release.Version : installedVersion);
        return (header, tabs);

        void SelectRelease(Version version)
        {
            selectedVersion = version;
            var tag = $"v{version.ToString(3)}";
            var action = $"View {tag} on GitHub";
            title.Text = $"Release notes · {tag}";
            AutomationProperties.SetName(open, action);
            ToolTip.SetTip(open, action);
        }
    }

    private static StackPanel CreateReleaseNotesBody() =>
        new() { Spacing = 10, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Left };

    private void OpenReleasePage(Version version)
    {
        try { Process.Start(new ProcessStartInfo(UpdateFeed.ReleasePage(version).AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { Status("Could not open the release in your browser."); }
    }

    private async Task ShowCurrentReleaseNotes(StackPanel body)
    {
        var request = _releaseNotesRequest ??= _verification
            ? Task.FromResult(Array.Empty<RssBlock>()) : LoadReleaseNotes(_background.Token);
        RssBlock[] blocks;
        try { blocks = await request; }
        catch (OperationCanceledException) { blocks = []; }
        if (blocks.Length == 0 && _releaseNotesRequest == request) _releaseNotesRequest = null;
        if (_closed || !body.IsAttachedToVisualTree()) return;
        RenderReleaseNotes(body, blocks);
    }

    private void RenderReleaseNotes(StackPanel body, RssBlock[] blocks)
    {
        body.Children.Clear();
        foreach (var block in blocks)
            body.Children.Add(BuildRssParagraph(block));
        if (blocks.Length == 0)
            body.Children.Add(Label("Release notes are unavailable for this version.", muted: true));
    }

    private static async Task<RssBlock[]> LoadReleaseNotes(CancellationToken token, HttpClient? client = null)
    {
        var markdown = await UpdateFeed.GetReleaseNotes(token, client).ConfigureAwait(false);
        return ReadReleaseNotes(markdown);
    }

    private static RssBlock[] ReadReleaseNotes(string? markdown) => markdown == null ? [] :
        RssMarkup.ReadMarkdown(markdown).Where(block => block.Runs is { Length: > 0 }).ToArray();
}
