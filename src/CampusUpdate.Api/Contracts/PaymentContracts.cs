using System.ComponentModel.DataAnnotations;

namespace CampusUpdate.Api.Contracts;

public sealed record InitializePaymentRequest(
    [Required, EmailAddress] string Email,
    [Range(100, long.MaxValue)] long Amount,
    [Required, MaxLength(100)] string Reference,
    [Url] string? CallbackUrl);

public sealed record PaymentResponse(string AuthorizationUrl, string AccessCode, string Reference);
public sealed record PaymentVerificationResponse(string Reference, string Status, long Amount, string Currency, string? PaidAt);
