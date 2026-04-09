namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Async gate that lets the pipeline loop pause between file dispatches.
/// Pause is "soft": files already in flight run to completion; only new
/// dispatches are held until Resume is called.
/// </summary>
public sealed class PauseController
{
    private readonly ManualResetEventSlim _gate = new(initialState: true);

    public bool IsPaused => !_gate.IsSet;

    public void Pause() => _gate.Reset();

    public void Resume() => _gate.Set();

    // Ensures gate is open on cancel or analysis completion.
    public void Reset() => _gate.Set();

    public Task WaitIfPausedAsync(CancellationToken ct) =>
        _gate.IsSet ? Task.CompletedTask : Task.Run(() => _gate.Wait(ct), ct);
}
