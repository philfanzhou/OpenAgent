using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

public class FileShareOptionsValidatorTests
{
    private readonly FileShareOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Passes()
    {
        ValidateOptionsResult result = _validator.Validate(null, new FileShareOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MaxLifetimeBeyond365Days_Fails()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileShareOptions { MaxLifetimeSeconds = FileShareOptions.MaxLifetimeLimitSeconds + 1 });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure => failure.Contains("365 days", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MaxLifetimeExactly365Days_Passes()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileShareOptions { MaxLifetimeSeconds = FileShareOptions.MaxLifetimeLimitSeconds });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveLifetimes_Fail(int lifetimeSeconds)
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileShareOptions { TemporaryLifetimeSeconds = lifetimeSeconds });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Validate_PublicBaseUrlWithUnsupportedScheme_Fails()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileShareOptions { PublicBaseUrl = "ftp://engine.example.com" });

        Assert.False(result.Succeeded);
    }
}
