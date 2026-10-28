using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenAgent.Contracts.Capabilities;

/// <summary>
/// Structured outcome of a single tool invocation. <see cref="Content"/> is the
/// model-facing text; the remaining fields carry error and truncation semantics
/// that the execution layer renders into the final wire payload.
/// </summary>
/// <remarks>
/// Errors are returned as an in-band JSON envelope
/// <c>{"error":"...","code":"...","hint":"..."}</c> (plus <c>timedOut</c>/<c>errorId</c>
/// where applicable) instead of throwing, so a failing tool cannot abort the
/// agent run and the model can read the failure and adjust.
/// </remarks>
public sealed record ToolResult(
    string Content,
    bool IsError = false,
    bool IsTruncated = false,
    string? TruncationHint = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        // Error text travels back to the model and may be shown to users;
        // keep raw characters readable instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static ToolResult Text(string content) => new(content);

    /// <summary>
    /// Builds an error result whose content is the unified error envelope.
    /// </summary>
    /// <param name="error">What failed, phrased so the model can correct itself.</param>
    /// <param name="code">Stable machine-readable error code, e.g. <c>invalid_arguments</c>.</param>
    /// <param name="hint">Optional correction guidance shown next to the error.</param>
    /// <param name="timedOut">Marks transport/deadline timeouts (compat marker).</param>
    /// <param name="errorId">Correlation id for server-side logs, when the raw
    /// exception must stay out of the model-visible payload.</param>
    public static ToolResult Error(
        string error,
        string? code = null,
        string? hint = null,
        bool timedOut = false,
        string? errorId = null)
    {
        Dictionary<string, object?> envelope = new() { ["error"] = error };
        if (code != null)
        {
            envelope["code"] = code;
        }
        if (hint != null)
        {
            envelope["hint"] = hint;
        }
        if (timedOut)
        {
            envelope["timedOut"] = true;
        }
        if (errorId != null)
        {
            envelope["errorId"] = errorId;
        }
        return new ToolResult(
            JsonSerializer.Serialize(envelope, EnvelopeOptions),
            IsError: true);
    }

    public static implicit operator ToolResult(string content) => new(content);
}
