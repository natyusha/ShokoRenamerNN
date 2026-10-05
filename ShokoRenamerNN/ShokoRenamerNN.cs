using System.Reflection;
using NLog;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Relocation;

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
public class ShokoRenamer : IRelocationProvider<RenamerConfig>
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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (context.GetType().GetProperty("Preview", BindingFlags.Public | BindingFlags.Instance)?.GetValue(context) as bool? ?? false)
                            return;
                        await Task.Delay(1500).ConfigureAwait(false); // Delay slightly to allow Shoko core to finish moving the main file first

                        string? srcPath =
                            context.File.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance)?.GetValue(context.File) as string
                            ?? context.File.GetType().GetProperty("FilePath", BindingFlags.Public | BindingFlags.Instance)?.GetValue(context.File) as string;
                        string destDir = result.ManagedFolder?.Path ?? Path.GetDirectoryName(srcPath) ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(srcPath))
                            RenamerHelper.RelocateSidecars(srcPath, Path.Combine(destDir, result.Path ?? string.Empty, result.FileName ?? context.File.FileName));
                    }
                    catch
                    { /* Ignore missing properties or IO errors during sidecar relocation */
                    }
                });

            return result;
        }
        catch (Exception e)
        {
            s_logger.Warn(e, "Shoko Renamer NN: Relocation failed for file -> {File}", context.File.FileName);
            return new RelocationResult { Error = new RelocationError(e.Message, e) };
        }
    }
}
