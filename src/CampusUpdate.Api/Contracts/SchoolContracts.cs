namespace CampusUpdate.Api.Contracts;

public sealed record AcademicLevelResponse(Guid Id, string Name, int SortOrder);
public sealed record ProgrammeResponse(Guid Id, string Name, string Code, IReadOnlyCollection<AcademicLevelResponse> Levels);
public sealed record DepartmentResponse(Guid Id, string Name, string Code, IReadOnlyCollection<ProgrammeResponse> Programmes);
public sealed record FacultyResponse(Guid Id, string Name, string Code, IReadOnlyCollection<DepartmentResponse> Departments);
public sealed record InstitutionResponse(Guid Id, string Name, string Slug, string? Acronym, string? LogoUrl, IReadOnlyCollection<FacultyResponse> Faculties);
