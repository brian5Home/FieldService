namespace FieldService.Client.Sync;

public interface ISyncService
{
    /// <summary>Run one full sync cycle: push pending outbox entries, then pull deltas.</summary>
    Task<SyncRunResult> SyncOnceAsync(CancellationToken ct = default);

    /// <summary>Starts the background loop that reacts to online events and periodic ticks.</summary>
    void Start();
    void Stop();

    event Action<SyncState>? StateChanged;
}

public enum SyncState { Idle, Pushing, Pulling, Offline, Error, ProtocolMismatch }

public sealed record SyncRunResult(int PushedCount, int PulledCount, int ConflictCount);
