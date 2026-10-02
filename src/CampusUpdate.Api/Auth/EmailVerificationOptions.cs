namespace CampusUpdate.Api.Auth;
public sealed class EmailVerificationOptions
{
    public const string SectionName = "EmailVerification";
    public int CodeLifetimeMinutes { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
}
