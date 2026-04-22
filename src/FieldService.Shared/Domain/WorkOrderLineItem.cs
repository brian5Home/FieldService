namespace FieldService.Shared.Domain;

public enum LineItemKind
{
    Part = 0,
    Labor = 1,
    Adjustment = 2,
}

public sealed class WorkOrderLineItem : ISyncableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }

    public LineItemKind Kind { get; set; }

    // For Kind == Part: which part was consumed. Null for Labor/Adjustment lines.
    public Guid? PartId { get; set; }

    public string Description { get; set; } = "";

    public decimal Quantity { get; set; }    // hours for Labor, units for Part
    public decimal UnitPrice { get; set; }
    public decimal LineTotal => Math.Round(Quantity * UnitPrice, 2);

    // For Part lines written offline: the stock delta the server should apply on accept.
    // This is a SEMANTIC field the push handler consumes — see SyncEndpoints.cs.
    public int StockDelta { get; set; }

    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
