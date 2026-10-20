namespace OpenAgent.Runtime.Acp.ProcessWorker;

// ProcessTests/TerminalTests 的辅助子进程：noisy 向 stderr 写满 4 MiB 后以退出码 17 结束，
// stall 挂起直至被 AcpProcess 释放击杀，terminal 按 UTF-8 输出固定文本供截断断言。
// 仅供测试拉起，无手动入口。
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["noisy"])
        { for (int i = 0; i < 1024; i++) Console.Error.Write(new string('x', 4096)); return 17; }
        if (args is ["stall"])
        { await Task.Delay(Timeout.Infinite); return 0; }
        if (args is ["terminal"])
        { Console.OutputEncoding = System.Text.Encoding.UTF8; Console.Write("prefix你好🥐"); return 0; }
        return 2;
    }
}
