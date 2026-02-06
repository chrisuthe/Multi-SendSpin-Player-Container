namespace MultiRoomAudio.Services;

/// <summary>
/// Provides centralized access to application version information.
/// </summary>
public class VersionService
{
    /// <summary>
    /// Gets the application version (e.g., "1.2.3" or "dev").
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the build SHA (short commit hash) if available.
    /// </summary>
    public string? BuildSha { get; }

    /// <summary>
    /// Gets the build date if available.
    /// </summary>
    public string? BuildDate { get; }

    /// <summary>
    /// Gets the formatted model string for display in Music Assistant.
    /// Format: "v{version} ({sha})" or "v{version}" if SHA unavailable.
    /// </summary>
    public string ModelString { get; }

    /// <summary>
    /// Gets the software version (version number without 'v' prefix or SHA).
    /// </summary>
    public string SoftwareVersion { get; }

    public VersionService()
    {
        Version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";

        // Get build SHA and truncate to short format (7 chars) if available
        var fullSha = Environment.GetEnvironmentVariable("APP_BUILD_SHA");
        BuildSha = !string.IsNullOrEmpty(fullSha) && fullSha.Length >= 7
            ? fullSha.Substring(0, 7)
            : fullSha;

        BuildDate = Environment.GetEnvironmentVariable("APP_BUILD_DATE");

        // Format model string: "v1.2.3 (abc123f)" for versions, "dev (abc123f)" for dev builds
        // Only add "v" prefix for actual version numbers, not for "dev"
        var versionPrefix = Version != "dev" ? "v" : "";
        ModelString = !string.IsNullOrEmpty(BuildSha)
            ? $"{versionPrefix}{Version} ({BuildSha})"
            : $"{versionPrefix}{Version}";

        // Software version is just the version number
        SoftwareVersion = Version;
    }
}
