using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    internal async Task VerifyModsOnly(string preview)
    {
        var game = _library.Games[0];
        await VerifyMods(game, preview);
        Capture(preview);
    }

    private async Task VerifyMods(Game game, string preview)
    {
        var width = Width;
        var height = Height;
        var scale = _library.Preferences.InterfaceScalePercent;
        var previews = Path.GetDirectoryName(preview)!;
        try
        {
            Navigate(Page.Settings);
            UpdateLayout();
            var modsSettings = PageBody.Children.OfType<Button>().Single(button =>
                button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Mods"));
            Require(Equals(modsSettings.GetVisualDescendants().OfType<SymbolIcon>().First().Data, this.FindResource("LG.Icon.Wrench")),
                "The Mods settings row did not use the shared wrench.");
            Require(!RequestModButton.IsVisible, "The mod request action appeared on the main Settings page.");
            var requestScope = this.FindControl<TextBlock>("ModRequestScope");
            Require(requestScope is not { IsVisible: true },
                "The mod request scope appeared on the main Settings page.");
            Capture(Path.Combine(previews, "mods-settings-navigation.png"));
            var issueQuery = Uri.UnescapeDataString(ModRequestPage.Query);
            Require(ModRequestPage.Scheme == Uri.UriSchemeHttps && ModRequestPage.Host == "github.com" &&
                ModRequestPage.AbsolutePath == "/H4RL33/lg_launcher/issues/new" &&
                issueQuery.Contains("title=[Mod request]", StringComparison.Ordinal) &&
                issueQuery.Contains("## Official project link", StringComparison.Ordinal),
                "The mod request action did not have a prefilled issue draft.");
            Navigate(Page.Mods);
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(100);
                UpdateLayout();
                Require(RequestModButton.IsEffectivelyVisible && Equals(RequestModButton.Content, "Request a mod") &&
                    RequestModButton.Bounds.Width > 0 && RequestModButton.Bounds.Left >= PageTitle.Bounds.Right,
                    "The Mods page did not show its request action in the heading.");
                Require(requestScope is { IsEffectivelyVisible: true, TextWrapping: TextWrapping.Wrap, Text: { } scopeText } &&
                    scopeText.Contains("game-agnostic", StringComparison.Ordinal) &&
                    scopeText.Contains("Vortex", StringComparison.Ordinal) && requestScope.Bounds.Height > 0,
                    "The Mods page did not show its request scope below the heading.");
                var available = PageBody.GetVisualDescendants().OfType<HeaderedContentControl>()
                    .Single(panel => Equals(panel.Header, "Available mods"));
                var settingsMods = available.GetVisualDescendants().OfType<Button>().Select(AutomationProperties.GetName)
                    .OfType<string>().Where(name => name.StartsWith("About ", StringComparison.Ordinal)).Select(name => name[6..]).ToArray();
                VerifyModIcons(available);
                Capture(Path.Combine(previews, $"mods-settings-{size.Width}.png"));
                if (size.Width == 1230) await VerifyModInformation(previews);
                SelectGame(game);
                Require(!RequestModButton.IsVisible, "The mod request action stayed visible on game details.");
                Require(requestScope is not { IsVisible: true }, "The mod request scope stayed visible on game details.");
                await WaitFor(() => _detailsRequest == null, "The mod detail fixture artwork did not finish loading.");
                UpdateLayout();
                Require(GameModsButton.IsVisible && GameModsButton.Content is SymbolIcon wrench &&
                    Equals(wrench.Data, this.FindResource("LG.Icon.Wrench")) &&
                    AutomationProperties.GetName(GameModsButton) == "Mods" && GameModsButton.Bounds.Size == RefreshSteamButton.Bounds.Size,
                    "The game details did not show an accessible wrench matching the Steam refresh button size.");
                Capture(Path.Combine(previews, $"mods-details-{size.Width}.png"));
                await OpenModsList();
                var gameMods = ActiveModDialog!;
                Require(settingsMods.SequenceEqual(gameMods.Body.Children.OfType<Button>().Select(AutomationProperties.GetName)) &&
                    settingsMods.SequenceEqual(["OptiScaler", "REFramework", "ReShade", "RenoDX"]),
                    "The Settings and game available-mod lists did not show the same supported mods in order.");
                VerifyModIcons(gameMods);
                Capture(Path.Combine(previews, $"mods-list-{size.Width}.png"));
                DialogHost.DismissTop();
                Navigate(Page.Mods);
            }
            _library.Preferences.InterfaceScalePercent = 125;
            ApplyInterfaceScale();
            Width = MinWidth;
            Height = MinHeight;
            await Task.Delay(100);
            UpdateLayout();
            Capture(Path.Combine(previews, "mods-settings-125-percent.png"));
            SelectGame(game);
            await WaitFor(() => _detailsRequest == null, "The scaled mod detail fixture artwork did not finish loading.");
            Capture(Path.Combine(previews, "mods-details-125-percent.png"));
            _library.Preferences.InterfaceScalePercent = scale;
            ApplyInterfaceScale();
            Width = 1230;
            Height = 805;
            await Task.Delay(100);
            SelectGame(game);
            var optiScaler = await OpenModsList();
            optiScaler.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => ActiveModDialog is { } dialog && AutomationProperties.GetName(dialog) == "OptiScaler",
                "The OptiScaler list item did not open its compatibility dialog.");
            Capture(Path.Combine(previews, "mods-game-dialog.png"));
            DialogHost.DismissTop();
            Require(ActiveModDialog is { } list && AutomationProperties.GetName(list) == "Mods" &&
                list.Body.Children.OfType<Expander>().Single() is { IsVisible: false, IsExpanded: false },
                "Closing the OptiScaler dialog did not return to the list.");
            DialogHost.DismissTop();
            await VerifyModCatalogUi(game, previews);
            await VerifyReShadeCacheDelete(previews);
            await VerifyReShadeDialog(game, previews);
            await VerifyModDownloadProgress(previews);
            await VerifyModPreviewPreparation(game, previews);
            await VerifySetupModDownload(game, previews);
            await VerifyManagedMod(previews);
            await VerifyNewGameModRows(previews);
        }
        finally
        {
            _library.Preferences.InterfaceScalePercent = scale;
            ApplyInterfaceScale();
            Width = width;
            Height = height;
            Navigate(Page.Settings);
        }
    }

    private async Task VerifyReShadeCacheDelete(string previews)
    {
        var approved = ModPackageStore.Approved(ModProviderId.ReShade).Key;
        var standard = approved with { Variant = "standard x64 with Vulkan", Digest = new string('A', 64) };
        var older = standard with { Version = "6.7.3" };
        ModPackageKey[] packages = [approved, standard, older];
        var approvedLabel = $"{approved.Version} · {approved.Variant}";
        var standardLabel = $"{approved.Version} · standard x64 with Vulkan";
        const string olderLabel = "6.7.3 · standard x64 with Vulkan";
        foreach (var package in packages)
            SeedCachedModPackage(package);

        var windowSize = (Width, Height);
        try
        {
            Require(_modPackages.List(approved.Provider).Count == packages.Length,
                "The ReShade cache fixture did not seed the expected downloads.");
            Navigate(Page.Settings);
            Navigate(Page.Mods);
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                (Width, Height) = size;
                await Task.Delay(100);
                UpdateLayout();
                var button = CacheDelete();
                if (size.Width == 730)
                {
                    button.BringIntoView();
                    UpdateLayout();
                }
                Require(button.Content is SymbolIcon icon && Equals(icon.Data, this.FindResource("LG.Icon.Delete")) &&
                    Equals(button.Background, this.FindResource("LG.DestructiveFill")) &&
                    button.GetVisualParent() is WrapPanel row &&
                    row.Children.OfType<Button>().All(action => Math.Abs(action.Bounds.Center.Y - button.Bounds.Center.Y) <= 1),
                    "The ReShade delete action did not share the red icon and action row.");
                Capture(Path.Combine(previews, $"reshade-cache-row-{size.Width}.png"));
                var chooser = await OpenCacheChooser(button);
                Require(chooser.Body.Children.OfType<Button>().Select(AutomationProperties.GetName).ToHashSet()
                    .SetEquals([approvedLabel, standardLabel, olderLabel]),
                    "The ReShade cache chooser did not distinguish every version and variant.");
                Require(chooser.GetVisualDescendants().OfType<TextBlock>().Count(label => label.Text == "0 games use this version") == 3,
                    "The ReShade cache chooser did not report usage per package.");
                Capture(Path.Combine(previews, $"reshade-cache-chooser-{size.Width}.png"));
                chooser.Close();
            }

            var standardConfirmation = await SelectCachedDownload(standardLabel);
            ConfirmedPackage(standardConfirmation, standardLabel);
            DialogHost.DismissTop();
            Require(_modPackages.List(approved.Provider).Select(package => package.Key).ToHashSet().SetEquals(packages),
                "Cancelling cache deletion removed a ReShade download.");

            var approvedConfirmation = await SelectCachedDownload(approvedLabel);
            ConfirmedPackage(approvedConfirmation, approvedLabel);
            Capture(Path.Combine(previews, "reshade-cache-confirmation.png"));
            approvedConfirmation.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Delete cached copy"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => _modPackageMessages.GetValueOrDefault(approved.Provider) == "Cached copy deleted." &&
                !_modPackageOperations.ContainsKey(approved.Provider), "Deleting the selected ReShade cache entry did not finish.");
            Require(_modPackages.List(approved.Provider).Select(package => package.Key).ToHashSet().SetEquals([standard, older]),
                "Deleting one ReShade download did not preserve the other versions and variants.");
            (Width, Height) = (730, 510);
            await Task.Delay(100);
            UpdateLayout();
            var remainingDelete = CacheDelete();
            remainingDelete.BringIntoView();
            UpdateLayout();
            Require(remainingDelete.GetVisualParent() is WrapPanel remainingActions &&
                remainingActions.Children.OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Download ReShade") &&
                remainingActions.Children.OfType<Button>().All(action => Math.Abs(action.Bounds.Center.Y - remainingDelete.Bounds.Center.Y) <= 1),
                "The ReShade download and delete actions did not fit beside each other when the approved version was missing.");
            Capture(Path.Combine(previews, "reshade-cache-remaining-730.png"));
            var remaining = await OpenCacheChooser(remainingDelete);
            Require(remaining.Body.Children.OfType<Button>().Select(AutomationProperties.GetName).ToHashSet().SetEquals([standardLabel, olderLabel]),
                "The ReShade cache chooser did not refresh after deletion.");
            remaining.Close();
        }
        finally
        {
            foreach (var package in packages)
                await _modPackages.DeleteAsync(package, CancellationToken.None);
            _modPackageMessages.Remove(approved.Provider);
            (Width, Height) = windowSize;
        }

        Button CacheDelete()
        {
            var deleteButtons = PageBody.GetVisualDescendants().OfType<Button>().Where(button =>
                AutomationProperties.GetName(button)?.StartsWith("Delete cached ReShade", StringComparison.Ordinal) == true).ToArray();
            Require(deleteButtons.Length == 1,
                $"The ReShade row had {deleteButtons.Length} delete actions for its cached downloads.");
            return deleteButtons.Single();
        }

        async Task<ModalDialog> SelectCachedDownload(string label)
        {
            var chooser = await OpenCacheChooser(CacheDelete());
            chooser.Body.Children.OfType<Button>().Single(item => AutomationProperties.GetName(item) == label)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => ActiveModDialog is { } dialog &&
                AutomationProperties.GetName(dialog) == "Delete cached ReShade?", "Selecting a ReShade download did not request confirmation.");
            return ActiveModDialog!;
        }

        async Task<ModalDialog> OpenCacheChooser(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => ActiveModDialog is { } dialog && AutomationProperties.GetName(dialog) == "Delete cached ReShade downloads",
                "The ReShade cache chooser did not open.");
            UpdateLayout();
            return ActiveModDialog!;
        }

        static void ConfirmedPackage(ModalDialog confirm, string label)
        {
            Require(confirm.Body.Children.OfType<TextBlock>().Any(text => text.Text is { } message &&
                message.Contains(label, StringComparison.Ordinal) &&
                message.Contains("Games that already have ReShade installed will keep it.", StringComparison.Ordinal)),
                "The ReShade confirmation did not identify the chosen download and preserve existing installations.");
        }
    }

    private async Task VerifyReShadeDialog(Game game, string previews)
    {
        Navigate(Page.Mods);
        UpdateLayout();
        Require(PageBody.GetVisualDescendants().OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Check ReShade for updates"),
            "The Mods settings did not list ReShade.");
        SelectGame(game);
        await OpenModsList();
        var list = ActiveModDialog!;
        var row = list.Body.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "ReShade");
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var setup = ActiveModDialog!;
        UpdateLayout();
        Require(AutomationProperties.GetName(setup) == "Set up ReShade" &&
            setup.Body.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == game.Path) &&
            setup.Body.GetVisualDescendants().OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Graphics API" &&
                button.ContextMenu is FrostedContextMenu) &&
            setup.Footer.Children.OfType<Button>().Any(button => Equals(button.Content, "Preview install") && !button.IsEnabled),
            "The ReShade dialog did not show its game target, shared graphics API picker, and install preview.");
        var windowSize = (Width, Height);
        try
        {
            var api = setup.Body.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Graphics API");
            await ChoosePreference(api, "Vulkan");
            Require(setup.Body.Children.OfType<TextBlock>().Any(label => label.IsVisible &&
                label.Text?.Contains("without administrator privileges", StringComparison.Ordinal) == true),
                "The Vulkan setup did not explain the current-user layer scope.");
            setup.Body.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(toggle => AutomationProperties.GetName(toggle) == "I will use ReShade only for offline play").IsChecked = true;
            Require(setup.Footer.Children.OfType<Button>().Any(button => Equals(button.Content, "Preview install") && button.IsEnabled),
                "The ReShade preview could not be opened after choosing offline use.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(40);
                Capture(Path.Combine(previews, $"mods-reshade-{size.Width}.png"));
            }
            var target = new GameInstallTarget(Path.GetDirectoryName(game.Path)!, Path.GetFileName(game.Path), "fixture", default);
            var package = ModPackageStore.Approved(ModProviderId.ReShade).Key;
            var plan = new ModInstallPlan(target, [new(ModReShadeVulkan.ActivationFile, null, [], ModProviderId.ReShade)],
                [package], ReShadeVulkan: new(null));
            await VerifyVulkanPreview(plan, "Register a shared ReShade layer", "register");
            var manifest = Path.Combine(Program.DataDirectory, "External layer", ModPackageStore.ReShadeVulkanManifest);
            await VerifyVulkanPreview(plan with { ReShadeVulkan = new(new(manifest, package)) }, "Reuse the enabled ReShade layer", "reuse");
            await VerifyVulkanPreview(plan with { ReShadeVulkan = new(new(manifest, package, CanUpgrade: true)) },
                "Upgrade the shared ReShade layer", "upgrade");
            await VerifyReShadeChainPreview(target, package);

            var limited = new ModInstallationRecord(Guid.NewGuid(), target,
                [package with { Variant = "standard x64 with Vulkan" }],
                [new("dxgi.dll", ModPackageStore.StandardReShadeModuleDigest, ModProviderId.ReShade, null, null)], []);
            _modInspections[limited.Id] = new(limited, [], [], false, false);
            var managed = ShowManagedMod(limited, ModProviderId.ReShade);
            var manager = ActiveModDialog!;
            UpdateLayout();
            var upgrade = manager.Body.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Enable full add-on support"));
            Require(upgrade.IsVisible && upgrade.IsEnabled, "The ReShade manager did not offer the full add-on upgrade for the standard build.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                (Width, Height) = size;
                await Task.Delay(40);
                Capture(Path.Combine(previews, $"mods-reshade-manage-upgrade-{size.Width}.png"));
            }
            upgrade.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => ActiveModDialog is { } confirm && AutomationProperties.GetName(confirm) == "Enable full add-on support?",
                "The upgrade did not ask for confirmation.");
            DialogHost.DismissTop();
            await managed;
            _modInspections.Remove(limited.Id);
        }
        finally { (Width, Height) = windowSize; }
        DialogHost.DismissTop();
        Require(ActiveModDialog == list, "Closing ReShade did not return to the game's Mods list.");
        DialogHost.DismissTop();

        async Task VerifyVulkanPreview(ModInstallPlan plan, string expected, string action)
        {
            var completion = ShowModPlan(plan, () => false, (_, _) => throw new InvalidOperationException("The fixture must not install a Windows layer."));
            var install = ActiveModDialog!;
            UpdateLayout();
            var labels = install.Body.GetVisualDescendants().OfType<TextBlock>().Select(label => label.Text).ToArray();
            Require(labels.Any(label => label?.StartsWith(expected, StringComparison.Ordinal) == true) &&
                labels.Contains("Add ReShade.ini"), "The install preview did not identify the shared layer action and game activation file.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                (Width, Height) = size;
                await Task.Delay(40);
                Capture(Path.Combine(previews, $"mods-reshade-vulkan-{action}-{size.Width}.png"));
            }
            DialogHost.DismissTop();
            await completion;
        }

        async Task VerifyReShadeChainPreview(GameInstallTarget target, ModPackageKey package)
        {
            const string configuration = "OptiScaler.ini";
            var original = "[Plugins]\r\nLoadReshade=false\r\n"u8.ToArray();
            var before = ModFileSnapshot.For(original).Hash;
            var plan = new ModInstallPlan(target,
                [new(ModPackageStore.ReShadeModule, null, [], ModProviderId.ReShade),
                    new(configuration, before, ModDxgiConfiguration.WithReShade(original, "true"), ModProviderId.OptiScaler)],
                [package], ExistingInstallation: Guid.NewGuid(), ReShadeChain: new(configuration, "false", before));
            var completion = ShowModPlan(plan, () => false, (_, _) => throw new InvalidOperationException("The fixture must not install ReShade."));
            var install = ActiveModDialog!;
            UpdateLayout();
            var labels = install.Body.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var modified = labels.SingleOrDefault(label => label.Text == "Modified OptiScaler.ini");
            Require(modified != null && Equals(modified.Foreground, this.FindResource("LG.Modified")) &&
                modified.Parent is Grid row && row.Children.OfType<SymbolIcon>().Single() is { } symbol &&
                Equals(symbol.Data, this.FindResource("LG.Icon.Edit")) && Equals(symbol.Foreground, modified.Foreground) &&
                labels.Any(label => label.Text == $"Add {ModPackageStore.ReShadeModule}" &&
                    Equals(label.Foreground, this.FindResource("LG.Success"))),
                "The ReShade install preview did not distinguish a modified configuration from an added DLL.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                (Width, Height) = size;
                await Task.Delay(40);
                UpdateLayout();
                modified!.BringIntoView();
                await Task.Delay(40);
                Capture(Path.Combine(previews, $"mods-reshade-modified-file-{size.Width}.png"));
            }
            DialogHost.DismissTop();
            await completion;
        }
    }

    private async Task VerifyModPreviewPreparation(Game game, string previews)
    {
        SelectGame(game);
        await OpenModsList();
        var list = ActiveModDialog!;
        var snapshot = new Game { Title = game.Title, Path = game.Path, SteamId = game.SteamId };
        var assessment = new ModAssessment(ModCompatibilityStatus.Unknown, null);
        var preview = new TaskCompletionSource<ModPlanPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ModSetupRequest? request = null;
        var work = ShowModSetup(game, snapshot, assessment, list, (input, token) =>
        {
            calls++;
            request = input;
            return preview.Task.WaitAsync(token);
        });
        var setup = ActiveModDialog!;
        var next = PreviewButton(setup);
        var spoofing = FindDxgiSpoofingChoice(setup);
        Require(AutomationProperties.GetHelpText(spoofing) == "Current value Automatic", "Guided setup did not default to automatic DXGI spoofing.");
        var loader = setup.Body.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "OptiScaler DLL name");
        await VerifyModPicker(loader, previews, "optiscaler-loader");
        await ChoosePreference(loader, "d3d12.dll");
        await ChoosePreference(spoofing, "Enabled");
        var handedOff = false;
        setup.Closed += (_, _) => handedOff = ActiveModDialog is { } dialog &&
            AutomationProperties.GetName(dialog) == "Install OptiScaler";
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(calls == 1 && ActiveModDialog == setup && Equals(next.Content, "Preparing preview…") && !next.IsEnabled &&
            request?.DxgiSpoofing == ModDxgiSpoofing.Enabled && request.Loader == "d3d12.dll",
            "Preparing an install preview exposed the Mods list or dropped the selected options.");
        foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
        {
            Width = size.Width;
            Height = size.Height;
            await Task.Delay(100);
            UpdateLayout();
            Require(ActiveModDialog == setup && next.Bounds.Width > 0, "The preparing action was lost at a supported width.");
            Capture(Path.Combine(previews, $"mods-preview-preparing-{size.Width}.png"));
        }
        Width = 1230;
        Height = 805;
        var target = new GameInstallTarget(Path.GetDirectoryName(game.Path)!, Path.GetFileName(game.Path), "", DateTime.UtcNow);
        var package = ModPackageStore.Approved(ModProviderId.OptiScaler).Key;
        preview.SetResult(new(new(target, [new("dxgi.dll", null, [], package.Provider)], [package], DxgiSpoofing: ModDxgiSpoofing.Enabled), [], ""));
        await WaitFor(() => handedOff, "The preview did not open before setup was dismissed.");
        UpdateLayout();
        Require(ActiveModDialog!.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Enabled"),
            "The install preview did not show the selected spoofing mode.");
        DialogHost.DismissTop();
        await work;
        Require(ActiveModDialog == list, "Closing the preview did not return to the Mods list.");

        var cancelled = CancellationToken.None;
        var retry = new TaskCompletionSource<ModPlanPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recipe = new ModRecipe(game.SteamId, [target.Executable], target.Executable, target.Build,
            package.Version, package.Digest, "dxgi.dll", [], [], DisableDxgiSpoofing: true);
        var verified = new ModAssessment(ModCompatibilityStatus.Verified, null, recipe, target);
        work = ShowModSetup(game, snapshot, verified, list, (input, token) =>
        {
            cancelled = token;
            request = input;
            return retry.Task.WaitAsync(token);
        });
        setup = ActiveModDialog!;
        next = PreviewButton(setup);
        spoofing = FindDxgiSpoofingChoice(setup);
        Require(AutomationProperties.GetHelpText(spoofing) == "Current value Disabled", "Verified setup did not retain the recipe's spoofing default.");
        await ChoosePreference(spoofing, "Enabled");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(request?.DxgiSpoofing == ModDxgiSpoofing.Enabled, "Verified setup ignored the explicit spoofing selection.");
        setup.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await work;
        Require(cancelled.IsCancellationRequested && ActiveModDialog == list, "Cancelling setup did not stop the pending preview.");

        work = ShowModSetup(game, snapshot, assessment, list, (_, _) =>
            Task.FromResult(new ModPlanPreview(null, [ModProviderId.ReFramework], "")));
        setup = ActiveModDialog!;
        next = PreviewButton(setup);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(ActiveModDialog is { } download && AutomationProperties.GetName(download) == "Download mod packages" &&
            DialogHost.Children.Contains(setup), "The package prompt displaced the setup form before the preview was ready.");
        DialogHost.DismissTop();
        await WaitFor(() => ActiveModDialog == setup && Equals(next.Content, "Preview install") && next.IsEnabled,
            "Closing the package prompt did not return to the editable setup form.");
        DialogHost.DismissTop();
        await work;
        DialogHost.DismissTop();

        Button PreviewButton(ModalDialog dialog)
        {
            UpdateLayout();
            dialog.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(toggle => AutomationProperties.GetName(toggle) == "I will use OptiScaler only for offline play").IsChecked = true;
            return dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Preview install"));
        }
    }

    private async Task VerifySetupModDownload(Game game, string previews)
    {
        SelectGame(game);
        await OpenModsList();
        var package = ModPackageStore.Approved(ModProviderId.ReFramework).Key;
        var downloaded = new TaskCompletionSource<CachedModPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = ShowModDownloads([package.Provider], () => true, (provider, bar) =>
            DownloadModPackage(provider, (progress, _) =>
            {
                progress.Report(43);
                return downloaded.Task;
            }, bar));
        try
        {
            var dialog = ActiveModDialog!;
            dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Download"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => dialog.Body.Children.OfType<ProgressBar>().Any(bar =>
                    bar.Value == 43 && AutomationProperties.GetName(bar) == "REFramework download progress"),
                "Setup did not show the download progress in its dialog.");
            Capture(Path.Combine(previews, "mods-setup-download.png"));
        }
        finally
        {
            downloaded.SetResult(new(package, "", []));
        }
        Require(await work, "Setup did not continue after the package downloaded.");
        DialogHost.DismissTop();
    }

    private async Task VerifyModDownloadProgress(string previews)
    {
        Navigate(Page.Mods);
        UpdateLayout();
        var download = FindDownload();
        Require(download.IsEnabled && download.Classes.Contains("primary") &&
            download.Content is SymbolIcon icon && Equals(icon.Data, this.FindResource("LG.Icon.Download")) &&
            Equals(ToolTip.GetTip(download), "Download OptiScaler"),
            "The uncached package did not use an accessible symbolic download button.");
        var completion = new TaskCompletionSource<CachedModPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<int>? transfer = null;
        var work = DownloadModPackage(ModProviderId.OptiScaler, (progress, _) =>
        {
            transfer = progress;
            progress.Report(35);
            return completion.Task;
        });
        try
        {
            await WaitFor(() => FindProgress()?.Value == 35, "The OptiScaler card did not show its download progress.");
            var bar = FindProgress()!;
            Require(bar.Classes.Contains("lg-progress") && Equals(bar.Foreground, this.FindResource("LG.Accent")) &&
                bar.Height == 6 && AutomationProperties.GetName(bar) == "OptiScaler download progress",
                "The package bar did not use the accessible splash progress style.");
            transfer!.Report(62);
            await WaitFor(() => bar.Value == 62, "The visible bar did not update in place.");
            Navigate(Page.Settings);
            Navigate(Page.Mods);
            UpdateLayout();
            Require(FindProgress()?.Value == 62, "The download progress was lost when returning to Mods settings.");
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(100);
                UpdateLayout();
                var active = FindProgress()!;
                Require(active.Bounds.Width > 0 && active.Value == 62, "The progress bar was not visible at a supported width.");
                var action = FindDownload();
                Require(!action.IsEnabled && action.GetVisualParent() is WrapPanel row &&
                    row.Children.OfType<Button>().All(button => Math.Abs(button.Bounds.Center.Y - action.Bounds.Center.Y) <= 1),
                    "The download button stayed active during transfer or wrapped below the other package actions.");
                Capture(Path.Combine(previews, $"mods-download-{size.Width}.png"));
            }
        }
        finally
        {
            completion.SetResult(new(ModPackageStore.Approved(ModProviderId.OptiScaler).Key, "", []));
            await work;
            Width = 1230;
            Height = 805;
            UpdateLayout();
        }
        Require(FindProgress() == null, "The progress bar remained after the package operation completed.");

        ProgressBar? FindProgress() => PageBody.GetVisualDescendants().OfType<ProgressBar>().SingleOrDefault();
        Button FindDownload() => PageBody.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Download OptiScaler");
    }

    private async Task VerifyModCatalogUi(Game game, string previews)
    {
        Navigate(Page.Mods);
        UpdateLayout();
        var card = PageBody.GetVisualDescendants().OfType<HeaderedContentControl>().Single(control =>
            control.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "OptiScaler"));
        var refresh = card.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Check OptiScaler for updates");
        Require(refresh.IsEnabled && ToolTip.GetTip(refresh) is string tip && tip.Contains("Bundled compatibility list", StringComparison.Ordinal) &&
            !card.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Refresh compatibility")),
            "The OptiScaler refresh icon did not absorb the compatibility check and bundled fallback status.");

        using var client = new HttpClient(new ModVerification.ResponseHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ModVerification.CatalogFixture(2, status: "Notes", steamId: game.SteamId))
        }));
        SelectGame(game);
        var mod = await OpenModsList();
        await RefreshModCatalog(force: true, client);
        Require(mod.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "See compatibility notes"),
            "A catalog refresh did not update the open Mods list.");
        mod.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => ActiveModDialog is { } dialog && AutomationProperties.GetName(dialog) == "OptiScaler",
            "The refreshed report did not open.");
        Require(ActiveModDialog!.Body.Children.OfType<TextBlock>().Any(label => label.Text == "Catalog fixture note."),
            "The OptiScaler dialog did not show the new report.");
        Capture(Path.Combine(previews, "mods-catalog-report.png"));
        DialogHost.DismissTop();
        DialogHost.DismissTop();
        Navigate(Page.Mods);
        var catalogChecks = 0;
        using var offline = new HttpClient(new ModVerification.ResponseHandler(_ =>
        {
            catalogChecks++;
            return new(HttpStatusCode.ServiceUnavailable);
        }));
        await CheckModUpdates(ModProviderId.OptiScaler, _ => Task.FromResult(new ModPackageCheck("up-to-date", null)), offline);
        UpdateLayout();
        Require(catalogChecks == 1 && _optiScalerUpdateCheck is { IsEnabled: true } action && ToolTip.GetTip(action) is string status &&
            status.Contains("Catalog 2", StringComparison.Ordinal) && status.Contains("Could not refresh compatibility", StringComparison.Ordinal),
            "The combined check did not preserve the active catalog when offline.");
        Require(_modPackageMessages[ModProviderId.OptiScaler] == "Mod: up-to-date, Compat List: refresh failed (using current list)" &&
            PageBody.GetVisualDescendants().OfType<TextBlock>().Any(label =>
                label.Text?.Contains(" · Mod: up-to-date, Compat List:", StringComparison.Ordinal) == true),
            "The combined update result was missing from the package summary.");
        Capture(Path.Combine(previews, "mods-catalog-offline.png"));
        var reviewed = ModPackageStore.Approved(ModProviderId.ReShade);
        var release = new ModUpstreamRelease("99.0.0", new("https://reshade.me/#download"));
        await CheckModUpdates(ModProviderId.ReShade, _ => Task.FromResult(new ModPackageCheck("Newer release awaiting review", reviewed, release)));
        var savedSize = (Width, Height);
        try
        {
            foreach (var size in new[] { (730, 510), (1230, 805), (1600, 900) })
            {
                (Width, Height) = size;
                await Task.Delay(40);
                var releaseAction = PageBody.GetVisualDescendants().OfType<Button>().Single(button =>
                    AutomationProperties.GetName(button) == "View ReShade 99.0.0 release");
                Require(releaseAction.Content is SymbolIcon icon && Equals(icon.Data, this.FindResource("LG.Icon.ExternalLink")),
                    "The newer ReShade release did not have a symbolic link in Settings.");
                releaseAction.BringIntoView();
                Capture(Path.Combine(previews, $"mods-upstream-release-{size.Item1}.png"));
            }
        }
        finally
        {
            (Width, Height) = savedSize;
            _checkedModReleases.Remove(ModProviderId.ReShade);
            _checkedModDownloads.Remove(ModProviderId.ReShade);
            _modPackageMessages.Remove(ModProviderId.ReShade);
            RefreshModsSettings();
        }
    }

    private async Task VerifyManagedMod(string previews)
    {
        var root = Path.Combine(Program.DataDirectory, "managed-mod-game");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "fixture-mod.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var fixture = new Game { Title = "Mod fixture", Path = executable };
        _library.Games.Add(fixture);
        try
        {
            var key = ModPackageStore.Approved(ModProviderId.OptiScaler).Key;
            var framework = ModPackageStore.Approved(ModProviderId.ReFramework).Key;
            var target = GameInstallTarget.Capture(root, executable);
            var original = Path.Combine(root, "vendor.dll");
            var config = Path.Combine(root, "OptiScaler.ini");
            File.WriteAllText(original, "original fixture");
            var plan = new ModInstallPlan(target,
                [new("dxgi.dll", null, "managed fixture"u8.ToArray(), key.Provider),
                    new("vendor.dll", ModFileSnapshot.Read(original)!.Hash, "replacement fixture"u8.ToArray(), key.Provider),
                    new("OptiScaler.ini", null, "[Spoofing]\nDxgi=auto\nStreamlineSpoofing=auto\n"u8.ToArray(), key.Provider),
                    new("fakenvapi.dll", null, "additional fixture file"u8.ToArray(), key.Provider),
                    new("fakenvapi.ini", null, "additional fixture configuration"u8.ToArray(), key.Provider),
                    new("license.txt", null, "additional fixture document"u8.ToArray(), key.Provider),
                    new("dinput8.dll", null, "dependency fixture"u8.ToArray(), framework.Provider)], [key, framework]);
            SelectGame(fixture);
            await VerifyModInstallDialog(plan, previews);
            await VerifyCachedModDelete(fixture, key, previews);
            await VerifyManagedModsSettings(fixture, previews);
            SelectGame(fixture);
            var optiScaler = await OpenModsList();
            await WaitFor(() => optiScaler.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Installed v0.9.5"),
                "The OptiScaler list item did not show the verified installed files.");
            Capture(Path.Combine(previews, "mods-installed.png"));
            var modsList = ActiveModDialog!;
            var dependency = modsList.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "REFramework");
            await VerifyGameModWarnings(modsList, previews);
            Require(dependency.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == $"Installed {framework.Version}"),
                "The REFramework row did not show its installed version.");
            dependency.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var frameworkDialog = ActiveModDialog!;
            Require(AutomationProperties.GetName(frameworkDialog) == "Manage REFramework", "The dependency row did not open the shared manager.");
            UpdateLayout();
            var frameworkRemove = ManagedAction(frameworkDialog, "Uninstall REFramework");
            Require(ManagedAction(frameworkDialog, "Verify REFramework installation").IsVisible &&
                ManagedAction(frameworkDialog, "View affected files").IsVisible, "REFramework did not expose the shared management actions.");
            VerifyModFooter(frameworkDialog, framework.Version);
            Capture(Path.Combine(previews, "mods-dependency.png"));
            frameworkRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => ActiveModDialog is { } warning && AutomationProperties.GetName(warning) == "Uninstall REFramework?",
                "Removing REFramework did not open its confirmation.");
            Require(ActiveModDialog!.Body.Children.OfType<TextBlock>()
                .Any(label => label.Text?.Contains("may break these installed mods: OptiScaler", StringComparison.Ordinal) == true),
                "The uninstall confirmation did not identify the installed dependent.");
            Capture(Path.Combine(previews, "mods-dependency-warning.png"));
            DialogHost.DismissTop();
            await Task.Yield();
            Require(ActiveModDialog == modsList, "Closing the REFramework uninstall prompt did not return to the Mods list.");
            await VerifyInstalledSpoofing(optiScaler, config, previews);
            await WaitFor(() => _modInspectionTasks.Count == 0, "The inspection after saving DXGI spoofing did not finish.");
            File.WriteAllText(Path.Combine(root, "dxgi.dll"), "third-party changes");
            File.AppendAllText(config, "\n; external note\n");
            await RefreshSelectedModStatus();
            UpdateLayout();
            Require(ActiveModDialog == modsList &&
                optiScaler.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Review changes"),
                "The Mods list did not update after an external change to a managed file.");
            optiScaler.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => ActiveModDialog is { } dialog && AutomationProperties.GetName(dialog) == "Manage OptiScaler",
                "The OptiScaler list item did not open its managed install dialog.");
            var manage = ActiveModDialog!;
            await WaitFor(() => manage.Body.IsEnabled, "The management dialog did not load its controls.");
            UpdateLayout();
            var uninstall = ManagedAction(manage, "Uninstall OptiScaler");
            Require(uninstall.Content is SymbolIcon trash && Equals(trash.Data, this.FindResource("LG.Icon.Delete")) &&
                Equals(uninstall.Background, this.FindResource("LG.DestructiveFill")) && Equals(uninstall.BorderBrush, this.FindResource("LG.Error")),
                "The managed mod dialog did not offer the filled red uninstall button.");
            var check = ManagedAction(manage, "Verify OptiScaler installation");
            var status = ManagedAction(manage, "View affected files");
            Require(check.Content is SymbolIcon refresh && Equals(refresh.Data, this.FindResource("LG.Icon.Refresh")) &&
                status.Content is SymbolIcon info && Equals(info.Data, this.FindResource("LG.Icon.Info")) &&
                Equals(info.Foreground, this.FindResource("LG.Text")) &&
                uninstall.GetVisualParent() is WrapPanel actions && actions.Children.Take(3).SequenceEqual([uninstall, check, status]),
                "The management actions did not show uninstall, verification, and affected files in order.");
            await WaitFor(() => manage.GetVisualDescendants().OfType<Button>().Any(button =>
                AutomationProperties.GetName(button) == "DXGI hardware spoofing"), "The inline setting did not load.");
            Require(!manage.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text is "Changed files" or "Review changes") &&
                FindDxgiSpoofingChoice(manage).TranslatePoint(default, manage) is { } choiceOrigin &&
                manage.GetVisualDescendants().OfType<ToggleSwitch>().Single().TranslatePoint(default, manage) is { } archiveOrigin &&
                choiceOrigin.Y < archiveOrigin.Y, "The changed-file list or inline DXGI controls were misplaced.");
            Require(!manage.GetVisualDescendants().OfType<TextBlock>().Any(label =>
                label.Text?.Contains("REFramework", StringComparison.Ordinal) == true) &&
                !manage.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "REFramework usage guide")),
                "The OptiScaler dialog included REFramework details.");
            File.Delete(original);
            var configHash = ModFileSnapshot.Read(config)?.Hash;
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var verification = ActiveModDialog!;
            Require(AutomationProperties.GetName(verification) == "Verify OptiScaler installation" && DialogHost.Children.Contains(manage),
                "Verification did not open above the existing management dialog.");
            UpdateLayout();
            var progress = verification.Body.Children.OfType<ProgressBar>().Single();
            Require(progress.Classes.Contains("lg-progress"), "Verification did not use the shared progress bar.");
            await WaitFor(() => progress.Value == 100 && verification.Body.Children.OfType<TextBlock>().Any(label =>
                label.Text?.Contains("2 changed · 1 missing", StringComparison.Ordinal) == true), "Verification did not report changed and missing files.");
            Require(!File.Exists(original) && ModFileSnapshot.Read(config)?.Hash == configHash &&
                File.ReadAllText(Path.Combine(root, "dxgi.dll")) == "third-party changes", "Verification changed the installation.");
            Capture(Path.Combine(previews, "mods-verification-result.png"));
            DialogHost.DismissTop();
            await WaitFor(() => ActiveModDialog == manage && manage.Body.IsEnabled &&
                Equals(ToolTip.GetTip(status), "View affected files (3)"), "The management dialog retained stale verification results.");
            status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var affected = ActiveModDialog!;
            UpdateLayout();
            Require(AutomationProperties.GetName(affected) == "Affected files" &&
                affected.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Changed dxgi.dll" && Equals(label.Foreground, this.FindResource("LG.Warning"))) &&
                affected.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Missing vendor.dll" && Equals(label.Foreground, this.FindResource("LG.Error"))),
                "The status dialog did not identify changed and missing files with the existing colors.");
            Capture(Path.Combine(previews, "mods-affected-files.png"));
            DialogHost.DismissTop();
            Capture(Path.Combine(previews, "mods-review-changes.png"));
            uninstall.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => AutomationProperties.GetName(ActiveModDialog!) == "Uninstall OptiScaler?",
                "The remove symbol did not open its confirmation.");
            var changes = ActiveModDialog!.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Require(changes.Any(label => label.Text == "Remove fakenvapi.dll" && Equals(label.Foreground, this.FindResource("LG.Error"))) &&
                changes.Any(label => label.Text == "These mods will stay installed: REFramework.") &&
                changes.All(label => label.Text?.Contains("dinput8.dll", StringComparison.Ordinal) != true) &&
                changes.Any(label => label.Text == "Review dxgi.dll" && Equals(label.Foreground, this.FindResource("LG.Warning"))),
                "The uninstall preview did not distinguish removals from changed files that need review.");
            Capture(Path.Combine(previews, "mods-uninstall-preview.png"));
            DialogHost.DismissTop();
            DialogHost.DismissTop();
        }
        finally { _library.Games.Remove(fixture); }
    }

    private async Task VerifyGameModWarnings(ModalDialog dialog, string previews)
    {
        UpdateLayout();
        var warnings = dialog.Body.Children.OfType<WarningExpander>().Single();
        var header = warnings.GetVisualDescendants().OfType<ToggleButton>().Single();
        var message = ((StackPanel)warnings.Content!).Children.OfType<TextBlock>().Single();
        var surface = warnings.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "WarningSurface");
        var chevron = header.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "WarningChevronFace");
        var headerFace = header.GetVisualDescendants().OfType<ContentPresenter>().First();
        var viewport = warnings.GetVisualDescendants().OfType<Canvas>().Single(canvas => canvas.Name == "WarningReveal");
        var detail = warnings.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Content == warnings.Content);
        Require(warnings.IsVisible && !warnings.IsExpanded && !detail.IsVisible && header.IsChecked != true,
            "The warning details did not start collapsed.");
        Require(AutomationProperties.GetName(warnings) == "Warnings, 1 warning" && AutomationProperties.GetName(header) == "Warnings" &&
            header.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "1"),
            "The warning panel did not report one warning.");
        Require(
            header.GetVisualDescendants().OfType<SymbolIcon>().Any(icon => Equals(icon.Data, this.FindResource("LG.Icon.Warning"))) &&
            Equals(surface.Background, this.FindResource("LG.WarningFill")) && Equals(surface.BorderBrush, this.FindResource("LG.Warning")),
            "The warning panel did not use the shared symbol and colours.");

        var windowSize = (Width, Height);
        var motion = Classes.Contains("lg-motion");
        var partialClip = false;
        void ObserveReveal(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == WarningExpander.RevealProgressProperty && viewport.Height > 0 && viewport.Height < detail.Bounds.Height)
                partialClip = true;
        }

        warnings.PropertyChanged += ObserveReveal;
        try
        {
            Classes.Add("lg-motion");
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                (Width, Height) = (size.Width, size.Height);
                await Task.Delay(40);
                UpdateLayout();
                var inset = chevron.TranslatePoint(default, header)!.Value;
                Require(Math.Abs(inset.Y - (header.Bounds.Width - inset.X - chevron.Bounds.Width)) <= 1 &&
                    Math.Abs(inset.Y - (header.Bounds.Height - inset.Y - chevron.Bounds.Height)) <= 1 &&
                    Equals(chevron.CornerRadius, this.FindResource("LG.ControlRadius")),
                    "The warning chevron did not use equal edge gaps and the shared control radius.");
                Capture(Path.Combine(previews, $"mods-warnings-collapsed-{size.Width}.png"));
                var headerState = (IPseudoClasses)header.Classes;
                var chevronState = (IPseudoClasses)chevron.Classes;
                headerState.Add(":pointerover");
                try
                {
                    Require(headerFace.Background is ISolidColorBrush { Color.A: 0 } && chevron.Background is ISolidColorBrush { Color.A: 0 },
                        "Hovering the warning label highlighted the header or chevron.");
                    chevronState.Add(":pointerover");
                    Require(headerFace.Background is ISolidColorBrush { Color.A: 0 } && chevron.Background is ISolidColorBrush { Color.A: > 0 },
                        "Hovering the warning chevron did not highlight just its face.");
                    Capture(Path.Combine(previews, $"mods-warnings-chevron-hover-{size.Width}.png"));
                }
                finally
                {
                    chevronState.Remove(":pointerover");
                    headerState.Remove(":pointerover");
                }

                partialClip = false;
                header.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                await WaitFor(() => warnings.RevealProgress == 1, "The warning panel did not finish opening.");
                UpdateLayout();
                Require(partialClip && viewport.ClipToBounds && viewport.Bounds.Height == detail.Bounds.Height &&
                    warnings.IsExpanded && message.IsEffectivelyVisible &&
                    message.Text?.Contains("Configuration → Menu key", StringComparison.Ordinal) == true,
                    "Expanding Warnings did not clip and reveal the shared hotkey warning.");
                Capture(Path.Combine(previews, $"mods-warnings-expanded-{size.Width}.png"));
                partialClip = false;
                header.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Require(detail.IsVisible, "The warning detail vanished before it could collapse.");
                await WaitFor(() => warnings.RevealProgress == 0, "The warning panel did not finish closing.");
                Require(partialClip && !warnings.IsExpanded && !detail.IsVisible,
                    "The warning details did not collapse through an intermediate clip.");
            }

            Classes.Remove("lg-motion");
            header.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Require(warnings.RevealProgress == 1 && detail.IsVisible, "Warnings animated open with Reduced Motion enabled.");
            header.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Require(warnings.RevealProgress == 0 && !detail.IsVisible, "Warnings animated closed with Reduced Motion enabled.");
        }
        finally
        {
            warnings.PropertyChanged -= ObserveReveal;
            Classes.Set("lg-motion", motion);
            (Width, Height) = windowSize;
        }
    }

    private async Task VerifyCachedModDelete(Game game, ModPackageKey key, string previews)
    {
        SeedCachedModPackage(key);
        var windowSize = (Width, Height);
        var record = _modTransactions.FindForGame(game.Path).Single();
        try
        {
            Navigate(Page.Mods);
            var name = $"Delete cached OptiScaler {key.Version}";
            foreach (var width in new[] { 730, 1230, 1600 })
            {
                Width = width;
                await Task.Delay(40);
                UpdateLayout();
                var button = CachedDelete();
                Require(button.Content is SymbolIcon icon && Equals(icon.Data, this.FindResource("LG.Icon.Delete")) &&
                    Equals(button.Background, this.FindResource("LG.DestructiveFill")) &&
                    Equals(button.BorderBrush, this.FindResource("LG.Error")) && Equals(ToolTip.GetTip(button), name),
                    "The cached download did not use the shared red delete symbol.");
                Require(button.GetVisualParent() is WrapPanel row &&
                    row.Children.OfType<Button>().All(action => Math.Abs(action.Bounds.Center.Y - button.Bounds.Center.Y) <= 1),
                    "The cached package actions wrapped onto different rows.");
                Capture(Path.Combine(previews, $"mods-cache-delete-{width}.png"));
            }

            CachedDelete().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var confirm = DialogHost.Children.OfType<ModalDialog>().Last();
            Require(AutomationProperties.GetName(confirm) == "Delete cached OptiScaler?" &&
                confirm.Body.Children.OfType<TextBlock>().Any(label => label.Text is { } text &&
                    text.Contains("Games that already have OptiScaler installed will keep it.", StringComparison.Ordinal) &&
                    text.Contains("used to set up OptiScaler in other games", StringComparison.Ordinal)),
                "The deletion confirmation did not distinguish the cached download from existing game installations.");
            Capture(Path.Combine(previews, "mods-cache-delete-confirmation.png"));
            var confirmButton = confirm.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Delete cached copy"));
            Require(confirmButton.Classes.Contains("destructive") && !confirmButton.Classes.Contains("primary"),
                "The cache-delete confirmation did not use the red action style.");
            confirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => _modPackageMessages.GetValueOrDefault(key.Provider) == "Cached copy deleted." &&
                !_modPackageOperations.ContainsKey(key.Provider), "Deleting the cached copy did not finish.");
            Require(!_modPackages.List(key.Provider).Any(package => package.Key == key) &&
                _modTransactions.FindForGame(game.Path).Single().Id == record.Id &&
                File.Exists(Path.Combine(record.Target.Root, "dxgi.dll")), "Deleting the cached download changed the game's installation.");

            Button CachedDelete() => PageBody.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == name);
        }
        finally
        {
            await _modPackages.DeleteAsync(key, CancellationToken.None);
            (Width, Height) = windowSize;
        }
    }

    private static void SeedCachedModPackage(ModPackageKey key)
    {
        var entry = Path.Combine(Program.DataDirectory, "mods", "packages", key.StoreId);
        Directory.CreateDirectory(entry);
        File.WriteAllText(Path.Combine(entry, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Key = key, Files = new Dictionary<string, string>() }));
    }

    private static Button ManagedAction(ModalDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == name);

    private static void VerifyModFooter(ModalDialog dialog, string version)
    {
        var label = dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == version);
        var close = dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Close"));
        Require(label.TranslatePoint(new Point(0, label.Bounds.Height / 2), dialog) is { } labelCenter &&
            close.TranslatePoint(new Point(0, close.Bounds.Height / 2), dialog) is { } closeCenter &&
            labelCenter.X < closeCenter.X && Math.Abs(labelCenter.Y - closeCenter.Y) < 1 &&
            label.FontSize == close.FontSize && label.FontWeight == close.FontWeight && Equals(label.Foreground, close.Foreground),
            "The installed version was not styled and aligned with the Close footer.");
    }

    private async Task VerifyManagedModsSettings(Game game, string previews)
    {
        Navigate(Page.Mods);
        UpdateLayout();
        var managed = PageBody.GetVisualDescendants().OfType<HeaderedContentControl>()
            .Single(section => Equals(section.Header, "Managed installations"));
        var icon = managed.GetVisualDescendants().OfType<Image>().Single();
        await WaitFor(() => icon.Source != null, "The managed game did not load its library icon.");
        var libraryRow = LibraryGames.GetVisualDescendants().OfType<LibraryRow>().Single(row => row.Title == game.Title);
        var libraryIcon = libraryRow.GetVisualDescendants().OfType<Image>().Single();
        var matchesLibraryClip = HasProportionalClip(libraryIcon.GetVisualParent() as Border) &&
            HasProportionalClip(icon.GetVisualAncestors().OfType<Border>().FirstOrDefault());
        var path = managed.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(label => label.Text == game.Path);
        Require(matchesLibraryClip && path != null,
            $"The managed row should match the Library icon clip ({matchesLibraryClip}) and show the full executable path ({path != null}).");
        var title = managed.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Text == game.Title);
        var manage = managed.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Manage"));
        var sizeBefore = new Size(Width, Height);
        try
        {
            foreach (var size in new[] { new Size(730, 510), new Size(1230, 805), new Size(1600, 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(85);
                UpdateLayout();
                if (size.Width == 730) ContentScroll.Offset = new Vector(0, ContentScroll.Extent.Height);
                var gameRow = libraryRow.GetVisualAncestors().OfType<ListBoxItem>().First();
                Require(Math.Abs(gameRow.Bounds.Height - 32) < 1 && new[] { HomeNav, CollectionsNav, LibraryNav, RssNav, AddGame, SettingsNav }
                    .All(button => Math.Abs(button.Bounds.Height - gameRow.Bounds.Height) < 1),
                    "The Library game entry and sidebar buttons did not have a consistent height.");
                Require(icon.TranslatePoint(default, managed) is { } iconOrigin && title.TranslatePoint(default, managed) is { } titleOrigin &&
                    manage.TranslatePoint(default, managed) is { } actionOrigin && iconOrigin.X < titleOrigin.X && titleOrigin.X < actionOrigin.X &&
                    path?.Classes.Contains("lg-muted") == true, "The managed mod row did not align its game, path, and Manage button.");
                Capture(Path.Combine(previews, $"mods-managed-settings-{size.Width}.png"));
            }
            var record = _modTransactions.FindForGame(game.Path).Single();
            var waiting = new TaskCompletionSource<ModInstallInspection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _modInspections.Remove(record.Id);
            _modInspectionTasks[record.Id] = waiting.Task;
            try
            {
                manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(ActiveModDialog is { } list && AutomationProperties.GetName(list) == "Mods",
                    "Manage did not open the game's Mods list.");
                var first = ActiveModDialog!.Body.Children.OfType<Button>().First();
                Require(AutomationProperties.GetName(first) == "OptiScaler" &&
                    ((Grid)first.Content!).Children.OfType<SymbolIcon>().Any(icon =>
                        icon.IsVisible && Equals(icon.Data, this.FindResource("LG.Icon.Check"))),
                    "Installed OptiScaler did not appear first with a checkmark.");
                first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(ActiveModDialog is { } dialog && AutomationProperties.GetName(dialog) == "Manage OptiScaler",
                    "The manager waited for a file inspection before opening.");
                UpdateLayout();
                Require(!ManagedAction(ActiveModDialog!, "View affected files").IsEnabled,
                    "The manager presented unverified findings as current.");
                DialogHost.DismissTop();
                DialogHost.DismissTop();
            }
            finally
            {
                _modInspectionTasks.Remove(record.Id);
                waiting.SetCanceled();
            }
        }
        finally
        {
            Width = sizeBefore.Width;
            Height = sizeBefore.Height;
            UpdateLayout();
        }

        static bool HasProportionalClip(Border? frame) =>
            frame is { ClipToBounds: true, Bounds.Width: > 0, Bounds.Height: > 0 } &&
            frame.CornerRadius == new CornerRadius(Math.Min(frame.Bounds.Width, frame.Bounds.Height) * 0.2);
    }

    private async Task VerifyInstalledSpoofing(Button optiScaler, string config, string previews)
    {
        optiScaler.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => ActiveModDialog is { } dialog && AutomationProperties.GetName(dialog) == "Manage OptiScaler",
            "The installed mod did not open its management dialog.");
        UpdateLayout();
        var manage = ActiveModDialog!;
        await WaitFor(() => manage.GetVisualDescendants().OfType<Button>().Any(button =>
            AutomationProperties.GetName(button) == "DXGI hardware spoofing"), "The installed setting did not load in the management dialog.");
        var choice = FindDxgiSpoofingChoice(manage);
        var save = ManagedAction(manage, "Save DXGI setting");
        Require(save.Content is SymbolIcon tick && Equals(tick.Data, this.FindResource("LG.Icon.Check")) && !save.IsEnabled &&
            choice.GetVisualParent() is Grid inputs && save.GetVisualParent() == inputs && Grid.GetColumn(save) == 1,
            "The DXGI setting did not use an inline tick action.");
        VerifyModFooter(manage, ModPackageStore.Approved(ModProviderId.OptiScaler).Key.Version);
        Require(choice.Classes.Contains("choice-picker") && choice.ContextMenu is FrostedContextMenu &&
            AutomationProperties.GetHelpText(choice) == "Current value Automatic", "The unified picker did not read the installed DXGI setting.");
        await ChoosePreference(choice, "Disabled");
        foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
        {
            Width = size.Width;
            Height = size.Height;
            await Task.Delay(100);
            UpdateLayout();
            Capture(Path.Combine(previews, $"mods-inline-spoofing-{size.Width}.png"));
        }
        Width = 1230;
        Height = 805;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => !_modFileOperation && manage.Body.IsEnabled && File.ReadAllText(config).Contains("Dxgi=false", StringComparison.Ordinal),
            "Saving the inline setting did not update the deployed INI.");
        Require(ActiveModDialog == manage && File.ReadAllText(config) == "[Spoofing]\nDxgi=false\nStreamlineSpoofing=auto\n",
            "Editing spoofing changed another setting or left the management dialog.");
        DialogHost.DismissTop();
    }

    private static Button FindDxgiSpoofingChoice(ModalDialog dialog) => ManagedAction(dialog, "DXGI hardware spoofing");

    private async Task VerifyModInstallDialog(ModInstallPlan plan, string previews)
    {
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = ShowModPlan(plan, () => true, async (progress, token) =>
        {
            progress.Report(44);
            await proceed.Task;
            return await _modTransactions.InstallAsync(plan, token, progress);
        });
        var dialog = ActiveModDialog!;
        try
        {
            foreach (var size in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(100);
                UpdateLayout();
                Capture(Path.Combine(previews, $"mods-install-preview-{size.Width}.png"));
                if (size.Width == 730)
                {
                    var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(view => view.Content == dialog.Body);
                    Require(scroll.Extent.Height > scroll.Viewport.Height, "The narrow install preview did not expose a scrollable file list.");
                    scroll.ScrollToEnd();
                    UpdateLayout();
                    Require(scroll.Offset.Y > 0, "The narrow preview could not scroll to the last file.");
                    Capture(Path.Combine(previews, "mods-install-preview-730-scrolled.png"));
                    scroll.Offset = default;
                }
            }
            var labels = dialog.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Require(labels.Any(label => label.Text == "Add dxgi.dll" && Equals(label.Foreground, this.FindResource("LG.Success"))) &&
                labels.Any(label => label.Text == "Replace vendor.dll" && Equals(label.Foreground, this.FindResource("LG.Warning"))) &&
                labels.Any(label => label.Text?.Contains("will be backed up", StringComparison.Ordinal) == true),
                "The install preview did not show the file changes and backup notice.");
            Require(dialog.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "OptiScaler usage guide")),
                "The install preview did not offer the upstream usage guide.");
            Width = 1230;
            Height = 805;
            await Task.Delay(100);
            UpdateLayout();
            dialog.Footer.Children.OfType<Button>().Single(button => Equals(button.Content, "Install")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => ActiveModDialog == dialog && dialog.Body.Children.OfType<ProgressBar>().Any(bar => bar.Value == 44),
                "Installation did not keep the preview dialog open with progress.");
            Capture(Path.Combine(previews, "mods-install-progress.png"));
        }
        finally
        {
            proceed.SetResult();
            Width = 1230;
            Height = 805;
        }
        await WaitFor(() => dialog.Body.Children.OfType<TextBlock>().Any(label => label.Text == "OptiScaler installed."),
            "The install dialog did not show the completed operation.");
        DialogHost.DismissTop();
        await work;
    }

    private ModalDialog? ActiveModDialog => DialogHost.Children.OfType<ModalDialog>().LastOrDefault();

    private async Task VerifyModInformation(string previews)
    {
        var panels = PageBody.GetVisualDescendants().OfType<HeaderedContentControl>().ToArray();
        Require(panels.Select(panel => panel.Header).SequenceEqual(["Available mods", "Managed installations"]),
            "Mods settings did not group packages and installations into separate panels.");
        var section = panels[0];
        foreach (var title in AvailableModProviders.Select(ModPackageStore.Name))
        {
            var info = section.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == $"About {title}");
            var label = section.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == title);
            Require(label.TranslatePoint(default, section) is { } textOrigin && info.TranslatePoint(default, section) is { } infoOrigin &&
                textOrigin.X < infoOrigin.X, $"The {title} actions were not to the right of its label.");
            Require(section.GetVisualDescendants().OfType<Button>().Any(button =>
                AutomationProperties.GetName(button) == $"Check {title} for updates" &&
                button.Content is SymbolIcon refresh && Equals(refresh.Data, this.FindResource("LG.Icon.Refresh"))),
                $"The {title} package check did not use the Steam refresh symbol.");
            Require(info.Content is SymbolIcon symbol && Equals(symbol.Data, this.FindResource("LG.Icon.Info")),
                $"The {title} settings card did not show its information icon.");
            if (title == "RenoDX")
            {
                Require(!section.GetVisualDescendants().OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Download RenoDX"),
                    "RenoDX offered a global download.");
                await VerifyRenoDxInformation(info, previews);
                continue;
            }
            info.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ToolTip.GetIsOpen(info) && ToolTip.GetTip(info) is TextBlock { Text.Length: > 20 },
                $"The {title} information button did not open its description.");
            if (title == "OptiScaler")
            {
                var popup = OpenedPopups.Where(candidate => candidate.PlacementTarget == info)
                    .Select(candidate => candidate.Child).OfType<ToolTip>().Single();
                await WaitFor(() => popup.Opacity == 1, "The mod description did not become visible.");
                CaptureControl(popup, Path.Combine(previews, "mods-settings-info.png"));
            }
            ToolTip.SetIsOpen(info, false);
        }
    }

    private async Task VerifyRenoDxInformation(Button info, string previews)
    {
        var size = (Width, Height);
        info.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = ActiveModDialog;
        try
        {
            Require(dialog != null && AutomationProperties.GetName(dialog) == "About RenoDX" &&
                dialog.Body.Children.OfType<TextBlock>().Any(label => label.Text is { } text &&
                    text.Contains("add-on files for each game", StringComparison.Ordinal) &&
                    text.Contains("game’s Mods list", StringComparison.Ordinal)),
                "RenoDX info did not open a modal describing game-specific downloads.");
            foreach (var window in new[] { (Width: 730, Height: 510), (Width: 1230, Height: 805), (Width: 1600, Height: 900) })
            {
                (Width, Height) = window;
                await Task.Delay(40);
                Capture(Path.Combine(previews, $"mods-renodx-info-{window.Width}.png"));
            }
        }
        finally
        {
            dialog?.Close();
            (Width, Height) = size;
        }
    }

    private async Task<Button> OpenModsList()
    {
        GameModsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => ActiveModDialog is { } dialog && AutomationProperties.GetName(dialog) == "Mods",
            "The wrench did not open the Mods list.");
        UpdateLayout();
        return ActiveModDialog!.Body.Children.OfType<Button>().Single(button =>
            button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "OptiScaler"));
    }

    private async Task VerifyNewGameModRows(string previews)
    {
        var root = Directory.CreateDirectory(Path.Combine(Program.DataDirectory, "new-mods-fixture")).FullName;
        var executable = Path.Combine(root, "re9.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var game = new Game { Title = "Resident Evil Requiem", SteamId = 3764200, Path = executable };
        _library.Games.Add(game);
        var (index, release) = ModVerification.RenoDxFixture(game.Title, game.SteamId, [1, 2, 3]);
        using var source = new HttpClient(new ModVerification.ResponseHandler(request => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri == ModRenoDxCatalog.Source ? index : release)
        }));
        var size = (Width, Height);
        try
        {
            Require(await _renoDxCatalog.RefreshAsync(CancellationToken.None, source), "The RenoDX UI fixture catalog did not load.");
            SelectGame(game);
            await OpenModsList();
            var list = ActiveModDialog!;
            var warnings = list.Body.Children.OfType<WarningExpander>().Single();
            warnings.IsExpanded = true;
            await WaitFor(() => warnings.RevealProgress == 1, "The dependency warnings did not open.");
            Require(warnings.Content is StackPanel messages && messages.Children.OfType<TextBlock>().Any(label =>
                label.Text?.Contains("OptiScaler needs REFramework", StringComparison.Ordinal) == true) &&
                messages.Children.OfType<TextBlock>().Any(label =>
                    label.Text?.Contains("RenoDX needs ReShade with full add-on support", StringComparison.Ordinal) == true),
                "The Mods list did not identify the missing REFramework and ReShade requirements.");
            foreach (var window in new[] { (730, 510), (1230, 805), (1600, 900) })
            {
                (Width, Height) = window;
                await Task.Delay(40);
                Capture(Path.Combine(previews, $"mods-required-{Width}.png"));
            }
            var framework = list.Body.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "REFramework");
            framework.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ActiveModDialog is { } setup && AutomationProperties.GetName(setup) == "Set up REFramework" &&
                setup.Footer.Children.OfType<Button>().Any(button => Equals(button.Content, "Preview install")),
                "REFramework did not expose standalone setup.");
            Capture(Path.Combine(previews, "mods-reframework-setup.png"));
            DialogHost.DismissTop();
            await Task.Yield();
            var renoDx = list.Body.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "RenoDX");
            renoDx.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ActiveModDialog?.Body.Children.OfType<TextBlock>().Any(label =>
                label.Text?.StartsWith("Install ReShade with full add-on support", StringComparison.Ordinal) == true) == true,
                "RenoDX setup did not explain the missing ReShade requirement.");
            DialogHost.DismissTop();
            await Task.Yield();

            var target = GameInstallTarget.Capture(root, executable);
            var reShade = ModPackageStore.Approved(ModProviderId.ReShade).Key;
            var legacy = ModPackageStore.Approved(ModProviderId.SpecialK).Key;
            await _modTransactions.InstallAsync(new(target,
                [new("dxgi.dll", null, "fixture ReShade"u8.ToArray(), reShade.Provider),
                    new("SpecialK64.dll", null, "fixture Special K"u8.ToArray(), legacy.Provider)],
                [reShade, legacy]), CancellationToken.None);
            RefreshGameModList(game);
            renoDx.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = ActiveModDialog!;
            UpdateLayout();
            var picker = dialog.Body.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "RenoDX add-on");
            await VerifyModPicker(picker, previews, "renodx-addon");
            await ChoosePreference(picker, "Fixture · SDR");
            DialogHost.DismissTop();
            await Task.Yield();
            var steamId = game.SteamId;
            game.SteamId = 1;
            renoDx.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ActiveModDialog?.Body.Children.OfType<MediaFileField>().Any(field => field.FileLabel == "RenoDX add-on") == true,
                "RenoDX setup did not offer local import when the game had no automatic download.");
            foreach (var window in new[] { (730, 510), (1230, 805), (1600, 900) })
            {
                (Width, Height) = window;
                await Task.Delay(40);
                Capture(Path.Combine(previews, $"mods-renodx-import-{Width}.png"));
            }
            DialogHost.DismissTop();
            await Task.Yield();
            game.SteamId = steamId;
            var legacyRow = list.Body.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single();
            Require(AutomationProperties.GetName(legacyRow) == "Special K" && list.Body.Children.OfType<Button>().Count() == 4,
                "A legacy Special K installation appeared as an available mod or could not be managed.");
            legacyRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ActiveModDialog is { } manager && AutomationProperties.GetName(manager) == "Manage Special K",
                "The legacy Special K row did not open its manager.");
            DialogHost.DismissTop();
            DialogHost.DismissTop();
        }
        finally
        {
            (Width, Height) = size;
            _library.Games.Remove(game);
        }
    }

    private async Task VerifyModPicker(Button picker, string previews, string name)
    {
        var menu = PreferenceMenu(picker);
        var size = (Width, Height);
        try
        {
            foreach (var window in new[] { (730, 510), (1230, 805), (1600, 900) })
            {
                (Width, Height) = window;
                await Task.Delay(40);
                UpdateLayout();
                Capture(Path.Combine(previews, $"mods-{name}-{Width}.png"));
                picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => menu.IsOpen && menu.RevealProgress == 1, "The mod picker menu did not open.");
                VerifyMenuTheme(menu, Path.Combine(previews, $"mods-{name}-menu-{Width}.png"));
                menu.Close();
                await WaitFor(() => !menu.IsOpen, "The mod picker menu did not close.");
            }
        }
        finally { (Width, Height) = size; }
    }

    private static void VerifyModIcons(Control view)
    {
        (string Title, string Icon)[] expected = [("OptiScaler", "scaling"), ("REFramework", "construction"),
            ("ReShade", "palette"), ("RenoDX", "lightbulb")];
        foreach (var (title, icon) in expected)
        {
            var label = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == title);
            var glyph = label.GetVisualAncestors().OfType<Grid>()
                .SelectMany(grid => grid.Children.OfType<SymbolIcon>()).FirstOrDefault();
            Require(glyph is { Width: 19, Height: 19, Data: not null } && Equals(glyph.Data, LucideIconCatalog.Find(icon)?.Geometry),
                $"The {title} row did not show its shared {icon} icon.");
        }
    }
}
