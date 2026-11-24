using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Application.Tests.TestDoubles;

internal sealed class FileDeliveryRuntimeDriver(bool publish = true, string outcome = "success") : IAgentRuntimeDriver
{
    internal string? ExportedFileId { get; private set; }
    internal static byte[] Result => [0, 255, 128, 10, 13];

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RecordingWorkspaceFiles workspace = new();
        workspace.Outputs["outputs/result.bin"] = Result;
        RuntimeFilePreparation prepared = await invocation.PrepareFilesAsync!(workspace, cancellationToken);
        try
        {
            RuntimeTool export = Assert.Single(invocation.Tools, tool => tool.Name == "export_file");
            Dictionary<string, object?> exportArgs = new() { ["path"] = "outputs/result.bin" };
            yield return new() { Type = AgentStreamEventType.ToolCall, ToolName = export.Name, ToolCallId = "export", ToolArguments = exportArgs };
            string saved = await export.InvokeAsync(exportArgs, cancellationToken);
            ExportedFileId = JsonDocument.Parse(saved).RootElement.GetProperty("fileId").GetString();
            yield return new() { Type = AgentStreamEventType.ToolResult, ToolName = export.Name, ToolCallId = "export", Content = saved };
            if (publish)
            {
                RuntimeTool deliver = Assert.Single(invocation.Tools, tool => tool.Name == "publish_files");
                Dictionary<string, object?> deliverArgs = new() { ["fileIds"] = new[] { ExportedFileId! } };
                yield return new() { Type = AgentStreamEventType.ToolCall, ToolName = deliver.Name, ToolCallId = "publish", ToolArguments = deliverArgs };
                string delivered = await deliver.InvokeAsync(deliverArgs, cancellationToken);
                yield return new() { Type = AgentStreamEventType.ToolResult, ToolName = deliver.Name, ToolCallId = "publish", Content = delivered };
            }
            yield return new() { Type = AgentStreamEventType.Content, Content = "Prepared result." };
            if (outcome == "failed")
            {
                throw new InvalidOperationException("Model disconnected.");
            }
            if (outcome == "cancelled")
            {
                throw new OperationCanceledException("Turn cancelled.");
            }
            await prepared.Lease.DisposeAsync();
            yield return new() { Type = AgentStreamEventType.Usage };
            await invocation.CommitAsync!(cancellationToken);
        }
        finally
        {
            await prepared.Lease.DisposeAsync();
        }
    }
}
