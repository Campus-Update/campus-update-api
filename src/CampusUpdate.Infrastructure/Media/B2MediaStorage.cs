using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Options;

namespace CampusUpdate.Infrastructure.Media;

public sealed class B2MediaStorage(IAmazonS3 client, IOptions<B2Options> options) : IMediaStorage
{
    public async Task<StoredMedia> UploadAsync(Stream content, string objectKey, string contentType, long sizeBytes, CancellationToken cancellationToken)
    {
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Value.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false
        }, cancellationToken);
        return new StoredMedia(objectKey, contentType, sizeBytes);
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.Value.BucketName,
            Key = objectKey,
            Expires = DateTime.UtcNow.Add(lifetime),
            Verb = HttpVerb.GET
        };
        return Task.FromResult(client.GetPreSignedURL(request));
    }
}
