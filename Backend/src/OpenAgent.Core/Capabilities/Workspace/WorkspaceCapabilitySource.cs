using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities.Workspace;

/// <summary>
/// 会话工作区工具：宿主侧 session-&lt;key&gt;/work 目录（bind-mount 进持久会话沙箱 /work），
/// 与 execute_code 共享同一份文件状态——读改写跑交付的闭环载体。gating 与代码执行一致
/// （宿主 CodeExecution:Enabled + 每代理开关 + 租户/用户/会话三要素）。
/// 编辑范式采用精确字符串替换（Claude Code Edit 式）：只依赖 JSON schema，
/// 跨 provider 可复制；语法约束补丁（Codex apply_patch）依赖供应商专属解码，不采用。
/// </summary>
internal sealed class WorkspaceCapabilitySource(
    IWorkspaceClient workspace,
    IFileAssetService files,
    FileAssetExecutionContext context,
    AgentAuthorizationGate authorization,
    IOptions<CodeExecutionOptions> options) : ICapabilitySource
{
    private const string ListName = "list_workspace_files";
    private const string ReadName = "read_workspace_file";
    private const string WriteName = "write_workspace_file";
    private const string EditName = "edit_workspace_file";
    private const string ExportName = "export_workspace_file";

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
        return Task.FromResult<IReadOnlyList<CapabilityDefinition>>(
        [
            new CapabilityDefinition(
                ListName,
                "List files and directories in the current conversation's sandbox workspace (shared with execute_code: "
                + "files you write under /work there appear here, and vice versa; state survives sandbox restarts until "
                + "roughly two hours of inactivity). "
                + "Optional path selects a subdirectory (default: the workspace root), optional pattern filters entry names "
                + "with * and ? wildcards. Use it before read/edit when unsure which files exist.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Subdirectory relative to the workspace root; omit for the root"},"pattern":{"type":"string","description":"Wildcard filter (* and ?) applied to entry names"}},"additionalProperties":false}""",
                AgentResourceType.Tool,
                "code-execution",
                (arguments, token) => InvokeGuardedAsync(agentId, user, ListName, ListAsync, arguments, token),
                Concurrency: ToolConcurrency.ReadOnly),
            new CapabilityDefinition(
                ReadName,
                "Read a text file from the conversation workspace with cat -n style line numbers ('line<tab>content'). "
                + "Defaults to the first 500 lines; use offset/limit to page through larger files instead of re-reading everything. "
                + "The result reports totalLines/startLine/endLine and truncated, so you can request the exact next range. "
                + "Always read a file before editing it — edit_workspace_file requires the exact current text.",
                """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"offset":{"type":"number","description":"First line to return, 1-based; defaults to 1"},"limit":{"type":"number","description":"Number of lines to return (1..2000); defaults to 500"}},"required":["path"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "code-execution",
                (arguments, token) => InvokeGuardedAsync(agentId, user, ReadName, ReadAsync, arguments, token),
                Concurrency: ToolConcurrency.ReadOnly),
            new CapabilityDefinition(
                WriteName,
                "Create or completely overwrite a UTF-8 text file in the conversation workspace (shared with execute_code /work). "
                + "Unlike write_file (which always creates a new immutable file asset), this writes the mutable workspace: "
                + "use it for files you plan to edit, run, or iterate on with execute_code. "
                + "Sending the full content replaces whatever was there.",
                """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"content":{"type":"string","description":"Complete UTF-8 text content; replaces any existing content"}},"required":["path","content"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "code-execution",
                (arguments, token) => InvokeGuardedAsync(agentId, user, WriteName, WriteAsync, arguments, token),
                Concurrency: ToolConcurrency.Exclusive),
            new CapabilityDefinition(
                EditName,
                "Make a precise replacement in a workspace file: old_string must match the file's exact current text "
                + "(including whitespace and indentation) and must be unique unless replace_all=true. "
                + "Always read_workspace_file the file first and copy the text verbatim. "
                + "Empty new_string deletes the match. On failure the error tells you whether the text was not found "
                + "or occurred multiple times — extend old_string with surrounding lines to disambiguate, or re-read the file.",
                """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"old_string":{"type":"string","description":"Exact text to replace; must be unique unless replace_all is true"},"new_string":{"type":"string","description":"Replacement text; empty string deletes the match"},"replace_all":{"type":"boolean","description":"Replace every occurrence of old_string; defaults to false"}},"required":["path","old_string","new_string"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "code-execution",
                (arguments, token) => InvokeGuardedAsync(agentId, user, EditName, EditAsync, arguments, token),
                Concurrency: ToolConcurrency.Exclusive),
            new CapabilityDefinition(
                ExportName,
                "Copy a workspace file into immutable conversation file storage and return its fileId — "
                + "the bridge from the editable workspace to delivery: hand the fileId to publish_files for user download, "
                + "or to compress_files/read_file. The workspace copy stays untouched.",
                """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"fileName":{"type":"string","description":"Asset file name; defaults to the workspace file name"}},"required":["path"],"additionalProperties":false}""",
                AgentResourceType.Tool,
                "code-execution",
                (arguments, token) => InvokeGuardedAsync(agentId, user, ExportName, ExportAsync, arguments, token),
                Concurrency: ToolConcurrency.Exclusive)
        ]);
    }

    /// <summary>调用时复查授权（含长轮次之后的权限变化），与 CodeCapabilitySource 口径一致。</summary>
    private async Task<ToolResult> InvokeGuardedAsync(
        string agentId,
        IAgentUserContext user,
        string toolName,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<ToolResult>> operation,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        foreach ((AgentResourceType type, string id) in new[]
        {
            (AgentResourceType.Tool, "code-execution"),
            (AgentResourceType.Tool, toolName),
            (AgentResourceType.Function, toolName)
        })
        {
            if (!await authorization.IsAvailableAsync(agentId, type, id, user, cancellationToken).ConfigureAwait(false))
            {
                return ToolResult.Error(
                    "Workspace access is not authorized for this agent.",
                    "unauthorized",
                    hint: "Ask the administrator to grant the code-execution capability.");
            }
        }
        return await operation(arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> ListAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? sessionKey = SessionKey();
        if (sessionKey == null)
        {
            return ContextUnavailable();
        }
        try
        {
            WorkspaceListResult result = await workspace.ListAsync(
                sessionKey, ReadString(arguments, "path"), ReadString(arguments, "pattern"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                path = result.Path,
                entries = result.Entries.Select(entry => new
                {
                    entry.Path,
                    type = entry.IsDirectory ? "directory" : "file",
                    bytes = entry.LengthBytes
                }).ToArray()
            }, JsonOptions);
        }
        catch (Exception exception)
        {
            return MapRunnerError(exception);
        }
    }

    private async Task<ToolResult> ReadAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? path = ReadString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolResult.Error("'path' is a required argument.", "invalid_arguments");
        }
        string? sessionKey = SessionKey();
        if (sessionKey == null)
        {
            return ContextUnavailable();
        }
        int offset = ReadInt(arguments, "offset") ?? 1;
        int limit = ReadInt(arguments, "limit") ?? 500;
        if (offset < 1 || limit < 1 || limit > 2_000)
        {
            return ToolResult.Error(
                "'offset' must be >= 1 and 'limit' must be within 1..2000.",
                "invalid_arguments");
        }
        try
        {
            WorkspaceReadResult result = await workspace.ReadAsync(
                sessionKey, path, offset, limit, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                result.Path,
                result.TotalLines,
                result.StartLine,
                result.EndLine,
                result.Truncated,
                content = result.Content
            }, JsonOptions);
        }
        catch (Exception exception)
        {
            return MapRunnerError(exception);
        }
    }

    private async Task<ToolResult> WriteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? path = ReadString(arguments, "path");
        string? content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolResult.Error("'path' is a required argument.", "invalid_arguments");
        }
        if (content == null)
        {
            return ToolResult.Error("'content' is a required argument (use an empty string to clear the file).",
                "invalid_arguments");
        }
        string? sessionKey = SessionKey();
        if (sessionKey == null)
        {
            return ContextUnavailable();
        }
        try
        {
            WorkspaceWriteResult result = await workspace.WriteAsync(
                sessionKey, path, content, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                result.Path,
                result.LengthBytes
            }, JsonOptions);
        }
        catch (Exception exception)
        {
            return MapRunnerError(exception);
        }
    }

    private async Task<ToolResult> EditAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? path = ReadString(arguments, "path");
        string? oldString = ReadString(arguments, "old_string");
        string? newString = ReadString(arguments, "new_string");
        bool replaceAll = ReadBool(arguments, "replace_all");
        if (string.IsNullOrWhiteSpace(path) || oldString == null || newString == null)
        {
            return ToolResult.Error(
                "'path', 'old_string' and 'new_string' are required arguments.",
                "invalid_arguments",
                hint: "Copy old_string verbatim from read_workspace_file output (mind tabs and indentation).");
        }
        string? sessionKey = SessionKey();
        if (sessionKey == null)
        {
            return ContextUnavailable();
        }
        try
        {
            WorkspaceEditResult result = await workspace.EditAsync(
                sessionKey, path, oldString, newString, replaceAll, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                result.Path,
                result.Replacements
            }, JsonOptions);
        }
        catch (Exception exception)
        {
            return MapRunnerError(exception);
        }
    }

    private async Task<ToolResult> ExportAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string? path = ReadString(arguments, "path");
        string? fileName = ReadString(arguments, "fileName");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolResult.Error("'path' is a required argument.", "invalid_arguments");
        }
        FileAssetScope? scope = context.Scope;
        string? sessionKey = SessionKey();
        if (scope == null || sessionKey == null)
        {
            return ContextUnavailable();
        }
        try
        {
            WorkspaceBytesResult bytes = await workspace.ReadBytesAsync(
                sessionKey, path, cancellationToken).ConfigureAwait(false);
            await using var input = new MemoryStream(Convert.FromBase64String(bytes.ContentBase64), writable: false);
            FileAsset asset = await files.UploadAsync(
                new FileAssetCreateRequest
                {
                    FileName = string.IsNullOrWhiteSpace(fileName)
                        ? Path.GetFileName(path.Replace('\\', '/'))
                        : fileName,
                    Source = FileAssetSource.Agent
                },
                input,
                scope,
                cancellationToken).ConfigureAwait(false);
            await files.EnsureReferencesAsync([asset.FileId], scope, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                fileId = asset.FileId,
                fileName = asset.FileName,
                length = asset.Length,
                sourcePath = path
            }, JsonOptions);
        }
        catch (OpenAgent.Contracts.Security.AgentException exception)
        {
            return ToolResult.Error(exception.Message, "invalid_request");
        }
        catch (Exception exception)
        {
            return MapRunnerError(exception);
        }
    }

    private string? SessionKey() =>
        context.Scope is { } scope && ExecutionLimits.IsSafeSessionKey(scope.ConversationId)
            ? scope.ConversationId
            : null;

    private ToolResult ContextUnavailable() =>
        ToolResult.Error(
            "The workspace requires an isolated Runner and a conversation context; neither is available for this request.",
            "unavailable_context");

    /// <summary>Runner 领域错误 → 可行动信封：409/404/400 的 detail 本身就是修正指引。</summary>
    private static ToolResult MapRunnerError(Exception exception) => exception switch
    {
        WorkspaceOperationException error when error.StatusCode is 400 or 404 or 409 or 413 =>
            ToolResult.Error(error.Message, $"workspace_{error.StatusCode}"),
        WorkspaceOperationException => ToolResult.Error(
            "The isolated Runner rejected the workspace operation.",
            "runner_unavailable",
            hint: "Retry after a short wait; if it persists, finish without workspace access."),
        OperationCanceledException => ToolResult.Error(
            "The Runner workspace request timed out.", "tool_timeout", timedOut: true),
        _ => ToolResult.Error(
            "The isolated Runner is unavailable; no host fallback is permitted.",
            "runner_unavailable",
            hint: "Retry after a short wait; if it persists, finish without workspace access.")
    };

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out object? value) ? value?.ToString() : null;

    private static int? ReadInt(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        string? value = ReadString(arguments, name);
        return value != null && int.TryParse(value, out int parsed) ? parsed : null;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out object? value)
            && value?.ToString() is { } text
            && bool.TryParse(text, out bool parsed)
            && parsed;
}
