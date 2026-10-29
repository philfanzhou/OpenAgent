using System.Net;
using System.Text.Json;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Contracts.Requests;
using Xunit;
using static OpenAgent.Engine.Tests.Hosting.SseToolStreamingTests;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class AdmissionStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpSse_QueueFullOrTimeout_UsesCorrectTransportAndDoesNotPersistWaiter(bool allowQueue)
    {
        ScriptedRuntimeDriver driver = new([], holdUntilCancelled: true);
        Dictionary<string, string?> settings = new()
        {
            ["RuntimeAdmission:MaxConcurrentTurns"] = "1", ["RuntimeAdmission:MaxConcurrentPerTenant"] = "1",
            ["RuntimeAdmission:MaxQueuedTurns"] = allowQueue ? "1" : "0", ["RuntimeAdmission:MaxQueuedPerTenant"] = allowQueue ? "1" : "0",
            ["RuntimeAdmission:QueueTimeoutSeconds"] = "1"
        };
        await using StreamingHost host = await StreamingHost.StartAsync(driver, settings: settings);
        using HttpRequestMessage activeRequest = Request("active");
        using HttpResponseMessage active = await host.Client.SendAsync(activeRequest, HttpCompletionOption.ResponseHeadersRead);
        using HttpRequestMessage queuedRequest = Request("waiting");
        using HttpResponseMessage response = await host.Client.SendAsync(queuedRequest, HttpCompletionOption.ResponseHeadersRead);
        string body = await response.Content.ReadAsStringAsync();
        if (allowQueue)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("event: execution_status", body);
            Assert.Contains("\"status\":\"queued\"", body);
            Assert.Contains("event: error", body);
            Assert.Contains("RuntimeQueueTimeout", body);
            Assert.Contains("\"retryAfterSeconds\":1", body);
            Assert.Contains("event: done", body);
            Assert.DoesNotContain("event: content", body);
        }
        else
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            using JsonDocument problem = JsonDocument.Parse(body);
            Assert.Equal((int)AgentErrorCode.RuntimeQueueFull, problem.RootElement.GetProperty("errorCode").GetInt32());
            Assert.DoesNotContain("event:", body);
        }
        Assert.Null(await host.Store.GetRecordAsync("tenant-1", "waiting"));
    }

    private static HttpRequestMessage Request(string id)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/agent/chat/stream");
        request.Headers.Add("X-Conversation-Id", id);
        return request;
    }
}
