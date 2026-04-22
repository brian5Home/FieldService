namespace FieldService.Shared.Domain;

public enum WorkOrderStatus
{
    Draft = 0,
    Scheduled = 10,
    EnRoute = 20,
    OnSite = 30,
    Completed = 40,
    Invoiced = 50,
    Canceled = 90,
}

/// <summary>
/// A service call. WorkOrder -> 0..N WorkOrderLineItem (parts + labor).
///
/// The WorkOrder and its line items sync as INDEPENDENT entities — each gets its own
/// version, each appears as its own row in the outbox. This matters because two technicians
/// might edit different line items on the same order concurrently; we don't want to serialize
/// the whole order on every line edit.
///
/// Status is a state machine; the server validates allowed transitions on push and may reject
/// a mutation with an illegal transition (e.g. Canceled -> Completed).
/// </summary>
public sealed class WorkOrder : ISyncableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Number { get; set; } = "";            // e.g. "WO-2026-04812"
    public Guid CustomerId { get; set; }
    public Guid? AssignedTechnicianId { get; set; }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Draft;

    public DateTime ScheduledFor { get; set; }
    public DateTime? ArrivedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string SignatureUrl { get; set; } = "";      // blob URL, synced lazily
    public string CustomerSignoffName { get; set; } = "";

    // Cached rollup; recomputed from line items on the server on any line change.
    public decimal TotalAmount { get; set; }

    // Child collection. Navigation is present on both client and server EF Core models.
    public List<WorkOrderLineItem> LineItems { get; set; } = new();

    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
