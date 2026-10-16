using System.Text;
using System.Text.Json;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using OpenAgent.Runtime.Acp.Transport;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Transport;

public sealed class AcpStreamTransportTests
{
    [Fact]
    public async Task ReadAsync_FragmentedUnicodeAndMultipleFrames_PreservesBodies()
    {
        await using AcpStreamTransport transport = new(
            new FragmentedReadStream(Encoding.UTF8.GetBytes("{\"text\":\"你好🥐\"}\r\n{}\n")), new MemoryStream());

        byte[]? first = await transport.ReadAsync();
        Assert.NotNull(first);
        using JsonDocument document = JsonDocument.Parse(first);
        Assert.Equal("你好🥐", document.RootElement.GetProperty("text").GetString());
        Assert.DoesNotContain((byte)'\r', first);
        Assert.Equal("{}", Encoding.UTF8.GetString((await transport.ReadAsync())!));
        Assert.Null(await transport.ReadAsync());
        await transport.Completion;
    }

    [Theory]
    [InlineData("{}", 10, typeof(EndOfStreamException))]
    [InlineData("12345\n", 4, typeof(InvalidDataException))]
    public async Task ReadAsync_InvalidFrame_FaultsReadAndCompletion(string input, int limit, Type expected)
    {
        await using AcpStreamTransport transport = new(new MemoryStream(Encoding.UTF8.GetBytes(input)),
            new MemoryStream(), new AcpTransportOptions { MaxMessageBytes = limit });

        Exception? failure = await Record.ExceptionAsync(() => transport.ReadAsync());
        Assert.NotNull(failure);
        Assert.Equal(expected, failure.GetType());
        Exception? completion = await Record.ExceptionAsync(() => transport.Completion);
        Assert.Same(failure, completion);
    }

    [Fact]
    public async Task ReadAsync_ByteLimitWithUnicode_CountsBytesInsteadOfCharacters()
    {
        await using AcpStreamTransport transport = new(new MemoryStream(Encoding.UTF8.GetBytes("你好\n")),
            new MemoryStream(), new AcpTransportOptions { MaxMessageBytes = 5 });
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.Completion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public async Task WriteAsync_ConcurrentWriters_ProducesIntactFrames(int count)
    {
        using YieldingWriteStream output = new();
        await using AcpStreamTransport transport = new(new MemoryStream(), output,
            new AcpTransportOptions { LeaveOpen = true });

        await Task.WhenAll(Enumerable.Range(0, count).Select(index => transport.WriteAsync(
            Encoding.UTF8.GetBytes($"{{\"id\":{index}}}"))));

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(count, lines.Length);
        int[] ids = lines.Select(line =>
        {
            using JsonDocument message = JsonDocument.Parse(line);
            return message.RootElement.GetProperty("id").GetInt32();
        }).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, count), ids);
    }

    [Fact]
    public async Task WriteAsync_CancelledAfterStart_PreservesCompleteFrame()
    {
        using CancellationTokenSource cancellation = new();
        using CancellingWriteStream output = new(cancellation);
        await using AcpStreamTransport transport = new(new MemoryStream(), output,
            new AcpTransportOptions { LeaveOpen = true });

        await transport.WriteAsync("{}"u8.ToArray(), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("{}\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task WriteAsync_PreCancelled_DoesNotWrite()
    {
        using MemoryStream output = new();
        await using AcpStreamTransport transport = new(new MemoryStream(), output,
            new AcpTransportOptions { LeaveOpen = true });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.WriteAsync("{}"u8.ToArray(), cancellation.Token));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}\n{}")]
    [InlineData("{}\r")]
    [InlineData("12345")]
    public async Task WriteAsync_InvalidBody_DoesNotWrite(string body)
    {
        using MemoryStream output = new();
        await using AcpStreamTransport transport = new(new MemoryStream(), output,
            new AcpTransportOptions { MaxMessageBytes = 4, LeaveOpen = true });

        Assert.NotNull(await Record.ExceptionAsync(() => transport.WriteAsync(Encoding.UTF8.GetBytes(body))));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_StreamOwnershipAndRepeatedCalls_AreExplicit(bool leaveOpen)
    {
        using MemoryStream stream = new();
        AcpStreamTransport transport = new(stream, stream, new AcpTransportOptions { LeaveOpen = leaveOpen });

        await transport.DisposeAsync();
        await transport.DisposeAsync();

        Assert.Equal(leaveOpen, stream.CanRead);
        await transport.Completion;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.ReadAsync());
    }
}
