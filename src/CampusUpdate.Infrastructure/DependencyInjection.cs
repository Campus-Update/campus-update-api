using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Amazon.S3;
using CampusUpdate.Infrastructure.Media;

namespace CampusUpdate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        services.AddDbContext<CampusUpdateDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddOptions<B2Options>().Bind(configuration.GetSection(B2Options.SectionName));
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var b2 = sp.GetRequiredService<IOptions<B2Options>>().Value;
            if (string.IsNullOrWhiteSpace(b2.Endpoint) || string.IsNullOrWhiteSpace(b2.BucketName) ||
                string.IsNullOrWhiteSpace(b2.KeyId) || string.IsNullOrWhiteSpace(b2.ApplicationKey))
                throw new InvalidOperationException("B2 storage configuration is required.");
            return new AmazonS3Client(b2.KeyId, b2.ApplicationKey, new AmazonS3Config
            {
                ServiceURL = b2.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = b2.Region
            });
        });
        services.AddScoped<IMediaStorage, B2MediaStorage>();

        return services;
    }
}
