using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LGLauncher.Components;

namespace LGLauncher;

internal sealed partial class Launcher
{
    private static readonly string[] WebSearchEngines = ["None", "Google", "DDG", "DDG-NOAI", "Startpage", "Custom"];
    private static readonly string[] WebTorrentProtocols = ["IPv4", "IPv6", "IPv4+IPv6"];

    private Control BuildBrowserSettings()
    {
        var preferences = _library.Preferences;
        var engine = WebSearchEngines.Contains(preferences.WebSearchEngine) ? preferences.WebSearchEngine : WebSearchEngines[0];
        var custom = WebTextPreference("Custom search URL", preferences.WebSearchTemplate, "https://example.com/search?q=%s",
            "Use %s where the search terms go.", value => value == null || WebAddress.IsSearchTemplate(value) ? null :
                "Enter an HTTP or HTTPS search URL containing %s.",
            value => SavePreference(preferences.WebSearchTemplate, value, saved => preferences.WebSearchTemplate = saved, ApplyWebBrowserPreferences),
            disabledReason: () => preferences.WebSearchEngine == "Custom" ? null : "Select Custom to use your own search URL.");
        var search = BuildChoicePicker("Default search engine", WebSearchEngines, engine, name => name, name =>
            SavePreference(preferences.WebSearchEngine, name == "None" ? null : name,
                value => preferences.WebSearchEngine = value, ApplyWebBrowserPreferences), choiceIcon: WebSearchEngineIcon);

        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("Behaviour", PreferenceToggle("Show single tab", "Keep the tab strip visible with one open tab.",
                    !preferences.HideWebSingleTab, value => preferences.HideWebSingleTab = !value, UpdateWebTabStripVisibility)),
                Section("Privacy & Security", WebPreferenceRows(
                    PreferenceToggle("Enable uBO Lite", "Block ads and trackers.", !preferences.DisableWebAdBlocker,
                        value => preferences.DisableWebAdBlocker = !value, ApplyWebBrowserPreferences),
                    PreferenceToggle("Enable pop-up blocking", "Block pages from opening tabs automatically.",
                        !preferences.DisableWebPopupBlocking, value => preferences.DisableWebPopupBlocking = !value))),
                Section("Search & Home", WebPreferenceRows(
                    WebTextPreference("Default homepage", preferences.WebHomepage, "Empty",
                        "Open this address in new tabs.", value => value == null || WebAddress.Parse(value) is { Scheme: "http" or "https" }
                            ? null : "Enter an HTTP or HTTPS address.",
                        value => SavePreference(preferences.WebHomepage, value, saved => preferences.WebHomepage = saved, apply: null)),
                    PreferenceRow("Default search engine", search, "Search from the address bar."),
                    custom))
            }
        };
    }

    private SymbolIcon WebSearchEngineIcon(string engine)
    {
        var name = engine switch
        {
            "Google" => "search",
            "DDG" => "bird",
            "DDG-NOAI" => "bot-off",
            "Startpage" => "shield",
            "Custom" => "sliders-horizontal",
            _ => "circle-slash",
        };
        var icon = Symbol("LG.Icon.Search");
        icon.Data = LucideIconCatalog.Find(name)?.Geometry ?? icon.Data;
        return icon;
    }

    private Control BuildTransferSettings()
    {
        var preferences = _library.Preferences;
        var protocol = WebTorrentProtocols.Contains(preferences.WebTorrentProtocol) ? preferences.WebTorrentProtocol : WebTorrentProtocols[0];
        var protocols = BuildChoicePicker("Protocol", WebTorrentProtocols, protocol, name => name, name =>
        {
            if (ValidateTorrentBindAddress(preferences.WebTorrentBindAddress, name) is { } error)
            {
                _ = Notice("Cannot change protocol", error);
                return false;
            }
            return SavePreference(preferences.WebTorrentProtocol, name,
                value => preferences.WebTorrentProtocol = value, apply: null);
        });

        var initialRatio = preferences.WebTorrentStopRatio is >= 0 and var savedRatio && Double.IsFinite(savedRatio) ? savedRatio : WebTransfers.DefaultStopRatio;
        var ratio = new NumericUpDown
        {
            Value = initialRatio >= (double)decimal.MaxValue ? decimal.MaxValue : (decimal)initialRatio,
            Minimum = 0,
            Increment = .1m,
            FormatString = "0.0###########################",
            NumberFormat = CultureInfo.CurrentCulture.NumberFormat,
            Width = 118,
            HorizontalAlignment = HorizontalAlignment.Right,
            Classes = { "lg-number" }
        };
        AutomationProperties.SetName(ratio, "Stop ratio");
        WireNumberPreference(ratio, value =>
        {
            var requested = (double)value;
            return SavePreference(preferences.WebTorrentStopRatio, requested, saved => preferences.WebTorrentStopRatio = saved,
                () => _webTransfers?.SetStopRatio(requested));
        });
        return Section("Torrent", WebPreferenceRows(
            PreferenceToggle("Enable DHT", "Find IPv4 peers without a tracker. Applies after restarting the launcher.",
                !preferences.DisableWebDht, value => preferences.DisableWebDht = !value),
            WebTextPreference("Bind address", preferences.WebTorrentBindAddress, "Any address",
                "Local IP for peers and DHT. Applies after restarting the launcher.",
                value => ValidateTorrentBindAddress(value, preferences.WebTorrentProtocol ?? "IPv4"),
                value => SavePreference(preferences.WebTorrentBindAddress, value, saved => preferences.WebTorrentBindAddress = saved, apply: null)),
            PreferenceRow("Protocol", protocols, "Applies after restarting the launcher."),
            PreferenceRow("Stop ratio", ratio, "Seed until uploads reach this multiple of the downloaded data. Enter 0 to stop on completion.")));
    }

    private Control BuildFileViewerSettings()
    {
        var preferences = _library.Preferences;
        var path = Label(WebDownloadDirectory);
        path.TextWrapping = TextWrapping.NoWrap;
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        var downloads = new Button { Classes = { "lg" }, Content = path, HorizontalContentAlignment = HorizontalAlignment.Left };
        Click(downloads, () => _ = ChangeDownloadsDirectory());
        AutomationProperties.SetName(downloads, "Launcher downloads directory");
        ToolTip.SetTip(downloads, path.Text);
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Section("View", WebPreferenceRows(
                    PreferenceToggle("Show hidden files", "Include hidden files and folders.", !preferences.HideWebHiddenFiles,
                        value => preferences.HideWebHiddenFiles = !value, RefreshWebFiles),
                    PreferenceToggle("Show file extensions", "Include extensions in file names.", !preferences.HideWebFileExtensions,
                        value => preferences.HideWebFileExtensions = !value, RefreshWebFiles))),
                Section("Quick Access", PreferenceRow("Launcher downloads directory", downloads,
                    "Default download location and the destination of the Downloads shortcut."))
            }
        };

        async Task ChangeDownloadsDirectory()
        {
            var previous = WebDownloadDirectory;
            var directory = await PickWebDownloadFolder();
            if (directory == null || WebQuickAccessPathComparer.Equals(previous, directory)) return;

            var requested = WebQuickAccessPathComparer.Equals(directory, DefaultWebDownloadDirectory) ? null : directory;
            if (SavePreference(preferences.WebDownloadsDirectory, requested, value => preferences.WebDownloadsDirectory = value,
                () => ApplyWebDownloadDirectory(previous)))
            {
                path.Text = WebDownloadDirectory;
                ToolTip.SetTip(downloads, path.Text);
            }
        }
    }

    private static StackPanel WebPreferenceRows(params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.AddRange(rows);
        return panel;
    }

    private Control WebTextPreference(string title, string? current, string placeholder, string description,
        Func<string?, string?> validate, Func<string?, bool> save, Func<string?>? disabledReason = null)
    {
        var field = TextField(current, title);
        field.PlaceholderText = placeholder;
        field.UseTextEditingMenu();
        var error = Label("", muted: true);
        error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("LG.Error"));
        error.IsVisible = false;
        field.TextChanged += (_, _) => error.IsVisible = false;
        field.LostFocus += (_, _) => Commit();
        field.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Commit();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                field.Text = current;
                error.IsVisible = false;
                args.Handled = true;
            }
        };
        var row = PreferenceRow(title, WebPreferenceRows(field, error), description);
        if (disabledReason != null) BindPreferenceAvailability(row, field, disabledReason);
        return row;

        void Commit()
        {
            if (!field.IsEffectivelyEnabled) return;
            var value = field.Text?.Trim();
            if (string.IsNullOrEmpty(value)) value = null;
            var message = validate(value);
            if (message == null && current != value)
            {
                if (save(value)) current = value;
                else message = "Could not save this setting.";
            }
            error.Text = message;
            error.IsVisible = message != null;
            if (message == null) field.Text = current;
        }
    }

    private static string? ValidateTorrentBindAddress(string? value, string protocol)
    {
        if (value == null) return null;
        if (!IPAddress.TryParse(value, out var address) || address.IsIPv4MappedToIPv6)
            return "Enter a local IPv4 or IPv6 address, or leave this empty to use any address.";
        if ((protocol == "IPv4" && address.AddressFamily != AddressFamily.InterNetwork) ||
            (protocol == "IPv6" && address.AddressFamily != AddressFamily.InterNetworkV6))
            return "The bind address must match the selected protocol. Clear the address to use any address.";
        return null;
    }
}
