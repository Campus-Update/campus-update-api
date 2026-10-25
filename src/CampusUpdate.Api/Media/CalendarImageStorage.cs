using CampusUpdate.Infrastructure.Media;
using Microsoft.AspNetCore.Http;

namespace CampusUpdate.Api.Media;

internal static class CalendarImageStorage
{
    internal static async Task<(string? Key, string? Error)> StoreAsync(
        IFormFile file,
        Guid institutionId,
        IMediaStorage storage,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length is <= 0 or > 5 * 1024 * 1024)
            return (null, "Calendar images must be between 1 byte and 5 MB.");

        await using var input = file.OpenReadStream();
        var header = new byte[12];
        var headerLength = await input.ReadAsync(header, cancellationToken);
        var isPng = headerLength >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var isJpeg = headerLength >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255;
        var isWebp = headerLength == 12 && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP";
        if (!isPng && !isJpeg && !isWebp)
            return (null, "The uploaded file is not a PNG, JPEG or WebP image.");

        try
        {
            input.Position = 0;
            var info = new ImageMagick.MagickImageInfo(input);
            if (info.Format is not (ImageMagick.MagickFormat.Png or ImageMagick.MagickFormat.Jpeg or ImageMagick.MagickFormat.WebP) ||
                (long)info.Width * info.Height > 40_000_000)
                return (null, "Use a PNG, JPEG or WebP image up to 40 megapixels.");

            input.Position = 0;
            using var image = new ImageMagick.MagickImage(input);
            await using var output = new MemoryStream();
            image.Quality = 85;
            image.Strip();
            await image.WriteAsync(output, ImageMagick.MagickFormat.WebP, cancellationToken);
            output.Position = 0;
            var key = $"calendar/{institutionId:N}/{Guid.NewGuid():N}.webp";
            await storage.UploadAsync(output, key, "image/webp", output.Length, cancellationToken);
            return (key, null);
        }
        catch (ImageMagick.MagickException)
        {
            return (null, "The uploaded file is not a valid PNG, JPEG or WebP image.");
        }
    }
}
