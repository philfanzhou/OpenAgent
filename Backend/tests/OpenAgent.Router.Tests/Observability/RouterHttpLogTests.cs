using System.Net.Http.Headers;
using OpenAgent.Router.Observability;
using Xunit;

namespace OpenAgent.Router.Tests.Observability;

public class RouterHttpLogTests
{
    [Fact]
    public void FormatRequestHeaders_RedactsSensitiveValuesAndIncludesContentHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/chat");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-secret");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "api-key-secret");
        request.Headers.TryAddWithoutValidation("X-Trace-Id", "trace-123");
        request.Content = new StringContent("message");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        string formatted = RouterHttpLog.FormatRequestHeaders(request);

        Assert.Contains("Authorization=[REDACTED]", formatted, StringComparison.Ordinal);
        Assert.Contains("X-Api-Key=[REDACTED]", formatted, StringComparison.Ordinal);
        Assert.Contains("X-Trace-Id=trace-123", formatted, StringComparison.Ordinal);
        Assert.Contains("Content-Type=application/json", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-secret", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-secret", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatBody_EscapesNewlinesAndTruncatesLargeBodies()
    {
        string formatted = RouterHttpLog.FormatBody("first\nnext" + new string('x', 16 * 1024 + 10));

        Assert.EndsWith("... [truncated]", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\\n", formatted, StringComparison.Ordinal);
    }
}
