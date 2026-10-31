using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Media;
using ImageMagick;

namespace CampusUpdate.Api.Media;

internal static class ProfileImageStorage
{
    internal const long MaxImageBytes = 5 * 1024 * 1024;
    internal const long MaxRequestBytes = MaxImageBytes + 64 * 1024;
    private const int MaxImagePixels = 40_000_000;
    private const int MaxDimension = 512;

    internal static async Task<(string? Key, string? Error)> StoreAsync(
        IFormFile file,
        AppUser user,
        IMediaStorage storage,
        CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxImageBytes)
            return (null, "Profile images must be between 1 byte and 5 MB.");

        var suppliedType = file.ContentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (suppliedType is not ("image/jpeg" or "image/png" or "image/webp"))
            return (null, "Upload a JPEG, PNG, or WebP image.");

        try
        {
            await using var input = file.OpenReadStream();
            var info = new MagickImageInfo(input);
            var expectedFormat = suppliedType switch
            {
                "image/jpeg" => MagickFormat.Jpeg,
                "image/png" => MagickFormat.Png,
                _ => MagickFormat.WebP
            };
            if (info.Format != expectedFormat || (long)info.Width * info.Height > MaxImagePixels)
                return (null, "The image content is invalid or exceeds 40 megapixels.");

            input.Position = 0;
            using var image = new MagickImage(input);
            image.AutoOrient();
            image.Resize(new MagickGeometry(MaxDimension, MaxDimension));
            image.Quality = 85;
            image.Strip();

            await using var output = new MemoryStream();
            await image.WriteAsync(output, MagickFormat.WebP, cancellationToken);
            output.Position = 0;

            var key = $"profile-images/{user.InstitutionId:N}/{user.Id:N}/{Guid.NewGuid():N}.webp";
            await storage.UploadAsync(output, key, "image/webp", output.Length, cancellationToken);
            return (key, null);
        }
        catch (MagickException)
        {
            return (null, "The uploaded file is not a valid JPEG, PNG, or WebP image.");
        }
    }
}
