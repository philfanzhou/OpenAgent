using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using OpenAgent.Core.Capabilities.Code;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class RunnerClientTests
{
    [Theory]
    [InlineData(false, "/api/v1/execute")]
    [InlineData(true, "/api/v1/workspace/session/read")]
    public async Task Requests_UseSharedTransportAndBearer(bool workspace, string path)
    {
        using HttpClient http = new(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(new string('a', 32), request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(workspace ? "{}" : JsonSerializer.Serialize(new CodeExecutionResult()))
            });
        }));
        RunnerClient client = CreateClient(http);
        if (workspace)
        {
            await client.ReadAsync("session", "a.txt", 0, 10, CancellationToken.None);
        }
        else
        {
            await client.ExecuteAsync(new CodeExecutionRequest { Code = "print(1)" }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WorkspaceConflict_PreservesStatusAndCorrectionDetail()
    {
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"detail\":\"Old text matched twice; use replaceAll.\"}")
            })));
        WorkspaceOperationException error = await Assert.ThrowsAsync<WorkspaceOperationException>(
            () => CreateClient(http).EditAsync("session", "a.txt", "a", "b", false, CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
        Assert.Contains("replaceAll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceResponse_RejectsOversizedBodyBeforeDeserialization()
    {
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[ExecutionLimits.MaxWireBytes + 1])
            })));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(http).ReadBytesAsync("session", "a.txt", CancellationToken.None));
        Assert.Contains("wire limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requests_PropagateCallerCancellation(bool workspace)
    {
        using CancellationTokenSource cancellation = new();
        using HttpClient http = new(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        RunnerClient client = CreateClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (workspace)
            {
                await client.ReadAsync("session", "a.txt", 0, 10, cancellation.Token);
            }
            else
            {
                await client.ExecuteAsync(new CodeExecutionRequest { Code = "print(1)" }, cancellation.Token);
            }
        });
    }

    private static RunnerClient CreateClient(HttpClient http) => new(http, Options.Create(new CodeExecutionOptions
    {
        Enabled = true,
        Endpoint = "http://runner",
        ApiKey = new string('a', 32)
    }));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
