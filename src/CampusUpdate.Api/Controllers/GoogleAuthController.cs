using Google.Apis.Auth;
using CampusUpdate.Api.Auth;
using CampusUpdate.Api.Authentication;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CampusUpdate.Domain.Notifications;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class GoogleAuthController(
    CampusUpdateDbContext db,
    ITokenService tokenService,
    IPasswordHasher<AppUser> passwordHasher,
    IOptions<JwtOptions> jwtOptions,
    IOptions<GoogleOptions> googleOptions) : ControllerBase
{
    [HttpPost("google")]
    public async Task<ActionResult<GoogleAuthResponse>> SignIn(GoogleAuthRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(googleOptions.Value.ClientId))
            return StatusCode(503, new ProblemDetails { Title = "Google sign-in is not configured." });
        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(request.IdToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = [googleOptions.Value.ClientId] });
        }
        catch (Exception) { return Unauthorized(new ProblemDetails { Title = "The Google identity token is invalid." }); }
        if (string.IsNullOrWhiteSpace(payload.Email) || payload.EmailVerified != true)
            return Unauthorized(new ProblemDetails { Title = "A verified Google email is required." });

        var email = payload.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.GoogleSubject == payload.Subject || x.Email == email, cancellationToken);
        var isNew = user is null;
        if (user is null)
        {
            if (request.Role is not (UserRole.Student or UserRole.Staff) ||
                !await db.Institutions.AnyAsync(x => x.Id == request.InstitutionId && x.IsActive, cancellationToken))
                return BadRequest(new ProblemDetails { Title = "Complete onboarding with a valid student or staff role and institution." });
            user = new AppUser { Email = email, FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(), PasswordHash = passwordHasher.HashPassword(null!, Guid.NewGuid().ToString()), Role = request.Role, InstitutionId = request.InstitutionId, FacultyId = request.FacultyId, DepartmentId = request.DepartmentId, ProgrammeId = request.ProgrammeId, AcademicLevelId = request.AcademicLevelId, MatriculationOrStaffNumber = request.MatriculationOrStaffNumber?.Trim(), GoogleSubject = payload.Subject, FeedPreference = new FeedPreference() };
            db.Users.Add(user);
        }
        else
        {
            if (user.GoogleSubject is null) user.GoogleSubject = payload.Subject;
            if (!user.IsActive) return Unauthorized(new ProblemDetails { Title = "This account is inactive." });
        }
        var pair = tokenService.Create(user);
        db.UserActivities.Add(new UserActivity { UserId = user.Id, InstitutionId = user.InstitutionId, ActivityType = isNew ? "registration" : "login", OccurredAt = DateTimeOffset.UtcNow });
        user.RefreshTokenHash = tokenService.HashRefreshToken(pair.RefreshToken);
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new GoogleAuthResponse(user.Id, pair.AccessToken, pair.RefreshToken, pair.ExpiresAt, isNew));
    }
}
