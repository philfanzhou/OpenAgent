namespace OpenAgent.Runtime.Acp.TestConsole;
internal static class SelfCommand
{
    internal static (string File, string[] Args) For(string mode)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        string entry = Environment.GetCommandLineArgs()[0];
        return (executable, Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [Path.GetFullPath(entry), mode] : [mode]);
    }
}
