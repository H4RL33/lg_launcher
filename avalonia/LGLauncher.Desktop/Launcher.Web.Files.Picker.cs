using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private async Task<string?> PickWebDownloadFolder()
    {
        var downloads = WebDownloadDirectory;
        var defaultDownloads = DefaultWebDownloadDirectory;
        string? folder = null;
        CancellationTokenSource? loading = null;
        var selectedPath = ModalTextField(null, "Selected folder");
        selectedPath.Name = "WebDownloadFolderPath";
        selectedPath.IsReadOnly = true;
        selectedPath.PlaceholderText = "Selected folder";
        selectedPath.UseTextEditingMenu();

        // The navigation callbacks are available after the dialog and its controls are created.
        WebFilesView view = null!;
        Button choose = null!;
        view = CreateWebFilesView(location => _ = BrowseAsync(location), () => folder,
            () => _ = BrowseAsync(folder ?? downloads));

        var footer = new Border { Name = "WebDownloadFolderFooter", BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Bind(Border.BackgroundProperty, this.GetResourceObservable("LG.Sidebar"));
        footer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LG.Line"));
        var scale = EffectiveInterfaceScale;
        var dialog = new ModalDialog("", Math.Max(1, Math.Min(850, ClientSize.Width / scale - 52)),
            footerLeading: selectedPath, content: view.Surface, footerPanel: footer)
        {
            Height = Math.Max(1, Math.Min(540, ClientSize.Height / scale - 52))
        };
        AutomationProperties.SetName(dialog, "Choose download folder");
        selectedPath.Margin = new Thickness(14, 10, 8, 10);
        dialog.Footer.Margin = new Thickness(0, 10, 14, 10);
        choose = DialogActions(dialog, "Use this folder", () => dialog.Close(folder != null), defaultAction: false);
        choose.IsEnabled = false;
        dialog.Closed += (_, _) => loading?.Cancel();

        var accepted = ShowModal(dialog);
        _ = BrowseAsync(downloads, initial: true);
        return await accepted ? folder : null;

        async Task BrowseAsync(string location, bool initial = false)
        {
            loading?.Cancel();
            using var request = new CancellationTokenSource();
            loading = request;
            choose.IsEnabled = false;
            selectedPath.Text = "";
            ToolTip.SetTip(selectedPath, null);
            view.Path.Text = location;
            view.Up.IsEnabled = false;
            view.Entries.ItemsSource = null;
            view.Message.Text = "Reading folder…";
            view.Message.IsVisible = true;
            try
            {
                var relativeTo = folder ?? downloads;
                var browseRoot = downloads;
                var (listing, stagingRoot) = await Task.Run(() => initial
                    ? StartWebFolderPicker(browseRoot, defaultDownloads, request.Token)
                    : (ListWebFiles(location, relativeTo, browseRoot, null, request.Token, foldersOnly: true), browseRoot),
                    request.Token);
                if (_closed || request.IsCancellationRequested) return;

                downloads = stagingRoot;
                if (listing.Readable) folder = listing.Folder;
                view.Path.Text = listing.Folder ?? location;
                view.Up.IsEnabled = folder != null && Path.GetDirectoryName(folder) != null;
                view.Entries.ItemsSource = listing.Entries;
                view.Message.Text = listing.Message;
                view.Message.IsVisible = listing.Message.Length > 0;
                choose.IsEnabled = listing.Readable && folder != null;
                if (listing.Readable && folder is { } currentFolder)
                {
                    selectedPath.Text = currentFolder;
                    selectedPath.CaretIndex = currentFolder.Length;
                    ToolTip.SetTip(selectedPath, currentFolder);
                }
                foreach (var pin in view.Surface.GetVisualDescendants().OfType<Button>().Where(button => button.Tag is string))
                {
                    pin.Classes.Set("selected", String.Equals(pin.Tag as string, folder, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(loading, request)) loading = null;
            }
        }
    }

    private static (WebFileListing Listing, string StagingRoot) StartWebFolderPicker(string downloads, string defaultDownloads,
        CancellationToken cancellation)
    {
        if (!WebQuickAccessPathComparer.Equals(downloads, defaultDownloads))
        {
            var current = ListWebFiles(downloads, downloads, downloads, null, cancellation, foldersOnly: true);
            if (current.Readable) return (current, downloads);
        }

        cancellation.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(defaultDownloads);
        }
        catch (Exception error) when (IsWebQuickAccessFailure(error))
        {
            // The normal folder read reports the access or availability problem in the picker.
        }
        return (ListWebFiles(defaultDownloads, defaultDownloads, defaultDownloads, null, cancellation, foldersOnly: true),
            defaultDownloads);
    }
}
