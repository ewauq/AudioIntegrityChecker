using System.Collections.Frozen;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Immutable bag of lookup tables computed once at the start of a scan and
/// shared by every downstream component (file collector, analysis pipeline,
/// checkers). Holding these references in one place eliminates per-file
/// lookups against a registry and per-file extension parsing.
/// </summary>
internal sealed class ScanContext
{
    public FrozenSet<string> SupportedExtensions { get; }

    public IReadOnlyDictionary<string, IFormatChecker> CheckersByExtension { get; }

    private ScanContext(
        FrozenSet<string> supportedExtensions,
        IReadOnlyDictionary<string, IFormatChecker> checkersByExtension
    )
    {
        SupportedExtensions = supportedExtensions;
        CheckersByExtension = checkersByExtension;
    }

    public static ScanContext Create(CheckerRegistry registry)
    {
        var checkers = registry.CheckersByExtension;
        var extensions = checkers.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        return new ScanContext(extensions, checkers);
    }
}
