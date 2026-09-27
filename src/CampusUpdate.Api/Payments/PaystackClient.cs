using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CampusUpdate.Api.Payments;

public sealed class PaystackClient(HttpClient httpClient, IOptions<PaystackOptions> options)
{
    private readonly PaystackOptions settings = options.Value;

    public async Task<PaystackTransactionData> InitializeAsync(
        string email, long amount, string reference, string? callbackUrl, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var request = new { email, amount, currency = "NGN", reference, callback_url = callbackUrl };
        using var response = await httpClient.PostAsJsonAsync("/transaction/initialize", request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<PaystackResponse<PaystackTransactionData>>(cancellationToken);
        if (!response.IsSuccessStatusCode || result is null || !result.Status)
            throw new PaystackException(result?.Message ?? "Paystack transaction initialization failed.");
        return result.Data!;
    }

    public async Task<PaystackTransactionData> VerifyAsync(string reference, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var response = await httpClient.GetAsync($"/transaction/verify/{Uri.EscapeDataString(reference)}", cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<PaystackResponse<PaystackTransactionData>>(cancellationToken);
        if (!response.IsSuccessStatusCode || result is null || !result.Status)
            throw new PaystackException(result?.Message ?? "Paystack transaction verification failed.");
        return result.Data!;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(settings.SecretKey))
            throw new PaystackNotConfiguredException();
    }
}

public sealed class PaystackNotConfiguredException() : Exception("Paystack is not configured.");
public sealed class PaystackException(string message) : Exception(message);
public sealed record PaystackResponse<T>(bool Status, string Message, T? Data);
public sealed record PaystackTransactionData(
    [property: JsonPropertyName("authorization_url")] string AuthorizationUrl,
    [property: JsonPropertyName("access_code")] string AccessCode,
    string Reference,
    string Status,
    long Amount,
    string Currency,
    string? PaidAt);
