using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Notifications;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CampusUpdate.Api.Tests;

public sealed class AdministratorWorkflowTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public AdministratorWorkflowTests()
    {
        _client = _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        _factory.Seed();
    }

    [Fact]
    public async Task LeadSchoolAdminCanManageDelegatedAdministratorsInOwnInstitution()
    {
        Login(UserRole.LeadSchoolAdmin);
        var create = await _client.PostAsJsonAsync("/api/v1/admin/administrators",
            new CreateInstitutionAdministratorRequest(
                "news-admin@example.test", "long-test-password", "News", "Editor",
                _factory.SchoolId, UserRole.NewsAdmin));

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<InstitutionAdministratorResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(UserRole.NewsAdmin, created.Role);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/api/v1/admin/administrators",
                new CreateInstitutionAdministratorRequest(
                    "foreign-admin@example.test", "long-test-password", "Foreign", "Editor",
                    _factory.OtherSchoolId, UserRole.CalendarAdmin))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/v1/admin/administrators",
                new CreateInstitutionAdministratorRequest(
                    "lead-admin@example.test", "long-test-password", "Lead", "Admin",
                    _factory.SchoolId, UserRole.LeadSchoolAdmin))).StatusCode);

        var updated = await _client.PatchAsJsonAsync(
            $"/api/v1/admin/administrators/{created.Id}",
            new UpdateInstitutionAdministratorRequest(UserRole.CalendarAdmin, false));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedAdministrator = await updated.Content.ReadFromJsonAsync<InstitutionAdministratorResponse>(JsonOptions);
        Assert.Equal(UserRole.CalendarAdmin, updatedAdministrator!.Role);
        Assert.False(updatedAdministrator.IsActive);
    }

    [Fact]
    public async Task OpenApiDocumentIncludesNewAdministrativeEndpointsAndRoles()
    {
        var openApiResponse = await _client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
        var openApiDocument = await openApiResponse.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/admin/administrators", openApiDocument, StringComparison.Ordinal);
        Assert.Contains("/api/v1/content/pending-approval", openApiDocument, StringComparison.Ordinal);
        Assert.Contains("/api/v1/emergency-alerts", openApiDocument, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/profile/photo", openApiDocument, StringComparison.Ordinal);
        Assert.Contains("profileImageUrl", openApiDocument, StringComparison.Ordinal);
        Assert.Contains("isEmergencyAlert", openApiDocument, StringComparison.Ordinal);

        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/admin/administrators", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/content/pending-approval", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/emergency-alerts", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/profile/photo", document, StringComparison.Ordinal);
        Assert.Contains("profileImageUrl", document, StringComparison.Ordinal);
        Assert.Contains("LeadSchoolAdmin", document, StringComparison.Ordinal);
        Assert.Contains("isEmergencyAlert", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewsAdminSubmitsContentForLeadApprovalAndCannotPublishIt()
    {
        Login(UserRole.NewsAdmin);
        var request = new CreateContentRequest(
            "Campus announcement", "Details", null, ContentType.Announcement, UrgencyLevel.Normal,
            SourceType.Official, "School", null, null, null, null, null, null,
            [new AudienceRequest(_factory.SchoolId, null, null, null, null)]);
        var create = await _client.PostAsJsonAsync("/api/v1/content/json", request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var content = await create.Content.ReadFromJsonAsync<ContentResponse>(JsonOptions);
        Assert.NotNull(content);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/content/{content.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/v1/content/{_factory.PendingEventId}")).StatusCode);

        var submit = await _client.PatchAsJsonAsync(
            $"/api/v1/content/{content.Id}/status",
            new ChangeContentStatusRequest(ContentStatus.PendingApproval));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PatchAsJsonAsync(
                $"/api/v1/content/{content.Id}/status",
                new ChangeContentStatusRequest(ContentStatus.Published))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/api/v1/content/json",
                request with { Type = ContentType.Event, EventStartsAt = DateTimeOffset.UtcNow.AddDays(1) })).StatusCode);

        Login(UserRole.LeadSchoolAdmin);
        var queue = await _client.GetFromJsonAsync<ContentResponse[]>(
            "/api/v1/content/pending-approval?institutionId=" + _factory.SchoolId, JsonOptions);
        Assert.Contains(queue!, item => item.Id == content.Id);

        var approve = await _client.PatchAsJsonAsync(
            $"/api/v1/content/{content.Id}/status",
            new ChangeContentStatusRequest(ContentStatus.Published));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    [Fact]
    public async Task CalendarAdminMustSubmitDraftBeforeLeadCanPublish()
    {
        Login(UserRole.CalendarAdmin);
        var create = await _client.PostAsJsonAsync("/api/v1/calendar/json",
            new CreateAcademicCalendarRequest(
                _factory.SchoolId, "Calendar draft", "2026/2027", "https://example.test/calendar.png",
                DateTimeOffset.UtcNow, false, "Draft"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var calendar = await create.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        Assert.NotNull(calendar);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsync($"/api/v1/calendar/{calendar.Id}/publish", null)).StatusCode);
        var submit = await _client.PostAsync($"/api/v1/calendar/{calendar.Id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        Login(UserRole.LeadSchoolAdmin);
        var pending = await _client.GetFromJsonAsync<AcademicCalendarResponse[]>(
            "/api/v1/calendar/manage?status=PendingApproval", JsonOptions);
        Assert.Contains(pending!, item => item.Id == calendar.Id);
        var publish = await _client.PostAsync($"/api/v1/calendar/{calendar.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = await publish.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        Assert.Equal("Published", published!.Status);
        Assert.True(published.IsOfficial);
    }

    [Fact]
    public async Task RejectedCalendarCanBeRevisedAndResubmitted()
    {
        Login(UserRole.CalendarAdmin);
        var create = await _client.PostAsJsonAsync("/api/v1/calendar/json",
            new CreateAcademicCalendarRequest(
                _factory.SchoolId, "Calendar draft", "2026/2027", "https://example.test/calendar.png",
                DateTimeOffset.UtcNow, false, "Draft"));
        var calendar = await create.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsync($"/api/v1/calendar/{calendar!.Id}/submit", null)).StatusCode);
        Login(UserRole.LeadSchoolAdmin);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(
                $"/api/v1/calendar/{calendar.Id}/reject",
                new RejectAcademicCalendarRequest("Revise the exam schedule."))).StatusCode);

        Login(UserRole.CalendarAdmin);
        var revised = await _client.PutAsJsonAsync($"/api/v1/calendar/{calendar.Id}",
            new CreateAcademicCalendarRequest(
                _factory.SchoolId, "Revised calendar", "2026/2027", "https://example.test/calendar-v2.png",
                DateTimeOffset.UtcNow, false, "Draft"));
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var revisedCalendar = await revised.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        Assert.Equal("Draft", revisedCalendar!.Status);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsync($"/api/v1/calendar/{calendar.Id}/submit", null)).StatusCode);
    }

    [Fact]
    public async Task EmergencyAlertIsPublishedAndQueuedForStudentsAndStaffRegardlessOfPreferences()
    {
        var (studentId, _) = _factory.AddUser(UserRole.Student);
        var (staffId, _) = _factory.AddUser(UserRole.Staff);
        var (foreignStudentId, _) = _factory.AddUser(UserRole.Student, _factory.OtherSchoolId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            foreach (var userId in new[] { studentId, staffId, foreignStudentId })
            {
                var preference = await db.FeedPreferences.SingleAsync(x => x.UserId == userId);
                preference.AnnouncementsEnabled = false;
                preference.PushNotificationsEnabled = false;
            }
            await db.SaveChangesAsync();
        }

        Login(UserRole.LeadSchoolAdmin);
        var response = await _client.PostAsJsonAsync("/api/v1/emergency-alerts",
            new CreateEmergencyAlertRequest("Campus closure", "Leave the campus immediately."));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var alert = await response.Content.ReadFromJsonAsync<ContentResponse>(JsonOptions);
        Assert.NotNull(alert);
        Assert.Equal(ContentStatus.Published, alert.Status);
        Assert.Equal(UrgencyLevel.Urgent, alert.Urgency);
        Assert.True(alert.IsEmergencyAlert);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        var savedAlert = await verifyDb.ContentItems.Include(x => x.Audiences)
            .SingleAsync(x => x.Id == alert.Id);
        Assert.True(savedAlert.IsEmergencyAlert);
        Assert.Equal(2, savedAlert.Audiences.Count);
        var notifications = await verifyDb.UserNotifications
            .Where(x => x.ContentItemId == alert.Id)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.Contains(notifications, notification => notification.UserId == studentId);
        Assert.Contains(notifications, notification => notification.UserId == staffId);
        Assert.DoesNotContain(notifications, notification => notification.UserId == foreignStudentId);
        Assert.All(notifications, notification =>
            Assert.Contains("\"emergencyAlert\":\"true\"", notification.DataJson, StringComparison.Ordinal));

        Login(UserRole.Student);
        var feed = await _client.GetFromJsonAsync<ContentResponse[]>(
            "/api/v1/feed?type=Announcement", JsonOptions);
        Assert.Contains(feed!, item => item.Id == alert.Id);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/api/v1/emergency-alerts",
                new CreateEmergencyAlertRequest("Unauthorized", "Not permitted."))).StatusCode);
    }

    private void Login(UserRole role)
    {
        var (_, token) = _factory.AddUser(role);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
