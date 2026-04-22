namespace FieldService.Shared.Sync;

/// <summary>
/// Wire protocol between client and server. Bump <see cref="Current"/> whenever a change
/// is NOT backwards compatible (e.g., a renamed field with no fallback, a required field
/// added to a payload, a new required mutation outcome code).
///
/// The client sends <c>X-FieldService-Protocol: {Current}</c> on every request. The server
/// replies 426 Upgrade Required if the version is below the minimum it supports.
/// </summary>
public static class SyncProtocol
{
    public const int Current = 2;
    public const int MinimumSupported = 1;
    public const string HeaderName = "X-FieldService-Protocol";
}

public enum EntityKind
{
    Customer = 1,
    Technician = 2,
    Part = 3,
    WorkOrder = 4,
    WorkOrderLineItem = 5,
}

public enum MutationKind
{
    Insert = 1,
    Update = 2,
    Delete = 3,
}

public enum MutationOutcome
{
    /// <summary>The mutation was accepted and produced a new server version.</summary>
    Applied = 1,

    /// <summary>The server already saw this ClientMutationId. Treat as success.</summary>
    Duplicate = 2,

    /// <summary>
    /// The base version the mutation expected is stale. The server returns its current payload
    /// in <see cref="MutationResult.ServerPayloadJson"/>; the client must re-apply or surface
    /// a conflict UI.
    /// </summary>
    Conflict = 3,

    /// <summary>
    /// The mutation is structurally invalid or violates a server-side rule (e.g. illegal status
    /// transition, missing foreign key). <see cref="MutationResult.Error"/> explains.
    /// </summary>
    Rejected = 4,
}
