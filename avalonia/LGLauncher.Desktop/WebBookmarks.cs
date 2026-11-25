using System.Text.Json;

namespace LGLauncher;

internal sealed record WebBookmark(string Title, string Url);

internal static class WebBookmarks
{
    private const string FileName = "bookmarks.json";

    public static List<WebBookmark> Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return [];

        return (JsonSerializer.Deserialize<List<WebBookmark?>>(File.ReadAllText(path)) ?? [])
            .OfType<WebBookmark>()
            .Where(bookmark => WebAddress.IsPage(bookmark.Url))
            .ToList();
    }

    public static void Save(string directory, IReadOnlyList<WebBookmark> bookmarks)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(bookmarks));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class WebAddress
{
    public static Uri? ParseInput(string? value, string? engine, string? customTemplate)
    {
        value = value?.Trim();
        var address = Parse(value);
        var template = SearchTemplate(engine, customTemplate);
        if (template == null || string.IsNullOrEmpty(value)) return address;

        if (address != null && (address.Scheme == "magnet" || address.IsLoopback ||
            value.IndexOfAny(['.', ':', '/', '?', '#']) >= 0))
        {
            return address;
        }

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (Uri.TryCreate(value, UriKind.Absolute, out _) || value.StartsWith('/') || value.StartsWith('\\') ||
            (schemeEnd > 0 && Uri.CheckSchemeName(value[..schemeEnd])))
        {
            return null;
        }

        var search = template.Replace("%s", Uri.EscapeDataString(value), StringComparison.Ordinal);
        return IsPage(search) ? new Uri(search) : null;
    }

    public static string? SearchTemplate(string? engine, string? customTemplate) => engine switch
    {
        "Google" => "https://www.google.com/search?q=%s",
        "DDG" => "https://duckduckgo.com/?q=%s",
        "DDG-NOAI" => "https://noai.duckduckgo.com/?q=%s",
        "Startpage" => "https://www.startpage.com/sp/search?query=%s",
        "Custom" when IsSearchTemplate(customTemplate) => customTemplate?.Trim(),
        _ => null,
    };

    public static bool IsSearchTemplate(string? value)
    {
        value = value?.Trim();
        return !string.IsNullOrEmpty(value) && !value.Any(char.IsWhiteSpace) &&
            value.Contains("%s", StringComparison.Ordinal) &&
            IsPage(value.Replace("%s", "search", StringComparison.Ordinal));
    }

    public static Uri? Parse(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace)) return null;
        if (IsMagnet(value)) return new Uri(value);
        if (value.Contains("://", StringComparison.Ordinal))
        {
            return IsPage(value) ? new Uri(value) : null;
        }
        if (value.StartsWith('/') || value.StartsWith('\\') || value.Contains('\\')) return null;

        var address = "https://" + value;
        return IsPage(address) ? new Uri(address) : null;
    }

    public static bool IsPage(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    public static bool IsMagnet(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase);
}
