using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

public sealed class FileAssetOptionsValidatorTests
{
    private readonly FileAssetOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Passes()
    {
        ValidateOptionsResult result = _validator.Validate(null, new FileAssetOptions { Enabled = true });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_NegativeInlineImageMaxLongEdge_Fails()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileAssetOptions { Enabled = true, InlineImageMaxLongEdge = -1 });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("InlineImageMaxLongEdge", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_InlineImageQualityOutOfRange_Fails(int quality)
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileAssetOptions { Enabled = true, InlineImageQuality = quality });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("InlineImageQuality", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroLongEdgeDisablesCompression_Passes()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileAssetOptions { Enabled = true, InlineImageMaxLongEdge = 0 });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Validate_NonNegativeInlineImageHistoryTurns_Passes(int turns)
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileAssetOptions { Enabled = true, InlineImageHistoryTurns = turns });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_NegativeInlineImageHistoryTurns_Fails()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new FileAssetOptions { Enabled = true, InlineImageHistoryTurns = -1 });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("InlineImageHistoryTurns", StringComparison.Ordinal));
    }
}
