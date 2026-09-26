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
