using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.OpenCode;

namespace OpenAgent.Runtime.Pi;

/// <summary>Replays committed platform history in a private ACP session per turn.</summary>
internal sealed class PiRuntime(PiOptions options, RuntimeToolBridge bridge, ILogger<PiRuntime> logger) : IAgentRuntimeDriver
{
    public string Provider => "Pi";

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(invocation.Profile.Runtime?.TurnTimeoutSeconds ?? 1800));
        await using IAsyncEnumerator<AgentStreamEvent> events = RunTurnAsync(invocation, deadline.Token).GetAsyncEnumerator();
        while (true)
        {
            bool more;
            try
            {
                more = await events.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new AgentException(AgentErrorCode.LlmTimeout, "The Pi turn exceeded its time limit.");
            }
            catch (Exception error) when (error is not AgentException)
            {
                logger.LogWarning("Pi runtime failed. TraceId={TraceId}, ErrorType={ErrorType}", invocation.Turn.TraceId, error.GetType().Name);
                throw new AgentException(AgentErrorCode.DependencyUnavailable, "The Pi runtime could not complete the turn.");
            }
            if (!more)
                yield break;
            yield return events.Current;
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> RunTurnAsync(RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!options.Enabled || !options.AllowedTenants.Contains(invocation.Turn.TenantId, StringComparer.Ordinal))
            throw new AgentException(AgentErrorCode.PermissionDenied, "Pi is not enabled for this tenant.");

        string root = Path.Combine(Path.GetTempPath(), "openagent-pi-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        string agentDirectory = Path.Combine(root, "agent");
        OpenCodeWorkspace.CreatePrivateDirectory(root);
        try
        {
            OpenCodeWorkspace.CreatePrivateDirectory(workspace);
            OpenCodeWorkspace.CreatePrivateDirectory(agentDirectory);
            OpenCodeSkills.Materialize(workspace, invocation.Skills, ct);
            await PiProcessConfiguration.WriteAsync(options, invocation, workspace, agentDirectory, ct).ConfigureAwait(false);
            AcpProcessOptions processOptions = PiProcessConfiguration.Launch(options, invocation, workspace, agentDirectory);
            await using AcpProcess process = AcpProcess.Start(processOptions);
            await using AcpClient client = new(process.Transport);
            InitializeResponse initialize = await client.InitializeAsync(new("OpenAgent", "1"), ct).ConfigureAwait(false);
            if (initialize.AgentInfo?.Version != "0.1.8"
                || initialize.AgentCapabilities?.McpCapabilities is not { ValueKind: JsonValueKind.Object } mcp
                || !mcp.TryGetProperty("http", out JsonElement http) || http.ValueKind != JsonValueKind.True
                || initialize.AgentCapabilities?.SessionCapabilities?.AdditionalDirectories is null)
                throw new AgentException(AgentErrorCode.ConfigurationError, "The supported Pi ACP adapter is openma-pi-acp 0.1.8.");

            await using RuntimeToolLease tools = await bridge.RegisterAsync(invocation.Tools, ct, invocation.Turn.ConversationId ?? string.Empty,
                invocation.Turn.UserId, invocation.Username ?? string.Empty, invocation.Profile.AgentId).ConfigureAwait(false);
            McpServer[] servers = [tools.Server, .. AcpSessionMcp.Map(invocation.McpServers)];
            await using AcpSession session = await client.NewSessionAsync(new(workspace, servers, AdditionalDirectories: []), ct).ConfigureAwait(false);
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.RuntimeStarted,
                Runtime = new(Provider, invocation.Profile.Runtime?.Id, invocation.Profile.Runtime?.Name, initialize.AgentInfo.Name, initialize.AgentInfo.Version)
            };

            OpenCodeRuntime.MaterializeAttachments(workspace, invocation.Messages);
            RuntimeFilePreparation? files = invocation.PrepareFilesAsync is null ? null
                : await invocation.PrepareFilesAsync(new OpenCodeWorkspaceFiles(workspace), ct).ConfigureAwait(false);
            await using IAsyncDisposable? fileLease = files?.Lease;
            string manifest = files is null ? string.Empty : OpenCodeRuntime.BuildFileManifest(files, invocation.Tools);
            ContentBlock[] prompt = OpenCodeRuntime.BuildPrompt(invocation.Messages, 2 * 1024 * 1024 - manifest.Length);
            if (manifest.Length > 0)
                prompt = [.. prompt, new TextContent(manifest)];

            await using AcpPromptTurn turn = await session.StartPromptAsync(prompt, ct).ConfigureAwait(false);
            await using IAsyncEnumerator<SessionNotification> updates = session.ReadUpdatesAsync(ct).GetAsyncEnumerator();
            AcpEventMapper mapper = new("mcp__openagent__", session.SessionId);
            bool closed = false;
            while (true)
            {
                Task<bool> read = updates.MoveNextAsync().AsTask();
                if (!closed && await Task.WhenAny(read, turn.Completion).ConfigureAwait(false) == turn.Completion)
                {
                    await turn.Completion.ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                    closed = true;
                }
                if (!await read.ConfigureAwait(false))
                    break;
                foreach (AgentStreamEvent item in mapper.Map(updates.Current.Update))
                    yield return item;
            }
            PromptResponse result = await turn.Completion.ConfigureAwait(false);
            if (result.StopReason == "cancelled")
                throw new OperationCanceledException("Pi prompt was cancelled.", ct);
            if (result.StopReason != "end_turn")
                throw new AgentException(AgentErrorCode.PipelineExecutionFailed, "Pi did not complete the prompt.");
            await tools.DisposeAsync().ConfigureAwait(false);
            if (files?.SaveOutputsAsync is { } saveOutputs)
                await saveOutputs(ct).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Pi workspace cleanup failed. TraceId={TraceId}, ErrorType={ErrorType}", invocation.Turn.TraceId, error.GetType().Name);
            }
        }
    }
}
