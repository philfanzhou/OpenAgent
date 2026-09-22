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
        CodeExecutionOptions settings = options.Value;
        if (!settings.Enabled || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("The isolated code Runner is not configured.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/api/v1/execute"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > ExecutionLimits.MaxWireBytes)
            {
                throw new InvalidOperationException("The code Runner response exceeds the wire limit.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
        }
        CodeExecutionResult result = JsonSerializer.Deserialize<CodeExecutionResult>(
            buffer.ToArray(), JsonOptions)
            ?? throw new InvalidOperationException("The code Runner returned an empty result.");
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

    private async Task<TResult> PostWorkspaceAsync<TResult>(
        string relativeUrl,
        object request,
        CancellationToken cancellationToken)
    {
        CodeExecutionOptions settings = options.Value;
        if (!settings.Enabled || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("The isolated Runner is not configured.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, $"/api/v1/{relativeUrl}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await ReadProblemDetailAsync(response, deadline.Token).ConfigureAwait(false);
            throw new WorkspaceOperationException((int)response.StatusCode, detail);
        }
        return await response.Content.ReadFromJsonAsync<TResult>(JsonOptions, deadline.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Runner returned an empty workspace result.");
    }

    private static async Task<string> ReadProblemDetailAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("detail", out JsonElement detail)
                && detail.ValueKind == JsonValueKind.String
                ? detail.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return string.Empty;
        }
    }
}
