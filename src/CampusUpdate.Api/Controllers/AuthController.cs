using System.Security.Claims;
using CampusUpdate.Api.Authentication;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    CampusUpdateDbContext db,
    IPasswordHasher<AppUser> passwordHasher,
    ITokenService tokenService,
    Microsoft.Extensions.Options.IOptions<JwtOptions> jwtOptions) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (request.Role is not (UserRole.Student or UserRole.Staff))
            return BadRequest(new ProblemDetails { Title = "Administrative accounts cannot self-register." });
        if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken))
            return Conflict(new ProblemDetails { Title = "An account with this email already exists." });
        if (!await AcademicSelectionIsValid(request, cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected academic hierarchy is invalid." });

        var user = new AppUser
        {
            Email = email,
            PasswordHash = string.Empty,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Role = request.Role,
            InstitutionId = request.InstitutionId,
            FacultyId = request.FacultyId,
            DepartmentId = request.DepartmentId,
            ProgrammeId = request.ProgrammeId,
            AcademicLevelId = request.AcademicLevelId,
            MatriculationOrStaffNumber = request.MatriculationOrStaffNumber?.Trim(),
            FeedPreference = new FeedPreference()
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        var pair = tokenService.Create(user);
        user.RefreshTokenHash = tokenService.HashRefreshToken(pair.RefreshToken);
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Register), new AuthResponse(user.Id, pair.AccessToken, pair.RefreshToken, pair.ExpiresAt));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (user is null || !user.IsActive ||
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized(new ProblemDetails { Title = "Invalid email or password." });

        var pair = tokenService.Create(user);
        user.RefreshTokenHash = tokenService.HashRefreshToken(pair.RefreshToken);
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new AuthResponse(user.Id, pair.AccessToken, pair.RefreshToken, pair.ExpiresAt));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        var hash = tokenService.HashRefreshToken(request.RefreshToken);
        var user = await db.Users.SingleOrDefaultAsync(
            x => x.RefreshTokenHash == hash && x.RefreshTokenExpiresAt > DateTimeOffset.UtcNow && x.IsActive,
            cancellationToken);
        if (user is null)
            return Unauthorized(new ProblemDetails { Title = "The refresh token is invalid or expired." });

        var pair = tokenService.Create(user);
        user.RefreshTokenHash = tokenService.HashRefreshToken(pair.RefreshToken);
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new AuthResponse(user.Id, pair.AccessToken, pair.RefreshToken, pair.ExpiresAt));
    }

    [Authorize]
    [HttpGet("profile")]
    public async Task<ActionResult<UserProfileResponse>> GetProfile(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        return user is null ? Unauthorized() : Ok(ToProfileResponse(user));
    }

    [Authorize]
    [HttpPut("profile")]
    public async Task<ActionResult<UserProfileResponse>> UpdateProfile(
        UpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null)
            return Unauthorized();

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        user.MatriculationOrStaffNumber = request.MatriculationOrStaffNumber?.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToProfileResponse(user));
    }

    [Authorize]
    [HttpPut("academic-settings")]
    public async Task<ActionResult<UserProfileResponse>> UpdateAcademicSettings(
        AcademicSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null)
            return Unauthorized();
        if (!await AcademicSelectionIsValid(
                request.InstitutionId,
                request.FacultyId,
                request.DepartmentId,
                request.ProgrammeId,
                request.AcademicLevelId,
                cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected academic hierarchy is invalid." });

        user.InstitutionId = request.InstitutionId;
        user.FacultyId = request.FacultyId;
        user.DepartmentId = request.DepartmentId;
        user.ProgrammeId = request.ProgrammeId;
        user.AcademicLevelId = request.AcademicLevelId;
        user.FeedPreference.NewsEnabled = request.NewsEnabled;
        user.FeedPreference.AnnouncementsEnabled = request.AnnouncementsEnabled;
        user.FeedPreference.EventsEnabled = request.EventsEnabled;
        user.FeedPreference.AdvertisementsEnabled = request.AdvertisementsEnabled;
        user.FeedPreference.PushNotificationsEnabled = request.PushNotificationsEnabled;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToProfileResponse(user));
    }

    private async Task<AppUser?> GetCurrentUser(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;
        return await db.Users.Include(x => x.FeedPreference)
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
    }

    private static UserProfileResponse ToProfileResponse(AppUser user) => new(
        user.Id,
        user.Email,
        user.FirstName,
        user.LastName,
        user.Role,
        user.MatriculationOrStaffNumber,
        user.InstitutionId,
        user.FacultyId,
        user.DepartmentId,
        user.ProgrammeId,
        user.AcademicLevelId,
        user.FeedPreference.NewsEnabled,
        user.FeedPreference.AnnouncementsEnabled,
        user.FeedPreference.EventsEnabled,
        user.FeedPreference.AdvertisementsEnabled,
        user.FeedPreference.PushNotificationsEnabled);

    private async Task<bool> AcademicSelectionIsValid(RegisterRequest request, CancellationToken cancellationToken)
        => await AcademicSelectionIsValid(
            request.InstitutionId,
            request.FacultyId,
            request.DepartmentId,
            request.ProgrammeId,
            request.AcademicLevelId,
            cancellationToken);

    private async Task<bool> AcademicSelectionIsValid(
        Guid institutionId,
        Guid? facultyId,
        Guid? departmentId,
        Guid? programmeId,
        Guid? academicLevelId,
        CancellationToken cancellationToken)
    {
        if (!await db.Institutions.AnyAsync(x => x.Id == institutionId && x.IsActive, cancellationToken))
            return false;
        if (facultyId is not null && !await db.Faculties.AnyAsync(
                x => x.Id == facultyId && x.InstitutionId == institutionId, cancellationToken))
            return false;
        if (departmentId is not null && (facultyId is null || !await db.Departments.AnyAsync(
                x => x.Id == departmentId && x.FacultyId == facultyId, cancellationToken)))
            return false;
        if (programmeId is not null && (departmentId is null || !await db.Programmes.AnyAsync(
                x => x.Id == programmeId && x.DepartmentId == departmentId, cancellationToken)))
            return false;
        if (academicLevelId is not null && (programmeId is null || !await db.AcademicLevels.AnyAsync(
                x => x.Id == academicLevelId && x.ProgrammeId == programmeId, cancellationToken)))
            return false;
        return true;
    }
}
