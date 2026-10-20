using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.Processes;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Client;

public sealed class TerminalTests
{
    [Fact]
    public async Task Terminal_QuotaOwnershipUtf8KillAndReleaseFollowLifecycle()
    {
        // Process.StandardOutput 按宿主控制台编码解码子进程输出；代码页 936 会把 UTF-8 断言变成 GBK 乱码，统一为 UTF-8。
        System.Text.Encoding original = Console.OutputEncoding;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            await using LocalTerminalHandler handler = new(_ => true, maxOutputBytes: 4, maxTerminals: 1);
            var command = ProcessTests.Command("terminal");
            CreateTerminalRequest request = new("one", command.FileName, command.Arguments.ToArray());
            string id = (await handler.CreateAsync(request)).TerminalId;
            TerminalRequest terminal = new("one", id);
            await handler.WaitAsync(terminal).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(10002, (await Assert.ThrowsAsync<AcpRpcException>(() => handler.CreateAsync(request))).Code);
            Assert.Equal(-32002, (await Assert.ThrowsAsync<AcpRpcException>(() => handler.OutputAsync(new("other", id)))).Code);
            await handler.KillAsync(terminal);
            TerminalOutputResponse output = await handler.OutputAsync(terminal);
            Assert.True(output.Truncated);
            Assert.Equal("🥐", output.Output);
            await handler.ReleaseAsync(terminal);
            await Assert.ThrowsAsync<AcpRpcException>(() => handler.OutputAsync(terminal));
            string next = (await handler.CreateAsync(request)).TerminalId;
            await handler.CloseSessionAsync("one");
            await Assert.ThrowsAsync<AcpRpcException>(() => handler.OutputAsync(new("one", next)));
        }
        finally
        {
            Console.OutputEncoding = original;
        }
    }
}
