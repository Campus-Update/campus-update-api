using CampusUpdate.Domain.Common;

namespace CampusUpdate.Domain.Users;

public sealed class AuditLog : Entity
{
    public Guid ActorId { get; set; }
    public required string Action { get; set; }
    public Guid TargetId { get; set; }
    public Guid InstitutionId { get; set; }
    public required string Details { get; set; }
}
