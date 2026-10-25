using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using CampusUpdate.Api.Authentication;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Identity;

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
    public async Task PasswordAndSessionRoutesUseTheCurrentSingleRefreshSession()
    {
        var (userId, token) = _factory.AddUser(UserRole.Student);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>()
                .HashPassword(user, "OldPassword1!");
            user.RefreshTokenHash = scope.ServiceProvider.GetRequiredService<ITokenService>().HashRefreshToken("refresh-token");
            user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
            db.UserActivities.Add(new CampusUpdate.Domain.Notifications.UserActivity
            {
                UserId = userId,
                InstitutionId = _factory.SchoolId,
                ActivityType = "login",
                OccurredAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sessions = await _client.GetFromJsonAsync<AuthSessionResponse[]>("/api/v1/auth/sessions", JsonOptions);
        var session = Assert.Single(sessions!);
        Assert.True(session.IsCurrent);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest("wrong", "NewPassword1!"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest("OldPassword1!", "NewPassword1!"))).StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var updatedUser = await verifyScope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().Users
            .SingleAsync(x => x.Id == userId);
        Assert.Null(updatedUser.RefreshTokenHash);
        Assert.Equal(PasswordVerificationResult.Success,
            verifyScope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>()
                .VerifyHashedPassword(updatedUser, updatedUser.PasswordHash, "NewPassword1!"));
    }

    [Fact]
    public async Task PasswordResetUsesGenericResponseAndConsumesCodeOnce()
    {
        var (userId, _) = _factory.AddUser(UserRole.Student);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>()
                .HashPassword(user, "OldPassword1!");
            user.RefreshTokenHash = scope.ServiceProvider.GetRequiredService<ITokenService>().HashRefreshToken("old-refresh-token");
            user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
            await db.SaveChangesAsync();
        }

        var userEmail = await GetEmail(userId);
        var knownAccount = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(userEmail));
        var unknownAccount = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("unknown@example.test"));
        Assert.Equal(HttpStatusCode.OK, knownAccount.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknownAccount.StatusCode);
        Assert.Equal(await knownAccount.Content.ReadAsStringAsync(), await unknownAccount.Content.ReadAsStringAsync());
        var code = Assert.IsType<string>(_factory.EmailSender.LastPasswordResetCode);
        Assert.Matches("^[0-9]{8}$", code);
        Assert.Equal(1, _factory.EmailSender.PasswordResetSendCount);

        var wrongCode = code == "00000000" ? "11111111" : "00000000";
        var invalid = await _client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(userEmail, wrongCode, "NewPassword2!"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var reset = await _client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(userEmail, code, "NewPassword2!"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var reusedCode = await _client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(userEmail, code, "AnotherPassword3!"));
        Assert.Equal(HttpStatusCode.BadRequest, reusedCode.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var resetUser = await verifyScope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().Users
            .SingleAsync(x => x.Id == userId);
        Assert.Null(resetUser.RefreshTokenHash);
        Assert.Null(resetUser.PasswordResetCodeHash);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(userEmail, "OldPassword1!"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(userEmail, "NewPassword2!"))).StatusCode);

        async Task<string> GetEmail(Guid id)
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().Users
                .Where(x => x.Id == id).Select(x => x.Email).SingleAsync();
        }
    }

    [Fact]
    public async Task PasswordResetCodeExpiresAndStopsAfterFiveWrongAttempts()
    {
        var (userId, _) = _factory.AddUser(UserRole.Student);
        var email = await GetEmail(userId);
        await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email));
        var code = _factory.EmailSender.LastPasswordResetCode!;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.PasswordResetCodeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
            user.PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(email, code, "NewPassword2!"))).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
            user.PasswordResetAttempts = 0;
            await db.SaveChangesAsync();
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrongCode = (attempt + 1).ToString("D8");
            if (wrongCode == code)
                wrongCode = ((int.Parse(code) + 1) % 100_000_000).ToString("D8");
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/reset-password",
                new ResetPasswordRequest(email, wrongCode, "NewPassword2!"))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(email, code, "NewPassword2!"))).StatusCode);

        async Task<string> GetEmail(Guid id)
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>().Users
                .Where(x => x.Id == id).Select(x => x.Email).SingleAsync();
        }
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
        var (viewerId, _) = _factory.AddUser(UserRole.Student);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            db.ContentViews.Add(new CampusUpdate.Domain.Notifications.ContentView
            {
                UserId = viewerId,
                ContentItemId = _factory.PendingEventId,
                InstitutionId = _factory.SchoolId
            });
            await db.SaveChangesAsync();
        }

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
        Assert.Equal(1, eventItem.UniqueReachCount);
        Assert.Equal(1, eventItem.Views);
        Assert.Equal(1, eventItem.ReachCount);

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
    public async Task SchoolAdminContentListIncludesOwnUnpublishedItemsAndResponseDetails()
    {
        Login(UserRole.SchoolAdmin);
        var startsAt = DateTimeOffset.UtcNow.AddDays(3);
        var endsAt = startsAt.AddHours(2);
        var eventRequest = new CreateContentRequest("Campus event", "Event details", "Event summary", ContentType.Event,
            UrgencyLevel.Normal, SourceType.Official, "Osun State University", startsAt, endsAt, "Main hall",
            "https://example.test/register", null, null,
            [new AudienceRequest(_factory.SchoolId, null, null, null, null)], "Events");
        var createdResponse = await _client.PostAsJsonAsync("/api/v1/content", eventRequest);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<ContentResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.NotNull(created.CreatedAt);
        Assert.NotNull(created.UpdatedAt);
        Assert.Equal(startsAt, created.EventStartsAt);
        Assert.Equal(endsAt, created.EventEndsAt);
        Assert.Equal("Main hall", created.EventLocation);
        Assert.Equal("https://example.test/register", created.RegistrationUrl);

        var allItems = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/content", JsonOptions);
        Assert.Contains(allItems!, x => x.Id == created.Id && x.Status == ContentStatus.Draft);
        Assert.Contains(allItems!, x => x.Id == _factory.PendingApprovalId);
        Assert.DoesNotContain(allItems!, x => x.Id == _factory.ForeignPendingApprovalId);

        var drafts = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/content?status=Draft", JsonOptions);
        Assert.Contains(drafts!, x => x.Id == created.Id);
        Assert.DoesNotContain(drafts!, x => x.Status != ContentStatus.Draft);
    }

    [Fact]
    public async Task SchoolAdminCanUpdateDraftWithoutReplacingItsExistingAudienceRow()
    {
        Login(UserRole.SchoolAdmin);
        var create = await _client.PostAsJsonAsync("/api/v1/content", Content(_factory.SchoolId));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ContentResponse>(JsonOptions);
        Assert.NotNull(created);

        Guid originalAudienceId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            originalAudienceId = await db.ContentAudiences.Where(x => x.ContentItemId == created.Id)
                .Select(x => x.Id).SingleAsync();
        }

        var update = new UpdateContentRequest(
            "Updated news", "Updated body", "Updated summary", UrgencyLevel.Important, "School",
            null, null, null, null, null, null,
            [new AudienceRequest(_factory.SchoolId, null, null, null, null, TargetAudience.Students)], "Academics");
        var response = await _client.PutAsJsonAsync($"/api/v1/content/{created.Id}", update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        var audience = await verifyDb.ContentAudiences.SingleAsync(x => x.ContentItemId == created.Id);
        Assert.Equal(originalAudienceId, audience.Id);
        Assert.Equal(TargetAudience.Students, audience.TargetAudience);
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
    public async Task SuperAdminCanReadAndUpdatePlatformSettings()
    {
        Login(UserRole.SuperAdmin);
        var defaults = await _client.GetFromJsonAsync<AdminPlatformSettingsResponse>("/api/v1/admin/platform-settings", JsonOptions);
        Assert.NotNull(defaults);
        Assert.Equal("Campus Update", defaults.PlatformName);

        var request = new AdminPlatformSettingsRequest("Campus Update", "support@campus.test", "+2348000000000",
            true, 2500m, 30, true);
        var update = await _client.PutAsJsonAsync("/api/v1/admin/platform-settings", request);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var saved = await update.Content.ReadFromJsonAsync<AdminPlatformSettingsResponse>(JsonOptions);
        Assert.NotNull(saved);
        Assert.Equal("support@campus.test", saved.SupportEmail);
        Assert.True(saved.MaintenanceMode);
        Assert.Equal(2500m, saved.ListingFee);
        Assert.Equal(30, saved.InvoiceDays);
        Assert.True(saved.HoldEventsUntilPayment);

        var persisted = await _client.GetFromJsonAsync<AdminPlatformSettingsResponse>("/api/v1/admin/platform-settings", JsonOptions);
        Assert.Equal(saved, persisted);
        Login(UserRole.Student);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/admin/platform-settings")).StatusCode);
    }

    [Fact]
    public async Task SchoolAndSuperAdminsCanManageTheirOwnNotificationPreferences()
    {
        Login(UserRole.SuperAdmin);
        var initial = await _client.GetFromJsonAsync<NotificationPreferencesResponse>("/api/v1/auth/notification-preferences", JsonOptions);
        Assert.NotNull(initial);
        Assert.Equal(NotificationSummaryCadence.Immediate, initial.SummaryCadence);

        var request = new NotificationPreferencesRequest(false, true, NotificationSummaryCadence.Weekly);
        var response = await _client.PutAsJsonAsync("/api/v1/auth/notification-preferences", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<NotificationPreferencesResponse>(JsonOptions);
        Assert.Equal(request.EmailNotificationsEnabled, saved!.EmailNotificationsEnabled);
        Assert.Equal(request.SummaryCadence, saved.SummaryCadence);

        Login(UserRole.SchoolAdmin);
        var schoolAdminInitial = await _client.GetFromJsonAsync<NotificationPreferencesResponse>(
            "/api/v1/auth/notification-preferences", JsonOptions);
        Assert.NotNull(schoolAdminInitial);
        var schoolAdminRequest = new NotificationPreferencesRequest(true, false, NotificationSummaryCadence.Daily);
        var schoolAdminUpdate = await _client.PutAsJsonAsync(
            "/api/v1/auth/notification-preferences", schoolAdminRequest);
        Assert.Equal(HttpStatusCode.OK, schoolAdminUpdate.StatusCode);
        var schoolAdminSaved = await schoolAdminUpdate.Content.ReadFromJsonAsync<NotificationPreferencesResponse>(JsonOptions);
        Assert.NotNull(schoolAdminSaved);
        Assert.Equal(schoolAdminRequest.EmailNotificationsEnabled, schoolAdminSaved.EmailNotificationsEnabled);
        Assert.Equal(schoolAdminRequest.PushNotificationsEnabled, schoolAdminSaved.PushNotificationsEnabled);
        Assert.Equal(schoolAdminRequest.SummaryCadence, schoolAdminSaved.SummaryCadence);

        Login(UserRole.Student);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.GetAsync("/api/v1/auth/notification-preferences")).StatusCode);
    }

    [Fact]
    public async Task InstitutionStatsAndDepartmentRoutesReturnScopedData()
    {
        var (studentId, _) = _factory.AddUser(UserRole.Student);
        Guid departmentId;
        Guid foreignDepartmentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var otherInstitution = await db.Institutions.SingleAsync(x => x.Id == _factory.OtherSchoolId);
            otherInstitution.IsActive = false;
            var faculty = await db.Faculties.FirstAsync(x => x.InstitutionId == _factory.SchoolId);
            var seededDepartment = new CampusUpdate.Domain.Schools.Department
            {
                Name = "Computer Science",
                Code = "CSC",
                FacultyId = faculty.Id,
                Programmes = [new CampusUpdate.Domain.Schools.Programme
                {
                    Name = "BSc Computer Science",
                    Code = "BSC-CS",
                    Levels = [new CampusUpdate.Domain.Schools.AcademicLevel { Name = "100L", SortOrder = 100 }]
                }]
            };
            db.Departments.Add(seededDepartment);
            var foreignFaculty = await db.Faculties.SingleAsync(x => x.Id == _factory.ForeignFacultyId);
            var foreignDepartment = new CampusUpdate.Domain.Schools.Department
            {
                Name = "Foreign Department",
                Code = "FD",
                FacultyId = foreignFaculty.Id
            };
            db.Departments.Add(foreignDepartment);
            db.ContentViews.Add(new CampusUpdate.Domain.Notifications.ContentView
            {
                UserId = studentId,
                ContentItemId = _factory.PublishedId,
                InstitutionId = _factory.SchoolId
            });
            await db.SaveChangesAsync();
            departmentId = seededDepartment.Id;
            foreignDepartmentId = foreignDepartment.Id;
        }

        Login(UserRole.SuperAdmin);
        var stats = await _client.GetFromJsonAsync<InstitutionStatsResponse>("/api/v1/institution/stats", JsonOptions);
        Assert.NotNull(stats);
        Assert.Equal(2, stats.TotalInstitutions);
        Assert.Equal(1, stats.ActiveInstitutions);
        Assert.Equal(1, stats.PendingVerificationInstitutions);
        Assert.Equal(1, stats.StudentsReached);

        var departments = await _client.GetFromJsonAsync<DepartmentAdminResponse[]>(
            $"/api/v1/departments?institutionId={_factory.SchoolId}", JsonOptions);
        var department = Assert.Single(departments!);
        Assert.Equal(departmentId, department.Id);
        Assert.Equal("Computer Science", department.Name);
        Assert.Equal("School A", department.InstitutionName);
        Assert.Equal("BSc Computer Science", Assert.Single(department.Programmes).Name);
        var detail = await _client.GetFromJsonAsync<DepartmentAdminResponse>($"/api/v1/departments/{departmentId}", JsonOptions);
        Assert.Equal(departmentId, detail!.Id);

        Login(UserRole.SchoolAdmin);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/departments?institutionId={_factory.SchoolId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.GetAsync($"/api/v1/departments?institutionId={_factory.OtherSchoolId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/v1/departments/{foreignDepartmentId}")).StatusCode);

        var schoolAdminStats = await _client.GetFromJsonAsync<InstitutionStatsResponse>(
            "/api/v1/institution/stats", JsonOptions);
        Assert.NotNull(schoolAdminStats);
        Assert.Equal(1, schoolAdminStats.TotalInstitutions);
        Assert.Equal(1, schoolAdminStats.ActiveInstitutions);
        Assert.Equal(0, schoolAdminStats.PendingVerificationInstitutions);
        Assert.Equal(1, schoolAdminStats.StudentsReached);

        Login(UserRole.Student);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/v1/institution/stats")).StatusCode);
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
        Assert.Equal(1, school.WeeklyActiveCount);
        Assert.Equal(0, school.ViewCount);
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
        Assert.Equal(1, response.Views);
        var feed = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed", JsonOptions);
        var feedItem = Assert.Single(feed!, x => x.Id == _factory.PublishedId);
        Assert.Equal(1, feedItem.UniqueReachCount);
        Assert.Equal(1, feedItem.NotificationOpenCount);
        Assert.Equal(1, feedItem.Views);
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
        Assert.Contains(audit.Items, x => x.Action == "ContentCreated" && x.TargetType == "Content" &&
            x.ActorEmail != null && x.ActorRole == UserRole.SchoolAdmin && x.InstitutionName == "School A" &&
            x.ActionTag == "Access");
        Assert.Contains(audit.Items, x => x.Action == "ContentStatusChanged");
        var searchedAudit = await _client.GetFromJsonAsync<AdminAuditLogPageResponse>(
            $"/api/v1/admin/audit?search=School%20A&administratorId={audit.Items.First().ActorId}&actionTag=Access", JsonOptions);
        Assert.NotNull(searchedAudit);
        Assert.Equal(2, searchedAudit.TotalCount);
    }

    [Fact]
    public async Task SchoolAdminCanCreateEventSubmitForApprovalAndPublishToStudentFeed()
    {
        Login(UserRole.SchoolAdmin);
        var startsAt = DateTimeOffset.UtcNow.AddDays(3);
        var request = new CreateContentRequest(
            "Computing Faculty Seminar", "Join the faculty seminar.", "A seminar for students.",
            ContentType.Event, UrgencyLevel.Normal, SourceType.Official, "School A",
            startsAt, startsAt.AddHours(2), "Main auditorium", "https://example.test/register",
            null, null, [new AudienceRequest(_factory.SchoolId, null, null, null, null, TargetAudience.Students)], "Academic");

        var invalidEvent = await _client.PostAsJsonAsync("/api/v1/content", request with { EventStartsAt = null });
        Assert.Equal(HttpStatusCode.BadRequest, invalidEvent.StatusCode);

        var create = await _client.PostAsJsonAsync("/api/v1/content", request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ContentResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(ContentType.Event, created.Type);
        Assert.Equal(ContentStatus.Draft, created.Status);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var saved = await db.ContentItems.Include(x => x.Audiences).SingleAsync(x => x.Id == created.Id);
            Assert.Equal(startsAt, saved.EventStartsAt);
            Assert.Equal(startsAt.AddHours(2), saved.EventEndsAt);
            Assert.Equal("Main auditorium", saved.EventLocation);
            Assert.Equal("https://example.test/register", saved.RegistrationUrl);
            Assert.Equal(TargetAudience.Students, Assert.Single(saved.Audiences).TargetAudience);
        }

        Assert.Equal(HttpStatusCode.OK, (await _client.PatchAsJsonAsync($"/api/v1/content/{created.Id}/status",
            new ChangeContentStatusRequest(ContentStatus.PendingApproval))).StatusCode);
        Login(UserRole.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await _client.PatchAsJsonAsync($"/api/v1/content/{created.Id}/status",
            new ChangeContentStatusRequest(ContentStatus.Published))).StatusCode);

        var (_, studentToken) = _factory.AddUser(UserRole.Student, _factory.SchoolId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", studentToken);
        var feed = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed?type=Event", JsonOptions);
        Assert.Contains(feed!, x => x.Id == created.Id && x.Status == ContentStatus.Published);
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
    public async Task SchoolAdminCanSaveListAndPublishCalendarDrafts()
    {
        Login(UserRole.SchoolAdmin);
        var draftRequest = new CreateAcademicCalendarRequest(_factory.SchoolId, "2026/2027 Draft", "2026/2027",
            "https://example.com/calendar.png", DateTimeOffset.UtcNow, false, "Draft");
        var create = await _client.PostAsJsonAsync("/api/v1/calendar", draftRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var draft = await create.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        Assert.NotNull(draft);
        Assert.Equal("Draft", draft.Status);
        Assert.False(draft.IsOfficial);

        var draftsResponse = await _client.GetAsync("/api/v1/calendar/manage?status=Draft");
        Assert.Equal(HttpStatusCode.OK, draftsResponse.StatusCode);
        var drafts = await draftsResponse.Content.ReadFromJsonAsync<AcademicCalendarResponse[]>(JsonOptions);
        Assert.Contains(drafts!, item => item.Id == draft.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/calendar/current")).StatusCode);

        var publishResponse = await _client.PostAsync($"/api/v1/calendar/{draft.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var published = await publishResponse.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        Assert.Equal("Published", published!.Status);
        Assert.True(published.IsOfficial);

        Login(UserRole.Student);
        var current = await _client.GetFromJsonAsync<AcademicCalendarResponse>("/api/v1/calendar/current", JsonOptions);
        Assert.Equal(draft.Id, current!.Id);
        Assert.Equal("Published", current.Status);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.GetAsync($"/api/v1/calendar/manage?institutionId={_factory.SchoolId}")).StatusCode);
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
    public async Task SchoolAdminCanUploadImageToOwnCalendarButNotAnotherInstitution()
    {
        Login(UserRole.SuperAdmin);
        var ownCalendarResponse = await _client.PostAsJsonAsync("/api/v1/calendar",
            new CreateAcademicCalendarRequest(_factory.SchoolId, "School A calendar", "2026/2027",
                "https://example.com/old.png", DateTimeOffset.UtcNow, false, "Draft"));
        var foreignCalendarResponse = await _client.PostAsJsonAsync("/api/v1/calendar",
            new CreateAcademicCalendarRequest(_factory.OtherSchoolId, "School B calendar", "2026/2027",
                "https://example.com/old.png", DateTimeOffset.UtcNow, false, "Draft"));
        Assert.Equal(HttpStatusCode.Created, ownCalendarResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, foreignCalendarResponse.StatusCode);
        var ownCalendar = await ownCalendarResponse.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        var foreignCalendar = await foreignCalendarResponse.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);

        Login(UserRole.SchoolAdmin);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue, 64, 64);
        var png = image.ToByteArray(ImageMagick.MagickFormat.Png);
        using var ownForm = new MultipartFormDataContent();
        var ownFile = new ByteArrayContent(png);
        ownFile.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        ownForm.Add(ownFile, "file", "calendar.png");
        var upload = await _client.PostAsync($"/api/v1/media/calendar/{ownCalendar!.Id}", ownForm);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var updated = await upload.Content.ReadFromJsonAsync<AcademicCalendarResponse>(JsonOptions);
        Assert.Contains("signed=test", updated!.ImageUrl);
        Assert.Equal("Draft", updated.Status);

        using var foreignForm = new MultipartFormDataContent();
        var foreignFile = new ByteArrayContent(png);
        foreignFile.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        foreignForm.Add(foreignFile, "file", "calendar.png");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsync($"/api/v1/media/calendar/{foreignCalendar!.Id}", foreignForm)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        var saved = await db.AcademicCalendars.SingleAsync(x => x.Id == ownCalendar.Id);
        Assert.StartsWith($"calendar/{_factory.SchoolId:N}/", saved.ImageUrl);
        var storage = (TestMediaStorage)scope.ServiceProvider.GetRequiredService<CampusUpdate.Infrastructure.Media.IMediaStorage>();
        var stored = Assert.Single(storage.Files);
        using var compressed = new ImageMagick.MagickImage(stored.Value);
        Assert.Equal(ImageMagick.MagickFormat.WebP, compressed.Format);
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
    public async Task ContentImageUploadSetsCoverAndFeedReturnsSignedImageUrl()
    {
        Login(UserRole.SuperAdmin);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue, 64, 64);
        var png = image.ToByteArray(ImageMagick.MagickFormat.Png);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");

        var upload = await _client.PostAsync($"/api/v1/media/content/{_factory.PublishedId}", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
            var content = await db.ContentItems.SingleAsync(x => x.Id == _factory.PublishedId);
            Assert.StartsWith("content/", content.CoverImageUrl);
        }

        Login(UserRole.Student);
        var feed = await _client.GetFromJsonAsync<ContentResponse[]>("/api/v1/feed", JsonOptions);
        var item = Assert.Single(feed!, x => x.Id == _factory.PublishedId);
        Assert.Contains("signed=test", item.ImageUrl);
    }

    [Fact]
    public async Task ContentCreateDoesNotSetImageFromRequest()
    {
        Login(UserRole.SuperAdmin);
        var request = Content(_factory.SchoolId);

        var response = await _client.PostAsJsonAsync("/api/v1/content", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<ContentResponse>(JsonOptions);

        Assert.Null(content!.ImageUrl);
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
