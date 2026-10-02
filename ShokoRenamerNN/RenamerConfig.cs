using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Attributes;
using Shoko.Abstractions.Config.Enums;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;

namespace ShokoRenamerNN;

/// <summary>Configuration settings for Shoko Renamer NN.</summary>
public class RenamerConfig : IRelocationProviderConfiguration, IConfigurationWithNewFactory<RenamerConfig>
{
    /// <summary>The default destination folder for anime.</summary>
    [Display(Name = "Main Folder Name", Description = "The default destination folder for anime.")]
    [DefaultValue("Anime")]
    public string MainDestination { get; set; } = "Anime";

    /// <summary>The destination folder for 18 Restricted/Hentai content.</summary>
    [Display(Name = "18 Restricted Folder Name", Description = "The destination folder for anime tagged as '18 Restricted'.")]
    [DefaultValue("Hentai")]
    public string RestrictedDestination { get; set; } = "Hentai";

    /// <summary>Whether to preserve matched bracket tags from the original filename.</summary>
    [Display(Name = "Preserve Custom File Tags", Description = "Preserve custom tags like [uncensored] or [raw] from the original filename.")]
    [DefaultValue(true)]
    public bool PreserveCustomTags { get; set; } = true;

    /// <summary>Whether to move common prefixes like 'OVA' to the end of folder names.</summary>
    [Display(Name = "Move Common Title Prefixes", Description = "Shift prefixes like 'Gekijouban' or 'OVA' to the end of folder names for better sorting.")]
    [DefaultValue(true)]
    public bool MoveCommonPrefixes { get; set; } = true;

    /// <summary>Whether to use the actual title instead of '01' for ambiguous single-entry episodes.</summary>
    [Display(Name = "Rename Ambiguous Episodes", Description = "Use titles like '(Special)' instead of '01' for ambiguous single-entry episodes.")]
    [DefaultValue(true)]
    public bool RenameAmbiguousEpisodes { get; set; } = true;

    /// <summary>Whether to safely replace invalid path characters with visual Unicode equivalents.</summary>
    [Display(Name = "Replace Invalid Characters", Description = "Safely replace characters like '?' and '/' with visual Unicode equivalents.")]
    [DefaultValue(true)]
    public bool ReplaceInvalidCharacters { get; set; } = true;

    /// <summary>Whether to apply stylistic replacements like fractions and arrows.</summary>
    [Display(Name = "Apply Stylistic Replacements", Description = "Apply stylistic replacements for fractions (e.g. 1/2 -> ½) and arrows.")]
    [DefaultValue(true)]
    public bool ApplyStylisticReplacements { get; set; } = true;

    /// <summary>CSV mappings for series and episode overrides.</summary>
    [Display(Name = "Overrides Script", Description = "CSV mappings for series and episode overrides.")]
    [CodeEditor(CodeEditorLanguage.Yaml)]
    [Visibility(Size = DisplayElementSize.Full)]
    public string Script { get; set; } =
        "### Shoko Renamer NN Overrides ###\n\n"
        + "# AniDB IDs (Series or Episode) semicolon delimited, Title, Episode Number (Only If Episode)\n"
        + "2840;2841,Akahori Gedou Hour Rabuge\n"
        + "147453,Toriko (2011),099\n";

    /// <summary>Creates a new default instance of the settings.</summary>
    public static RenamerConfig New(IConfigurationService configurationService, IPluginManager pluginManager) => new();
}
