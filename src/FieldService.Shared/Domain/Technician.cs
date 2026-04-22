namespace FieldService.Shared.Domain;

public sealed class Technician : ISyncableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";

    // Skill codes are stored as a comma-separated string so the relational schema stays flat;
    // if you need real many-to-many on skills, introduce a SkillAssignment entity and let sync
    // handle it independently.
    public string SkillCodes { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
