namespace AudioIntegrityChecker.Core;

/// <summary>
/// Flat mapping from file extension (lowercase, no dot) to the
/// <see cref="IFormatChecker"/> that handles it. Populated once at startup
/// and handed to the collector and pipeline.
/// </summary>
internal sealed class CheckerRegistry
{
    private readonly Dictionary<string, IFormatChecker> _checkersByExtension = new(
        StringComparer.OrdinalIgnoreCase
    );

    public void Add(string extension, IFormatChecker checker)
    {
        _checkersByExtension[extension] = checker;
    }

    public IReadOnlyDictionary<string, IFormatChecker> CheckersByExtension => _checkersByExtension;
}
