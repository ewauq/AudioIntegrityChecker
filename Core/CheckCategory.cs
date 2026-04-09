namespace AudioIntegrityChecker.Core;

/// <summary>
/// Classifies a check result by the layer of the file affected and the nature of the issue.
/// The numeric values establish a "worst wins" ordering: higher is more severe.
/// </summary>
public enum CheckCategory
{
    Ok = 0,

    /// <summary>Tag metadata inconsistency, zero impact on audio playback or seeking.</summary>
    Metadata = 1,

    /// <summary>Navigation metadata wrong (e.g. frame count), may affect seeking or duration display.</summary>
    Index = 2,

    /// <summary>Stream structure anomaly. Audio samples likely intact, but some frames may be missing or skipped.</summary>
    Structure = 3,

    /// <summary>Audio data demonstrably damaged, truncated, or undecodable.</summary>
    Corruption = 4,

    /// <summary>The analysis tool itself failed. The file's actual state is unknown.</summary>
    Error = 5,
}
