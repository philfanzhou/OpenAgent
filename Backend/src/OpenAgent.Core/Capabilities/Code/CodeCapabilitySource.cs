using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities.Code;

internal sealed class CodeCapabilitySource(
    ICodeExecutor executor,
    IFileAssetService files,
    FileAssetExecutionContext context,
    AgentAuthorizationGate authorization,
    IOptions<CodeExecutionOptions> options) : ICapabilitySource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int _executions;

    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId, AgentConfig config, IAgentUserContext user, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || config.CodeExecution?.Enabled != true || context.Scope == null
            || string.IsNullOrWhiteSpace(context.Scope.TenantId)
            || string.IsNullOrWhiteSpace(context.Scope.UserId)
            || string.IsNullOrWhiteSpace(context.Scope.ConversationId))
        {
            return Task.FromResult<IReadOnlyList<CapabilityDefinition>>([]);
        }
        FileAssetScope scope = context.Scope;
        return Task.FromResult<IReadOnlyList<CapabilityDefinition>>([
            new CapabilityDefinition(
                "execute_code",
                "Execute Python in an isolated, non-root Bubblewrap sandbox with no network. "
                + "Available libraries: python-pptx, openpyxl, XlsxWriter, pandas, matplotlib, Pillow. "
                + "Use inputFiles to mount authorized conversation files read-only at /input/<name>; main.py is reserved. "
                + "Write deliverables directly under /output (up to 8 files, 10 MiB each, 20 MiB total). "
                + "Print concise results. Inspect exitCode and stderr, then fix failures with another call. "
                + "Each call starts fresh: pass previous output fileIds as inputFiles to continue editing. "
                + "Returned files are registered; use publish_files to deliver selected fileIds. "
                + "Use fixed templates where possible. Reopen generated documents to validate contents. "
                + "No host tools, credentials, pip installs, or internet access are available inside Python.",
                """{"type":"object","properties":{"code":{"type":"string"},"inputFiles":{"type":"array","maxItems":8,"items":{"type":"object","properties":{"fileId":{"type":"string"},"name":{"type":"string"}},"required":["fileId","name"],"additionalProperties":false}}},"required":["code"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "code-execution",
                (arguments, token) => ExecuteAsync(agentId, user, scope, arguments, token))]);
    }

    private async Task<string> ExecuteAsync(string agentId, IAgentUserContext user, FileAssetScope scope,
        IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        // Recheck authorization at invocation, including calls after a long model turn.
        foreach ((AgentResourceType type, string id) in new[]
        {
            (AgentResourceType.Tool, "code-execution"),
            (AgentResourceType.Tool, "execute_code"),
            (AgentResourceType.Function, "execute_code")
        })
        {
            if (!await authorization.IsAvailableAsync(agentId, type, id, user, cancellationToken).ConfigureAwait(false))
            {
                throw new AgentException(AgentErrorCode.InvalidRequest, "Code execution is not authorized.");
            }
        }
        try
        {
            if (Interlocked.Increment(ref _executions) > options.Value.MaxExecutionsPerRequest)
            {
                return "{\"error\":\"Code execution budget exhausted for this request.\"}";
            }
            string code = arguments.TryGetValue("code", out object? value) ? value?.ToString() ?? string.Empty : string.Empty;
            List<InputFile> inputs = arguments.TryGetValue("inputFiles", out object? input)
                ? JsonSerializer.Deserialize<List<InputFile>>(JsonSerializer.Serialize(input), JsonOptions) ?? [] : [];
            var request = new CodeExecutionRequest { Code = code };
            ExecutionLimits.Validate(request);
            if (inputs.Count > ExecutionLimits.MaxFiles)
            {
                throw new ArgumentException("Too many input files.");
            }
            long total = 0;
            foreach (InputFile item in inputs)
            {
                if (item == null || !ExecutionLimits.IsSafeFileName(item.Name) || string.IsNullOrWhiteSpace(item.FileId))
                {
                    throw new ArgumentException("Invalid input file reference.");
                }
                FileAsset? asset = await files.GetReferencedAsync(item.FileId, scope, cancellationToken).ConfigureAwait(false);
                if (asset == null || asset.State != FileAssetState.Ready)
                {
                    throw new ArgumentException("Input file is unavailable in this conversation.");
                }
                total += asset.Length;
                if (asset.Length > ExecutionLimits.MaxFileBytes || total > ExecutionLimits.MaxTotalFileBytes)
                {
                    throw new ArgumentException("Input files exceed the execution limit.");
                }
                FileAssetContent content = await files.ReadAsync(item.FileId, scope, cancellationToken,
                    ExecutionLimits.MaxFileBytes).ConfigureAwait(false);
                request.Files.Add(new ExecutionFile { Name = item.Name, Content = content.Data });
            }
            ExecutionLimits.Validate(request);
            CodeExecutionResult result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            ExecutionLimits.ValidateFiles(result.Files);
            foreach (ExecutionFile output in result.Files)
            {
                _ = GetMediaType(output.Name);
                if (output.Content.Length == 0)
                {
                    throw new ArgumentException("Generated files must not be empty.");
                }
            }
            var artifacts = new List<object>();
            foreach (ExecutionFile output in result.Files)
            {
                await using var stream = new MemoryStream(output.Content, writable: false);
                FileAsset asset = await files.UploadAsync(new FileAssetCreateRequest
                {
                    FileName = output.Name,
                    MediaType = GetMediaType(output.Name),
                    Source = FileAssetSource.Agent
                }, stream, scope, cancellationToken).ConfigureAwait(false);
                await files.EnsureReferencesAsync([asset.FileId], scope, cancellationToken).ConfigureAwait(false);
                artifacts.Add(new { fileId = asset.FileId, fileName = asset.FileName, length = asset.Length });
            }
            return JsonSerializer.Serialize(new
            {
                result.ExecutionId, result.ExitCode, result.TimedOut, result.Stdout, result.Stderr, files = artifacts
            }, JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or AgentException)
        {
            return JsonSerializer.Serialize(new { error = exception.Message }, JsonOptions);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return "{\"error\":\"The isolated Runner is unavailable or returned an invalid result. No host execution fallback is permitted.\"}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "{\"error\":\"The Runner request timed out.\"}";
        }
    }

    private static string GetMediaType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        _ => throw new ArgumentException("Unsupported generated file type.")
    };

    private sealed class InputFile
    {
        public string FileId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
