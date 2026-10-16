using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using OpenAgent.Router.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public sealed class RegistrationEndpointHandlerTests
{
    // The writer is never reached on the rejection paths below, so no Redis is needed.
    private static readonly EngineRegistryWriter Writer = new(null!, TimeProvider.System);

    [Theory]
    [InlineData(null, "s3cret")]
    [InlineData("s3cret", null)]
    [InlineData("s3cret", "wrong")]
    public async Task Put_MissingOrWrongKey_Returns401(string? configured, string? presented)
    {
        IResult result = await RegistrationEndpointHandler.PutAsync(
            "engine-1", ValidBody(), Context(presented), Config(configured), Writer);

        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task Delete_WrongKey_Returns401()
    {
        IResult result = await RegistrationEndpointHandler.DeleteAsync(
            "engine-1", Context("wrong"), Config("s3cret"), Writer);

        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task Put_WithoutRedis_Returns503()
    {
        IResult result = await RegistrationEndpointHandler.PutAsync(
            "engine-1", ValidBody(), Context("s3cret"), Config("s3cret"), writer: null);

        Assert.Equal(503, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("https://engine:5208")]
    [InlineData("http://engine:5208/prefix")]
    [InlineData("not a url")]
    public async Task Put_UnsupportedBaseUrl_Returns400(string baseUrl)
    {
        RegistrationRequest body = ValidBody();
        body.BaseUrl = baseUrl;

        IResult result = await RegistrationEndpointHandler.PutAsync(
            "engine-1", body, Context("s3cret"), Config("s3cret"), Writer);

        Assert.IsType<BadRequest<string>>(result);
    }

    [Fact]
    public async Task Put_InvalidInstanceId_Returns400()
    {
        IResult result = await RegistrationEndpointHandler.PutAsync(
            "a:b", ValidBody(), Context("s3cret"), Config("s3cret"), Writer);

        Assert.IsType<BadRequest<string>>(result);
    }

    private static RegistrationRequest ValidBody() => new() { BaseUrl = "http://engine:5208" };

    private static IConfiguration Config(string? secret) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RegistrationEndpointHandler.SecretConfigKey] = secret
            })
            .Build();

    private static DefaultHttpContext Context(string? key)
    {
        DefaultHttpContext context = new();
        if (key != null)
        {
            context.Request.Headers[RegistrationRequest.KeyHeader] = key;
        }

        return context;
    }
}
