using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Shoko;

namespace ShokoRenamerNN;

/// <summary>Utility methods for cleaning and formatting strings for the renaming engine.</summary>
public static class RenamerHelper
{
    #region Static Configuration

    /// <summary>Regex matching specific file tags to preserve and append to the final filename.</summary>
    private static readonly Regex s_customFileTagsRegex = new(
        @"\[(?i)(ptcen|unc|uncen|uncensored|cut|uncut|director's cut|original|rebroadcast|recap|[a-z]{3} documentary|documentary|\+?fvo|silent|native [a-z]{3}|raw|p?t?hardsubs [a-z]{3}|p?t?hardsubs|vertical|uhd|lq|watermark|cropped|logo|test|movie part\d\d?|part\d\d?)\]",
        RegexOptions.Compiled
    );

    /// <summary>Regex for isolating and shifting common prefixes in folder names.</summary>
    private static readonly Regex s_commonTitlePrefixesRegex = new(
        @"^(Gekijou Henshuuban |Gekijou Soushuuhen |Gekijou Remix Ban |Gekijou Tanpen |Gekijouban 3D |Gekijouban |Eiga |OVA )(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>Regex for condensing multiple spaces into a single space.</summary>
    private static readonly Regex s_condenseSpacesRegex = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Regex for replacing double quotes with curly quotes.</summary>
    private static readonly Regex s_quoteRegex = new("\"(.*?)\"", RegexOptions.Compiled);

    /// <summary>Table of ambiguous single-entry titles.</summary>
    private static readonly FrozenSet<string> s_singleEntryTitles = ((string[])["Complete Movie", "Short Movie", "Music Video", "Special", "TV Special", "OAD", "OVA", "Web"]).ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Mapping of invalid Windows path characters to visual unicode equivalents.</summary>
    private static readonly FrozenDictionary<char, char> s_replacementCharMap = new Dictionary<char, char>
    {
        ['\\'] = '⧵',
        ['/'] = '⁄',
        [':'] = '꞉',
        ['*'] = '＊',
        ['?'] = '？',
        ['<'] = '＜',
        ['>'] = '＞',
        ['|'] = '｜',
    }.ToFrozenDictionary();

    /// <summary>Mapping of stylistic text replacements.</summary>
    private static readonly (string Find, string Replace)[] s_styledReplacements = [("1/2", "½"), ("1/6", "⅙"), ("-->", "→"), ("<--", "←"), ("->", "→"), ("<-", "←")];

    #endregion

    #region CSV Parsing

    private static string? s_lastScript;
    private static Dictionary<int, string> s_seriesOverrides = [];
    private static Dictionary<int, (string? Title, string EpNumber)> s_episodeOverrides = [];
    private static readonly Lock s_overrideLock = new();

    /// <summary>Parses the CSV script into series and episode overrides, utilizing a lock and string-hash cache to prevent redundant processing.</summary>
    /// <param name="script">The raw CSV script string.</param>
    /// <returns>A tuple containing series and episode override dictionaries.</returns>
    public static (Dictionary<int, string> Series, Dictionary<int, (string? Title, string EpNumber)> Episodes) GetOverrides(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return ([], []);

        lock (s_overrideLock)
        {
            if (s_lastScript == script)
                return (s_seriesOverrides, s_episodeOverrides);

            var newSeries = new Dictionary<int, string>();
            var newEps = new Dictionary<int, (string?, string)>();

            foreach (var line in script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#'))
                    continue;

                var parts = line.Split(',', 3, StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                    continue;

                var ids = parts[0].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out int parsed) ? parsed : 0).Where(id => id > 0).ToList();

                if (ids.Count == 0)
                    continue;

                if (parts.Length == 2)
                    foreach (var id in ids)
                        newSeries[id] = parts[1];
                else
                {
                    string? epTitle = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
                    foreach (var id in ids)
                        newEps[id] = (epTitle, parts[2]);
                }
            }

            s_seriesOverrides = newSeries;
            s_episodeOverrides = newEps;
            s_lastScript = script;

            return (newSeries, newEps);
        }
    }

    #endregion

    #region String Manipulation

    /// <summary>Cleans a series title by replacing illegal characters, styling text, and trimming whitespace.</summary>
    /// <param name="title">The raw series title.</param>
    /// <param name="config">The active renamer configuration.</param>
    /// <returns>A cleaned string safe for filenames.</returns>
    public static string CleanTitle(string title, RenamerConfig config)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        string c = title;
        if (config.ReplaceInvalidCharacters && c.StartsWith('.'))
            c = "․" + c[1..];

        if (config.ApplyStylisticReplacements)
        {
            foreach (var (f, r) in s_styledReplacements)
                c = c.Replace(f, r, StringComparison.Ordinal);
            c = s_quoteRegex.Replace(c, "“$1”");
        }

        if (config.ReplaceInvalidCharacters)
        {
            c = string.Create(
                c.Length,
                c,
                (chars, state) =>
                {
                    for (int i = 0; i < state.Length; i++)
                        chars[i] = s_replacementCharMap.TryGetValue(state[i], out var m) ? m : state[i];
                }
            );
        }

        return s_condenseSpacesRegex.Replace(c, " ").Trim();
    }

    /// <summary>Extracts allowed custom tags from the original filename and concatenates them.</summary>
    /// <param name="originalFileName">The original filename.</param>
    /// <param name="config">The active renamer configuration.</param>
    /// <returns>A formatted string of tags with a leading space, or an empty string.</returns>
    public static string ExtractTags(string originalFileName, RenamerConfig config)
    {
        if (!config.PreserveCustomTags)
            return "";

        var matches = s_customFileTagsRegex.Matches(originalFileName);
        if (matches.Count == 0)
            return "";

        var tags = matches.Select(m => m.Value).ToList();
        return " " + string.Join(" ", tags);
    }

    /// <summary>Generates a formatted episode number string, applying padding and handling special relations.</summary>
    /// <param name="episodes">All episodes linked to the file.</param>
    /// <param name="series">The primary series metadata.</param>
    /// <param name="config">The active renamer configuration.</param>
    /// <returns>A formatted episode number string.</returns>
    public static string GenerateEpisodeNumber(IReadOnlyList<IShokoEpisode> episodes, IShokoSeries series, RenamerConfig config)
    {
        if (episodes.Count == 0)
            return "";

        var sortedEps = episodes.OrderBy(e => e.Type == EpisodeType.Other ? int.MinValue : (int)e.Type).ThenBy(e => e.EpisodeNumber).ToList();
        var primaryEp = sortedEps.First();

        int zeros = primaryEp.Type != EpisodeType.Episode ? 1 : 2;
        int maxEps = Math.Max(series.EpisodeCounts[primaryEp.Type], 1);
        int pad = Math.Max(maxEps.ToString().Length, zeros);
        string format = $"D{pad}";

        var ranges = new List<string>();
        foreach (var group in sortedEps.GroupBy(e => e.Type))
        {
            string prefix = group.Key switch
            {
                EpisodeType.Special => "S",
                EpisodeType.Credits => "C",
                EpisodeType.Other => "O",
                EpisodeType.Parody => "P",
                EpisodeType.Trailer => "T",
                EpisodeType.Episode => "",
                _ => "U",
            };

            var nums = group.Select(e => e.EpisodeNumber).ToList();
            if (nums.Count == 1 || nums.Last() - nums.First() != nums.Count - 1)
                ranges.Add($"{prefix}{string.Join($" {prefix}", nums.Select(n => n.ToString(format)))}");
            else
                ranges.Add($"{prefix}{nums.First().ToString(format)}-{nums.Last().ToString(format)}");
        }

        string epNumber = string.Join(" ", ranges);

        if (sortedEps.Count == 2 && sortedEps[0].Type == EpisodeType.Episode)
        {
            if (sortedEps[1].Type == EpisodeType.Other)
                epNumber = $"O{sortedEps[1].EpisodeNumber} (E{sortedEps[0].EpisodeNumber.ToString(format)})";
            else if (sortedEps[1].Type == EpisodeType.Special)
                epNumber = $"{sortedEps[0].EpisodeNumber.ToString(format)} (S{sortedEps[1].EpisodeNumber})";
        }

        if (config.RenameAmbiguousEpisodes)
        {
            string epName = primaryEp.Titles.FirstOrDefault(t => t.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            if (primaryEp.EpisodeNumber == 1 && s_singleEntryTitles.Contains(epName))
                epNumber = $"({epName})";

            if (primaryEp.Type == EpisodeType.Episode && sortedEps.Count > 1 && epName.Contains("Part 1 of", StringComparison.OrdinalIgnoreCase))
                epNumber = $"{sortedEps[0].EpisodeNumber - 1:D2} (E{sortedEps[1].EpisodeNumber})";
        }

        return epNumber;
    }

    /// <summary>Formats the folder name by moving common prefixes and sanitizing trailing ellipses.</summary>
    /// <param name="title">The cleaned title.</param>
    /// <param name="config">The active renamer configuration.</param>
    /// <returns>A formatted folder name.</returns>
    public static string FormatFolderName(string title, RenamerConfig config)
    {
        string folderName = config.MoveCommonPrefixes ? s_commonTitlePrefixesRegex.Replace(title, "$2 — $1") : title;

        if (config.ReplaceInvalidCharacters)
        {
            if (folderName.EndsWith("......"))
                folderName = folderName[..^6] + "……";
            else if (folderName.EndsWith("..."))
                folderName = folderName[..^3] + "…";
            else if (folderName.EndsWith(".."))
                folderName = folderName[..^2] + "‥";
            else if (folderName.EndsWith("."))
                folderName = folderName[..^1] + "․";
        }

        return folderName.Trim();
    }

    #endregion
}
