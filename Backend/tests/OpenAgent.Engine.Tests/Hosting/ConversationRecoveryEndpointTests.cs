using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints.Chat;
using OpenAgent.Engine.Host.Endpoints.Conversations;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Host.Streaming;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class ConversationRecoveryEndpointTests
{
    [Fact]
    public async Task DisconnectedPost_ActiveOnlyDetailAndReplay_PreserveInputAndOriginalTrace()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        using (HttpResponseMessage initial = await host.PostStreamAsync())
        {
            using StreamReader reader = new(await initial.Content.ReadAsStreamAsync());
            await ReadThroughContentAsync(reader);
        }
        ConversationRun run = Assert.IsType<ConversationRun>(host.Runs.Find("tenant-1", "conversation-1"));
        Assert.False(run.Completion.IsCompleted);

        ConversationRecord detail = (await host.Client.GetFromJsonAsync<ConversationRecord>(host.ConversationPath))!;
        Assert.Equal(ConversationStatus.Running, detail.Status);
        Assert.Equal("conversation-1", detail.ConversationId);
        ConversationMessage input = Assert.Single(detail.Messages);
        Assert.Equal("turn-trace-input", input.MessageId);
        Assert.Equal("current question", input.Content);
        Assert.Equal("user", input.Role);
        Assert.Equal(1, input.Sequence);
        Assert.Equal(run.StartedAt, input.Timestamp);
        MessageFileMetadata attachment = Assert.Single(input.Metadata!.Files!);
        Assert.Equal("upload-1", attachment.FileId);
        Assert.Equal("upload-1", attachment.FileName);

        using HttpResponseMessage replay = await host.Client.GetAsync(
            host.ConversationPath + "/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using StreamReader replayReader = new(await replay.Content.ReadAsStreamAsync());
        string replayPrefix = await ReadThroughContentAsync(replayReader);
        Assert.Contains("replayed answer", replayPrefix);
        host.Record = CreateRecord();
        host.Producer.Finish.TrySetResult();
        host.Producer.AllowFinalization.TrySetResult();
        string replayTail = await replayReader.ReadToEndAsync();
        Assert.Contains("event: done", replayTail);
        Assert.Contains("\"traceId\":\"turn-trace\"", replayTail);
        Assert.DoesNotContain("recovery-trace", replayTail);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        using HttpResponseMessage completed = await host.Client.GetAsync(host.ConversationPath + "/stream");
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Empty(await completed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_ActiveTurn_PreservesHistorySummariesAndStableInputWithoutMutatingStore()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        ConversationRecord persisted = CreateRecord();
        persisted.Messages =
        [
            new ConversationMessage { MessageId = "previous-user", Sequence = 4, Role = "user", Content = "old question" },
            new ConversationMessage { MessageId = "previous-answer", Sequence = 5, Role = "assistant", Content = "old answer" }
        ];
        persisted.MessageCount = 2;
        persisted.ContextSummaries =
        [
            new ContextSummary
            {
                CompressionId = "summary-1", Strategy = "runtime", Trigger = "runtime", Status = "Succeeded",
                Summary = "preserved summary", SourceEndSequence = 5
            }
        ];
        host.Record = persisted;
        host.StartRun();

        ConversationRecord detail = (await host.Client.GetFromJsonAsync<ConversationRecord>(host.ConversationPath))!;
        Assert.Equal(3, detail.Messages.Count);
        Assert.Equal("previous-user", detail.Messages[0].MessageId);
        Assert.Equal("previous-answer", detail.Messages[1].MessageId);
        Assert.Equal("turn-trace-input", detail.Messages[2].MessageId);
        Assert.Equal(6, detail.Messages[2].Sequence);
        Assert.Equal("preserved summary", Assert.Single(detail.ContextSummaries).Summary);
        Assert.Equal(2, persisted.Messages.Count);
        Assert.Equal(ConversationStatus.Completed, persisted.Status);

        host.Record = detail;
        ConversationRecord repeated = (await host.Client.GetFromJsonAsync<ConversationRecord>(host.ConversationPath))!;
        Assert.Equal(3, repeated.Messages.Count);
        Assert.Single(repeated.Messages, message => message.MessageId == "turn-trace-input");
    }

    [Fact]
    public async Task Detail_AlreadyPersistedCurrentInput_DoesNotDuplicateItDuringFinalization()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        host.StartRun();
        host.Record = CreateRecord() with
        {
            Messages =
            [
                new ConversationMessage { MessageId = "stored-input", Sequence = 1, Role = "user", Content = "current question", TraceId = "turn-trace" },
                new ConversationMessage { MessageId = "stored-answer", Sequence = 2, Role = "assistant", Content = "persisted answer", TraceId = "turn-trace" }
            ]
        };

        ConversationRecord detail = (await host.Client.GetFromJsonAsync<ConversationRecord>(host.ConversationPath))!;

        Assert.Single(detail.Messages, message => message.Role == "user");
        Assert.Equal("persisted answer", detail.Messages.Last().Content);
    }

    [Fact]
    public async Task Detail_RunCompletesDuringLookup_ReturnsFinalPersistedResponse()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        ConversationRun run = host.StartRun();
        host.Record = CreateRecord() with { Status = ConversationStatus.Running };
        int reads = 0;
        host.Query.Setup(query => query.GetRecordAsync("tenant-1", "conversation-1", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    ConversationRecord? stale = host.Record;
                    host.Producer.Finish.TrySetResult();
                    host.Producer.AllowFinalization.TrySetResult();
                    await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                    host.Record = CreateRecord() with
                    {
                        Messages = [new ConversationMessage { MessageId = "stored-answer", Sequence = 2, Role = "assistant", Content = "final answer" }]
                    };
                    return stale;
                }
                return host.Record;
            });

        ConversationRecord detail = (await host.Client.GetFromJsonAsync<ConversationRecord>(host.ConversationPath))!;

        Assert.Equal(ConversationStatus.Completed, detail.Status);
        Assert.Equal("final answer", Assert.Single(detail.Messages).Content);
    }

    [Fact]
    public async Task Detail_RunStartsDuringLookup_IncludesItsCurrentInput()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        host.Record = CreateRecord();
        host.Query.Setup(query => query.GetRecordAsync("tenant-1", "conversation-1", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                host.StartRun();
                return Task.FromResult<ConversationRecord?>(host.Record);
            });

        ConversationRecord detail = (await host.Client.GetFromJsonAsync<ConversationRecord>(host.ConversationPath))!;

        Assert.Equal(ConversationStatus.Running, detail.Status);
        Assert.Equal("current question", Assert.Single(detail.Messages).Content);
    }

    [Theory]
    [InlineData("POST", "/cancel")]
    [InlineData("DELETE", "")]
    public async Task CancelAndDelete_AwaitProducerFinalizationBeforeRespondingOrDeleting(string method, string suffix)
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        ConversationRun run = host.StartRun();
        await host.Producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Record = CreateRecord();
        using HttpRequestMessage request = new(new HttpMethod(method), host.ConversationPath + suffix);
        Task<HttpResponseMessage> pending = host.Client.SendAsync(request);
        await host.Producer.Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        Assert.False(run.Completion.IsCompleted);
        host.Query.Verify(query => query.SoftDeleteAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never());

        host.Producer.AllowFinalization.TrySetResult();
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(run.Completion.IsCompleted);
        host.Query.Verify(query => query.SoftDeleteAsync("tenant-1", "conversation-1", It.IsAny<CancellationToken>()),
            method == "DELETE" ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("user-2", false, ConversationType.User, HttpStatusCode.Forbidden)]
    [InlineData("user-1", true, ConversationType.User, HttpStatusCode.NotFound)]
    [InlineData("user-1", false, ConversationType.Internal, HttpStatusCode.NotFound)]
    public async Task ActiveRun_DoesNotBypassPersistedRecordGuards(
        string recordUser, bool deleted, ConversationType type, HttpStatusCode expected)
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        ConversationRun run = host.StartRun();
        host.Record = CreateRecord() with { UserId = recordUser, IsDeletedByUser = deleted, Type = type };

        using HttpResponseMessage detail = await host.Client.GetAsync(host.ConversationPath);
        using HttpResponseMessage stream = await host.Client.GetAsync(host.ConversationPath + "/stream");
        using HttpResponseMessage cancel = await host.Client.PostAsync(host.ConversationPath + "/cancel", null);
        using HttpResponseMessage delete = await host.Client.DeleteAsync(host.ConversationPath);

        Assert.Equal(expected, detail.StatusCode);
        Assert.Equal(expected, stream.StatusCode);
        Assert.Equal(expected, cancel.StatusCode);
        Assert.Equal(expected, delete.StatusCode);
        Assert.False(run.Completion.IsCompleted);
        Assert.False(host.Producer.Finalizing.Task.IsCompleted);
        host.Query.Verify(query => query.SoftDeleteAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData("tenant-1", "user-2", HttpStatusCode.Forbidden)]
    [InlineData("tenant-2", "user-1", HttpStatusCode.NotFound)]
    public async Task ActiveOnlyRun_EnforcesRunTenantAndUser(string tenantId, string userId, HttpStatusCode expected)
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        ConversationRun run = host.StartRun();
        host.Client.DefaultRequestHeaders.Add("X-Test-Tenant", tenantId);
        host.Client.DefaultRequestHeaders.Add("X-Test-User", userId);

        using HttpResponseMessage detail = await host.Client.GetAsync(host.ConversationPath);
        using HttpResponseMessage stream = await host.Client.GetAsync(host.ConversationPath + "/stream");
        using HttpResponseMessage cancel = await host.Client.PostAsync(host.ConversationPath + "/cancel", null);
        using HttpResponseMessage delete = await host.Client.DeleteAsync(host.ConversationPath);

        Assert.Equal(expected, detail.StatusCode);
        Assert.Equal(expected, stream.StatusCode);
        Assert.Equal(expected, cancel.StatusCode);
        Assert.Equal(expected, delete.StatusCode);
        Assert.False(run.Completion.IsCompleted);
    }

    private static ConversationRecord CreateRecord() => new()
    {
        ConversationId = "conversation-1", TenantId = "tenant-1", UserId = "user-1",
        Status = ConversationStatus.Completed
    };

    private static async Task<string> ReadThroughContentAsync(StreamReader reader)
    {
        List<string> lines = [];
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is { } line)
        {
            lines.Add(line);
            if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("replayed answer", StringComparison.Ordinal))
                return string.Join('\n', lines);
        }
        throw new InvalidOperationException("Stream ended before the buffered content event.");
    }

    private sealed class RecoveryHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private ConversationRecord? _record;

        private RecoveryHost(WebApplication app, ControlledProducer producer, Mock<IConversationQueryService> query)
        {
            _app = app;
            Producer = producer;
            Query = query;
            Runs = app.Services.GetRequiredService<ConversationRunRegistry>();
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        }

        internal ControlledProducer Producer { get; }
        internal Mock<IConversationQueryService> Query { get; }
        internal ConversationRunRegistry Runs { get; }
        internal HttpClient Client { get; }
        internal string ConversationPath => "/api/v1/agent/conversations/conversation-1";
        internal ConversationRecord? Record
        {
            get => Volatile.Read(ref _record);
            set => Volatile.Write(ref _record, value);
        }

        internal static async Task<RecoveryHost> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            ControlledProducer producer = new();
            Mock<IConversationQueryService> query = new();
            RecoveryHost? host = null;
            query.Setup(value => value.GetRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => host!.Record);
            query.Setup(value => value.SoftDeleteAsync("tenant-1", "conversation-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            builder.Services.AddSingleton(query.Object);
            builder.Services.AddSingleton(Mock.Of<IFileAssetService>());
            builder.Services.AddSingleton(new ConversationRunRegistry(producer.ExecuteAsync));
            builder.Services.AddAuthentication("test").AddCookie("test", options =>
            {
                options.Events.OnRedirectToAccessDenied = redirect =>
                {
                    redirect.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
            WebApplication app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Features.Set(new AgentRequestFeature(
                    context.Request.Method == "POST" && context.Request.Path == "/api/v1/agent/chat/stream"
                        ? "turn-trace" : "recovery-trace",
                    new AgentUserContext
                    {
                        TenantId = context.Request.Headers["X-Test-Tenant"].FirstOrDefault() ?? "tenant-1",
                        UserId = context.Request.Headers["X-Test-User"].FirstOrDefault() ?? "user-1"
                    }));
                await next(context);
            });
            var group = app.MapGroup("/api/v1/agent");
            group.MapAgentChat();
            group.MapConversations();
            await app.StartAsync();
            host = new RecoveryHost(app, producer, query);
            return host;
        }

        internal ConversationRun StartRun() => Runs.Start(new AgentRequest
        {
            Query = "current question", ConversationId = "conversation-1", TraceId = "turn-trace",
            FileIds = ["upload-1"]
        }, new AgentUserContext { TenantId = "tenant-1", UserId = "user-1" });

        internal async Task<HttpResponseMessage> PostStreamAsync()
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/agent/chat/stream")
            {
                Content = JsonContent.Create(new { message = "current question", fileIds = new[] { "upload-1" } })
            };
            request.Headers.Add("X-Conversation-Id", "conversation-1");
            return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public async ValueTask DisposeAsync()
        {
            Producer.AllowFinalization.TrySetResult();
            Producer.Finish.TrySetResult();
            Client.Dispose();
            await Runs.DisposeAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class ControlledProducer
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finalizing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowFinalization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async IAsyncEnumerable<AgentStreamEvent> ExecuteAsync(
            AgentRequest request,
            IAgentUserContext user,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                yield return new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "replayed answer" };
                await Finish.Task.WaitAsync(cancellationToken);
                yield return new AgentStreamEvent { Type = AgentStreamEventType.Usage, TraceId = request.TraceId };
            }
            finally
            {
                Finalizing.TrySetResult();
                await AllowFinalization.Task;
            }
        }
    }
}
