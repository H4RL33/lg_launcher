using Avalonia.Controls;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private CancellationTokenSource? _updateInstall;
    private bool _automaticUpdatePending;
    private bool _automaticInstall;
    private bool _updateHandoff;
    private Button? _settingsInstallAction;
    private Button? _updateReleaseAction;

    private UpdateFeed.Package? AvailableInstaller => UpdateInstaller.SupportsSetupUpdates
        ? _updateResult?.SetupInstaller : UpdateInstaller.IsSupported ? _updateResult?.Installer : null;

    private Task<bool> OfferAutomaticUpdates()
    {
        var dialog = new ModalDialog("Enable automatic updates?", 475);
        dialog.Body.Children.Add(Label("LG Launcher can download and install official updates at startup, then restart when the update is ready."));
        dialog.Body.Children.Add(Label("You can change this any time in Settings → App Behaviour.", muted: true));
        var warning = Label("", muted: true);
        warning.IsVisible = false;
        dialog.Body.Children.Add(warning);
        var keepManual = ActionButton("Keep manual updates", () => dialog.Close(), "quiet");
        dialog.InitialFocus = keepManual;
        dialog.Footer.Children.Add(keepManual);
        dialog.Footer.Children.Add(ActionButton("Enable automatic updates", () =>
        {
            var preferences = _library.Preferences;
            if (SavePreference(preferences.AutomaticUpdates, true, value => preferences.AutomaticUpdates = value, null))
            {
                dialog.Close(true);
                return;
            }

            warning.Text = "Could not save your update preference. You can try again or keep manual updates.";
            warning.IsVisible = true;
        }, "primary"));
        return ShowModal(dialog);
    }

    private void RefreshUpdateActions()
    {
        var installable = AvailableInstaller != null;
        var busy = _updateInstall != null;
        UpdateAction.IsVisible = installable || busy;
        UpdateAction.Content = busy ? "Cancel update" : "Install update";
        UpdateAction.IsEnabled = !_updateHandoff;
        UpdateDownloadProgress.IsVisible = busy;
        MainShell.IsEnabled = ManageFolder.IsEnabled = !_updateHandoff;

        if (_settingsInstallAction != null)
        {
            _settingsInstallAction.IsVisible = installable;
            _settingsInstallAction.IsEnabled = !busy;
        }
        if (_updateReleaseAction != null)
            _updateReleaseAction.IsVisible = _updateResult?.ReleasePage != null && !installable;
    }

    private void TryAutomaticUpdate()
    {
        if (!_automaticUpdatePending || !_ready || _verification || _closed) return;
        if (!_library.Preferences.AutomaticUpdates) { _automaticUpdatePending = false; return; }
        if (_updateInstall != null || _modFileOperation || _playing.Count > 0 || _editingArtwork || DialogHost.HasModal) return;
        _automaticUpdatePending = false;
        _ = InstallUpdate(true);
    }

    private async Task InstallUpdate(bool automatic)
    {
        if (_updateInstall != null || AvailableInstaller is not { } package || !_ready || _closed) return;
        if (!CanRestartForUpdate()) return;

        var background = _background;
        var work = CancellationTokenSource.CreateLinkedTokenSource(background.Token);
        _updateInstall = work;
        _automaticInstall = automatic;
        var handedOff = false;
        UpdateInstaller.Staging? stage = null;
        UpdateDownloadProgress.Value = 0;
        RefreshUpdateActions();
        Status($"Downloading LG Launcher {package.Version}…");
        try
        {
            stage = UpdateInstaller.CreateStage(UpdateInstaller.IsSetupInstallation);
            var progress = new Progress<int>(value =>
            {
                if (!_closed && _updateInstall == work) UpdateDownloadProgress.Value = value;
            });
            await UpdateInstaller.Download(package, stage, progress, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (_closed || (automatic && !_library.Preferences.AutomaticUpdates)) return;
            if (!CanRestartForUpdate())
            {
                _automaticUpdatePending = automatic && DialogHost.HasModal;
                return;
            }

            _updateHandoff = true;
            RefreshUpdateActions();
            Status(stage.Setup ? "Closing LG Launcher to install the update through Windows Setup…" :
                "Restarting LG Launcher to install the update…");
            await Task.Run(() => UpdateInstaller.StartWorker(stage, package, work.Token), work.Token);
            handedOff = true;
            _allowClose = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                _automaticUpdatePending = automatic && _library.Preferences.AutomaticUpdates && background != _background;
                Status(_automaticUpdatePending ? "Update paused while a game is running." :
                    work.IsCancellationRequested ? "Update cancelled." : "The update timed out. Try again.");
            }
        }
        catch (Exception error) when (UpdateInstaller.ExpectedFailure(error) || error is HttpRequestException)
        {
            if (!_closed)
            {
                Status(error is UnauthorizedAccessException
                    ? "The update needs write access to the launcher folder."
                    : "Could not install the update. " + error.Message);
            }
        }
        finally
        {
            if (!handedOff && stage != null) await Task.Run(() => UpdateInstaller.Discard(stage));
            _updateInstall = null;
            _automaticInstall = false;
            _updateHandoff = false;
            work.Dispose();
            if (!_closed)
            {
                RefreshUpdateActions();
                TryAutomaticUpdate();
            }
        }
    }

    private bool CanRestartForUpdate()
    {
        if (_modFileOperation)
        {
            Status("Wait for the mod file operation to finish.");
            return false;
        }
        if (_playing.Count > 0)
        {
            Status("Close running games before installing an update.");
            return false;
        }
        if (_editingArtwork || DialogHost.HasModal)
        {
            Status("Finish editing before installing an update.");
            return false;
        }
        return !_saveFailed || Save();
    }
}
