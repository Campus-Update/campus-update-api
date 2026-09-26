namespace CampusUpdate.Infrastructure.Media;

public sealed record StoredMedia(string ObjectKey, string ContentType, long SizeBytes);

public interface IMediaStorage
{
    Task<StoredMedia> UploadAsync(Stream content, string objectKey, string contentType, long sizeBytes, CancellationToken cancellationToken);
    Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken);
}
