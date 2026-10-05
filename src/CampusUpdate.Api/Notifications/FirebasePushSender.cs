using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace CampusUpdate.Api.Notifications;

public sealed class FirebasePushSender : IDisposable
{
    private readonly FirebaseApp? app;
    private readonly ILogger<FirebasePushSender> logger;
    public bool IsConfigured => app is not null;

    public FirebasePushSender(IOptions<FcmOptions> options, ILogger<FirebasePushSender> logger)
    {
        this.logger = logger;
        if (!string.IsNullOrWhiteSpace(options.Value.ServiceAccountJson))
            app = FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromJson(options.Value.ServiceAccountJson) });
    }

    public async Task<bool> SendAsync(string token, string title, string body, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        if (app is null) return false;
        try
        {
            await FirebaseMessaging.GetMessaging(app).SendAsync(new Message
            {
                Token = token,
                Notification = new Notification { Title = title, Body = body },
                Data = data.ToDictionary(x => x.Key, x => x.Value)
            }, false, ct);
            return true;
        }
        catch (FirebaseMessagingException ex)
        {
            logger.LogWarning("FCM send failed ({Code}): {Message}", ex.MessagingErrorCode, ex.Message);
            return false;
        }
    }

    public void Dispose() => app?.Delete();
}
