using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Core.Capabilities.Code;

internal sealed class RunnerClient(HttpClient http, IOptions<CodeExecutionOptions> options) : ICodeExecutor
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
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/v1/execute"));
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
}
