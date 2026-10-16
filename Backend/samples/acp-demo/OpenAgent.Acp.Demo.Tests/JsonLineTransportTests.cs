using System.Text;
using System.Text.Json;
using OpenAgent.Acp.Demo.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Acp.Demo.Tests;

public sealed class JsonLineTransportTests
{
    [Fact]
    public async Task ReadAsync_FragmentedUnicodeAndMultipleFrames_PreservesContent()
    {
        byte[] input = Encoding.UTF8.GetBytes("{\"text\":\"你好🥐\"}\r\n{\"ok\":true}\n");
        await using JsonLineTransport transport = new(new FragmentedReadStream(input), new MemoryStream());
        byte[]? first = await transport.ReadAsync();
        byte[]? second = await transport.ReadAsync();
        Assert.NotNull(first);
        Assert.NotNull(second);
        using JsonDocument firstMessage = JsonDocument.Parse(first);
        using JsonDocument secondMessage = JsonDocument.Parse(second);
        Assert.Equal("你好🥐", firstMessage.RootElement.GetProperty("text").GetString());
        Assert.True(secondMessage.RootElement.GetProperty("ok").GetBoolean());
        Assert.Null(await transport.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_PartialFrameAtEof_ReportsTruncatedMessage()
    {
        await using JsonLineTransport transport = new(new MemoryStream("{}"u8.ToArray()), new MemoryStream());
        await Assert.ThrowsAsync<EndOfStreamException>(() => transport.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_OversizedUnterminatedFrame_FailsWithBoundedBuffer()
    {
        await using JsonLineTransport transport = new(new MemoryStream(new byte[1024 * 1024 + 1]), new MemoryStream());
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.ReadAsync());
    }

    [Fact]
    public async Task WriteAsync_CancelledAfterFrameStarts_CompletesNewlineBeforeCancelling()
    {
        using CancellationTokenSource cancellation = new();
        CancellingWriteStream output = new(cancellation);
        await using JsonLineTransport transport = new(new MemoryStream(), output);
        await transport.WriteAsync(new { text = "complete frame" }, cancellation.Token);
        byte[] frame = output.ToArray();
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal((byte)'\n', frame[^1]);
        using JsonDocument document = JsonDocument.Parse(frame);
        Assert.Equal("complete frame", document.RootElement.GetProperty("text").GetString());
    }
}
