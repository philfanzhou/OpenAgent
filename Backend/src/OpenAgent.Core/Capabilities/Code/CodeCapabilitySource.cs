using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Capabilities;
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
                "Execute Python (default) or JavaScript (\"language\":\"javascript\", ESM entry main.mjs, "
                + "built-in Node modules only) in an isolated sandbox: no network, no package installs, no credentials. "
                + "Python ships pandas, matplotlib, openpyxl, XlsxWriter, python-pptx and Pillow. "
                + "Mount conversation files read-only via inputFiles at /input/<name>; main.py/main.mjs are reserved. "
                + "Write deliverables under /output (max 8 files, 10 MiB each, 20 MiB total) and print concise results — "
                + "very verbose output is truncated with a marker, so print only what matters. "
                + "Outputs of any type are collected; only storable types (e.g. html, css, md, csv, json, png, pdf, pptx, xlsx) "
                + "are registered as files — others are listed under skippedFiles with a reason, so rename or convert them. "
                + "Calls in one conversation share a persistent sandbox: files under /work survive between calls and "
                + "even sandbox restarts (host-backed workspace; /tmp and /input are per-sandbox scratch and clear when "
                + "the sandbox is rebuilt). After roughly two hours of inactivity the workspace is reclaimed and the "
                + "result carries sandboxReset=true, meaning earlier files are gone. "
                + "/work is the conversation workspace: read/edit/write/list_workspace_file tools operate on the same "
                + "files — prefer them for inspection and small edits, use code for computation. "
                + "On failure inspect exitCode/stderr and retry with fixes. "
                + "Deliver returned files with publish_files.",
                """{"type":"object","properties":{"code":{"type":"string","description":"Full source code to execute; entry point is the code itself (main.py/main.mjs are reserved names)"},"language":{"type":"string","enum":["python","javascript"],"description":"Execution language; defaults to python."},"inputFiles":{"type":"array","maxItems":8,"items":{"type":"object","properties":{"fileId":{"type":"string","description":"File asset to mount read-only"},"name":{"type":"string","description":"Mount name under /input"}},"required":["fileId","name"],"additionalProperties":false}}},"required":["code"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "code-execution",
                (arguments, token) => ExecuteAsync(agentId, user, scope, arguments, token))]);
    }

    private async Task<ToolResult> ExecuteAsync(string agentId, IAgentUserContext user, FileAssetScope scope,
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
            string code = arguments.TryGetValue("code", out object? value) ? value?.ToString() ?? string.Empty : string.Empty;
            string language = arguments.TryGetValue("language", out object? languageValue)
                ? ExecutionLanguage.Normalize(languageValue?.ToString())
                    ?? throw new ArgumentException("Unsupported code execution language.")
                : ExecutionLanguage.Python;
            List<InputFile> inputs = arguments.TryGetValue("inputFiles", out object? input)
                ? JsonSerializer.Deserialize<List<InputFile>>(JsonSerializer.Serialize(input), JsonOptions) ?? [] : [];
            var request = new CodeExecutionRequest
            {
                Code = code,
                Language = language,
                // Same conversation reuses one sandbox workspace (mounted inputs
                // persist); unsafe ids fall back to a stateless workspace.
                SessionKey = ExecutionLimits.IsSafeSessionKey(scope.ConversationId)
                    ? scope.ConversationId
                    : null
            };
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
            CodeExecutionArtifacts.PublishResult publish = await CodeExecutionArtifacts.PublishAsync(
                result, files, scope, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                result.ExecutionId, result.ExitCode, result.TimedOut, result.Stdout, result.Stderr,
                result.SandboxReset, files = publish.Files,
                skippedFiles = publish.Skipped.Select(skipped => new { skipped.Name, skipped.Reason }).ToArray()
            }, JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or AgentException)
        {
            return ToolResult.Error(exception.Message, "invalid_arguments");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return ToolResult.Error(
                "The isolated Runner is unavailable or returned an invalid result. No host execution fallback is permitted.",
                "runner_unavailable",
                hint: "Retry after a short wait; if it persists, finish without code execution.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error("The Runner request timed out.", "tool_timeout", timedOut: true);
        }
    }

    private sealed class InputFile
    {
        public string FileId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}

