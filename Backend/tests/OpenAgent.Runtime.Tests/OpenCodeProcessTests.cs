using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode-executable-" + Guid.NewGuid().ToString("N"));

    private string CreateDir(params string[] segments)
    {
        string dir = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string CreateFile(params string[] segments)
    {
        string path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Resolve_ConfiguredAbsolutePathExists_UsesItDirectly()
    {
        string executable = CreateFile("tools", "opencode.exe");
        var spec = OpenCodeProcess.ResolveCore(executable, ["acp"], [], windows: true, npmDefaultRoot: CreateDir());
        Assert.Equal(executable, spec.FileName);
        Assert.Equal("acp", Assert.Single(spec.Arguments));
        Assert.False(spec.ShellFallback);
    }

    [Fact]
    public void Resolve_ConfiguredAbsolutePathMissing_ThrowsConfigurationError()
    {
        string missing = Path.Combine(_root, "tools", "missing.exe");
        AgentException error = Assert.Throws<AgentException>(
            () => OpenCodeProcess.ResolveCore(missing, ["acp"], [], windows: true, npmDefaultRoot: CreateDir()));
        Assert.Equal(AgentErrorCode.ConfigurationError, error.ErrorCode);
    }

    [Fact]
    public void Resolve_WindowsPathExecutableHit_ReturnsPathEntry()
    {
        string bin = CreateDir("bin");
        string executable = CreateFile("bin", "opencode.exe");
        var spec = OpenCodeProcess.ResolveCore("opencode", [], [bin], windows: true, npmDefaultRoot: CreateDir("npm"));
        Assert.Equal(executable, spec.FileName);
        Assert.False(spec.ShellFallback);
    }

    [Fact]
    public void Resolve_WindowsNpmPackageLayoutHit_ReturnsPackageBinary()
    {
        string prefix = CreateDir("nodejs");
        string executable = CreateFile("nodejs", "node_modules", "opencode-ai", "bin", "opencode.exe");
        var spec = OpenCodeProcess.ResolveCore("opencode", [], [prefix], windows: true, npmDefaultRoot: CreateDir("npm"));
        Assert.Equal(executable, spec.FileName);
    }

    [Fact]
    public void Resolve_WindowsPlatformPackageLayoutHit_ReturnsPlatformBinary()
    {
        string prefix = CreateDir("nodejs");
        string executable = CreateFile("nodejs", "node_modules", "opencode-windows-x64", "bin", "opencode.exe");
        var spec = OpenCodeProcess.ResolveCore("opencode", [], [prefix], windows: true, npmDefaultRoot: CreateDir("npm"));
        Assert.Equal(executable, spec.FileName);
    }

    [Fact]
    public void Resolve_WindowsDefaultNpmRootHit_ReturnsGlobalBinary()
    {
        string npmRoot = CreateDir("npm-global");
        string executable = CreateFile("npm-global", "node_modules", "opencode-ai", "bin", "opencode.exe");
        var spec = OpenCodeProcess.ResolveCore("opencode", [], [], windows: true, npmDefaultRoot: npmRoot);
        Assert.Equal(executable, spec.FileName);
    }

    [Fact]
    public void Resolve_WindowsOnlyCmdShimAvailable_LaunchesThroughCmdExe()
    {
        string bin = CreateDir("bin");
        string shim = CreateFile("bin", "opencode.cmd");
        var spec = OpenCodeProcess.ResolveCore("opencode", ["acp"], [bin], windows: true, npmDefaultRoot: CreateDir("npm"));
        Assert.Equal("cmd.exe", spec.FileName);
        Assert.True(spec.ShellFallback);
        Assert.Equal(shim, spec.Source);
        Assert.Equal(["/d", "/s", "/c"], spec.Arguments.Take(3));
        Assert.EndsWith("acp", spec.Arguments[3], StringComparison.Ordinal);
        Assert.Contains(shim, spec.Arguments[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WindowsNothingFound_ThrowsDependencyUnavailable()
    {
        AgentException error = Assert.Throws<AgentException>(
            () => OpenCodeProcess.ResolveCore("opencode", [], [], windows: true, npmDefaultRoot: CreateDir("npm")));
        Assert.Equal(AgentErrorCode.DependencyUnavailable, error.ErrorCode);
    }

    [Fact]
    public void Resolve_UnixPathHit_ReturnsLauncher()
    {
        string bin = CreateDir("bin");
        string launcher = CreateFile("bin", "opencode");
        var spec = OpenCodeProcess.ResolveCore("opencode", [], [bin], windows: false, npmDefaultRoot: CreateDir());
        Assert.Equal(launcher, spec.FileName);
        Assert.False(spec.ShellFallback);
    }

    [Fact]
    public void Resolve_UnixNodeModulesLayoutHit_ReturnsPackageBinary()
    {
        string tools = CreateDir("tools");
        string executable = CreateFile("tools", "node_modules", "opencode-ai", "bin", "opencode");
        var spec = OpenCodeProcess.ResolveCore("opencode", [], [tools], windows: false, npmDefaultRoot: CreateDir());
        Assert.Equal(executable, spec.FileName);
    }

    [Fact]
    public void Resolve_UnixNothingFound_ThrowsDependencyUnavailable()
    {
        AgentException error = Assert.Throws<AgentException>(
            () => OpenCodeProcess.ResolveCore("opencode", [], [], windows: false, npmDefaultRoot: CreateDir()));
        Assert.Equal(AgentErrorCode.DependencyUnavailable, error.ErrorCode);
    }

    [Fact]
    public void SplitPath_WindowsSeparator_SplitsSemicolonAndDedups()
    {
        List<string> entries = OpenCodeProcess.SplitPath(@"C:\a;C:\b;;C:\a", windows: true);
        Assert.Equal([@"C:\a", @"C:\b"], entries);
    }

    [Fact]
    public void SplitPath_UnixSeparator_SplitsColonAndStripsQuotes()
    {
        List<string> entries = OpenCodeProcess.SplitPath(@"/usr/bin:""/opt/bin"":/usr/bin", windows: false);
        Assert.Equal(["/usr/bin", "/opt/bin"], entries);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
