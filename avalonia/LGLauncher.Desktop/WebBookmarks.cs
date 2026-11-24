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
