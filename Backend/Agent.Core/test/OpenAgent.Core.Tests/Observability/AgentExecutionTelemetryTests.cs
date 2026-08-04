using Microsoft.Extensions.Logging;
using OpenAgent.Core.Observability;
using Xunit;

namespace OpenAgent.Core.Tests.Observability;

public class AgentExecutionTelemetryTests
{
    [Fact]
    public void RecordMetricsAndLog_EmitsExecutionSummary()
    {
        var logger = new CaptureLogger<AgentExecutionTelemetry>();
        var telemetry = new AgentExecutionTelemetry("agent-1", "conversation-1", "tenant-1", "trace-1", streaming: true);

        telemetry.RecordTurn(1, 12.3, toolCallCount: 2);
        telemetry.RecordToolCall("search", "mcp", "success", 4.5);
        telemetry.MarkCompleted();

        telemetry.RecordMetricsAndLog(logger, "agent-service-stream");

        var entry = Assert.Single(logger.Entries.Where(e => e.LogLevel == LogLevel.Information));
        Assert.Equal(
            "AgentExecutionSummary Status={Status}, FailureStage={FailureStage}, ErrorCode={ErrorCode}, TurnCount={TurnCount}, ToolCallCount={ToolCallCount}, DurationMs={DurationMs}",
            entry.Properties["{OriginalFormat}"]);
        Assert.Equal("success", entry.Properties["Status"]);
        Assert.Equal(1, entry.Properties["ToolCallCount"]);
    }
}
