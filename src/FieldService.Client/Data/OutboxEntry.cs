using FieldService.Shared.Sync;

namespace FieldService.Client.Data;

public enum OutboxStatus
{
    Pending = 0,
    Sending = 1,
    Confirmed = 2,
    Conflicted = 3,
    Failed = 4,
}

/// <summary>
/// A queued mutation awaiting push to the server. The local INSERT/UPDATE/DELETE on the
/// domain table and the INSERT into this table happen in a single EF Core transaction, so
/// the outbox is consistent with local UI state — the classic "transactional outbox" pattern.
/// </summary>
public sealed class OutboxEntry
{
    public long Id { get; set; }                           // local autoincrement
    public Guid ClientMutationId { get; set; }             // idempotency key sent to server

    public EntityKind Entity { get; set; }
    public Guid EntityId { get; set; }
    public MutationKind Kind { get; set; }
    public long? BaseVersion { get; set; }                 // server version the change was made against
    public string PayloadJson { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public string? LastError { get; set; }
    public string? ConflictServerPayloadJson { get; set; } // populated when Status == Conflicted
}
