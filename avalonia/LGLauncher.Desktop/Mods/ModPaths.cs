namespace LGLauncher;

internal static class ModPaths
{
    internal static string Relative(string name)
    {
        var parts = name.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(character => character < 32 || "<>:\"|?*~".Contains(character)) ||
                IsDeviceName(part.Split('.')[0].TrimEnd(' ')))
                throw new InvalidDataException($"Unsafe file path: {name}");
        }
        return String.Join('/', parts);
    }

    private static bool IsDeviceName(string value)
    {
        var name = value.ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(name[3]);
    }

    internal static string Within(string root, string relative) => Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar));

    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    internal static void RejectReparsePoints(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (Attributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"A symbolic link or junction is in the path: {current}");
        }
    }
}
