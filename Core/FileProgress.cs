namespace AudioIntegrityChecker.Core;

/// <summary>
/// Normalized per-file decode progress in [0.0, 1.0].
/// </summary>
public readonly struct FileProgress
{
    public float Value { get; }

    public FileProgress(float value) => Value = Math.Clamp(value, 0f, 1f);

    public static implicit operator float(FileProgress progress) => progress.Value;

    public static implicit operator FileProgress(float value) => new(value);
}
