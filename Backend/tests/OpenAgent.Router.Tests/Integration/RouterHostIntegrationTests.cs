using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Xunit;

namespace OpenAgent.Router.Tests.Integration;

public sealed class RouterHostIntegrationTests : IClassFixture<RouterHostFixture>
{
    private readonly RouterHostFixture _fixture;

    public RouterHostIntegrationTests(RouterHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Chat_WithoutAuthentication_ReturnsUnauthorized()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/agent/chat",
            new { message = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CurrentUser_WithApiKey_ForwardsThroughRouter()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/agent/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "oa_test_router_key");

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Contains("integration:partner-a", body, StringComparison.Ordinal);
        Assert.Equal("Bearer oa_test_router_key", _fixture.PrimaryEngine.LastAuthorization);
    }

    [Fact]
    public async Task Chat_WithValidRequest_ForwardsIdentityAndRecordsSelection()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateChatRequest("ordinary chat");

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        string metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("primary-engine", body, StringComparison.Ordinal);
        Assert.Equal(
            "default",
            Assert.Single(response.Headers.GetValues("X-OpenAgent-Selected-Agent-Id")));
        Assert.Null(_fixture.PrimaryEngine.LastUserId);
        Assert.Null(_fixture.PrimaryEngine.LastTenantId);
        Assert.StartsWith("Basic ", _fixture.PrimaryEngine.LastAuthorization, StringComparison.Ordinal);
        Assert.Contains("openagent_router_provider_selections_total", metrics, StringComparison.Ordinal);
        Assert.Contains("source=\"explicit\"", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_DevelopmentTenantHeader_IsForwardedWithoutRouterInterpretation()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateChatRequest("development tenant");
        request.Headers.Add("X-Tenant-Id", "development-tenant");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("development-tenant", _fixture.PrimaryEngine.LastTenantId);
        Assert.Null(_fixture.PrimaryEngine.LastCatalogTenantId);
    }

    [Fact]
    public async Task Chat_RepeatedQuery_UsesCacheThroughHttpPipeline()
    {
        int requestCount = _fixture.PrimaryEngine.ChatRequestCount;
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage first = await client.SendAsync(CreateChatRequest("cacheable query"));
        string firstBody = await first.Content.ReadAsStringAsync();
        using HttpResponseMessage second = await client.SendAsync(CreateChatRequest("cacheable query"));
        string secondBody = await second.Content.ReadAsStringAsync();
        string metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.Equal(requestCount + 1, _fixture.PrimaryEngine.ChatRequestCount);
        Assert.Contains("cache=\"query\"", metrics, StringComparison.Ordinal);
        Assert.Contains("openagent_router_cache_hits_total", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_MultipartUploadAndBinaryDownload_PreservePayloads()
    {
        byte[] uploadBytes = [0x00, 0x10, 0x80, 0xff];
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage upload = new(HttpMethod.Post, "/api/v1/agent/files");
        AddAuthentication(upload);
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(uploadBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(file, "file", "payload.bin");
        upload.Content = multipart;

        using HttpResponseMessage uploadResponse = await client.SendAsync(upload);
        using HttpRequestMessage download = new(
            HttpMethod.Get,
            "/api/v1/agent/files/file-1/download");
        AddAuthentication(download);
        using HttpResponseMessage downloadResponse = await client.SendAsync(download);
        byte[] downloadBytes = await downloadResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        Assert.Equal("payload.bin", _fixture.PrimaryEngine.UploadedFileName);
        Assert.Equal("application/octet-stream", _fixture.PrimaryEngine.UploadedContentType);
        Assert.Equal(uploadBytes, _fixture.PrimaryEngine.UploadedBytes);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("application/octet-stream", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestEngineHost.ExpectedDownloadBytes.ToArray(), downloadBytes);
    }

    [Fact]
    public async Task ConversationCompaction_ForwardsPostToEngine()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/v1/agent/conversations/conversation-1/compact");
        AddAuthentication(request);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("conversation-1", _fixture.PrimaryEngine.LastCompactedConversationId);
        Assert.StartsWith("Basic ", _fixture.PrimaryEngine.LastAuthorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_SseClientCancellation_ReachesDownstream()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateChatRequest(
            "stream response",
            "/api/v1/agent/chat/sse");
        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCancellation.Token);
        await using Stream stream = await response.Content.ReadAsStreamAsync(
            requestCancellation.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal("event: token", await reader.ReadLineAsync(requestCancellation.Token));
        Assert.Equal("data: first", await reader.ReadLineAsync(requestCancellation.Token));

        requestCancellation.Cancel();
        response.Dispose();
        using var propagationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _fixture.PrimaryEngine.WaitForSseCancellationAsync(propagationTimeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task File_UnreachableEngine_MapsForwardingFailureMetric()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory("http://127.0.0.1:1");
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            "/api/v1/agent/files/file-1/download");
        AddAuthentication(request);

        using HttpResponseMessage response = await client.SendAsync(request);
        string metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("openagent_router_forwarding_failures_total", metrics, StringComparison.Ordinal);
    }

    private static HttpRequestMessage CreateChatRequest(
        string message,
        string path = "/api/v1/agent/chat")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { message })
        };
        AddAuthentication(request);
        request.Headers.Add("X-Agent-Id", "default");
        return request;
    }

    private static void AddAuthentication(HttpRequestMessage request)
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("admin:admin"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }
}
