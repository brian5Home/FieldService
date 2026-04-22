namespace FieldService.Shared.Domain;

public sealed class Customer : ISyncableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";

    // Address is stored flat rather than as a child entity because it never exists without its
    // customer and we don't want to teach the sync protocol about owned types.
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string PostalCode { get; set; } = "";

    public CustomerTier Tier { get; set; } = CustomerTier.Standard;
    public string Notes { get; set; } = "";

    // ISyncableEntity
    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public enum CustomerTier
{
    Standard = 0,
    Preferred = 1,
    Vip = 2,
}
