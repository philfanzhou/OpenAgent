using System.Net;
using System.Text.Json;
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
    public async Task ConversationCompaction_IsUnavailable()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/v1/agent/conversations/conversation-1/compact");
        AddAuthentication(request);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    public async Task Chat_SseIdleTimeoutReleasesQuietUpstreamAndAllowsHeartbeats()
    {
        await using TestEngineHost idleEngine = new("idle-engine");
        await using TestEngineHost heartbeatEngine = new("heartbeat-engine", sendHeartbeats: true);
        await Task.WhenAll(idleEngine.StartAsync(), heartbeatEngine.StartAsync());
        Dictionary<string, string?> settings = new() { ["RouterSettings:Routing:StreamingIdleTimeoutSeconds"] = "16" };
        using RouterApplicationFactory idleRouter = _fixture.CreateFactory(idleEngine.Endpoint, settings: settings);
        using RouterApplicationFactory heartbeatRouter = _fixture.CreateFactory(heartbeatEngine.Endpoint, settings: settings);
        using HttpClient idleClient = idleRouter.CreateClient();
        using HttpClient heartbeatClient = heartbeatRouter.CreateClient();
        using HttpRequestMessage idleRequest = CreateChatRequest("idle", "/api/v1/agent/chat/sse");
        using HttpRequestMessage heartbeatRequest = CreateChatRequest("heartbeats", "/api/v1/agent/chat/sse");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(36));
        Task<HttpResponseMessage> heartbeatResponse = heartbeatClient.SendAsync(heartbeatRequest, deadline.Token);
        using HttpResponseMessage idleResponse = await idleClient.SendAsync(idleRequest,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await using Stream idleStream = await idleResponse.Content.ReadAsStreamAsync(deadline.Token);
        using StreamReader idleReader = new(idleStream);
        Assert.Equal("event: token", await idleReader.ReadLineAsync(deadline.Token));
        await idleEngine.WaitForSseCancellationAsync(deadline.Token);

        using HttpResponseMessage completed = await heartbeatResponse;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Contains("event: done", await completed.Content.ReadAsStringAsync(deadline.Token));
    }

    [Fact]
    public async Task RateLimiting_ConversationPollingUploadAndAnonymousUseIndependentBudgets()
    {
        Dictionary<string, string?> settings = new()
        {
            ["RouterSettings:RateLimiting:RequestsPerSecond"] = "0.001",
            ["RouterSettings:RateLimiting:Conversations:BurstCapacity"] = "4",
            ["RouterSettings:RateLimiting:Uploads:RequestsPerSecond"] = "0.001",
            ["RouterSettings:RateLimiting:Uploads:BurstCapacity"] = "1",
            ["RouterSettings:RateLimiting:Anonymous:RequestsPerSecond"] = "0.001",
            ["RouterSettings:RateLimiting:Anonymous:BurstCapacity"] = "1"
        };
        using RouterApplicationFactory factory = _fixture.CreateFactory(burstCapacity: 1, settings: settings);
        using HttpClient client = factory.CreateClient();
        for (int request = 0; request < 5; request++)
        {
            using HttpRequestMessage poll = new(HttpMethod.Get, "/api/v1/agent/conversations");
            AddAuthentication(poll);
            using HttpResponseMessage response = await client.SendAsync(poll);
            Assert.Equal(request < 4 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using HttpRequestMessage upload = new(HttpMethod.Post, "/api/v1/agent/files");
        AddAuthentication(upload);
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(new byte[31 * 1024 * 1024]), "file", "large.bin");
        upload.Content = form;
        using HttpResponseMessage uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        using HttpRequestMessage extraUpload = new(HttpMethod.Post, "/api/v1/agent/files");
        AddAuthentication(extraUpload);
        using HttpResponseMessage limitedUpload = await client.SendAsync(extraUpload);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedUpload.StatusCode);

        using HttpResponseMessage unauthorized = await client.GetAsync("/api/v1/agent/conversations");
        using HttpResponseMessage limitedAnonymous = await client.GetAsync("/api/v1/agent/conversations");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedAnonymous.StatusCode);
        Assert.NotNull(limitedAnonymous.Headers.RetryAfter);
        using HttpRequestMessage chat = CreateChatRequest("chat uses a separate budget");
        using HttpResponseMessage chatted = await client.SendAsync(chat);
        Assert.Equal(HttpStatusCode.OK, chatted.StatusCode);
    }

    [Fact]
    public async Task Chat_AfterSseClientCancellation_DownstreamNotQuarantined()
    {
        // 用户停止生成（客户端断开 SSE）是正常取消：一次断开不得把下游 Engine
        // 计为故障并隔离 30 秒（FailureThreshold=1），否则紧随其后的请求被 503，
        // 表现为“会话失败”。断开后立即再发一次普通对话必须成功。
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using (HttpRequestMessage aborting = CreateChatRequest(
            "to be cancelled",
            "/api/v1/agent/chat/sse"))
        using (var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            using HttpResponseMessage response = await client.SendAsync(
                aborting,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
            await using Stream stream = await response.Content.ReadAsStreamAsync(
                requestCancellation.Token);
            using var reader = new StreamReader(stream);
            await reader.ReadLineAsync(requestCancellation.Token);
            await reader.ReadLineAsync(requestCancellation.Token);

            requestCancellation.Cancel();
            response.Dispose();
            using var propagationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _fixture.PrimaryEngine.WaitForSseCancellationAsync(propagationTimeout.Token);
        }

        using (HttpRequestMessage followup = CreateChatRequest("after cancellation"))
        using (HttpResponseMessage next = await client.SendAsync(followup))
        {
            string body = await next.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Contains("primary-engine", body, StringComparison.Ordinal);
        }
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

    [Fact]
    public async Task Chat_QueueFullDownstream_PassesThrough429WithRetryAfterAndProblemDetails()
    {
        using RouterApplicationFactory factory = _fixture.CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = CreateChatRequest("queued out", "/api/v1/agent/chat/stream");

        using HttpResponseMessage response = await client.SendAsync(request);

        // 队列满是过载信号而不是转发故障：状态码、重试间隔和问题详情原样到达客户端，不被改写成流。
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("Retry-After")));
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RuntimeQueueFull", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(8201, problem.RootElement.GetProperty("errorCode").GetInt32());
        Assert.Equal(1, problem.RootElement.GetProperty("retryAfterSeconds").GetInt32());
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
