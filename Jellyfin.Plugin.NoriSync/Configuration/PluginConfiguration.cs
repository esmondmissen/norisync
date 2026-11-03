using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NoriSync.Configuration;

/// <summary>
/// Plugin configuration for NoriSync.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        RetentionDays = 30;

        MaxTracksPerRun = 0;
        AnalysisConcurrency = 2;

        LidarrBaseUrl = string.Empty;
        LidarrApiKey = string.Empty;
        LidarrRequireAdmin = true;
        LidarrRootFolderPath = string.Empty;

        AnimatedArtworkEnabled = true;
        AnimatedArtworkTargetResolution = 1080;

        AppleArtworkBackfillEnabled = false;
        AppleArtworkServiceUrl = string.Empty;
        AppleArtworkVariant = "Both";
        AppleArtworkMaxPerRun = 100;
    }

    /// <summary>
    /// Gets or sets the number of days to retain change records before automatic purge.
    /// Acts as a safety ceiling — records older than this are purged even if a registered
    /// client has not yet acknowledged them. Also serves as the idle threshold for
    /// registered clients: instances that haven't contacted the server within this many
    /// days are dropped from retention gating.
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// Gets or sets the maximum tracks processed per scheduled run. Zero means no cap.
    /// </summary>
    public int MaxTracksPerRun { get; set; }

    /// <summary>
    /// Gets or sets the number of tracks processed concurrently by the waveform analysis task.
    /// Each worker spawns its own ffmpeg decode and runs its own waveform analysis, so this
    /// multiplies CPU and process count. Values of 1–3 are typical; 0 or negative collapses
    /// to sequential behaviour.
    /// </summary>
    public int AnalysisConcurrency { get; set; }

    /// <summary>
    /// Gets or sets the base URL of the Lidarr instance, e.g. http://lidarr:8686. Empty
    /// disables the integration. Combined with <see cref="LidarrApiKey"/>; both must be set
    /// for the proxy to be active.
    /// </summary>
    public string LidarrBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Lidarr API key. Sent as <c>X-Api-Key</c> on every upstream call.
    /// </summary>
    public string LidarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Lidarr write actions (album/artist requests)
    /// require an administrator account. When false, any authenticated Jellyfin user can submit
    /// requests. Search and cover lookups are always available to authenticated users.
    /// </summary>
    public bool LidarrRequireAdmin { get; set; }

    /// <summary>
    /// Gets or sets the root folder path used when adding new artists to Lidarr. Picked by an
    /// administrator from Lidarr's <c>/rootfolder</c> list in the plugin configuration UI.
    /// </summary>
    public string LidarrRootFolderPath { get; set; }

    /// <summary>
    /// Gets or sets the quality profile id used when adding new artists/albums to Lidarr.
    /// </summary>
    public int LidarrQualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the metadata profile id used when adding new artists to Lidarr.
    /// </summary>
    public int LidarrMetadataProfileId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether animated-artwork upload and serving are enabled. When
    /// false, uploads are rejected, the serve/list endpoints return 404, and the sync feed stops
    /// advertising animated tags.
    /// </summary>
    public bool AnimatedArtworkEnabled { get; set; }

    /// <summary>
    /// Gets or sets the resolution, in pixels, that served animated artwork targets. The number is
    /// the video's <em>short</em> edge — the "1080p" reading, which holds whatever the shape: a
    /// square cover at 1080 is 1080x1080, a 3:4 cover 1080x1440, a 16:9 artist loop 1920x1080.
    /// Surfaced in the dashboard as a quality dropdown with a custom option. Masters are stored at
    /// full upload quality and the rendition is cut from the master once, then cached; the bitrate
    /// ceiling follows from the step. A client may name its own size with <c>?quality=</c>, which
    /// wins over this — the setting is the default, not a ceiling. Also the size the Apple backfill
    /// aims for when picking which published rendition to store: the nearest rendition at or above
    /// this is stored without re-encoding, so the master may sit a few percent over; more than 25%
    /// over is re-encoded down instead. Zero or negative means original size — no serve-time cap,
    /// and the backfill fetches the largest rendition offered.
    /// </summary>
    public int AnimatedArtworkTargetResolution { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the scheduled task that backfills animated album
    /// artwork from Apple Music is active. When true (and <see cref="AnimatedArtworkEnabled"/> is
    /// also true), the task walks albums missing the configured slot(s) and fetches motion covers
    /// from the service at <see cref="AppleArtworkServiceUrl"/>. When false, the task is a no-op.
    /// </summary>
    public bool AppleArtworkBackfillEnabled { get; set; }

    /// <summary>
    /// Gets or sets the base URL of the Apple Music animated-artwork service (the
    /// <c>apple-music-animated-artworks</c> API — see
    /// <see href="https://github.com/m8tec/apple-music-animated-artworks"/>). Empty by default: there
    /// is no built-in host, so the backfill stays inert until an instance is configured. The task
    /// calls <c>{url}/api/v1/artwork/search</c>.
    /// </summary>
    public string AppleArtworkServiceUrl { get; set; }

    /// <summary>
    /// Gets or sets which motion-cover variant to fetch: <c>Square</c> (stored in the album's
    /// <c>Primary</c> slot), <c>Tall</c> (stored in the album's <c>Tall</c> slot), or <c>Both</c>.
    /// </summary>
    public string AppleArtworkVariant { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of albums the backfill task fetches per run. Keeps the first
    /// pass over a large library from hammering the service (which in turn scrapes Apple). Zero or
    /// negative means no cap.
    /// </summary>
    public int AppleArtworkMaxPerRun { get; set; }
}
