using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Core.Capabilities.Code;

internal sealed class RunnerClient(HttpClient http, IOptions<CodeExecutionOptions> options)
    : ICodeExecutor, IWorkspaceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
    {
        ExecutionLimits.Validate(request);
        CodeExecutionResult result = await PostAsync<CodeExecutionResult>(
            "execute", request, workspace: false, cancellationToken).ConfigureAwait(false);
        ExecutionLimits.ValidateFiles(result.Files);
        if (result.Stdout == null || result.Stderr == null
            || result.Stdout.Length > ExecutionLimits.MaxLogCharacters || result.Stderr.Length > ExecutionLimits.MaxLogCharacters)
        {
            throw new InvalidOperationException("The code Runner returned oversized logs.");
        }
        return result;
    }

    // ---- 会话工作区文件操作（/api/v1/workspace/*） ----

    public Task<WorkspaceListResult> ListAsync(
        string sessionKey, string? path, string? pattern, CancellationToken cancellationToken) =>
        PostWorkspaceAsync<WorkspaceListResult>(
            $"workspace/{sessionKey}/list",
            new WorkspaceListRequest { Path = path, Pattern = pattern },
            cancellationToken);

    public Task<WorkspaceReadResult> ReadAsync(
        string sessionKey, string path, int offsetLine, int limitLines, CancellationToken cancellationToken) =>
        PostWorkspaceAsync<WorkspaceReadResult>(
            $"workspace/{sessionKey}/read",
            new WorkspaceReadRequest { Path = path, OffsetLine = offsetLine, LimitLines = limitLines },
            cancellationToken);

    public Task<WorkspaceWriteResult> WriteAsync(
        string sessionKey, string path, string content, CancellationToken cancellationToken) =>
        PostWorkspaceAsync<WorkspaceWriteResult>(
            $"workspace/{sessionKey}/write",
            new WorkspaceWriteRequest { Path = path, Content = content },
            cancellationToken);

    public Task<WorkspaceEditResult> EditAsync(
        string sessionKey, string path, string oldString, string newString, bool replaceAll,
        CancellationToken cancellationToken) =>
        PostWorkspaceAsync<WorkspaceEditResult>(
            $"workspace/{sessionKey}/edit",
            new WorkspaceEditRequest { Path = path, OldString = oldString, NewString = newString, ReplaceAll = replaceAll },
            cancellationToken);

    public Task<WorkspaceBytesResult> ReadBytesAsync(
        string sessionKey, string path, CancellationToken cancellationToken) =>
        PostWorkspaceAsync<WorkspaceBytesResult>(
            $"workspace/{sessionKey}/bytes/read",
            new WorkspaceBytesRequest { Path = path },
            cancellationToken);

    public Task<WorkspaceWriteResult> UploadAsync(
        string sessionKey, string path, byte[] content, CancellationToken cancellationToken) =>
        PostWorkspaceAsync<WorkspaceWriteResult>(
            $"workspace/{sessionKey}/bytes/upload",
            new WorkspaceUploadRequest { Path = path, ContentBase64 = Convert.ToBase64String(content) },
            cancellationToken);

    private Task<TResult> PostWorkspaceAsync<TResult>(
        string relativeUrl, object request, CancellationToken cancellationToken) =>
        PostAsync<TResult>(relativeUrl, request, workspace: true, cancellationToken);

    private async Task<TResult> PostAsync<TResult>(
        string relativeUrl, object request, bool workspace, CancellationToken cancellationToken)
    {
        CodeExecutionOptions settings = options.Value;
        if (!settings.Enabled || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("The isolated Runner is not configured.");
        }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        using HttpRequestMessage message = new(HttpMethod.Post, new Uri(endpoint, $"/api/v1/{relativeUrl}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!workspace)
        {
            response.EnsureSuccessStatusCode();
        }
        byte[] content = await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new WorkspaceOperationException((int)response.StatusCode, ReadProblemDetail(content));
        }
        return JsonSerializer.Deserialize<TResult>(content, JsonOptions)
            ?? throw new InvalidOperationException("The Runner returned an empty result.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > ExecutionLimits.MaxWireBytes)
            {
                throw new InvalidOperationException("The Runner response exceeds the wire limit.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private static string ReadProblemDetail(byte[] content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("detail", out JsonElement detail)
                && detail.ValueKind == JsonValueKind.String
                ? detail.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
