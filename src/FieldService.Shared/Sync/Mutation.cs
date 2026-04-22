namespace FieldService.Shared.Sync;

/// <summary>
/// A single client-originated mutation. Carries a client-generated idempotency key so retries
/// are safe. PayloadJson is the full entity after the mutation (not a diff); this simplifies
/// the server handler at the cost of slightly larger requests — an acceptable trade for the
/// kinds of entities in this app.
/// </summary>
public sealed record Mutation(
    Guid ClientMutationId,
    EntityKind Entity,
    Guid EntityId,
    MutationKind Kind,
    long? BaseVersion,          // null for Insert; otherwise the Version the mutation was made against
    string PayloadJson,         // JSON of the entity; empty for Delete
    DateTime CreatedAtUtc
);

public sealed record PushRequest(List<Mutation> Mutations);

public sealed record MutationResult(
    Guid ClientMutationId,
    MutationOutcome Outcome,
    long? NewVersion,
    string? ServerPayloadJson,  // populated on Conflict so client can re-apply
    string? Error
);

public sealed record PushResponse(List<MutationResult> Results, long ServerVersionHighWaterMark);
