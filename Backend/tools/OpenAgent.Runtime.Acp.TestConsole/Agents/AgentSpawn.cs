using System.Runtime.InteropServices;

namespace OpenAgent.Runtime.Acp.TestConsole.Agents;

/// <summary>
/// Computes the executable/argument pair that runs this console again as a real child
/// process. Under the dotnet host the DLL is the first argument; an apphost already binds
/// its own DLL, so passing the DLL path again would land as a stray argument. An apphost
/// child needs <c>DOTNET_ROOT</c> to find the runtime when this machine's install location
/// is not one of the apphost's default search paths.
/// </summary>
internal static class AgentSpawn
{
    internal static string Host => Environment.ProcessPath ?? "dotnet";

    private static bool ViaDotnetHost =>
        Path.GetFileNameWithoutExtension(Host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prefixed arguments that start a child of this console in the given mode.</summary>
    internal static string[] Self(params string[] arguments) => ViaDotnetHost
        ? [Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll"), .. arguments]
        : [.. arguments];

    /// <summary>
    /// The environment entries a self-spawned child needs, or null under the dotnet host.
    /// The loaded runtime sits at <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;</c>,
    /// so the dotnet root is three levels up.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? ChildEnvironment()
    {
        if (ViaDotnetHost) return null;
        string? runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory)));
        string dotnet = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (root is null || !File.Exists(Path.Combine(root, dotnet))) return null;
        string? arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "ARM64",
            Architecture.X64 => "X64",
            Architecture.Arm => "ARM",
            _ => null
        };
        return arch is null
            ? new Dictionary<string, string> { ["DOTNET_ROOT"] = root }
            : new Dictionary<string, string> { ["DOTNET_ROOT"] = root, [$"DOTNET_ROOT_{arch}"] = root };
    }
}
