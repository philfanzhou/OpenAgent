using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Engine;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Capabilities.Rag;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class AgentRunExecutionTests
{
    [Fact]
    public async Task ExecuteStreamAsync_OnCancellation_PersistsCancelledConversationWithPartialAssistant()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new StreamingExceptionEngine();
        var run = AgentRunTestFactory.CreateRun(engine, store, AgentRunTestFactory.CreateConfig());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in run.RunStreamingAsync("hello", AgentRunTestFactory.CreateContext("conv-cancel"), CancellationToken.None))
            {
            }
        });

        var record = await store.GetRecordAsync("tenant-1", "conv-cancel", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(ConversationStatus.Cancelled, record!.Status);
        Assert.Collection(record.Messages,
            message => Assert.Equal("user", message.Role),
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("partial-response", message.Content);
            });
    }

    [Fact]
    public async Task ExecuteStreamAsync_OnFailure_PersistsFailedConversationWithPartialAssistant()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new StreamingFailureEngine();
        var run = AgentRunTestFactory.CreateRun(engine, store, AgentRunTestFactory.CreateConfig());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in run.RunStreamingAsync("hello", AgentRunTestFactory.CreateContext("conv-failed"), CancellationToken.None))
            {
            }
        });

        Assert.Equal("stream failed", exception.Message);

        var record = await store.GetRecordAsync("tenant-1", "conv-failed", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(ConversationStatus.Failed, record!.Status);
        Assert.Collection(record.Messages,
            message => Assert.Equal("user", message.Role),
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("partial-response", message.Content);
            });
    }

    [Fact]
    public async Task ExecuteAsync_SkipsUnsupportedConversationRoles()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        await store.CreateAsync(new ConversationRecord
        {
            ConversationId = "conv-history",
            TenantId = "tenant-1",
            UserId = "user-1",
            AgentId = "default",
            MessageCount = 2,
            Messages =
            [
                new ConversationMessage
                {
                    MessageId = "m1",
                    Sequence = 1,
                    Role = "assistant",
                    Content = "kept-history",
                    Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2)
                },
                new ConversationMessage
                {
                    MessageId = "m2",
                    Sequence = 2,
                    Role = "unknown-role",
                    Content = "should-not-be-used",
                    Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
                }
            ]
        }, CancellationToken.None);

        var engine = new RecordingEngine();
        var run = AgentRunTestFactory.CreateRun(engine, store, AgentRunTestFactory.CreateConfig());

        var result = await run.RunAsync("current-input", AgentRunTestFactory.CreateContext("conv-history"), CancellationToken.None);

        Assert.Equal("final-answer", result);
        Assert.NotNull(engine.LastRequest);
        Assert.Contains(engine.LastRequest!.Messages, m => m.Content == "kept-history");
        Assert.DoesNotContain(engine.LastRequest.Messages, m => m.Content == "should-not-be-used");
    }

    [Fact]
    public async Task ExecuteStreamAsync_ToolCallingFlow_PersistsToolAndFinalAssistantMessages()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var skillProvider = new RecordingSkillProvider(
            [
                new SkillDescriptor
                {
                    Id = "skill-lookup-data",
                    Name = "lookup_data",
                    Description = "Lookup data",
                    ParametersJsonSchema = "{\"type\":\"object\"}"
                }
            ],
            "lookup-result");
        var engine = new StreamingToolCallingEngine();
        var run = AgentRunTestFactory.CreateRun(
            engine,
            store,
            AgentRunTestFactory.CreateConfig(),
            skillProvider: skillProvider);

        var chunks = new List<string>();
        await foreach (var chunk in run.RunStreamingAsync("hello", AgentRunTestFactory.CreateContext("conv-stream-tool"), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(new[] { "thinking", "\n[Calling tool: lookup_data]\n", "final-answer" }, chunks);
        Assert.Contains(skillProvider.ExecutionLog, execution => execution.SkillName == "lookup_data");

        var record = await store.GetRecordAsync("tenant-1", "conv-stream-tool", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(ConversationStatus.Running, record!.Status);
        Assert.Collection(record.Messages,
            message => Assert.Equal(("user", "hello"), (message.Role, message.Content)),
            message => Assert.Equal(("assistant", "thinking"), (message.Role, message.Content)),
            message => Assert.Equal(("tool", "lookup-result"), (message.Role, message.Content)),
            message => Assert.Equal(("assistant", "final-answer"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task ExecuteStreamAsync_YieldsFirstContentChunkBeforeEngineStreamCompletes()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new GatedStreamingEngine();
        var run = AgentRunTestFactory.CreateRun(engine, store, AgentRunTestFactory.CreateConfig());

        await using var enumerator = run
            .RunStreamingAsync("hello", AgentRunTestFactory.CreateContext("conv-stream-immediate"), CancellationToken.None)
            .GetAsyncEnumerator();

        var firstMoveTask = enumerator.MoveNextAsync().AsTask();
        var firstChunkArrived = await Task.WhenAny(firstMoveTask, Task.Delay(TimeSpan.FromMilliseconds(250))) == firstMoveTask;
        if (!firstChunkArrived)
        {
            engine.ReleaseCompletion();
            await firstMoveTask.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.True(firstChunkArrived, "Service streaming should yield content before the engine stream completes.");
        Assert.True(await firstMoveTask);
        Assert.Equal("first", enumerator.Current);

        engine.ReleaseCompletion();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(" second", enumerator.Current);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsReached_PersistsLastAssistantMessage()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var skillProvider = new RecordingSkillProvider(
            [
                new SkillDescriptor
                {
                    Id = "skill-loop",
                    Name = "lookup_data",
                    Description = "Lookup data",
                    ParametersJsonSchema = "{\"type\":\"object\"}"
                }
            ],
            "lookup-result");
        var engine = new MaxTurnsToolLoopEngine();
        var run = AgentRunTestFactory.CreateRun(
            engine,
            store,
            AgentRunTestFactory.CreateConfig(),
            skillProvider: skillProvider);

        var result = await run.RunAsync("hello", AgentRunTestFactory.CreateContext("conv-max-turns"), CancellationToken.None);

        Assert.Equal("assistant-turn-3", result);
        Assert.Equal(3, skillProvider.ExecutionLog.Count);

        var record = await store.GetRecordAsync("tenant-1", "conv-max-turns", CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(ConversationStatus.Running, record!.Status);
        Assert.Equal(("assistant", "assistant-turn-3"), (record.Messages[^1].Role, record.Messages[^1].Content));
    }

    [Fact]
    public async Task ExecuteAsync_UsesAgentIdFromContextWhenProvided()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new RecordingEngine();
        var config = AgentRunTestFactory.CreateConfig();
        var configProvider = new RecordingAgentConfigProvider(config);
        var run = AgentRunTestFactory.CreateRun(
            engine,
            store,
            config,
            configProvider: configProvider);

        var context = AgentRunTestFactory.CreateContext("conv-agent-id");
        context["AgentId"] = "agent-explicit";

        var result = await run.RunAsync("hello", context, CancellationToken.None);

        Assert.Equal("final-answer", result);
        Assert.Equal("agent-explicit", configProvider.LastRequestedAgentId);
    }

    [Theory]
    [InlineData("agent-context", "agent-header", "agent-item", "agent-context")]
    [InlineData(null, "agent-header", "agent-item", "agent-header")]
    [InlineData(null, null, "agent-item", "agent-item")]
    [InlineData(null, null, null, "default")]
    public async Task ExecuteAsync_ResolvesAgentIdInDocumentedPriority(
        string? explicitAgentId,
        string? headerAgentId,
        string? itemAgentId,
        string expectedAgentId)
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new RecordingEngine();
        var config = AgentRunTestFactory.CreateConfig();
        var configProvider = new RecordingAgentConfigProvider(config);
        var httpContext = new DefaultHttpContext();
        if (headerAgentId != null)
        {
            httpContext.Request.Headers["X-Agent-Id"] = headerAgentId;
        }

        if (itemAgentId != null)
        {
            httpContext.Items["AgentId"] = itemAgentId;
        }

        var run = AgentRunTestFactory.CreateRun(
            engine,
            store,
            config,
            configProvider: configProvider,
            httpContextAccessor: new HttpContextAccessor { HttpContext = httpContext });

        var context = AgentRunTestFactory.CreateContext($"conv-{expectedAgentId}");
        if (explicitAgentId != null)
        {
            context["AgentId"] = explicitAgentId;
        }

        var result = await run.RunAsync("hello", context, CancellationToken.None);

        Assert.Equal("final-answer", result);
        Assert.Equal(expectedAgentId, configProvider.LastRequestedAgentId);
    }

    [Fact]
    public async Task Streaming_PreservesTokenUsage_ToFinalChunk()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new StreamingUsageEngine();
        var run = AgentRunTestFactory.CreateRun(engine, store, AgentRunTestFactory.CreateConfig());

        var chunks = new List<string>();
        await foreach (var chunk in run.RunStreamingAsync("hello", AgentRunTestFactory.CreateContext("conv-stream-usage"), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var usageChunk = chunks.LastOrDefault(c => c.StartsWith("__OPENAGENT_USAGE__:", StringComparison.Ordinal));
        Assert.NotNull(usageChunk);
        var usageJson = usageChunk!["__OPENAGENT_USAGE__:".Length..];
        var usage = JsonSerializer.Deserialize<TokenUsage>(usageJson);
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
        Assert.Equal(15, usage.TotalTokens);
    }

    [Fact]
    public async Task Streaming_PersistsTokenUsage_ToConversationMetadata()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new StreamingUsageEngine();
        var run = AgentRunTestFactory.CreateRun(engine, store, AgentRunTestFactory.CreateConfig());

        await foreach (var _ in run.RunStreamingAsync("hello", AgentRunTestFactory.CreateContext("conv-stream-usage-meta"), CancellationToken.None))
        {
        }

        var record = await store.GetRecordAsync("tenant-1", "conv-stream-usage-meta", CancellationToken.None);
        Assert.NotNull(record);
        var assistantMessage = record!.Messages.Last(m => m.Role == "assistant");
        Assert.NotNull(assistantMessage.Metadata);
        Assert.True(assistantMessage.Metadata!.TryGetValue("TokenUsage", out var usageJson));
        var usage = JsonSerializer.Deserialize<TokenUsage>(usageJson);
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
        Assert.Equal(15, usage.TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_ParsesRagToolArgumentsAsJsonObject()
    {
        var store = new InMemoryConversationStore(NullLogger<InMemoryConversationStore>.Instance);
        var engine = new RagToolCallingEngine();
        var config = AgentRunTestFactory.CreateConfig();
        config.Rag.Enabled = true;

        var ragService = new RecordingRagService();
        var ragTool = new RagSearchTool(ragService, NullLogger<RagSearchTool>.Instance);
        var run = AgentRunTestFactory.CreateRun(
            engine,
            store,
            config,
            ragSearchTool: ragTool);

        var result = await run.RunAsync("search", AgentRunTestFactory.CreateContext("conv-rag"), CancellationToken.None);

        Assert.Equal("done", result);
        Assert.Equal("benefits", ragService.LastQuery);
        Assert.Equal(2, ragService.LastLimit);
    }
}
