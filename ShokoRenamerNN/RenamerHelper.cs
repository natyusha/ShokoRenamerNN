using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.FileSystem;
using Shoko.Abstractions.FileSystem.Services;
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
        @"\[(?:(?:pt)?cen(?:sored)?|unc(?:en(?:sored)?)?|(?:un)?cut|director's cut|original|rebroadcast|recap|(?:[a-z]{2,3} )?documentary|\+?fvo|silent|native [a-z]{2,3}|"
            + @"raw|(?:pt)?hardsubs(?: [a-z]{2,3})?|vertical|uhd|lq|watermark|cropped|logo|test|variation(?: [1-9])?|(?:v(?:ersion)? ?|(?:movie )?part ?)\d{1,2})\]",
        RegexOptions.IgnoreCase
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

    // csharpier-ignore
    /// <summary>Set of extensions considered as episode-level sidecar files.</summary>
    private static readonly FrozenSet<string> s_sidecarExtensions = ((string[])[
        ".nfo", ".xml", ".chp", ".ass", ".srt", ".ssa", ".smi", ".sub", ".idx", ".vtt", ".sup",
        ".mka", ".flac", ".opus", ".m4a", ".aac", ".ac3", ".eac3", ".dts", ".dtshd", ".mp3", ".ogg", ".oga", ".thd", ".wav"
    ]).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Suffixes for episode-level attachment folders.</summary>
    private static readonly FrozenSet<string> s_attachFolderSuffixes = ((string[])["_attach", "_attachments"]).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Listing options for recursive directory walks without traversing symlinked folders.</summary>
    private static readonly FileSystemListingOptions s_recursiveListingOptions = new() { Recursive = true };

    #endregion

    #region CSV Parsing

    private static string? s_lastScript;
    private static Dictionary<int, string> s_seriesOverrides = [];
    private static Dictionary<int, (string? Title, string EpisodeNumber)> s_episodeOverrides = [];
    private static readonly Lock s_overrideLock = new();

    /// <summary>Parses the CSV script into series and episode overrides, utilizing a lock and string-hash cache to prevent redundant processing.</summary>
    /// <param name="script">The raw CSV script string.</param>
    /// <returns>A tuple containing series and episode override dictionaries.</returns>
    public static (Dictionary<int, string> Series, Dictionary<int, (string? Title, string EpisodeNumber)> Episodes) GetOverrides(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return ([], []);

        lock (s_overrideLock)
        {
            if (s_lastScript == script)
                return (s_seriesOverrides, s_episodeOverrides);

            var newSeries = new Dictionary<int, string>();
            var newEpisodes = new Dictionary<int, (string? Title, string EpisodeNumber)>();

            foreach (var line in script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#'))
                    continue;

                var parts = line.Split(',', 3, StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                    continue;

                var ids = parts[0]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(segment => int.TryParse(segment, out int parsedId) ? parsedId : 0)
                    .Where(anidbId => anidbId > 0)
                    .ToList();
                if (ids.Count == 0)
                    continue;

                if (parts.Length == 2)
                    foreach (var anidbId in ids)
                        newSeries[anidbId] = parts[1];
                else
                {
                    string? episodeTitle = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
                    foreach (var anidbId in ids)
                        newEpisodes[anidbId] = (episodeTitle, parts[2]);
                }
            }

            s_seriesOverrides = newSeries;
            s_episodeOverrides = newEpisodes;
            s_lastScript = script;

            return (newSeries, newEpisodes);
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

        string cleaned = title;
        if (config.ReplaceInvalidCharacters && cleaned.StartsWith('.'))
            cleaned = "․" + cleaned[1..];

        if (config.ApplyStylisticReplacements)
        {
            foreach (var (find, replace) in s_styledReplacements)
                cleaned = cleaned.Replace(find, replace, StringComparison.Ordinal);
            cleaned = QuoteRegex().Replace(cleaned, "“$1”");
        }

        if (config.ReplaceInvalidCharacters)
            cleaned = string.Create(cleaned.Length, cleaned, (chars, state) =>
            {
                for (int index = 0; index < state.Length; index++)
                    chars[index] = s_replacementCharMap.TryGetValue(state[index], out char mappedChar) ? mappedChar : state[index];
            });

        return CondenseSpacesRegex().Replace(cleaned, " ").Trim();
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
        return matches.Count == 0 ? "" : " " + string.Join(" ", matches.Select(match => match.Value));
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

        var sortedEpisodes = episodes.OrderBy(episode => (int)episode.Type).ThenBy(episode => episode.EpisodeNumber).ToList();
        var primaryEpisode = sortedEpisodes[0];

        int zeros = primaryEpisode.Type != EpisodeType.Episode ? 1 : 2;
        string format = $"D{Math.Max(Math.Max(series.EpisodeCounts[primaryEpisode.Type], 1).ToString().Length, zeros)}";

        var ranges = new List<string>();
        foreach (var group in sortedEpisodes.GroupBy(episode => episode.Type))
        {
            string prefix = Enum.IsDefined(group.Key) ? group.Key.Prefix : "U";
            var numbers = group.Select(episode => episode.EpisodeNumber).ToList();
            if (numbers.Count == 1 || numbers[^1] - numbers[0] != numbers.Count - 1)
                ranges.Add($"{prefix}{string.Join($" {prefix}", numbers.Select(number => number.ToString(format)))}");
            else
                ranges.Add($"{prefix}{numbers[0].ToString(format)}-{numbers[^1].ToString(format)}");
        }

        string episodeNumber =
            ranges.Count > 1 && sortedEpisodes[0].Type == EpisodeType.Episode
                ? ranges.Count == 2 && sortedEpisodes[^1].Type == EpisodeType.Other
                    ? $"{ranges[1]} (E{ranges[0]})"
                    : $"{ranges[0]} ({string.Join(", ", ranges.Skip(1))})"
                : string.Join(" ", ranges);

        if (config.RenameAmbiguousEpisodes)
        {
            string episodeName = primaryEpisode.Titles.FirstOrDefault(titleEntry => titleEntry.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            if (primaryEpisode.EpisodeNumber == 1 && sortedEpisodes.Count == 1 && s_singleEntryTitles.Contains(episodeName))
                episodeNumber = $"({episodeName})";
            if (primaryEpisode.Type == EpisodeType.Episode && sortedEpisodes.Count > 1 && episodeName.Contains("Part 1 of", StringComparison.OrdinalIgnoreCase))
                episodeNumber = $"{sortedEpisodes[0].EpisodeNumber - 1:D2} (E{sortedEpisodes[1].EpisodeNumber})";
        }

        return episodeNumber;
    }

    /// <summary>Formats the folder name by moving common prefixes and sanitizing trailing ellipses.</summary>
    /// <param name="title">The cleaned title.</param>
    /// <param name="config">The active renamer configuration.</param>
    /// <returns>A formatted folder name.</returns>
    public static string FormatFolderName(string title, RenamerConfig config)
    {
        string folderName = config.MoveCommonPrefixes ? CommonTitlePrefixesRegex().Replace(title, "$2 — $1") : title;

        if (config.ReplaceInvalidCharacters)
            folderName = folderName switch
            {
                _ when folderName.EndsWith("......") => folderName[..^6] + "……",
                _ when folderName.EndsWith("...") => folderName[..^3] + "…",
                _ when folderName.EndsWith("..") => folderName[..^2] + "‥",
                _ when folderName.EndsWith(".") => folderName[..^1] + "․",
                _ => folderName,
            };

        return folderName.Trim();
    }

    #endregion

    #region Relocation Helpers

    /// <summary>Moves associated sidecar files, images, attachment folders, and series assets when a file is relocated.</summary>
    /// <param name="sourcePath">The original full path of the file.</param>
    /// <param name="destinationPath">The new full path of the file.</param>
    /// <param name="sourceFolder">The source managed folder containing the file.</param>
    /// <param name="videoService">The video service injected from Shoko.</param>
    /// <param name="fileSystem">The Shoko filesystem service for watcher-safe operations.</param>
    /// <param name="logger">The logger instance.</param>
    public static void RelocateSidecars(string sourcePath, string destinationPath, IManagedFolder? sourceFolder, IVideoService videoService, IFileSystem fileSystem, ILogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
                return;

            string cleanSourcePath = Path.GetFullPath(sourcePath);
            string cleanDestinationPath = Path.GetFullPath(destinationPath);
            string sourceDirectory = Path.GetDirectoryName(cleanSourcePath)!;
            string destinationDirectory = Path.GetDirectoryName(cleanDestinationPath)!;
            if (!fileSystem.DirectoryExists(sourceDirectory))
                return;

            string sourceBaseName = Path.GetFileNameWithoutExtension(cleanSourcePath);
            string destinationBaseName = Path.GetFileNameWithoutExtension(cleanDestinationPath);
            var comparison = StringComparison.OrdinalIgnoreCase;

            if (sourceDirectory.Equals(destinationDirectory, StringComparison.Ordinal) && sourceBaseName.Equals(destinationBaseName, StringComparison.Ordinal))
                return;
            if (!fileSystem.DirectoryExists(destinationDirectory))
                fileSystem.CreateDirectory(destinationDirectory);

            // Local helper to rapidly identify source and destination paths to avoid evaluating them repeatedly
            bool IsSourceOrDestination(string path)
            {
                string fullPath = Path.GetFullPath(path);
                return fullPath.Equals(cleanSourcePath, comparison) || fullPath.Equals(cleanDestinationPath, comparison);
            }

            // Episode-level sidecars and attachment folders (safe to move out of any folder type)
            foreach (var entry in fileSystem.GetFileSystemEntries(sourceDirectory))
            {
                if (!entry.Name.StartsWith(sourceBaseName, comparison) || IsSourceOrDestination(entry.Path))
                    continue;

                string suffix = entry.Name[sourceBaseName.Length..];
                if (entry.IsDirectory)
                {
                    if (s_attachFolderSuffixes.Contains(suffix))
                    {
                        string targetPath = Path.Join(destinationDirectory, destinationBaseName + suffix);
                        MoveDirectorySafely(entry.Path, targetPath, fileSystem, logger);
                        logger.LogInformation("Shoko Renamer NN: Relocated attachment folder -> \"{Old}\" to \"{New}\"", entry.Name, targetPath);
                    }
                }
                else if (s_sidecarExtensions.Contains(Path.GetExtension(entry.Name)) || s_imageExtensions.Contains(Path.GetExtension(entry.Name)))
                {
                    string targetPath = Path.Join(destinationDirectory, destinationBaseName + suffix);
                    MoveFileSafely(entry.Path, targetPath, fileSystem, logger);
                    logger.LogInformation("Shoko Renamer NN: Relocated sidecar file -> \"{Old}\" to \"{New}\"", entry.Name, targetPath);
                }
            }

            // Loose files and loose folders (only moved when relocating out of an existing destination managed folder)
            bool canMoveLoose =
                !sourceDirectory.Equals(destinationDirectory, StringComparison.Ordinal)
                && sourceFolder is { } managedFolder
                && managedFolder.DropFolderType.HasFlag(DropFolderType.Destination)
                && !managedFolder.DropFolderType.HasFlag(DropFolderType.Source);
            if (canMoveLoose && fileSystem.DirectoryExists(sourceDirectory))
            {
                bool hasOtherActiveVideos = fileSystem
                    .GetFilePaths(sourceDirectory)
                    .Any(file => videoService.IsAllowedVideoExtension(file) && !IsSourceOrDestination(file) && !IsIgnoredVideo(file, videoService));

                if (!hasOtherActiveVideos)
                {
                    foreach (var directory in fileSystem.GetDirectoryEntries(sourceDirectory))
                    {
                        if (ContainsActiveVideos(directory.Path, videoService, fileSystem))
                            continue;

                        string targetPath = Path.Join(destinationDirectory, directory.Name);
                        MoveDirectorySafely(directory.Path, targetPath, fileSystem, logger);
                        logger.LogInformation("Shoko Renamer NN: Relocated loose folder -> \"{Old}\" to \"{New}\"", directory.Name, targetPath);
                    }

                    foreach (var file in fileSystem.GetFileEntries(sourceDirectory))
                    {
                        if (IsSourceOrDestination(file.Path) || (videoService.IsAllowedVideoExtension(file.Path) && !IsIgnoredVideo(file.Path, videoService)))
                            continue;

                        string targetPath = Path.Join(destinationDirectory, file.Name);
                        MoveFileSafely(file.Path, targetPath, fileSystem, logger);
                        logger.LogInformation("Shoko Renamer NN: Relocated loose file -> \"{Old}\" to \"{New}\"", file.Name, targetPath);
                    }

                    CleanEmptyDirectories(sourceDirectory, sourceFolder?.Path, fileSystem, logger);
                }
            }

            EnsureDirectoryCase(destinationDirectory, fileSystem, logger);
        }
        catch (DirectoryNotFoundException)
        { /* Directory was already cleaned up or moved concurrently by Shoko core */
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Shoko Renamer NN: Error relocating sidecars for {File}", sourcePath);
        }
    }

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
    /// <param name="directory">The absolute directory path to scan.</param>
    /// <param name="videoService">The video service injected from Shoko.</param>
    /// <param name="fileSystem">The Shoko filesystem service.</param>
    /// <returns>True if any file within the directory is an active matched episode; otherwise, false.</returns>
    private static bool ContainsActiveVideos(string directory, IVideoService videoService, IFileSystem fileSystem)
    {
        try
        {
            return fileSystem.GetFilePaths(directory, s_recursiveListingOptions).Any(filePath => videoService.IsAllowedVideoExtension(filePath) && IsMatchedActiveEpisode(filePath, videoService));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Safely moves a file, skipping if the destination already exists.</summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="fileSystem">The Shoko filesystem service.</param>
    /// <param name="logger">The logger instance.</param>
    private static void MoveFileSafely(string sourcePath, string destinationPath, IFileSystem fileSystem, ILogger logger)
    {
        try
        {
            if (fileSystem.FileExists(destinationPath))
            {
                logger.LogDebug("Shoko Renamer NN: Destination file already exists, skipping move -> \"{Destination}\"", destinationPath);
                return;
            }
            fileSystem.MoveFile(sourcePath, destinationPath);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Shoko Renamer NN: Failed to move file -> \"{Source}\" to \"{Destination}\"", sourcePath, destinationPath);
        }
    }

    /// <summary>Safely moves or merges a directory into a destination, skipping conflicting files.</summary>
    /// <param name="sourceDirectory">The source directory path.</param>
    /// <param name="destinationDirectory">The destination directory path.</param>
    /// <param name="fileSystem">The Shoko filesystem service.</param>
    /// <param name="logger">The logger instance.</param>
    private static void MoveDirectorySafely(string sourceDirectory, string destinationDirectory, IFileSystem fileSystem, ILogger logger)
    {
        try
        {
            if (!fileSystem.DirectoryExists(sourceDirectory))
                return;

            // If the destination doesn't exist, move the entire directory via IFileSystem (handles cross-volume and symlink moves safely)
            if (!fileSystem.DirectoryExists(destinationDirectory))
            {
                fileSystem.MoveDirectory(sourceDirectory, destinationDirectory);
                return;
            }

            // Merge into the existing destination directory without overwriting existing files
            foreach (var filePath in fileSystem.GetFilePaths(sourceDirectory, s_recursiveListingOptions))
            {
                try
                {
                    string relativePath = Path.GetRelativePath(sourceDirectory, filePath);
                    string targetPath = Path.Join(destinationDirectory, relativePath);
                    string? targetDirectory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDirectory) && !fileSystem.DirectoryExists(targetDirectory))
                        fileSystem.CreateDirectory(targetDirectory);

                    if (fileSystem.FileExists(targetPath) || fileSystem.DirectoryExists(targetPath))
                    {
                        logger.LogDebug("Shoko Renamer NN: Destination entry already exists, skipping move -> \"{Destination}\"", targetPath);
                        continue;
                    }

                    fileSystem.MoveFile(filePath, targetPath);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Shoko Renamer NN: Could not move entry -> \"{File}\"", filePath);
                }
            }

            // Safely clean up the source directory structure (bottom-up), leaving behind any skipped files
            foreach (var subDirectory in fileSystem.GetDirectoryPaths(sourceDirectory, s_recursiveListingOptions).OrderByDescending(directory => directory.Length))
                try
                {
                    if (fileSystem.IsDirectoryEmpty(subDirectory))
                        fileSystem.DeleteDirectory(subDirectory);
                }
                catch
                { /* Ignore */
                }

            try
            {
                if (fileSystem.IsDirectoryEmpty(sourceDirectory))
                    fileSystem.DeleteDirectory(sourceDirectory);
            }
            catch
            { /* Ignore */
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Shoko Renamer NN: Failed to move directory -> \"{Source}\" to \"{Destination}\"", sourceDirectory, destinationDirectory);
        }
    }

    /// <summary>Recursively deletes empty directories starting from the target directory upwards until a non-empty directory or root is reached.</summary>
    /// <param name="directory">The directory to delete if empty.</param>
    /// <param name="rootPath">The root directory path that should never be deleted.</param>
    /// <param name="fileSystem">The Shoko filesystem service.</param>
    /// <param name="logger">The logger instance.</param>
    private static void CleanEmptyDirectories(string directory, string? rootPath, IFileSystem fileSystem, ILogger logger)
    {
        try
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            while (
                !string.IsNullOrEmpty(directory) && (string.IsNullOrEmpty(rootPath) || !directory.Equals(rootPath, comparison)) && fileSystem.DirectoryExists(directory) && fileSystem.IsDirectoryEmpty(directory)
            )
            {
                fileSystem.DeleteDirectory(directory);
                logger.LogInformation("Shoko Renamer NN: Cleaned up empty folder -> \"{Directory}\"", directory);
                directory = Path.GetDirectoryName(directory)!;
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Shoko Renamer NN: Could not remove directory -> \"{Directory}\"", directory);
        }
    }

    /// <summary>Ensures the destination directory on disk matches the exact casing of the expected path, merging duplicates on case-sensitive or renaming on case-insensitive filesystems.</summary>
    /// <param name="destinationDirectory">The expected destination directory path.</param>
    /// <param name="fileSystem">The Shoko filesystem service.</param>
    /// <param name="logger">The logger instance.</param>
    private static void EnsureDirectoryCase(string destinationDirectory, IFileSystem fileSystem, ILogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                return;
            string cleanDestinationDirectory = Path.GetFullPath(destinationDirectory);
            string? parentDirectory = Path.GetDirectoryName(cleanDestinationDirectory);
            if (string.IsNullOrEmpty(parentDirectory) || !fileSystem.DirectoryExists(parentDirectory))
                return;

            string expectedName = Path.GetFileName(cleanDestinationDirectory);
            if (string.IsNullOrEmpty(expectedName))
                return;

            var matchingDirectories = fileSystem.GetDirectoryEntries(parentDirectory).Where(entry => entry.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingDirectories.Count == 0)
                return;

            string targetPath = Path.Join(parentDirectory, expectedName);

            // On case-sensitive systems (e.g. Linux), both old and new casing directories can exist at once; merge old into new
            if (matchingDirectories.Count > 1)
            {
                foreach (var oldDirectory in matchingDirectories.Where(entry => !string.Equals(entry.Name, expectedName, StringComparison.Ordinal)))
                {
                    MoveDirectorySafely(oldDirectory.Path, targetPath, fileSystem, logger);
                    logger.LogInformation("Shoko Renamer NN: Merged folder casing -> \"{Old}\" into \"{New}\"", oldDirectory.Name, expectedName);
                }
                return;
            }

            // IFileSystem.MoveDirectory handles case-only renames even on case-insensitive filesystems (e.g. Windows/macOS)
            var actualEntry = matchingDirectories[0];
            if (!string.Equals(actualEntry.Name, expectedName, StringComparison.Ordinal))
            {
                fileSystem.MoveDirectory(actualEntry.Path, targetPath);
                logger.LogInformation("Shoko Renamer NN: Corrected folder casing -> \"{Old}\" to \"{New}\"", actualEntry.Name, expectedName);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Shoko Renamer NN: Could not update directory casing for -> \"{Directory}\"", destinationDirectory);
        }
    }

    #endregion
}
