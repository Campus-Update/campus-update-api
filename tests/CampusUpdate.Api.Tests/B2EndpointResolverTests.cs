using CampusUpdate.Infrastructure.Media;

namespace CampusUpdate.Api.Tests;

public sealed class B2EndpointResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3.us-west-004.backblazeb2.com")]
    public void MissingOrHostOnlyEndpointIsDerivedFromRegion(string? configuredEndpoint)
    {
        Assert.Equal("https://s3.us-west-004.backblazeb2.com",
            B2EndpointResolver.Resolve(configuredEndpoint, "us-west-004"));
    }

    [Fact]
    public void ConfiguredAbsoluteEndpointIsPreserved()
    {
        Assert.Equal("https://s3.eu-central-003.backblazeb2.com/",
            B2EndpointResolver.Resolve("https://s3.eu-central-003.backblazeb2.com", "eu-central-003"));
    }

    [Fact]
    public void InvalidEndpointAndRegionFailWithActionableConfigurationError()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            B2EndpointResolver.Resolve("not-a-url", "unknown-region"));

        Assert.Contains("B2 endpoint", error.Message);
    }
}
