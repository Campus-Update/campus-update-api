using System.ComponentModel.DataAnnotations;
namespace CampusUpdate.Api.Contracts;

public sealed record RequestVerificationRequest([Required, EmailAddress] string Email);
public sealed record ConfirmVerificationRequest([Required, EmailAddress] string Email, [Required, MinLength(6), MaxLength(6)] string Code);
