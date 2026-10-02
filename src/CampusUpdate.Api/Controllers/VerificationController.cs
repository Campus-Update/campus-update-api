using System.Security.Cryptography;
using System.Text;
using CampusUpdate.Api.Auth;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CampusUpdate.Api.Controllers;
[ApiController, Route("api/v1/auth/verification")]
public sealed class VerificationController(CampusUpdateDbContext db, IOptions<EmailVerificationOptions> options, ILogger<VerificationController> logger, ResendEmailSender emailSender) : ControllerBase
{
    [HttpPost("request")]
    public async Task<IActionResult> RequestCode(RequestVerificationRequest request, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == request.Email.Trim().ToLowerInvariant(), ct);
        if (user is null) return Ok(new { message = "If the account exists, a verification code has been sent." });
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        user.EmailVerificationCodeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        user.EmailVerificationExpiresAt = DateTimeOffset.UtcNow.AddMinutes(options.Value.CodeLifetimeMinutes);
        user.EmailVerificationAttempts = 0;
        await db.SaveChangesAsync(ct);
        try { await emailSender.SendVerificationAsync(user.Email, code, ct); }
        catch (InvalidOperationException ex) { logger.LogWarning(ex, "Email delivery unavailable."); return StatusCode(503, new ProblemDetails { Title = "Email delivery is not configured." }); }
        return Ok(new { message = "If the account exists, a verification code has been sent." });
    }

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(ConfirmVerificationRequest request, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == request.Email.Trim().ToLowerInvariant(), ct);
        if (user is null || user.EmailVerificationCodeHash is null || user.EmailVerificationExpiresAt < DateTimeOffset.UtcNow || user.EmailVerificationAttempts >= options.Value.MaxAttempts)
            return BadRequest(new ProblemDetails { Title = "The verification code is invalid or expired." });
        user.EmailVerificationAttempts++;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Code)));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(user.EmailVerificationCodeHash), Convert.FromHexString(hash)))
        { await db.SaveChangesAsync(ct); return BadRequest(new ProblemDetails { Title = "The verification code is invalid or expired." }); }
        user.EmailVerified = true; user.EmailVerificationCodeHash = null; user.EmailVerificationExpiresAt = null; user.EmailVerificationAttempts = 0;
        await db.SaveChangesAsync(ct); return Ok(new { message = "Email verified." });
    }
}
