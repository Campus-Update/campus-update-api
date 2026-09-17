using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Users;

namespace CampusUpdate.Api.Contracts;

public sealed record CreateSchoolRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(120), RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")] string Slug,
    [Required, MaxLength(100)] string State,
    bool IsActive = true);

public sealed record AdminSchoolResponse(Guid Id, string Name, string Slug, string? State, bool IsActive);

public sealed record CreateSchoolAdminRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(12)] string Password,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    Guid InstitutionId);

public sealed record UpdateAdminUserRequest(UserRole Role, bool IsActive);
public sealed record AdminUserResponse(Guid Id, string Email, UserRole Role, Guid InstitutionId, bool IsActive);
