using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Services;

namespace ShokoRenamerNN;

/// <summary>Utility methods for cleaning and formatting strings for the renaming engine.</summary>
public static partial class RenamerHelper
{
    #region Static Configuration

    /// <summary>Regex matching specific file tags to preserve and append to the final filename.</summary>
    [GeneratedRegex(
        @"\[(?i)(ptcen|unc|uncen|uncensored|cut|uncut|director's cut|original|rebroadcast|recap|[a-z]{3} documentary|documentary|\+?fvo|silent|native [a-z]{3}|raw|p?t?hardsubs [a-z]{3}|p?t?hardsubs|vertical|uhd|lq|watermark|cropped|logo|test|movie part\d\d?|part\d\d?)\]"
    )]
    private static partial Regex CustomFileTagsRegex();

    /// <summary>Regex for isolating and shifting common prefixes in folder names.</summary>
    [GeneratedRegex(@"^(Gekijou Henshuuban |Gekijou Soushuuhen |Gekijou Remix Ban |Gekijou Tanpen |Gekijouban 3D |Gekijouban |Eiga |OVA )(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CommonTitlePrefixesRegex();

    /// <summary>Regex for condensing multiple spaces into a single space.</summary>
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CondenseSpacesRegex();

    /// <summary>Regex for replacing double quotes with curly quotes.</summary>
    [GeneratedRegex("\"(.*?)\"")]
    private static partial Regex QuoteRegex();

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

    /// <summary>Set of extensions considered as image files for series-level sidecars.</summary>
    private static readonly FrozenSet<string> s_imageExtensions = ((string[])[".bmp", ".gif", ".jpe", ".jpeg", ".jpg", ".png", ".tbn", ".tif", ".tiff", ".webp"]).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set of extensions considered as episode-level sidecar files.</summary>
    private static readonly FrozenSet<string> s_sidecarExtensions = ((string[])[".nfo", ".xml", ".chp"]).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Suffixes for episode-level attachment folders.</summary>
    private static readonly FrozenSet<string> s_attachFolderSuffixes = ((string[])["_attach", "_attachments"]).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            var newEps = new Dictionary<int, (string? Title, string EpNumber)>();

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
            c = QuoteRegex().Replace(c, "“$1”");
        }

        if (config.ReplaceInvalidCharacters)
            c = string.Create(
                c.Length,
                c,
                (chars, state) =>
                {
                    for (int i = 0; i < state.Length; i++)
                        chars[i] = s_replacementCharMap.TryGetValue(state[i], out char m) ? m : state[i];
                }
            );

        return CondenseSpacesRegex().Replace(c, " ").Trim();
    }

    /// <summary>Extracts allowed custom tags from the original filename and concatenates them.</summary>
    /// <param name="originalFileName">The original filename.</param>
    /// <param name="config">The active renamer configuration.</param>
    /// <returns>A formatted string of tags with a leading space, or an empty string.</returns>
    public static string ExtractTags(string originalFileName, RenamerConfig config)
    {
        if (!config.PreserveCustomTags)
            return "";
        var matches = CustomFileTagsRegex().Matches(originalFileName);
        return matches.Count == 0 ? "" : " " + string.Join(" ", matches.Select(m => m.Value));
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
        var primaryEp = sortedEps[0];

        int zeros = primaryEp.Type != EpisodeType.Episode ? 1 : 2;
        string format = $"D{Math.Max(Math.Max(series.EpisodeCounts[primaryEp.Type], 1).ToString().Length, zeros)}";

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
            if (nums.Count == 1 || nums[^1] - nums[0] != nums.Count - 1)
                ranges.Add($"{prefix}{string.Join($" {prefix}", nums.Select(n => n.ToString(format)))}");
            else
                ranges.Add($"{prefix}{nums[0].ToString(format)}-{nums[^1].ToString(format)}");
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
        string folderName = config.MoveCommonPrefixes ? CommonTitlePrefixesRegex().Replace(title, "$2 — $1") : title;

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

    #region Relocation Helpers

    /// <summary>Checks whether a video file exists in Shoko and is explicitly flagged as ignored.</summary>
    /// <param name="filePath">The absolute path to the file.</param>
    /// <param name="videoService">The video service injected from Shoko.</param>
    /// <returns>True if the video file exists and is flagged as ignored; otherwise, false.</returns>
    private static bool IsIgnoredVideo(string filePath, IVideoService videoService)
    {
        try
        {
            return videoService.GetVideoFileByAbsolutePath(Path.GetFullPath(filePath))?.Video?.IsIgnored == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Checks whether a file path corresponds to an active, unignored episode video matched in Shoko.</summary>
    /// <param name="filePath">The absolute path to the file.</param>
    /// <param name="videoService">The video service injected from Shoko.</param>
    /// <returns>True if the file is tracked by Shoko, matched to an episode, and not marked as ignored; otherwise, false.</returns>
    private static bool IsMatchedActiveEpisode(string filePath, IVideoService videoService)
    {
        try
        {
            return videoService.GetVideoFileByAbsolutePath(Path.GetFullPath(filePath))?.Video is { CrossReferences.Count: > 0, IsIgnored: false };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Checks whether a folder contains any active episode videos matched and unignored by Shoko.</summary>
    /// <param name="dir">The absolute directory path to scan.</param>
    /// <param name="videoService">The video service injected from Shoko.</param>
    /// <returns>True if any file within the directory is an active matched episode; otherwise, false.</returns>
    private static bool ContainsActiveVideos(string dir, IVideoService videoService)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(dir, "*", options).Any(f => IsMatchedActiveEpisode(f, videoService));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Moves associated sidecar files, images, attachment folders, and series assets when a file is relocated.</summary>
    /// <param name="srcPath">The original full path of the file.</param>
    /// <param name="destPath">The new full path of the file.</param>
    /// <param name="sourceFolder">The source managed folder containing the file.</param>
    /// <param name="videoService">The video service injected from Shoko.</param>
    /// <param name="logger">The logger instance.</param>
    public static void RelocateSidecars(string srcPath, string destPath, IManagedFolder? sourceFolder, IVideoService videoService, ILogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(destPath))
                return;

            string cleanSrcPath = Path.GetFullPath(srcPath);
            string cleanDestPath = Path.GetFullPath(destPath);
            string srcDir = Path.GetDirectoryName(cleanSrcPath)!;
            string destDir = Path.GetDirectoryName(cleanDestPath)!;
            if (!Directory.Exists(srcDir))
                return;

            string srcBase = Path.GetFileNameWithoutExtension(cleanSrcPath);
            string destBase = Path.GetFileNameWithoutExtension(cleanDestPath);
            var cmp = StringComparison.OrdinalIgnoreCase;

            if (srcDir.Equals(destDir, cmp) && srcBase.Equals(destBase, cmp))
                return;
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            // Episode-level sidecars and attachment folders (safe to move out of any folder type)
            foreach (var entry in Directory.EnumerateFileSystemEntries(srcDir, srcBase + "*"))
            {
                if (Path.GetFullPath(entry).Equals(cleanSrcPath, cmp) || Path.GetFullPath(entry).Equals(cleanDestPath, cmp))
                    continue;

                string name = Path.GetFileName(entry);
                bool isDir = Directory.Exists(entry);
                string suffix = name[srcBase.Length..];

                if (isDir)
                {
                    if (s_attachFolderSuffixes.Contains(suffix))
                    {
                        string target = Path.Combine(destDir, destBase + suffix);
                        MoveDirectorySafely(entry, target, logger);
                        logger.LogInformation("Shoko Renamer NN: Relocated attachment folder -> \"{Old}\" to \"{New}\"", name, target);
                    }
                }
                else if (s_sidecarExtensions.Contains(Path.GetExtension(entry)) || s_imageExtensions.Contains(Path.GetExtension(entry)))
                {
                    string target = Path.Combine(destDir, destBase + suffix);
                    MoveFileSafely(entry, target, logger);
                    logger.LogInformation("Shoko Renamer NN: Relocated sidecar file -> \"{Old}\" to \"{New}\"", name, target);
                }
            }

            // Loose files and loose folders (only moved when relocating out of an existing destination managed folder)
            bool canMoveLoose = !srcDir.Equals(destDir, cmp) && sourceFolder is { } fld && fld.DropFolderType.HasFlag(DropFolderType.Destination) && !fld.DropFolderType.HasFlag(DropFolderType.Source);
            if (canMoveLoose && Directory.Exists(srcDir))
            {
                bool hasOtherActiveVideos = Directory
                    .EnumerateFiles(srcDir)
                    .Any(f => !Path.GetFullPath(f).Equals(cleanSrcPath, cmp) && !Path.GetFullPath(f).Equals(cleanDestPath, cmp) && videoService.IsAllowedVideoExtension(f) && !IsIgnoredVideo(f, videoService));

                if (!hasOtherActiveVideos)
                {
                    foreach (var dir in Directory.EnumerateDirectories(srcDir))
                    {
                        if (ContainsActiveVideos(dir, videoService))
                            continue;

                        string target = Path.Combine(destDir, Path.GetFileName(dir));
                        MoveDirectorySafely(dir, target, logger);
                        logger.LogInformation("Shoko Renamer NN: Relocated loose folder -> \"{Old}\" to \"{New}\"", Path.GetFileName(dir), target);
                    }

                    foreach (var file in Directory.EnumerateFiles(srcDir))
                    {
                        if (Path.GetFullPath(file).Equals(cleanSrcPath, cmp) || Path.GetFullPath(file).Equals(cleanDestPath, cmp))
                            continue;
                        if (videoService.IsAllowedVideoExtension(file) && !IsIgnoredVideo(file, videoService))
                            continue;

                        string target = Path.Combine(destDir, Path.GetFileName(file));
                        MoveFileSafely(file, target, logger);
                        logger.LogInformation("Shoko Renamer NN: Relocated loose file -> \"{Old}\" to \"{New}\"", Path.GetFileName(file), target);
                    }

                    CleanEmptyDirectories(srcDir, sourceFolder?.Path, logger);
                }
            }
        }
        catch (DirectoryNotFoundException)
        { /* Directory was already cleaned up or moved concurrently by Shoko core */
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shoko Renamer NN: Error relocating sidecars for {File}", srcPath);
        }
    }

    /// <summary>Safely moves a file, skipping if the destination already exists.</summary>
    /// <param name="src">The source file path.</param>
    /// <param name="dest">The destination file path.</param>
    /// <param name="logger">The logger instance.</param>
    private static void MoveFileSafely(string src, string dest, ILogger logger)
    {
        try
        {
            if (File.Exists(dest))
            {
                logger.LogDebug("Shoko Renamer NN: Destination file already exists, skipping move -> \"{Destination}\"", dest);
                return;
            }
            File.Move(src, dest);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shoko Renamer NN: Failed to move file -> \"{Source}\" to \"{Destination}\"", src, dest);
        }
    }

    /// <summary>Safely moves or merges a directory into a destination, skipping conflicting files.</summary>
    /// <param name="src">The source directory path.</param>
    /// <param name="dest">The destination directory path.</param>
    /// <param name="logger">The logger instance.</param>
    private static void MoveDirectorySafely(string src, string dest, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(src))
                return;

            // If the destination doesn't exist, try moving the entire directory at once
            if (!Directory.Exists(dest))
            {
                try
                {
                    Directory.Move(src, dest);
                    return;
                }
                catch
                { /* Fall back to entry enumeration if cross-filesystem or symlink move fails */
                }
            }

            // Fallback: merge into the existing destination directory
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(src, "*", options))
            {
                try
                {
                    string rel = Path.GetRelativePath(src, file);
                    string target = Path.Combine(dest, rel);
                    string? dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        logger.LogDebug("Shoko Renamer NN: Destination entry already exists, skipping move -> \"{Destination}\"", target);
                        continue;
                    }

                    File.Move(file, target);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Shoko Renamer NN: Could not move entry -> \"{File}\"", file);
                }
            }

            // Safely clean up the source directory structure (bottom-up), leaving behind any skipped files
            foreach (var dir in Directory.EnumerateDirectories(src, "*", options).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir, false);
                }
                catch
                { /* Ignore */
                }
            }
            try
            {
                if (!Directory.EnumerateFileSystemEntries(src).Any())
                    Directory.Delete(src, false);
            }
            catch
            { /* Ignore */
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shoko Renamer NN: Failed to move directory -> \"{Source}\" to \"{Destination}\"", src, dest);
        }
    }

    /// <summary>Recursively deletes empty directories starting from the target directory upwards until a non-empty directory or root is reached.</summary>
    /// <param name="dir">The directory to delete if empty.</param>
    /// <param name="rootPath">The root directory path that should never be deleted.</param>
    /// <param name="logger">The logger instance.</param>
    private static void CleanEmptyDirectories(string dir, string? rootPath, ILogger logger)
    {
        try
        {
            var cmp = StringComparison.OrdinalIgnoreCase;
            while (!string.IsNullOrEmpty(dir) && (string.IsNullOrEmpty(rootPath) || !dir.Equals(rootPath, cmp)) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, false);
                logger.LogInformation("Shoko Renamer NN: Cleaned up empty folder -> \"{Directory}\"", dir);
                dir = Path.GetDirectoryName(dir)!;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Shoko Renamer NN: Could not remove directory -> \"{Directory}\"", dir);
        }
    }

    #endregion
}
