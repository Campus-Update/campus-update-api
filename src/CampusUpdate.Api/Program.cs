using System.Text;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json.Serialization;
using CampusUpdate.Api.Authentication;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure;
using CampusUpdate.Infrastructure.Persistence;
using CampusUpdate.Api.Seeding;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using CampusUpdate.Api.Payments;
using CampusUpdate.Api.Auth;
using CampusUpdate.Api.Notifications;

var builder = WebApplication.CreateBuilder(args);
ImageMagick.ResourceLimits.Memory = 256UL * 1024 * 1024;
ImageMagick.ResourceLimits.Disk = 512UL * 1024 * 1024;
ImageMagick.ResourceLimits.Width = 20000;
ImageMagick.ResourceLimits.Height = 20000;
ImageMagick.ResourceLimits.ListLength = 16;
ImageMagick.ResourceLimits.Thread = 2;

// Container platforms supply the port their ingress forwards traffic to.
if (builder.Configuration["PORT"] is { Length: > 0 } port)
{
    if (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
        throw new InvalidOperationException("PORT must be a valid TCP port.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{portNumber}");
}

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddInfrastructure(builder.Configuration);
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.AddOptions<JwtOptions>()
    .Bind(jwtSection)
    .Validate(x => Encoding.UTF8.GetByteCount(x.Key) >= 32, "JWT key must contain at least 32 bytes.")
    .Validate(x => !string.IsNullOrWhiteSpace(x.Issuer) && !string.IsNullOrWhiteSpace(x.Audience), "JWT issuer and audience are required.")
    .Validate(x => x.AccessTokenMinutes > 0 && x.RefreshTokenDays > 0, "Token lifetimes must be positive.")
    .ValidateOnStart();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
                {
                    context.Fail("Invalid user.");
                    return;
                }
                var db = context.HttpContext.RequestServices.GetRequiredService<CampusUpdateDbContext>();
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id,
                    context.HttpContext.RequestAborted);
                if (user is null || !user.IsActive ||
                    context.Principal?.FindFirstValue(ClaimTypes.Role) != user.Role.ToString())
                    context.Fail("This session is no longer valid.");
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddOptions<GoogleOptions>().Bind(builder.Configuration.GetSection(GoogleOptions.SectionName));
builder.Services.AddOptions<EmailVerificationOptions>().Bind(builder.Configuration.GetSection(EmailVerificationOptions.SectionName));
builder.Services.AddOptions<EmailOptions>().Bind(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddOptions<FcmOptions>().Bind(builder.Configuration.GetSection(FcmOptions.SectionName));
builder.Services.AddSingleton<FirebasePushSender>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddScoped<UsageAggregator>();
builder.Services.AddHttpClient<ResendEmailSender>(client =>
{
    client.BaseAddress = new Uri("https://api.resend.com");
    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + builder.Configuration["Email:ApiKey"]);
});
builder.Services.AddOptions<PaystackOptions>().Bind(builder.Configuration.GetSection(PaystackOptions.SectionName));
builder.Services.AddHttpClient<PaystackClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PaystackOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
});

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/v1/mock/calendar.png", () =>
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 800, 500);
        new ImageMagick.Drawing.Drawables().FillColor(ImageMagick.MagickColors.Navy).Rectangle(30, 30, 770, 100)
            .FillColor(ImageMagick.MagickColors.LightBlue).Rectangle(30, 130, 770, 210)
            .Rectangle(30, 240, 770, 320).Rectangle(30, 350, 770, 430).Draw(image);
        return Results.File(image.ToByteArray(ImageMagick.MagickFormat.Png), "image/png");
    });
}

if (args.Contains("--seed-development"))
{
    if (!app.Environment.IsDevelopment())
        throw new InvalidOperationException("Demo seeding is restricted to Development.");
    await DevelopmentSeeder.Run(app.Services, app.Configuration);
    return;
}
if (args.Contains("--bootstrap-admin"))
{
    await AdminBootstrap.Run(app.Services, app.Configuration);
    return;
}

app.UseExceptionHandler();
var swaggerEnabled = app.Environment.IsDevelopment() ||
                     builder.Configuration.GetValue<bool>("Swagger:Enabled");
if (swaggerEnabled)
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Campus Update API v1");
        options.RoutePrefix = "swagger";
    });
}

// Vercel terminates TLS and enforces HTTPS at its public ingress.
if (builder.Configuration["VERCEL"] != "1")
    app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
