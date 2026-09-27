namespace CampusUpdate.Api.Contracts;

public sealed record MediaUploadResponse(Guid AttachmentId, string FileName, string ContentType, long SizeBytes, string DownloadUrl);
