namespace FieldService.Shared.Domain;

/// <summary>
/// Every syncable row carries these three fields. They are what the sync protocol operates over.
///  - Version: a server-assigned monotonic number (unique across the whole tenant) that lets the
///    client ask "give me everything since X" and lets the server implement optimistic concurrency.
///  - UpdatedAt: wall-clock timestamp, informational only — DO NOT use for conflict resolution
///    because clocks lie; Version is the source of truth.
///  - IsDeleted: soft-delete tombstone so deletions can propagate through the pull protocol.
///    Hard deletes would make the "since X" delta impossible to compute.
/// </summary>
public interface ISyncableEntity
{
    Guid Id { get; set; }
    long Version { get; set; }
    DateTime UpdatedAt { get; set; }
    bool IsDeleted { get; set; }
}
