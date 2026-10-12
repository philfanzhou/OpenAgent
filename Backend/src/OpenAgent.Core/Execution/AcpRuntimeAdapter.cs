using OpenAgent.Contracts.Security;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Core.Mapping;

namespace OpenAgent.Core.Execution;

internal sealed class AcpRuntimeAdapter(IOptions<AcpOptions> configured, ConversationSessionStore conversations,
    IConversationLock locks) : IRuntimeAdapter
{
    private readonly AcpOptions _options = configured.Value;
    public string Id => "acp";
    public IReadOnlyList<RuntimeDescriptor> Describe() => !_options.Enabled ? [] : _options.Harnesses
        .Select(harness => new RuntimeDescriptor("acp", harness.Id, harness.Name)).ToArray();

    public async Task<AgentResponse> ExecuteAsync(RuntimeInvocation invocation, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        TokenUsage? usage = null;
        await foreach (AgentStreamEvent item in ExecuteStreamingAsync(invocation, cancellationToken).ConfigureAwait(false))
        {
            if (item.Type == AgentStreamEventType.Content)
                text.Append(item.Content);
            if (item.Type == AgentStreamEventType.Usage)
                usage = item.Usage;
        }
        return new AgentResponse
        {
            Content = text.ToString(),
            ModelId = invocation.Profile.Model.ModelId,
            TraceId = invocation.Turn.TraceId,
            TokenUsage = usage
        };
    }

    public async IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate(invocation);
        AcpHarness harness = _options.Harnesses.Single(item => item.Id == invocation.Profile.Config.Runtime.Harness);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TurnTimeoutSeconds));
        await using ExternalRuntimeTurn turn = await ExternalRuntimeTurn.OpenAsync(conversations, locks, invocation,
            TimeSpan.FromSeconds(_options.TurnTimeoutSeconds + 60), cancellationToken).ConfigureAwait(false);
        string workspace = AcpLaunch.Workspace(_options, invocation);
        await using var connection = new AcpConnection(AcpLaunch.Create(harness, workspace, invocation), harness.PermissionPolicy);
        JsonElement init = await RequestAsync(connection, "initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { },
            clientInfo = new { name = "Agent.Matrix", version = "acp-preview-1" }
        }, deadline.Token).ConfigureAwait(false);
        if (init.GetProperty("protocolVersion").GetInt32() != 1)
            throw new AgentException(AgentErrorCode.InvalidRequest, "The harness did not negotiate ACP protocol version 1.");
        JsonElement capabilities = init.GetProperty("agentCapabilities");
        JsonElement session;
        if (turn.SessionId != null)
        {
            if (!capabilities.TryGetProperty("loadSession", out JsonElement load) || load.ValueKind != JsonValueKind.True)
                throw new AgentException(AgentErrorCode.InvalidRequest, "The harness cannot reload this conversation. Start a new conversation.");
            session = await RequestAsync(connection, "session/load", new { sessionId = turn.SessionId, cwd = workspace, mcpServers = Array.Empty<object>() }, deadline.Token).ConfigureAwait(false);
            connection.SessionId = turn.SessionId;
        }
        else
        {
            session = await RequestAsync(connection, "session/new", new { cwd = workspace, mcpServers = Array.Empty<object>() }, deadline.Token).ConfigureAwait(false);
            connection.SessionId = session.GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException("The ACP harness returned an empty session identifier.");
        }
        string modelId = harness.OpenCode ? $"matrix/{invocation.Profile.Model.ModelId}"
            : harness.ModelIdTemplate.Replace("{provider}", invocation.Profile.Model.Provider, StringComparison.Ordinal)
                .Replace("{model}", invocation.Profile.Model.ModelId, StringComparison.Ordinal);
        if (session.TryGetProperty("configOptions", out JsonElement options)
            && options.EnumerateArray().Any(option => AcpEventMapper.ReadString(option, "id") == "model"))
        {
            await RequestAsync(connection, "session/set_config_option", new { sessionId = connection.SessionId, configId = "model", value = modelId }, deadline.Token).ConfigureAwait(false);
        }
        else if (session.TryGetProperty("models", out _))
            await RequestAsync(connection, "session/set_model", new { sessionId = connection.SessionId, modelId }, deadline.Token).ConfigureAwait(false);
        else
            throw new AgentException(AgentErrorCode.InvalidRequest, "The harness cannot bind the selected platform model.");
        await turn.BindAsync(connection.SessionId!, deadline.Token).ConfigureAwait(false);
        connection.CaptureUpdates = true;
        Task<JsonElement> prompt = connection.RequestAsync("session/prompt", new
        {
            sessionId = connection.SessionId,
            prompt = new[] { new { type = "text", text = harness.OpenCode
                || string.IsNullOrWhiteSpace(invocation.Profile.Config.Instructions) ? invocation.Request.Query
                : $"Agent instructions:\n{invocation.Profile.Config.Instructions}\n\nUser request:\n{invocation.Request.Query}" } }
        }, deadline.Token);
        var mapper = new AcpEventMapper();
        bool finished = false;
        try
        {
            while (!prompt.IsCompleted || connection.Updates.TryPeek(out _))
            {
                while (connection.Updates.TryRead(out JsonElement update))
                {
                    AgentStreamEvent? item = mapper.Map(update);
                    if (item == null)
                        continue;
                    turn.Append(item);
                    yield return item;
                }
                if (prompt.IsCompleted)
                    break;
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                Task<bool> next = connection.Updates.WaitToReadAsync(waiting.Token).AsTask();
                await Task.WhenAny(prompt, next).ConfigureAwait(false);
                if (next.IsCompleted)
                    await next.ConfigureAwait(false);
                else
                {
                    waiting.Cancel();
                    try
                    { await next.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                deadline.Token.ThrowIfCancellationRequested();
            }
            JsonElement result = await prompt.ConfigureAwait(false);
            string? reason = AcpEventMapper.ReadString(result, "stopReason");
            if (reason == "cancelled")
            {
                turn.Cancel();
                throw new OperationCanceledException("The harness cancelled the turn.", cancellationToken);
            }
            if (reason != "end_turn")
                throw new AgentException(AgentErrorCode.InvalidRequest, "The harness stopped before completing the turn.");
            TokenUsage? usage = ReadUsage(result);
            await turn.CompleteAsync(usage, invocation.Profile.Model.ModelId, deadline.Token).ConfigureAwait(false);
            finished = true;
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.Usage,
                Usage = usage,
                ModelId = invocation.Profile.Model.ModelId,
                TraceId = invocation.Turn.TraceId
            };
        }
        finally
        {
            connection.CaptureUpdates = false;
            if (!finished)
            {
                if ((!prompt.IsCompleted || cancellationToken.IsCancellationRequested) && !deadline.IsCancellationRequested)
                    turn.Cancel();
                await connection.CancelAsync(connection.SessionId!).ConfigureAwait(false);
            }
            // Observe a cancelled/faulted prompt even when the caller stops enumerating.
            if (!finished)
            {
                deadline.Cancel();
                try
                { await prompt.ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
    }

    private async Task<JsonElement> RequestAsync(AcpConnection connection, string method, object parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        return await connection.RequestAsync(method, parameters, timeout.Token).ConfigureAwait(false);
    }

    private void Validate(RuntimeInvocation invocation)
    {
        if (!_options.Enabled || _options.TurnTimeoutSeconds <= 0 || _options.RequestTimeoutSeconds <= 0)
            throw new AgentException(AgentErrorCode.InvalidRequest, "ACP execution is disabled or has invalid limits.");
        if (invocation.Files.Count > 0 || invocation.Profile.Config.Rag.Enabled
            || invocation.Profile.Config.Mcp.EnabledServerIds.Count > 0 || invocation.Profile.Config.Mcp.Servers.Count > 0
            || invocation.Profile.Config.Skills.EnabledSkills.Count > 0 || invocation.Profile.Config.Skills.Instances.Any(skill => skill.Enabled))
            throw new AgentException(AgentErrorCode.InvalidRequest, "This ACP preview supports harness-native tools. Platform file, MCP, Skill and RAG bindings require a runtime bridge and are not supported yet.");
    }

    private static TokenUsage? ReadUsage(JsonElement result)
    {
        if (!result.TryGetProperty("usage", out JsonElement usage))
            return null;
        int Read(string name) => usage.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int count) ? count : 0;
        return new TokenUsage { PromptTokens = Read("inputTokens"), CompletionTokens = Read("outputTokens"), TotalTokens = Read("totalTokens") };
    }
}
