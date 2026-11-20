using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Capabilities.Extensions;
using System.Net;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Capabilities.Files.Services;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Files;

public sealed class FileAssetUrlDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_ReadsResponseAndInfersExtension()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("hello"u8.ToArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/plain") }
            }
        });
        FileAssetUrlDownloader downloader = CreateDownloader(handler);

        DownloadedFile result = await downloader.DownloadAsync(
            "https://public.example/download",
            CancellationToken.None);

        Assert.Equal("download.txt", result.FileName);
        Assert.Equal("text/plain", result.MediaType);
        Assert.Equal("hello"u8.ToArray(), result.Content);
    }

    [Fact]
    public async Task DownloadAsync_RejectsPrivateLiteralAddress()
    {
        FileAssetUrlDownloader downloader = CreateDownloader(new StubHandler(_ =>
            throw new InvalidOperationException("The request must not be sent.")));

        OpenAgent.Contracts.Security.AgentException exception = await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(
            () => downloader.DownloadAsync("http://127.0.0.1/private.txt", CancellationToken.None));

        Assert.Equal(OpenAgent.Contracts.Requests.AgentErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Contains("内网", exception.Message);
    }

    [Fact]
    public async Task DownloadAsync_RejectsOversizedStreamingResponse()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new RepeatingStream(20))
        });
        FileAssetUrlDownloader downloader = CreateDownloader(handler, maxBytes: 10);

        OpenAgent.Contracts.Security.AgentException exception = await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(
            () => downloader.DownloadAsync("https://public.example/file.txt", CancellationToken.None));

        Assert.Contains("大小限制", exception.Message);
    }

    [Fact]
    public async Task DownloadAsync_ValidatesRedirectTarget()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1/private.txt") }
        });
        FileAssetUrlDownloader downloader = CreateDownloader(handler);

        OpenAgent.Contracts.Security.AgentException exception = await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(
            () => downloader.DownloadAsync("https://public.example/file.txt", CancellationToken.None));

        Assert.Equal(OpenAgent.Contracts.Requests.AgentErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void EnsureAddressAllowed_RejectsBlockedRangesAndAllowsPublic()
    {
        string[] blocked =
        {
            "127.0.0.1", "10.1.2.3", "172.16.0.9", "172.31.255.1", "192.168.1.1",
            "169.254.169.254", "100.64.0.1", "0.0.0.0", "::1", "fe80::1", "fd00::1",
        };
        foreach (string address in blocked)
        {
            Assert.Throws<OpenAgent.Contracts.Security.AgentException>(
                () => FileAssetUrlDownloader.EnsureAddressAllowed(IPAddress.Parse(address)));
        }

        // Public addresses pass the connect-time gate.
        FileAssetUrlDownloader.EnsureAddressAllowed(IPAddress.Parse("93.184.216.34"));
        FileAssetUrlDownloader.EnsureAddressAllowed(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"));
    }

    [Fact]
    public async Task RegisteredClient_ConnectCallback_RejectsLoopbackTargets()
    {
        // Mirrors the DI registration: a rebinding attempt that swaps the validated
        // public DNS answer for a loopback address must fail at connect time.
        ServiceCollection services = new();
        services.AddFileAssetServices(new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        using HttpClient client = factory.CreateClient("AgentFileDownload");

        // HttpClient wraps connect-stage failures; the gate exception is the inner one.
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://127.0.0.1:1/forbidden.txt"));

        OpenAgent.Contracts.Security.AgentException? gate =
            failure.InnerException as OpenAgent.Contracts.Security.AgentException;
        Assert.NotNull(gate);
        Assert.Equal(OpenAgent.Contracts.Requests.AgentErrorCode.InvalidRequest, gate!.ErrorCode);
    }
    private static FileAssetUrlDownloader CreateDownloader(
        HttpMessageHandler handler,
        long maxBytes = 1024) => new(
            new StubHttpClientFactory(new HttpClient(handler, disposeHandler: false)),
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = maxBytes,
            }),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class RepeatingStream : Stream
    {
        private readonly int _length;
        private int _remaining;

        public RepeatingStream(int length)
        {
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = Math.Min(_remaining, buffer.Length);
            buffer[..read].Fill((byte)'x');
            _remaining -= read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

