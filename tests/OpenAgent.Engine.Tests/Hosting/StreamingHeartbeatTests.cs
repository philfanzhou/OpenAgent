using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class StreamingHeartbeatTests
{
    [Fact]
    public async Task StartAsync_WritesHeartbeatWithoutPipelineChunks()
    {
        var body = new MemoryStream();
        var heartbeatWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var heartbeat = StreamingHeartbeat.Start(
            async cancellationToken =>
            {
                await body.WriteAsync(Encoding.UTF8.GetBytes(": heartbeat\n\n"), cancellationToken);
                heartbeatWritten.TrySetResult();
            },
            TimeSpan.FromMilliseconds(10),
            NullLogger.Instance,
            "chat-stream",
            "trace-id",
            CancellationToken.None);

        await heartbeatWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var responseBody = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains(": heartbeat", responseBody);
    }
}
