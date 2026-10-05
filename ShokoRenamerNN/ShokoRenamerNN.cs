using NLog;
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

    /// <inheritdoc/>
    public void Setup(IServiceProvider serviceProvider) { }
}

/// <summary>A C# relocation provider acting as a direct replacement for LuaRenamer scripts.</summary>
/// <param name="videoService">Shoko video service for extension checking.</param>
public class ShokoRenamer(IVideoService videoService) : IRelocationProvider<RenamerConfig>
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc/>
    public string Name => ShokoRenamerNNConstants.Name;

    /// <inheritdoc/>
    public string Description => ShokoRenamerNNConstants.Description;

    /// <inheritdoc/>
    public RelocationResult GetPath(RelocationContext<RenamerConfig> context)
    {
        try
        {
            if (context.File.Video is null || context.Series.Count == 0 || context.Episodes.Count == 0)
                throw new ArgumentException("File is missing required video, series, or episode metadata.");

            // Resolve the primary series and episodes
            IShokoSeries primarySeries = context.Series.OrderBy(s => s.AnidbAnimeID).First();
            var episodes = context.Episodes.Where(e => e.AnidbEpisode.AnidbAnimeID == primarySeries.AnidbAnimeID).ToList();

            string title = primarySeries.PreferredTitle?.Value ?? primarySeries.DefaultTitle.Value;
            string epNumber = RenamerHelper.GenerateEpisodeNumber(episodes, primarySeries, context.Configuration);

            // Manual Overrides
            var (seriesOverrides, episodeOverrides) = RenamerHelper.GetOverrides(context.Configuration.Script);

            if (seriesOverrides.TryGetValue(primarySeries.AnidbAnimeID, out string? overrideTitle))
                title = overrideTitle;

            if (episodes.FirstOrDefault()?.AnidbEpisode?.AnidbID is int epId && episodeOverrides.TryGetValue(epId, out var epOverride))
            {
                if (!string.IsNullOrWhiteSpace(epOverride.Title))
                    title = epOverride.Title;
                epNumber = epOverride.EpNumber;
            }

            title = RenamerHelper.CleanTitle(title, context.Configuration);
            string fileTags = RenamerHelper.ExtractTags(context.File.FileName, context.Configuration);
            string folderName = RenamerHelper.FormatFolderName(title, context.Configuration);

            var result = new RelocationResult { SkipMove = false, SkipRename = false };

            if (context.MoveEnabled)
            {
                // Ensure anime tagged as "18 restricted" are segregated into the configued '18 Restricted Folder Name' destination
                string destName = primarySeries.Restricted ? context.Configuration.RestrictedDestination : context.Configuration.MainDestination;
                var destFolder =
                    (
                        context.AvailableFolders.FirstOrDefault(f => f.DropFolderType.HasFlag(DropFolderType.Destination) && f.Name.Equals(destName, StringComparison.OrdinalIgnoreCase))
                        ?? context.AvailableFolders.FirstOrDefault(f => f.DropFolderType.HasFlag(DropFolderType.Destination))
                    ) ?? throw new InvalidOperationException($"Could not find an available destination folder for {destName}.");

                result.ManagedFolder = destFolder;
                result.Path = folderName;
            }

            if (context.RenameEnabled)
            {
                string extension = Path.GetExtension(context.File.FileName);
                result.FileName = $"{title} - {epNumber}{fileTags}{extension}";
            }

            if (context.MoveEnabled || context.RenameEnabled)
            {
                string srcPath = context.File.Path;
                IManagedFolder? sourceFolder = context.File.ManagedFolder;
                string destDir = context.MoveEnabled && result.ManagedFolder != null ? Path.Combine(result.ManagedFolder.Path, result.Path ?? string.Empty) : Path.GetDirectoryName(srcPath) ?? string.Empty;
                string destFileName = context.RenameEnabled && !string.IsNullOrWhiteSpace(result.FileName) ? result.FileName : Path.GetFileName(srcPath);
                string destPath = Path.Combine(destDir, destFileName);

                if (!srcPath.Equals(destPath, StringComparison.OrdinalIgnoreCase))
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool moved = false;
                            for (int i = 0; i < 30; i++)
                            {
                                await Task.Delay(200).ConfigureAwait(false);
                                if (File.Exists(destPath) && !File.Exists(srcPath))
                                {
                                    moved = true;
                                    break;
                                }
                            }

                            if (moved)
                                RenamerHelper.RelocateSidecars(srcPath, destPath, sourceFolder, videoService);
                        }
                        catch (DirectoryNotFoundException)
                        { /* Directory was already cleaned up by Shoko or a concurrent job */
                        }
                        catch (Exception ex)
                        {
                            s_logger.Warn(ex, "Shoko Renamer NN: Error relocating sidecars for {File}", srcPath);
                        }
                    });
            }

            return result;
        }
        catch (Exception e)
        {
            s_logger.Warn(e, "Shoko Renamer NN: Relocation failed for file -> {File}", context.File.FileName);
            return new RelocationResult { Error = new RelocationError(e.Message, e) };
        }
    }
}
