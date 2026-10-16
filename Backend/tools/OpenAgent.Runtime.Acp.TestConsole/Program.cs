namespace OpenAgent.Runtime.Acp.TestConsole;

internal static class Program
{
    private static Task<int> Main(string[] args) => ConsoleApplication.RunAsync(args, Console.Out, Console.Error);
}
