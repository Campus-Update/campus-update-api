using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Users;

namespace CampusUpdate.Api.Contracts;

public sealed record RegisterRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required, MinLength(8)] string Password,
    [property: Required, MaxLength(100)] string FirstName,
    [property: Required, MaxLength(100)] string LastName,
    UserRole Role,
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId,
    string? MatriculationOrStaffNumber);

public sealed record LoginRequest([property: EmailAddress] string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record AuthResponse(Guid UserId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
