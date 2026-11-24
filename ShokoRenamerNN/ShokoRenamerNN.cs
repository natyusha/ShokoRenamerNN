using Microsoft.Extensions.Logging;
using Shoko.Abstractions.FileSystem.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Relocation;
using Shoko.Abstractions.Video.Services;

namespace ShokoRenamerNN;

/// <summary>Plugin entry point and descriptor for Shoko Server.</summary>
public class Plugin : IPlugin
{
    #region Plugin Metadata

    /// <inheritdoc/>
    public Guid ID => new(ShokoRenamerNNConstants.PluginId);

    /// <inheritdoc/>
    public string Name => ShokoRenamerNNConstants.Name;

    /// <inheritdoc/>
    public string Description => ShokoRenamerNNConstants.Description;

    /// <inheritdoc/>
    public string? EmbeddedThumbnailResourceName => "ShokoRenamerNN.Assets.shoko-renamer-nn-logo.png";

    /// <inheritdoc/>
    public string? EmbeddedIconResourceName => "ShokoRenamerNN.Assets.shoko-renamer-nn-icon.png";

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public void Setup(IServiceProvider serviceProvider) { }

    #endregion
}

/// <summary>A C# relocation provider acting as a direct replacement for LuaRenamer scripts.</summary>
/// <param name="videoService">Shoko video service for extension checking.</param>
/// <param name="fileSystem">Shoko filesystem service for watcher-safe file and directory operations.</param>
/// <param name="logger">The logger instance.</param>
public class ShokoRenamer(IVideoService videoService, IFileSystem fileSystem, ILogger<ShokoRenamer> logger) : IRelocationProvider<RenamerConfig>
{
    #region Provider Metadata

    /// <inheritdoc/>
    public string Name => ShokoRenamerNNConstants.Name;

    /// <inheritdoc/>
    public string Description => ShokoRenamerNNConstants.Description;

    #endregion

    #region Relocation

    /// <inheritdoc/>
    public RelocationResult GetPath(RelocationContext<RenamerConfig> context)
    {
        try
        {
            if (context.File.Video is null || context.Series.Count == 0 || context.Episodes.Count == 0)
                throw new ArgumentException("File is missing required video, series, or episode metadata.");

            // Resolve the primary series and episodes
            IShokoSeries primarySeries = context.Series.OrderBy(series => series.AnidbAnimeID).First();
            var episodes = context.Episodes.Where(episode => episode.AnidbEpisode.AnidbAnimeID == primarySeries.AnidbAnimeID).ToList();

            string title = primarySeries.PreferredTitle?.Value ?? primarySeries.DefaultTitle.Value;
            string episodeNumber = RenamerHelper.GenerateEpisodeNumber(episodes, primarySeries, context.Configuration);

            // Manual Overrides
            var (seriesOverrides, episodeOverrides) = RenamerHelper.GetOverrides(context.Configuration.Script);

            if (seriesOverrides.TryGetValue(primarySeries.AnidbAnimeID, out string? overrideTitle))
                title = overrideTitle;

            if (episodes.FirstOrDefault()?.AnidbEpisode?.AnidbID is int episodeId && episodeOverrides.TryGetValue(episodeId, out var episodeOverride))
            {
                if (!string.IsNullOrWhiteSpace(episodeOverride.Title))
                    title = episodeOverride.Title;
                episodeNumber = episodeOverride.EpisodeNumber;
            }

            title = RenamerHelper.CleanTitle(title, context.Configuration);
            string fileTags = RenamerHelper.ExtractTags(context.File.FileName, context.Configuration);
            string folderName = RenamerHelper.FormatFolderName(title, context.Configuration);

            var result = new RelocationResult { SkipMove = false, SkipRename = false };

            if (context.MoveEnabled)
            {
                // Ensure anime tagged as "18 restricted" are segregated into the configued '18 Restricted Folder Name' destination
                string destinationName = primarySeries.Restricted ? context.Configuration.RestrictedDestination : context.Configuration.MainDestination;
                var destinationFolder = (
                    context.AvailableFolders.FirstOrDefault(folder => folder.DropFolderType.HasFlag(DropFolderType.Destination) && folder.Name.Equals(destinationName, StringComparison.OrdinalIgnoreCase))
                    ?? context.AvailableFolders.FirstOrDefault(folder => folder.DropFolderType.HasFlag(DropFolderType.Destination))
                ) ?? throw new InvalidOperationException($"Could not find an available destination folder for {destinationName}.");

                result.ManagedFolder = destinationFolder;
                result.Path = folderName;
            }

            if (context.RenameEnabled)
            {
                string extension = Path.GetExtension(context.File.FileName);
                result.FileName = $"{title} - {episodeNumber}{fileTags}{extension}";
            }

            if (context.MoveEnabled || context.RenameEnabled)
            {
                string sourcePath = context.File.Path;
                IManagedFolder? sourceFolder = context.File.ManagedFolder;
                string destinationDirectory =
                    context.MoveEnabled && result.ManagedFolder != null ? Path.Join(result.ManagedFolder.Path, result.Path ?? string.Empty) : Path.GetDirectoryName(sourcePath) ?? string.Empty;
                string destinationFileName = context.RenameEnabled && !string.IsNullOrWhiteSpace(result.FileName) ? result.FileName : Path.GetFileName(sourcePath);
                string destinationPath = Path.Join(destinationDirectory, destinationFileName);

                if (!string.IsNullOrWhiteSpace(sourcePath) && !sourcePath.Equals(destinationPath, StringComparison.Ordinal))
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool isCaseOnly = sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase);
                            bool moved = false;

                            if (isCaseOnly)
                            {
                                await Task.Delay(1000).ConfigureAwait(false);
                                moved = true;
                            }
                            else
                                for (int attempt = 0; attempt < 300; attempt++) // Allow up to 5 minutes for large files crossing filesystem boundaries
                                {
                                    await Task.Delay(1000).ConfigureAwait(false);
                                    if (fileSystem.FileExists(destinationPath) && !fileSystem.FileExists(sourcePath))
                                    {
                                        moved = true;
                                        break;
                                    }
                                }

                            if (moved)
                                RenamerHelper.RelocateSidecars(sourcePath, destinationPath, sourceFolder, videoService, fileSystem, logger);
                        }
                        catch (DirectoryNotFoundException)
                        { /* Directory was already cleaned up by Shoko or a concurrent job */
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Shoko Renamer NN: Error relocating sidecars for {File}", sourcePath);
                        }
                    });
            }

            return result;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Shoko Renamer NN: Relocation failed for file -> {File}", context.File.FileName);
            return new RelocationResult { Error = new RelocationError(exception.Message, exception) };
        }
    }

    #endregion
}
