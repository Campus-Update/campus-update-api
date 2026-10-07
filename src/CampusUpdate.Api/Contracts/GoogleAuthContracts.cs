using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Users;

namespace CampusUpdate.Api.Contracts;

public sealed record GoogleAuthRequest(
    [Required] string IdToken,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    UserRole Role,
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId,
    string? MatriculationOrStaffNumber);

public sealed record GoogleAuthResponse(Guid UserId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, bool IsNewAccount);
