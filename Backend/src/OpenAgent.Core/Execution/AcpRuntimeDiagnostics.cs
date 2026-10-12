using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Mapping;

namespace OpenAgent.Core.Execution;

internal sealed class AcpRuntimeDiagnostics(IOptions<AcpOptions> configured) : IRuntimeDiagnostics
{
    public async Task<RuntimeProbeResult> TestAsync(string harness, CancellationToken cancellationToken = default)
    {
        AcpOptions options = configured.Value;
        AcpHarness? target = options.Enabled ? options.Harnesses.Find(item => item.Id == harness) : null;
        if (target == null)
            return new(false, "configuration", "This harness is not registered on this Engine.");
        if (target.OpenCode || target.UsePlatformModel)
            return new(false, "configuration", "This harness uses a platform model. Test it with a configured Agent and model profile.");
        string stage = "launch";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        var invocation = new RuntimeInvocation(new AgentRequest { Query = "probe" },
            new AgentRuntimeProfile { AgentId = "probe", Config = new AgentConfig { Runtime = new() { Adapter = "acp", Harness = harness } }, Model = new() },
            new TurnContext { TenantId = "probe", UserId = "probe", ConversationId = Guid.NewGuid().ToString("N"), AgentId = "probe", TraceId = "probe" },
            new AgentUserContext { TenantId = "probe", UserId = "probe" }, []);
        string? workspace = null;
        try
        {
            workspace = AcpLaunch.Workspace(options, invocation);
            await using var connection = new AcpConnection(AcpLaunch.Create(target, workspace, invocation), "deny");
            stage = "initialize";
            var init = await connection.RequestAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { },
                clientInfo = new { name = "Agent.Matrix", version = "acp-preview-2" } }, timeout.Token).ConfigureAwait(false);
            if (init.GetProperty("protocolVersion").GetInt32() != 1)
                return new(false, stage, "The harness did not negotiate ACP version 1.");
            stage = "session/new";
            await connection.RequestAsync(stage, new { cwd = workspace, mcpServers = Array.Empty<object>() }, timeout.Token).ConfigureAwait(false);
            bool load = init.GetProperty("agentCapabilities").TryGetProperty("loadSession", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
            string? name = init.TryGetProperty("agentInfo", out var info) ? AcpEventMapper.ReadString(info, "name") : null;
            return new(true, stage, "ACP handshake and session creation passed. Model access and tool execution require a chat test.", load, name);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, stage, "The harness timed out at this stage."); }
        catch (AgentException exception)
        { return new(false, stage, exception.Message); }
        catch (Exception)
        { return new(false, stage, "The harness could not complete this stage. Check the executable, native login and ACP command arguments."); }
        finally
        {
            // A probe owns only its temporary workspace, never a configured project directory.
            if (workspace != null && target.WorkingDirectory == null && Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }
}
