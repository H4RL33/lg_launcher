using Avalonia.Media;

namespace LGLauncher.Components;

public sealed class LucideIcon
{
    private readonly Lazy<Geometry> _geometry;

    internal LucideIcon(string name, string keywords, string path)
    {
        Name = name;
        Label = name.Replace('-', ' ');
        Keywords = keywords;
        _geometry = new Lazy<Geometry>(() => StreamGeometry.Parse(path));
    }

    public string Name { get; }
    public string Label { get; }
    public string Keywords { get; }
    public Geometry Geometry => _geometry.Value;
}

public static class LucideIconCatalog
{
    private const string ResourceName = "LGLauncher.Components.Lucide.0.468.0.tsv";
    private static readonly Lazy<Catalog> _catalog = new(Load);

    public static IReadOnlyList<LucideIcon> Icons => _catalog.Value.Icons;

    public static LucideIcon? Find(string? name) =>
        String.IsNullOrEmpty(name) ? null : _catalog.Value.ByName.GetValueOrDefault(name);

    private static Catalog Load()
    {
        using var stream = typeof(LucideIconCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException("The pinned Lucide icon catalog is missing.");
        using var reader = new StreamReader(stream);
        var icons = new List<LucideIcon>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t', 3);
            icons.Add(new LucideIcon(fields[0], fields[1], fields[2]));
        }
        return new Catalog(icons.AsReadOnly(), icons.ToDictionary(icon => icon.Name, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record Catalog(IReadOnlyList<LucideIcon> Icons, Dictionary<string, LucideIcon> ByName);
}
