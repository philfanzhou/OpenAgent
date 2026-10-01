using System.Text.Json;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.Code;

/// <summary>Shared model-facing results for isolated execution and workspace operations.</summary>
internal static class RunnerToolResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<ToolResult> CreateAsync(
        CodeExecutionResult result, IFileAssetService files, FileAssetScope scope, CancellationToken cancellationToken)
    {
        ExecutionLimits.ValidateFiles(result.Files);
        CodeExecutionArtifacts.PublishResult publish = await CodeExecutionArtifacts.PublishAsync(
            result, files, scope, cancellationToken).ConfigureAwait(false);
        return ToolResult.Text(JsonSerializer.Serialize(new
        {
            result.ExecutionId, result.ExitCode, result.TimedOut, result.Stdout, result.Stderr,
            result.SandboxReset, files = publish.Files,
            skippedFiles = publish.Skipped.Select(skipped => new { skipped.Name, skipped.Reason }).ToArray()
        }, JsonOptions));
    }

    internal static ToolResult FromException(Exception exception) => exception switch
    {
        WorkspaceOperationException error when error.StatusCode is 400 or 404 or 409 or 413 =>
            ToolResult.Error(error.Message, $"workspace_{error.StatusCode}"),
        ArgumentException or JsonException or AgentException =>
            ToolResult.Error(exception.Message, "invalid_arguments"),
        OperationCanceledException =>
            ToolResult.Error("The Runner request timed out.", "tool_timeout", timedOut: true),
        _ => ToolResult.Error(
            "The isolated Runner is unavailable or returned an invalid result. No host execution fallback is permitted.",
            "runner_unavailable",
            hint: "Retry after a short wait; if it persists, finish without Runner access.")
    };
}
