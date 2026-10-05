using System.Net.Http.Json;
using Microsoft.Extensions.Options;
namespace CampusUpdate.Api.Auth;

public sealed class ResendEmailSender(HttpClient client, IOptions<EmailOptions> options)
{
    private readonly EmailOptions settings = options.Value;
    public async Task SendVerificationAsync(string email, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.From)) throw new InvalidOperationException("Email delivery is not configured.");
        using var response = await client.PostAsJsonAsync("/emails", new { from = settings.From, to = new[] { email }, subject = "Campus Update verification code", html = $"<p>Your Campus Update verification code is <strong>{code}</strong>.</p><p>This code expires in 10 minutes.</p>" }, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Email delivery failed.");
    }
}
