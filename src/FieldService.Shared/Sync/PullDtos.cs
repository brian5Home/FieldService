namespace FieldService.Shared.Sync;

/// <summary>
/// Delta-pull request. Client sends its highest known server version; server responds with
/// all rows whose Version &gt; SinceVersion, in ascending Version order, bounded by MaxBatchSize.
/// If HasMore is true, the client repeats with SinceVersion = last row's Version.
/// </summary>
public sealed record PullRequest(long SinceVersion, int MaxBatchSize = 500);

public sealed record EntityChange(
    EntityKind Entity,
    Guid EntityId,
    long Version,
    bool IsDeleted,
    string PayloadJson     // empty when IsDeleted == true
);

public sealed record PullResponse(
    long ServerVersionHighWaterMark,   // latest version that exists on the server right now
    List<EntityChange> Changes,
    bool HasMore
);

/// <summary>
/// Lightweight handshake. The client calls this on startup to verify protocol compatibility
/// and to learn the server's current version watermark (useful for progress UI).
/// </summary>
public sealed record CapabilitiesResponse(
    int MinProtocolVersion,
    int MaxProtocolVersion,
    long ServerVersionHighWaterMark,
    DateTime ServerTimeUtc
);
