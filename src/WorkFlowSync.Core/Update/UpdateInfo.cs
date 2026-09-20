namespace WorkFlowSync.Core.Update;

/// <summary>Information about an available update, returned by <see cref="UpdateChecker"/>.</summary>
public sealed record UpdateInfo(
    /// <summary>Semantic version string without the "v" prefix, e.g. "0.2.0".</summary>
    string Version,
    /// <summary>Browser URL of the release page (for "What's new").</summary>
    string ReleaseUrl,
    /// <summary>Direct download URL for the single-file exe asset.</summary>
    string AssetUrl,
    /// <summary>Markdown body of the release (may be null if the author left it blank).</summary>
    string? ReleaseNotes);
