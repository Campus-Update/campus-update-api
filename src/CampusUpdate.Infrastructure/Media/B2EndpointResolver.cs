using System.Text.RegularExpressions;

namespace CampusUpdate.Infrastructure.Media;

public static class B2EndpointResolver
{
    public static string Resolve(string? configuredEndpoint, string region)
    {
        if (Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpoint) &&
            (endpoint.Scheme is "https" or "http") && !string.IsNullOrWhiteSpace(endpoint.Host))
            return endpoint.ToString();

        // Backblaze's S3 endpoint is deterministic from the bucket region.
        if (Regex.IsMatch(region, "^[a-z]{2}-[a-z]+-[0-9]{3}$", RegexOptions.IgnoreCase))
            return $"https://s3.{region}.backblazeb2.com";

        throw new InvalidOperationException("B2 endpoint must be an absolute HTTP(S) URL, and B2 region must be a valid Backblaze region.");
    }
}
