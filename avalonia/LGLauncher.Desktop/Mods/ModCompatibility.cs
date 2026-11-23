using System.Text.Json;
using System.Text.Json.Serialization;

namespace LGLauncher;

internal enum ModCompatibilityStatus { Unknown, Reported, Dependency, Blocked, Verified }

internal sealed record ModCompatibilityEntry(string Title, int SteamId, ModCompatibilityStatus Status, string Page, string Inputs, string Note);

internal sealed record ModRecipeDependency(ModProviderId Provider, string Version, string Digest, string Destination, bool Optional = false);
internal sealed record ModRecipe(int SteamId, string[] LaunchExecutables, string Executable, string Build, string OptiScalerVersion,
    string OptiScalerDigest, string Loader, ModRecipeDependency[] Dependencies, Dictionary<string, string> OriginalHashes,
    string[] Omit, bool DisableDxgiSpoofing, string? Title = null);
internal sealed record ModAssessment(ModCompatibilityStatus Status, ModCompatibilityEntry? Entry, ModRecipe? Recipe = null,
    GameInstallTarget? Target = null, string? Reason = null);

internal sealed class ModCompatibility(ModCompatibilityEntry[] entries, ModRecipe[] recipes)
{
    private sealed record Catalog(string Revision, ModCompatibilityEntry[] Entries, ModRecipe[] Recipes);

    internal static ModCompatibility Load()
    {
        using var stream = typeof(ModCompatibility).Assembly.GetManifestResourceStream("LGLauncher.ModCatalog.json")!;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, options) ?? throw new InvalidDataException("The mod catalog is missing.");
        return new(catalog.Entries, catalog.Recipes);
    }

    internal ModAssessment Advisory(Game game)
    {
        var title = Normalize(game.Title);
        var match = entries.Where(entry => (entry.SteamId > 0 && entry.SteamId == game.SteamId) || Normalize(entry.Title) == title)
            .OrderByDescending(entry => entry.Status).FirstOrDefault();
        return new(match?.Status ?? ModCompatibilityStatus.Unknown, match);
    }

    internal ModAssessment Assess(Game game)
    {
        var advisory = Advisory(game);
        if (advisory.Status == ModCompatibilityStatus.Blocked || !File.Exists(game.Path)) return advisory;
        var path = Path.GetFullPath(game.Path);
        var title = Normalize(game.Title);
        foreach (var recipe in recipes.Where(recipe =>
            (recipe.SteamId > 0 && recipe.SteamId == game.SteamId) || (recipe.Title != null && Normalize(recipe.Title) == title)))
        {
            foreach (var launch in recipe.LaunchExecutables)
            {
                var suffix = Path.DirectorySeparatorChar + ModPaths.Relative(launch).Replace('/', Path.DirectorySeparatorChar);
                if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var root = path[..^suffix.Length];
                try
                {
                    var target = GameInstallTarget.Capture(root, ModPaths.Within(root, recipe.Executable));
                    if (target.Build.Equals(recipe.Build, StringComparison.OrdinalIgnoreCase) && !HasAntiCheat(target))
                        return advisory with { Status = ModCompatibilityStatus.Verified, Recipe = recipe, Target = target };
                }
                catch (Exception error) when (ModTransactions.ModFailure(error)) { }
            }
        }
        return advisory;
    }

    internal static bool HasAntiCheat(GameInstallTarget target)
    {
        string[] names = ["EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_EOS_Setup.exe", "BattlEye", "BEClient_x64.dll",
            "start_protected_game.exe", "BEService_x64.exe", "EAanticheat", "EAAC", "EAanticheat.GameServiceLauncher.exe",
            "ACE-Base64.dll", "vgk.sys"];
        return new[] { target.Root, Path.GetDirectoryName(target.ExePath)! }.Any(directory => names.Any(name =>
            ModPaths.Attributes(Path.Combine(directory, name)) != null));
    }

    internal static bool RequiresSpecialSetup(GameInstallTarget target) => Path.GetFileName(target.Executable).ToUpperInvariant() is
        "RE2.EXE" or "RE3.EXE" or "RE4.EXE" or "RE7.EXE" or "RE8.EXE" or "RE9.EXE" or "PRAGMATA.EXE" or
        "DEVILMAYCRY5.EXE" or "MONSTERHUNTERWILDS.EXE" or "MONSTERHUNTERRISE.EXE";

    internal static Uri WikiPage(string page) => new("https://github.com/optiscaler/OptiScaler/wiki/" +
        (String.IsNullOrWhiteSpace(page) ? "Compatibility-List" : Uri.EscapeDataString(page)));

    internal static string Normalize(string? title) => title == null ? "" : new(title.Replace("™", "").Replace("®", "")
        .Normalize(System.Text.NormalizationForm.FormKC).Where(Char.IsLetterOrDigit)
        .Select(Char.ToUpperInvariant).ToArray());
}
