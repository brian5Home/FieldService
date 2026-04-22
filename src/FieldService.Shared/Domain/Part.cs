namespace FieldService.Shared.Domain;

public sealed class Part : ISyncableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // SKU is the human-facing identifier, but it's NOT the primary key because it can be renamed
    // and we need a stable Id for sync references. A unique index on SKU is enforced server-side.
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";

    public decimal UnitPrice { get; set; }

    // StockOnHand is a CONVERGENT quantity — technicians deduct parts offline and the server
    // aggregates. We don't resolve stock conflicts with last-write-wins; instead, the line item
    // mutation carries a stock delta and the server accumulates on apply. StockOnHand here is
    // only the last-pulled snapshot for display purposes.
    public int StockOnHand { get; set; }

    public bool IsActive { get; set; } = true;

    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
