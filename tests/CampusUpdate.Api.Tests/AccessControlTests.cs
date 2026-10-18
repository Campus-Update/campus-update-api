using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CampusUpdate.Api.Authentication;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CampusUpdate.Api.Tests;

public sealed class AccessControlTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public AccessControlTests()
    {
        _client = _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        _factory.Seed();
    }

    [Fact]
    public async Task AnonymousAndMalformedTokensReturn401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/feed")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/feed")).StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    public async Task InvalidSignedTokensReturn401(string failure)
    {
        var id = Login(UserRole.Student);
        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var token = new JwtSecurityToken(
            issuer: failure == "issuer" ? "wrong-issuer" : options.Issuer,
            audience: failure == "audience" ? "wrong-audience" : options.Audience,
            claims: [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, "Student")],
            expires: failure == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                failure == "signature" ? "wrong-signing-key-with-at-least-32-bytes" : options.Key)), SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/auth/profile")).StatusCode);
    }

    [Fact]
    public async Task DeletedUserCannotUseExistingAccessToken()
    {
        var id = Login(UserRole.Student);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        db.Users.Remove(await db.Users.SingleAsync(x => x.Id == id));
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/auth/profile")).StatusCode);
    }

    [Fact]
    public async Task InvalidOnboardingRequestsReturn400WithoutAuditRecords()
    {
        Login(UserRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/admin/schools",
            new CreateSchoolRequest("School", "INVALID SLUG", "Osun"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/admin/users",
            new CreateSchoolAdminRequest("admin@example.test", "short", "Test", "Admin", _factory.SchoolId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/admin/users",
            new CreateSchoolAdminRequest("admin@example.test", "test-password-123", "Test", "Admin", Guid.NewGuid()))).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SuperAdminCannotBeCreatedOrModifiedThroughRoleUpdates()
    {
        var id = Login(UserRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PatchAsJsonAsync($"/api/v1/admin/users/{id}",
            new UpdateAdminUserRequest(UserRole.Student, false))).StatusCode);
        var (studentId, _) = _factory.AddUser(UserRole.Student);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PatchAsJsonAsync($"/api/v1/admin/users/{studentId}",
            new UpdateAdminUserRequest(UserRole.SuperAdmin, true))).StatusCode);
    }

    [Fact]
    public async Task SuperAdminCanListPendingContentWithFiltersAndPagination()
    {
        Login(UserRole.SuperAdmin);

        var response = await _client.GetAsync("/api/v1/admin/content/pending?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var firstPage = await response.Content.ReadFromJsonAsync<AdminModerationContentPageResponse>(JsonOptions);
        Assert.NotNull(firstPage);
        Assert.Equal(1, firstPage.Page);
        Assert.Equal(1, firstPage.PageSize);
        Assert.Equal(3, firstPage.TotalCount);
        Assert.Single(firstPage.Items);
        Assert.Equal(ContentStatus.PendingApproval, firstPage.Items.Single().Status);

        var filteredResponse = await _client.GetAsync($"/api/v1/admin/content/pending?institutionId={_factory.SchoolId}&type=Event");
        var filtered = await filteredResponse.Content.ReadFromJsonAsync<AdminModerationContentPageResponse>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);
        Assert.NotNull(filtered);
        var eventItem = Assert.Single(filtered.Items);
        Assert.Equal(_factory.PendingEventId, eventItem.Id);
        Assert.Equal("Main auditorium", eventItem.EventLocation);
        Assert.Equal(ContentType.Event, eventItem.Type);

        var searchResponse = await _client.GetAsync($"/api/v1/admin/content/pending?institutionId={_factory.SchoolId}&search=Pending%20summary");
        var search = await searchResponse.Content.ReadFromJsonAsync<AdminModerationContentPageResponse>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.Equal(_factory.PendingApprovalId, Assert.Single(search!.Items).Id);
    }

    [Theory]
    [InlineData(UserRole.Student)]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.SchoolAdmin)]
    public async Task NonSuperAdminsCannotListPendingContent(UserRole role)
    {
        Login(role);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.GetAsync("/api/v1/admin/content/pending")).StatusCode);
    }

    [Fact]
    public async Task CalendarReadsStayWithinTheUsersSchool()
    {
        Login(UserRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/calendar",
            new CreateAcademicCalendarRequest(_factory.OtherSchoolId, "Foreign calendar", "2026/2027",
                "https://example.com/calendar.png", DateTimeOffset.UtcNow))).StatusCode);
        Login(UserRole.Student);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/calendar/latest")).StatusCode);
    }

    [Theory]
    [InlineData(UserRole.Student)]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.SchoolAdmin)]
    public async Task NonSuperAdminsCannotOnboardSchoolsOrAdmins(UserRole role)
    {
        Login(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/admin/schools",
            new CreateSchoolRequest("Example", "example", "Osun"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/admin/users",
            new CreateSchoolAdminRequest("admin@example.test", "test-password-123", "Test", "Admin", _factory.SchoolId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/admin/schools")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/admin/audit")).StatusCode);
    }

    [Theory]
    [InlineData(UserRole.Student)]
    [InlineData(UserRole.Staff)]
    public async Task ReadersCannotCreateContentOrReadDrafts(UserRole role)
    {
        Login(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/content", Content(_factory.SchoolId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/content/{_factory.DraftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/content/{_factory.PublishedId}")).StatusCode);
    }

    [Fact]
    public async Task SchoolAdminCannotReadForeignDraftOrWriteToForeignSchool()
    {
        Login(UserRole.SchoolAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/content/{_factory.DraftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/content", Content(_factory.OtherSchoolId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/calendar",
            new CreateAcademicCalendarRequest(_factory.OtherSchoolId, "Calendar", "2026/2027", "https://example.com/calendar.png", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/content", Content(_factory.SchoolId))).StatusCode);
    }

    [Fact]
    public async Task SchoolAdminCannotReassignTheirInstitution()
    {
        Login(UserRole.SchoolAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PutAsJsonAsync("/api/v1/auth/academic-settings",
            Settings(_factory.OtherSchoolId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/v1/content", Content(_factory.OtherSchoolId))).StatusCode);
    }

    [Fact]
    public async Task AudienceCannotReferenceAnotherSchoolsFaculty()
    {
        Login(UserRole.SchoolAdmin);
        var request = Content(_factory.SchoolId) with
        {
            Audiences = [new AudienceRequest(_factory.SchoolId, _factory.ForeignFacultyId, null, null, null)]
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/content", request)).StatusCode);
    }

    [Fact]
    public async Task AllCampusPreferenceExpandsFeedOnlyWithinOwnSchool()
    {
        Login(UserRole.Student);
        var personalized = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed", JsonOptions);
        Assert.DoesNotContain(personalized!, x => x.Id == _factory.TargetedId);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/v1/auth/academic-settings",
            Settings(_factory.SchoolId) with { AllCampusFeed = true })).StatusCode);
        var campus = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed", JsonOptions);
        Assert.Contains(campus!, x => x.Id == _factory.TargetedId);
        Assert.DoesNotContain(campus!, x => x.Id == _factory.DraftId);
        Assert.DoesNotContain(campus!, x => x.Id == _factory.ForeignPublishedId);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/content/{_factory.TargetedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/content/{_factory.DraftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/content/{_factory.ForeignPublishedId}")).StatusCode);
    }

    [Fact]
    public async Task RegistrationLoginAndRefreshRotationWorkThroughHttp()
    {
        var registration = new RegisterRequest("student-new@example.test", "test-password-123", "Test", "Student", UserRole.Student,
            _factory.SchoolId, null, null, null, null, null);
        var registered = await _client.PostAsJsonAsync("/api/v1/auth/register", registration);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/v1/auth/register", registration)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(registration.Email, "wrong-password"))).StatusCode);
        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(registration.Email, registration.Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<AuthResponse>();
        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(tokens!.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(tokens.RefreshToken))).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var profile = await _client.GetFromJsonAsync<UserProfileResponse>("/api/v1/auth/profile", JsonOptions);
        Assert.Equal(registration.Email, profile!.Email);
        Assert.False(profile.AllCampusFeed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledUsersOrChangedRolesInvalidateExistingAccessTokens(bool disable)
    {
        var id = Login(UserRole.SchoolAdmin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        var user = await db.Users.SingleAsync(x => x.Id == id);
        if (disable) user.IsActive = false;
        else user.Role = UserRole.Student;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/feed")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/content", Content(_factory.SchoolId))).StatusCode);
    }

    [Fact]
    public async Task SuperAdminOnboardingAndAccessUpdatesAreAuditedWithoutPasswords()
    {
        var actorId = Login(UserRole.SuperAdmin);
        var schoolResponse = await _client.PostAsJsonAsync("/api/v1/admin/schools", new CreateSchoolRequest("New School", "new-school", "Osun"));
        Assert.Equal(HttpStatusCode.Created, schoolResponse.StatusCode);
        var school = await schoolResponse.Content.ReadFromJsonAsync<AdminSchoolResponse>();
        var adminResponse = await _client.PostAsJsonAsync("/api/v1/admin/users",
            new CreateSchoolAdminRequest("new-admin@example.test", "test-password-123", "School", "Admin", school!.Id));
        Assert.Equal(HttpStatusCode.Created, adminResponse.StatusCode);
        var admin = await adminResponse.Content.ReadFromJsonAsync<AdminUserResponse>(JsonOptions);
        Assert.Equal(UserRole.SchoolAdmin, admin!.Role);
        Assert.Equal(school.Id, admin.InstitutionId);
        Assert.Equal(HttpStatusCode.OK, (await _client.PatchAsJsonAsync($"/api/v1/admin/users/{admin.Id}",
            new UpdateAdminUserRequest(UserRole.Staff, false))).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var logs = await scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().AuditLogs.ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Equal(actorId, log.ActorId);
            Assert.DoesNotContain("test-password", log.Details);
        });
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/content/{_factory.DraftId}")).StatusCode);
    }

    [Fact]
    public async Task AdministrativeRolesCannotSelfRegister()
    {
        var request = new RegisterRequest("attacker@example.test", "password-123", "Test", "User", UserRole.SuperAdmin,
            _factory.SchoolId, null, null, null, null, null);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/register", request)).StatusCode);
    }

    [Fact]
    public async Task StudentAudienceRemainsRestrictedWithAllCampusEnabled()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var audience = await db.ContentAudiences.SingleAsync(a => a.ContentItemId == _factory.PublishedId);
            audience.TargetAudience = TargetAudience.Students;
            await db.SaveChangesAsync();
        }
        Login(UserRole.Staff);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/v1/auth/academic-settings", Settings(_factory.SchoolId))).StatusCode);
        var feed = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed", JsonOptions);
        Assert.DoesNotContain(feed!, c => c.Id == _factory.PublishedId);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/content/{_factory.PublishedId}")).StatusCode);
        Login(UserRole.Student);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/content/{_factory.PublishedId}")).StatusCode);
    }

    [Fact]
    public async Task FeedFetchRecordsActivityAndAnalyticsReportsDau()
    {
        var id = Login(UserRole.Student);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/feed")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            Assert.True(await db.UserActivities.AnyAsync(a => a.UserId == id && a.ActivityType == "feed_fetch"));
        }
        Login(UserRole.SuperAdmin);
        var response = await _client.GetAsync("/api/v1/admin/analytics");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("totals").GetProperty("dau").GetInt32());
        Assert.True(body.RootElement.GetProperty("totals").GetProperty("totalRegisteredUsers").GetInt32() >= 3);
    }

    [Fact]
    public async Task SuperAdminSchoolAndUserListsIncludeCountsStatusAndLastActive()
    {
        var (studentId, _) = _factory.AddUser(UserRole.Student);
        _factory.AddUser(UserRole.Staff);
        _factory.AddUser(UserRole.SchoolAdmin);
        var lastActive = DateTimeOffset.UtcNow.AddMinutes(-5);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            db.UserActivities.Add(new CampusUpdate.Domain.Notifications.UserActivity
            {
                UserId = studentId,
                InstitutionId = _factory.SchoolId,
                ActivityType = "login",
                OccurredAt = lastActive
            });
            db.SaveChanges();
        }

        Login(UserRole.SuperAdmin);
        var schools = await _client.GetFromJsonAsync<AdminSchoolPageResponse>("/api/v1/admin/schools?isActive=true", JsonOptions);
        Assert.NotNull(schools);
        var school = Assert.Single(schools.Items, x => x.Id == _factory.SchoolId);
        Assert.True(school.IsActive);
        Assert.Equal(1, school.StudentCount);
        Assert.Equal(1, school.StaffCount);
        Assert.Equal(1, school.AdminCount);
        Assert.Equal(2, school.LiveContentCount);
        var detail = await _client.GetFromJsonAsync<AdminSchoolDetailResponse>(
            $"/api/v1/admin/schools/{_factory.SchoolId}", JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal(school.StudentCount, detail.StudentCount);
        Assert.Single(detail.Faculties);

        var users = await _client.GetFromJsonAsync<AdminUserPageResponse>(
            $"/api/v1/admin/users?institutionId={_factory.SchoolId}&role=Student", JsonOptions);
        var listedStudent = Assert.Single(users!.Items);
        Assert.Equal(studentId, listedStudent.Id);
        Assert.Equal("School A", listedStudent.InstitutionName);
        Assert.Equal(lastActive, listedStudent.LastActiveAt);
    }

    [Fact]
    public async Task ContentResponsesIncludeUniqueReachAndNotificationOpens()
    {
        var (studentId, token) = _factory.AddUser(UserRole.Student);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            db.ContentViews.Add(new CampusUpdate.Domain.Notifications.ContentView
            {
                UserId = studentId,
                ContentItemId = _factory.PublishedId,
                InstitutionId = _factory.SchoolId
            });
            db.UserNotifications.Add(new CampusUpdate.Domain.Notifications.UserNotification
            {
                UserId = studentId,
                ContentItemId = _factory.PublishedId,
                Title = "News",
                Body = "News",
                OpenedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.GetFromJsonAsync<ContentResponse>($"/api/v1/content/{_factory.PublishedId}", JsonOptions);
        Assert.NotNull(response);
        Assert.Equal(1, response.UniqueReachCount);
        Assert.Equal(1, response.NotificationOpenCount);
        var feed = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed", JsonOptions);
        var feedItem = Assert.Single(feed!, x => x.Id == _factory.PublishedId);
        Assert.Equal(1, feedItem.UniqueReachCount);
        Assert.Equal(1, feedItem.NotificationOpenCount);
    }

    [Fact]
    public async Task ContentMutationsAreAuditedAndVisibleToSuperAdmin()
    {
        Login(UserRole.SchoolAdmin);
        var created = await _client.PostAsJsonAsync("/api/v1/content", Content(_factory.SchoolId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var content = await created.Content.ReadFromJsonAsync<ContentResponse>(JsonOptions);
        Assert.NotNull(content);
        Assert.Equal(HttpStatusCode.OK, (await _client.PatchAsJsonAsync($"/api/v1/content/{content.Id}/status",
            new ChangeContentStatusRequest(ContentStatus.PendingApproval))).StatusCode);

        Login(UserRole.SuperAdmin);
        var audit = await _client.GetFromJsonAsync<AdminAuditLogPageResponse>(
            $"/api/v1/admin/audit?targetId={content.Id}", JsonOptions);
        Assert.NotNull(audit);
        Assert.Equal(2, audit.TotalCount);
        Assert.Contains(audit.Items, x => x.Action == "ContentCreated" && x.TargetType == "Content" && x.ActorEmail != null);
        Assert.Contains(audit.Items, x => x.Action == "ContentStatusChanged");
    }

    [Fact]
    public async Task NotificationAcknowledgementsAreOwnedAndIdempotent()
    {
        var id = Login(UserRole.Student);
        var notification = new CampusUpdate.Domain.Notifications.UserNotification { UserId = id, Title = "Notice", Body = "Body" };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            db.UserNotifications.Add(notification);
            await db.SaveChangesAsync();
        }
        var path = $"/api/v1/notifications/{notification.Id}/ack";
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync(path, new { state = "delivered" })).StatusCode);
        DateTimeOffset? first;
        using (var scope = _factory.Services.CreateScope())
            first = (await scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().UserNotifications.FindAsync(notification.Id))!.DeliveredAt;
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync(path, new { state = "delivered" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync(path, new { state = "opened" })).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var saved = (await scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().UserNotifications.FindAsync(notification.Id))!;
            Assert.Equal(first, saved.DeliveredAt);
            Assert.NotNull(saved.OpenedAt);
            Assert.True(saved.IsRead);
        }
        Login(UserRole.Student);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync(path, new { state = "opened" })).StatusCode);
    }

    [Fact]
    public async Task CalendarCurrentHasDescriptiveEmptyState()
    {
        Login(UserRole.Student);
        var response = await _client.GetAsync("/api/v1/calendar/current");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No active academic calendar", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CalendarUploadCompressesImageAndMakesItCurrent()
    {
        Login(UserRole.SchoolAdmin);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 100, 100);
        var png = image.ToByteArray(ImageMagick.MagickFormat.Png);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_factory.SchoolId.ToString()), "institutionId");
        form.Add(new StringContent("Academic calendar"), "title");
        form.Add(new StringContent("2026/2027"), "academicSession");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "calendar.png");
        var response = await _client.PostAsync("/api/v1/calendar/upload", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var storage = (TestMediaStorage)scope.ServiceProvider.GetRequiredService<CampusUpdate.Infrastructure.Media.IMediaStorage>();
            var stored = Assert.Single(storage.Files);
            Assert.EndsWith(".webp", stored.Key);
            using var compressed = new ImageMagick.MagickImage(stored.Value);
            Assert.Equal(ImageMagick.MagickFormat.WebP, compressed.Format);
        }
        Login(UserRole.Student);
        var calendar = await _client.GetFromJsonAsync<AcademicCalendarResponse>("/api/v1/calendar/current");
        Assert.Equal("Academic calendar", calendar!.Title);
        Assert.Contains("signed=test", calendar.ImageUrl);
    }

    [Fact]
    public async Task StudentsCanDownloadVisibleMediaButCannotUpload()
    {
        Login(UserRole.Student);
        var attachment = new ContentAttachment { ContentItemId = _factory.PublishedId, FileName = "image.png", Url = "content/image.png", ContentType = "image/png", SizeBytes = 10 };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            db.ContentAttachments.Add(attachment);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/media/{attachment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync($"/api/v1/media/content/{_factory.PublishedId}", new MultipartFormDataContent())).StatusCode);
    }

    [Fact]
    public async Task NewsCategoryFilterReturnsOnlyMatchingCategory()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var item = await db.ContentItems.SingleAsync(c => c.Id == _factory.PublishedId);
            item.Category = "Academics";
            await db.SaveChangesAsync();
        }
        Login(UserRole.Student);
        var feed = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed?category=Academics", JsonOptions);
        var itemResponse = Assert.Single(feed!);
        Assert.Equal(_factory.PublishedId, itemResponse.Id);
        Assert.Equal("Academics", itemResponse.Category);
        Assert.Empty((await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed?category=Sports", JsonOptions))!);
    }

    private Guid Login(UserRole role)
    {
        var (id, token) = _factory.AddUser(role);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return id;
    }

    private static AcademicSettingsRequest Settings(Guid schoolId) => new(schoolId, null, null, null, null, true, true, true, true, true);
    private static CreateContentRequest Content(Guid schoolId) => new("News", "Body", null, ContentType.News, UrgencyLevel.Normal,
        SourceType.Official, "School", null, null, null, null, null, null, [new AudienceRequest(schoolId, null, null, null, null)]);

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
