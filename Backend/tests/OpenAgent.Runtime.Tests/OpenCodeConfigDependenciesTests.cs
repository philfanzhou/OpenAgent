using System.Text.Json;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeConfigDependenciesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode-config-deps-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Seed_EmptyDirectory_CreatesEmptyModulesAndRegistersPluginInLockRoot()
    {
        string directory = Path.Combine(_root, "config");
        OpenCodeConfigDependencies.Seed(directory);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(directory, "node_modules")));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
        Assert.Equal("0.0.0", manifest.RootElement.GetProperty("dependencies").GetProperty(OpenCodeConfigDependencies.PackageName).GetString());
        using JsonDocument lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package-lock.json")));
        JsonElement root = lockFile.RootElement.GetProperty("packages").GetProperty("");
        Assert.Equal("0.0.0", root.GetProperty("dependencies").GetProperty(OpenCodeConfigDependencies.PackageName).GetString());
    }

    [Fact]
    public void Seed_ExistingContent_IsNotOverwritten()
    {
        string directory = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(directory, "node_modules", "kept"));
        File.WriteAllText(Path.Combine(directory, "node_modules", "kept", "marker.txt"), "keep");
        File.WriteAllText(Path.Combine(directory, "package.json"), "{\"dependencies\":{\"custom\":\"1.0.0\"}}");

        OpenCodeConfigDependencies.Seed(directory);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "node_modules", "kept", "marker.txt")));
        Assert.Equal("{\"dependencies\":{\"custom\":\"1.0.0\"}}", File.ReadAllText(Path.Combine(directory, "package.json")));
        // 自定义依赖不在锁文件根依赖里，上游仍会按需安装；空壳只负责登记插件，不改写既有内容。
        Assert.True(File.Exists(Path.Combine(directory, "package-lock.json")));
    }

    [Fact]
    public void Seed_ExistingDirectoryCreatedByOpenCodeDefaultUmask_IsAccepted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // 升级前已提交的会话里，配置目录由 OpenCode 以默认 umask 创建；不能因为权限位不是 0700 就让追问失败。
        string directory = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        OpenCodeConfigDependencies.Seed(directory);

        Assert.True(File.Exists(Path.Combine(directory, "package-lock.json")));
    }

    [Fact]
    public void Seed_CalledTwice_IsIdempotent()
    {
        string directory = Path.Combine(_root, "config");
        OpenCodeConfigDependencies.Seed(directory);
        DateTimeOffset manifestWrite = File.GetLastWriteTimeUtc(Path.Combine(directory, "package.json"));

        OpenCodeConfigDependencies.Seed(directory);

        Assert.Equal(manifestWrite, File.GetLastWriteTimeUtc(Path.Combine(directory, "package.json")));
    }

    [Fact]
    public void Seed_NodeModulesIsSymbolicLink_Throws()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string directory = Path.Combine(_root, "config");
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(directory, "node_modules"), elsewhere);

        Assert.Throws<IOException>(() => OpenCodeConfigDependencies.Seed(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
