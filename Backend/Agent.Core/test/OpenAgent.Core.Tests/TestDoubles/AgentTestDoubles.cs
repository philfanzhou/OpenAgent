using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Engine;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Execution.Resolvers;
using OpenAgent.Core.Execution.Persistence;
using OpenAgent.Core.Execution.Phases;
using OpenAgent.Core.Impl;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Execution.Tools;
using OpenAgent.Core.Conversation.Lock;
using OpenAgent.Core.Conversation.Compression;
using OpenAgent.Core.Impl.Compression;
using OpenAgent.Core.Observability;
using OpenAgent.Core.Security;
using AgentExecutionContext = OpenAgent.Core.Execution.ExecutionContext;

namespace OpenAgent.Core.Tests;

internal interface ITestModelRuntime
{
    Task<EngineChatCompletionResult> ChatCompletionAsync(
        EngineChatRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
        EngineChatRequest request,
        CancellationToken cancellationToken = default);

}

internal static class AgentRunTestFactory
{
    public static AgentExecutionContext CreateExecutionContext(
        ITestModelRuntime engine,
        AgentConfig? config = null,
        int maxTurns = 3,
        bool streaming = false)
    {
        var resolvedConfig = config ?? CreateConfig();
        return new AgentExecutionContext
        {
            AgentId = "default",
            Config = resolvedConfig,
            ResolvedLlm = resolvedConfig.Llm,
            Run = engine.ChatCompletionAsync,
            RunStreaming = engine.StreamingChatCompletionAsync,
            UserContext = CreateUserContext(),
            Tools = Array.Empty<ToolDefinition>(),
            Messages = new List<EngineChatMessage>(),
            NewMessages = new List<ConversationMessage>(),
            ConvCtx = new ConversationContext(null, null, null, "default", null),
            LockHandle = null,
            Telemetry = new AgentExecutionTelemetry("default", null, null, null, streaming),
            EmitTelemetryEvents = false,
            MaxTurns = maxTurns,
            CurrentVersion = 0,
            NextSequence = 1
        };
    }

    public static AgentRun CreateRun(
        ITestModelRuntime engine,
        IConversationStore store,
        AgentConfig config,
        IMcpClient? mcpClient = null,
        ISkillProvider? skillProvider = null,
        IAgentConfigProvider? configProvider = null,
        RagSearchTool? ragSearchTool = null,
        ContextCompressorDispatcher? compressor = null,
        IConversationLock? conversationLock = null,
        ILoggerFactory? loggerFactory = null,
        ILlmRegistry? llmRegistry = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IAgentAuthorizationService? authorizationService = null)
    {
        var registry = llmRegistry ?? CreateDefaultLlmRegistry();
        var resolvedSkillProvider = skillProvider ?? new FakeSkillProvider();
        var resolvedLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        var mcpClients = new McpClientPool(new FakeMcpClientFactory(
            mcpClient ?? new FakeMcpClient(new Dictionary<string, List<McpTool>>())));
        var authorizationGate = CreateAuthorizationGate(authorizationService);
        var dispatcher = new ToolCallDispatcher(
            resolvedSkillProvider, mcpClients, authorizationGate,
            resolvedLoggerFactory.CreateLogger<ToolCallDispatcher>(), ragSearchTool);
        var assembler = new ToolAssembler(
            resolvedSkillProvider, mcpClients, authorizationGate,
            resolvedLoggerFactory.CreateLogger<ToolAssembler>(), ragSearchTool);
        var storeOptions = Options.Create(
            new ConversationStoreOptions { MaxHistoryMessages = 20, EnableColdArchive = false });
        var resolvedCompressor = compressor ?? CreateNoOpCompressor();
        var persister = new PartialMessagePersister(resolvedLoggerFactory.CreateLogger<PartialMessagePersister>());
        var loader = new ConversationLoader(
            store, storeOptions, resolvedCompressor, persister, resolvedLoggerFactory.CreateLogger<ConversationLoader>());
        var saver = new ConversationSaver(store, persister, resolvedLoggerFactory.CreateLogger<ConversationSaver>());
        var accessor = httpContextAccessor;
        var configSource = configProvider ?? new FakeAgentConfigProvider(config);
        return new AgentRun(
            new AgentRunPreparation(
                new IdentityResolution(
                    new AgentIdResolver(accessor),
                    new ExecutionConfigResolver(configSource, NullLogger<ExecutionConfigResolver>.Instance),
                    new UserContextBuilder(), authorizationGate, registry),
                new ToolPreparation(assembler, new SystemPromptBuilder(resolvedSkillProvider, ragSearchTool)),
                new ConversationPreparation(conversationLock ?? new InMemoryConversationLock(), loader, persister)),
            new AgentRuntime(
                engine.ChatCompletionAsync,
                engine.StreamingChatCompletionAsync),
            dispatcher,
            saver,
            resolvedLoggerFactory.CreateLogger<AgentRun>());
    }

    private static AgentAuthorizationGate CreateAuthorizationGate(
        IAgentAuthorizationService? authorizationService = null)
    {
        return new AgentAuthorizationGate(
            authorizationService ?? new AllowAllAgentAuthorizationService());
    }

    private static IAgentUserContext CreateUserContext()
    {
        return new AgentUserContext
        {
            UserId = "test-user",
            TenantId = "test-tenant",
            IsAuthenticated = true
        };
    }

    private static ILlmRegistry CreateDefaultLlmRegistry()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile
        {
            Id = "test-provider",
            Name = "Test Provider",
            Format = ApiFormat.OpenAIChatCompletions,
            Endpoint = "https://api.test.com",
            ApiKey = "test-key"
        });
        return registry;
    }

    private static ContextCompressorDispatcher CreateNoOpCompressor()
    {
        return new ContextCompressorDispatcher(
            Array.Empty<IContextCompressor>(),
            NullLogger<ContextCompressorDispatcher>.Instance,
            new CompressionMetrics());
    }

    public static AgentConfig CreateConfig() =>
        new()
        {
            MaxTurns = 3,
            Llm = new LlmConfig
            {
                Temperature = 0.1,
                Endpoint = "https://api.test.com",
                ApiKey = "test-key"
            }
        };

    public static Dictionary<string, object> CreateContext(string conversationId) =>
        new()
        {
            ["UserId"] = "user-1",
            ["TenantId"] = "tenant-1",
            ["ConversationId"] = conversationId
        };
}

internal sealed class CaptureLogger<T> : ILogger<T>
{
    private readonly CaptureLoggerCore _core = new();

    public List<CapturedLogEntry> Entries => _core.Entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _core.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _core.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _core.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>
/// LoggerFactory-level capture: every category logs into one shared entry list.
/// Use with LoggerFactory.Create(builder => builder.AddProvider(provider)).
/// </summary>
internal sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly CaptureLoggerCore _core = new();

    public List<CapturedLogEntry> Entries => _core.Entries;

    public ILogger CreateLogger(string categoryName) => _core;

    public void Dispose()
    {
    }
}

internal sealed class CaptureLoggerCore : ILogger
{
    private static readonly AsyncLocal<ScopeFrame?> CurrentScope = new();

    public List<CapturedLogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var frame = new ScopeFrame(CurrentScope.Value, CaptureProperties(state));
        CurrentScope.Value = frame;
        return new ScopeDisposable(frame);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), exception, CaptureProperties(state), CaptureScopeProperties()));
    }

    private static Dictionary<string, object?> CaptureProperties<TState>(TState state)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
        {
            foreach (var property in structuredState)
            {
                properties[property.Key] = property.Value;
            }
        }

        return properties;
    }

    private static Dictionary<string, object?> CaptureScopeProperties()
    {
        var frames = new Stack<ScopeFrame>();
        for (var frame = CurrentScope.Value; frame is not null; frame = frame.Parent)
        {
            frames.Push(frame);
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (frames.Count > 0)
        {
            foreach (var property in frames.Pop().Properties)
            {
                properties[property.Key] = property.Value;
            }
        }

        return properties;
    }

    private sealed class ScopeDisposable : IDisposable
    {
        private readonly ScopeFrame _frame;
        private bool _disposed;

        public ScopeDisposable(ScopeFrame frame)
        {
            _frame = frame;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentScope.Value = _frame.Parent;
            _disposed = true;
        }
    }

    private sealed record ScopeFrame(ScopeFrame? Parent, IReadOnlyDictionary<string, object?> Properties);
}

internal sealed class NoopDisposable : IDisposable
{
    public static NoopDisposable Instance { get; } = new();

    public void Dispose()
    {
    }
}

internal sealed record CapturedLogEntry(
    LogLevel LogLevel,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, object?> ScopeProperties);

internal sealed class FakeAgentConfigProvider : IAgentConfigProvider
{
    private readonly AgentConfig _config;

    public FakeAgentConfigProvider(AgentConfig config)
    {
        _config = config;
    }

    public Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default) => Task.FromResult(_config);

    public Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default) => Task.FromResult<AgentConfig?>(_config);

    public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentSummary>>(Array.Empty<AgentSummary>());
}

internal sealed class RecordingAgentConfigProvider : IAgentConfigProvider
{
    private readonly AgentConfig _config;

    public RecordingAgentConfigProvider(AgentConfig config)
    {
        _config = config;
    }

    public string? LastRequestedAgentId { get; private set; }

    public Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default) => Task.FromResult(_config);

    public Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default)
    {
        LastRequestedAgentId = agentId;
        return Task.FromResult<AgentConfig?>(_config);
    }

    public Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentSummary>>(Array.Empty<AgentSummary>());
}

internal sealed class FakeSkillProvider : ISkillProvider
{
    public Task<IReadOnlyList<SkillDescriptor>> GetSkillDescriptorsAsync(string? agentId, IAgentUserContext? userContext, SkillsConfig? overrideConfig = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SkillDescriptor>>(Array.Empty<SkillDescriptor>());

    public Task<string> ExecuteAsync(string skillName, Dictionary<string, object> arguments, IAgentUserContext? userContext, CancellationToken cancellationToken = default) =>
        Task.FromResult("skill-result");

    public void RegisterSkill(ISkill skill, SkillSource source = SkillSource.Local, string? sourceId = null)
    {
    }

    public void RegisterMcpSkills(string serverUrl, IReadOnlyList<McpTool> tools)
    {
    }
}

internal sealed class RecordingSkillProvider : ISkillProvider
{
    private readonly IReadOnlyList<SkillDescriptor> _descriptors;
    private readonly string _result;

    public RecordingSkillProvider(IReadOnlyList<SkillDescriptor> descriptors, string result)
    {
        _descriptors = descriptors;
        _result = result;
    }

    public List<(string SkillName, Dictionary<string, object> Arguments)> ExecutionLog { get; } = new();

    public Task<IReadOnlyList<SkillDescriptor>> GetSkillDescriptorsAsync(string? agentId, IAgentUserContext? userContext, SkillsConfig? overrideConfig = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_descriptors);

    public Task<string> ExecuteAsync(string skillName, Dictionary<string, object> arguments, IAgentUserContext? userContext, CancellationToken cancellationToken = default)
    {
        ExecutionLog.Add((skillName, new Dictionary<string, object>(arguments)));
        return Task.FromResult(_result);
    }

    public void RegisterSkill(ISkill skill, SkillSource source = SkillSource.Local, string? sourceId = null)
    {
    }

    public void RegisterMcpSkills(string serverUrl, IReadOnlyList<McpTool> tools)
    {
    }
}

internal sealed class FakeRagService : IRagService
{
    public Task IndexDocumentAsync(string content, Dictionary<string, object>? metadata = null, string? ragInstanceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<List<string>> SearchAsync(string query, int limit = 3, RagConfig? overrideConfig = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<string>());

    public Task<List<SearchResult>> SearchDetailedAsync(string query, int limit = 3, RagConfig? overrideConfig = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<SearchResult>());
}

internal sealed class RecordingRagService : IRagService
{
    public string? LastQuery { get; private set; }
    public int LastLimit { get; private set; }

    public Task IndexDocumentAsync(string content, Dictionary<string, object>? metadata = null, string? ragInstanceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<List<string>> SearchAsync(string query, int limit = 3, RagConfig? overrideConfig = null, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        LastLimit = limit;
        return Task.FromResult(new List<string> { "rag-result" });
    }

    public Task<List<SearchResult>> SearchDetailedAsync(string query, int limit = 3, RagConfig? overrideConfig = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<SearchResult>());
}

internal sealed class RecordingEngine : ITestModelRuntime
{
    public EngineChatRequest? LastRequest { get; private set; }

    public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new EngineChatCompletionResult { Content = "final-answer" });
    }

    public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

}

internal sealed class StreamingExceptionEngine : ITestModelRuntime
{
    public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EngineChatCompletionResult { Content = "unused" });

    public async IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
        EngineChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new EngineChatCompletionChunk { Content = "partial-response" };
        await Task.Yield();
        throw new OperationCanceledException();
    }

}

internal sealed class StreamingFailureEngine : ITestModelRuntime
{
    public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EngineChatCompletionResult { Content = "unused" });

    public async IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
        EngineChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new EngineChatCompletionChunk { Content = "partial-response" };
        await Task.Yield();
        throw new InvalidOperationException("stream failed");
    }

}

internal sealed class StreamingToolCallingEngine : ITestModelRuntime
{
    public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EngineChatCompletionResult { Content = "unused" });

    public async IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
        EngineChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new EngineChatCompletionChunk { Content = "thinking" };
        await Task.Yield();
        yield return new EngineChatCompletionChunk
        {
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "tool-stream-1",
                    Name = "lookup_data",
                    ArgumentsJson = "{\"id\":\"42\"}"
                }
            ]
        };
        await request.FunctionExecutor!(
            request.Tools.Single(tool => tool.Name == "lookup_data"),
            "{\"id\":\"42\"}",
            cancellationToken);
        yield return new EngineChatCompletionChunk { Content = "final-answer" };
    }

}

internal sealed class GatedStreamingEngine : ITestModelRuntime
{
    private readonly TaskCompletionSource _completionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EngineChatCompletionResult { Content = "unused" });

    public async IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
        EngineChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new EngineChatCompletionChunk { Content = "first" };
        await _completionGate.Task.WaitAsync(cancellationToken);
        yield return new EngineChatCompletionChunk { Content = " second" };
    }

    public void ReleaseCompletion()
    {
        _completionGate.TrySetResult();
    }

}

internal sealed class MaxTurnsToolLoopEngine : ITestModelRuntime
{
    public async Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default)
    {
        for (var turn = 1; turn <= request.MaximumIterations; turn++)
        {
            await request.FunctionExecutor!(
                request.Tools.Single(tool => tool.Name == "lookup_data"),
                "{\"id\":\"42\"}",
                cancellationToken);
        }

        return new EngineChatCompletionResult
        {
            Content = $"assistant-turn-{request.MaximumIterations}"
        };
    }

    public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

}

internal sealed class McpRoutingEngine : ITestModelRuntime
{
    public async Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default)
    {
        var betaTool = request.Tools.Single(
            tool => tool.Description.StartsWith("[MCP:Beta]", StringComparison.Ordinal));
        await request.FunctionExecutor!(
            betaTool, "{\"query\":\"value\"}", cancellationToken);
        return new EngineChatCompletionResult { Content = "done" };
    }

    public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

}

internal sealed class CollidingAliasMcpRoutingEngine : ITestModelRuntime
{
    public EngineChatRequest? FirstRequest { get; private set; }

    public async Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default)
    {
        FirstRequest ??= request;
        var targetTool = request.Tools
            .Where(tool => tool.Description.StartsWith("[MCP:", StringComparison.Ordinal))
            .Single(tool => tool.Description.StartsWith("[MCP:Beta 1]", StringComparison.Ordinal));
        await request.FunctionExecutor!(
            targetTool, "{\"query\":\"value\"}", cancellationToken);
        return new EngineChatCompletionResult { Content = "done" };
    }

    public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

}

internal sealed class RagToolCallingEngine : ITestModelRuntime
{
    public async Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default)
    {
        await request.FunctionExecutor!(
            request.Tools.Single(tool => tool.Name == "search_knowledge_base"),
            "{\"query\":\"benefits\",\"limit\":2}",
            cancellationToken);
        return new EngineChatCompletionResult { Content = "done" };
    }

    public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

}

internal sealed class StreamingUsageEngine : ITestModelRuntime
{
    public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EngineChatCompletionResult { Content = "final-answer" });

    public async IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(
        EngineChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new EngineChatCompletionChunk { Content = "final-answer" };
        await Task.Yield();
        yield return new EngineChatCompletionChunk
        {
            TokenUsage = new TokenUsage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15
            }
        };
    }

}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
    }
}

internal sealed class FakeMcpClient : IMcpClient
{
    private readonly Dictionary<string, List<McpTool>> _toolsByServer;
    private readonly string _toolResult;
    private string? _currentServerUrl;

    public FakeMcpClient(Dictionary<string, List<McpTool>> toolsByServer, string toolResult = "tool-result")
    {
        _toolsByServer = toolsByServer;
        _toolResult = toolResult;
    }

    public List<(string ServerUrl, string ToolName)> CallLog { get; } = new();
    public List<(string ServerUrl, McpServerType Type)> ConnectLog { get; } = new();
    public int DisconnectCount { get; private set; }
    public bool IsConnected { get; private set; }
    public McpServerType? LastConnectedType { get; private set; }

    public Task ConnectAsync(string serverUrl, McpServerType type = McpServerType.Http, CancellationToken cancellationToken = default)
    {
        ConnectLog.Add((serverUrl, type));
        _currentServerUrl = serverUrl;
        LastConnectedType = type;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        IsConnected = false;
        _currentServerUrl = null;
        return Task.CompletedTask;
    }

    public Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_currentServerUrl != null && _toolsByServer.TryGetValue(_currentServerUrl, out var tools)
            ? tools
            : new List<McpTool>());
    }

    public Task<string> CallToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default)
    {
        CallLog.Add((_currentServerUrl ?? string.Empty, toolName));
        return Task.FromResult(_toolResult);
    }

    public Task<Stream> ReadResourceAsync(string resourceUri, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());
}

internal sealed class FakeMcpClientFactory(IMcpClient client) : IMcpClientFactory
{
    public IMcpClient Create() => client;
}
