namespace AudioIntegrityChecker.Core;

/// <summary>
/// Resolves the active IFormatChecker implementation per format.
/// For each registered format, uses the native checker if available, otherwise falls back to process.
/// </summary>
public sealed class CheckerRegistry
{
    private static readonly Dictionary<string, string[]> ExtensionsByFormat = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        { "FLAC", ["flac"] },
        { "MP3",  ["mp3"] },
    };

    // extension (lower, no dot) → active checker instance
    private readonly Dictionary<string, IFormatChecker> _checkersByExtension = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly Dictionary<
        string,
        (Func<IFormatChecker> Native, Func<IFormatChecker> Process, Func<bool>? NativeAvailable)
    > _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a format with its native and process checker factories.
    /// <paramref name="nativeAvailable"/> is called during <see cref="Build"/> to decide which to use;
    /// if null, native is always selected.
    /// </summary>
    public void Register(
        string formatId,
        Func<IFormatChecker> nativeFactory,
        Func<IFormatChecker> processFactory,
        Func<bool>? nativeAvailable = null
    )
    {
        _factories[formatId] = (nativeFactory, processFactory, nativeAvailable);
    }

    /// <summary>
    /// Instantiates the active checker per format (native if available, process otherwise).
    /// Must be called after all formats are registered.
    /// </summary>
    public void Build()
    {
        foreach (var (formatId, factories) in _factories)
        {
            bool useNative = factories.NativeAvailable?.Invoke() ?? true;
            IFormatChecker checker = useNative ? factories.Native() : factories.Process();

            if (!ExtensionsByFormat.TryGetValue(formatId, out var extensions))
                extensions = [formatId.ToLowerInvariant()];

            foreach (var extension in extensions)
                _checkersByExtension[extension] = checker;
        }
    }

    public IFormatChecker? Resolve(string filePath)
    {
        var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return _checkersByExtension.TryGetValue(extension, out var checker) ? checker : null;
    }

    public IEnumerable<string> SupportedExtensions => _checkersByExtension.Keys;
}
