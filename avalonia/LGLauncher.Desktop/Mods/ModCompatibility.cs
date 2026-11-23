using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LGLauncher;

internal enum ModCompatibilityStatus { Unknown, Reported, Dependency, Blocked, Verified }

internal sealed record ModCompatibilityEntry(string Title, int SteamId, ModCompatibilityStatus Status, string Page, string Inputs, string Note,
    string[]? Aliases = null);

internal sealed record ModRecipeDependency(ModProviderId Provider, string Version, string Digest, string Destination, bool Optional = false);
internal sealed record ModRecipe(int SteamId, string[] LaunchExecutables, string Executable, string Build, string OptiScalerVersion,
    string OptiScalerDigest, string Loader, ModRecipeDependency[] Dependencies,
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
        var match = entries.Where(entry => (entry.SteamId > 0 && entry.SteamId == game.SteamId) || Normalize(entry.Title) == title ||
            entry.Aliases?.Any(alias => Normalize(alias) == title) == true)
            .OrderByDescending(entry => entry.Status).FirstOrDefault();
        return new(match?.Status ?? ModCompatibilityStatus.Unknown, match);
    }

    internal static ModDependencyLink[] OptiScalerRequirements(ModAssessment assessment, ModCatalogReport? report)
    {
        if (assessment.Status == ModCompatibilityStatus.Blocked) return [];
        if (assessment.Recipe is { } recipe)
            return recipe.Dependencies.Where(dependency => !dependency.Optional)
                .Select(dependency => new ModDependencyLink(ModProviderId.OptiScaler, dependency.Provider)).ToArray();

        var page = Normalize(String.IsNullOrEmpty(assessment.Entry?.Page) ? report?.Page : assessment.Entry.Page);
        var note = assessment.Entry?.Note ?? "";
        var upstreamNote = report?.Note ?? "";
        var variant = page is "DEVILMAYCRY5" or "RESIDENTEVIL22019" or "RESIDENTEVIL32020" or
            "RESIDENTEVIL42023" or "RESIDENTEVIL7BIOHAZARD" or "RESIDENTEVIL8VILLAGE" ||
            MentionsRequiredReFramework(note, "PDUPSCALER") || MentionsRequiredReFramework(upstreamNote, "PDUPSCALER");
        if (variant) return [new(ModProviderId.OptiScaler, ModProviderId.ReFramework, "pd-upscaler")];
        if (page is "MONSTERHUNTERWILDS" or "MONSTERHUNTERSTORIES3TWISTEDREFLECTION" or "RESIDENTEVIL9REQUIEM" or "PRAGMATA" ||
            MentionsRequiredReFramework(note) || MentionsRequiredReFramework(upstreamNote))
            return [new(ModProviderId.OptiScaler, ModProviderId.ReFramework)];
        return [];

        static bool MentionsRequiredReFramework(string value, string? variant = null) =>
            Regex.Matches(value, @"(?:^|[.!?]\s+)(?:this game\s+)?requires\s+[^.!?]*\bREFramework\b[^.!?]*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Any(match =>
                    variant == null || Normalize(match.Value).Contains(variant, StringComparison.Ordinal));
    }

    internal static bool SupportsReFramework(string? executable) => Path.GetFileNameWithoutExtension(executable)?.ToUpperInvariant() is
        "RE2" or "RE3" or "RE4" or "RE7" or "RE8" or "RE9" or "PRAGMATA" or "DEVILMAYCRY5" or
        "MONSTERHUNTERWILDS" or "MONSTERHUNTERRISE" or "MONSTERHUNTERSTORIES3" or "STREETFIGHTER6" or "DD2" or "STARFORCE";

    internal static bool CanGuideOptiScaler(ModAssessment assessment, ModDependencyLink[] requirements) =>
        assessment.Status != ModCompatibilityStatus.Blocked && (assessment.Recipe != null ||
            (assessment.Status != ModCompatibilityStatus.Dependency && requirements.Length == 0) ||
            (requirements is [{ Required: ModProviderId.ReFramework, Variant: null }] &&
                Normalize(assessment.Entry?.Page) != "PRAGMATA"));

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
