namespace CampusUpdate.Infrastructure.Media;

public sealed class B2Options
{
    public const string SectionName = "B2";
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string ApplicationKey { get; set; } = "";
}
