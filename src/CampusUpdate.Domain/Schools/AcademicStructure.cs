using CampusUpdate.Domain.Common;

namespace CampusUpdate.Domain.Schools;

public sealed class Institution : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Acronym { get; set; }
    public string? LogoUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<Faculty> Faculties { get; set; } = [];
}

public sealed class Faculty : Entity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public Guid InstitutionId { get; set; }
    public Institution Institution { get; set; } = null!;
    public ICollection<Department> Departments { get; set; } = [];
}

public sealed class Department : Entity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public Guid FacultyId { get; set; }
    public Faculty Faculty { get; set; } = null!;
    public ICollection<Programme> Programmes { get; set; } = [];
}

public sealed class Programme : Entity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public ICollection<AcademicLevel> Levels { get; set; } = [];
}

public sealed class AcademicLevel : Entity
{
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public Guid ProgrammeId { get; set; }
    public Programme Programme { get; set; } = null!;
}
