using System.Security.Cryptography;
using System.Text;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeWorkspaceFilesTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "workspace-files-" + Guid.NewGuid().ToString("N"));
    private string _root => Path.Combine(_home, "workspace");

    [Fact]
    public async Task SnapshotAsync_IsIndependentOfSubsequentSourceChangesAndDeletesTemporaryCopy()
    {
        OpenCodeWorkspaceFiles workspace = new(_root);
        string path = Path.Combine(_root, "outputs", "分析.bin");
        byte[] original = [0, 255, 128, 10];
        await File.WriteAllBytesAsync(path, original);
        await using (RuntimeOutputFile snapshot = await workspace.SnapshotAsync("outputs/分析.bin", 100, default))
        {
            await File.WriteAllTextAsync(path, "revised");
            await using MemoryStream downloaded = new();
            await snapshot.Content.CopyToAsync(downloaded);
            Assert.Equal(original, downloaded.ToArray());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), snapshot.Sha256);
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_home, ".file-exchange")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/tmp/outside")]
    [InlineData("inputs/a/data")]
    [InlineData("outputs/../input")]
    [InlineData("outputs\\data")]
    [InlineData("outputs/data:stream")]
    public async Task SnapshotAsync_RejectsPathsOutsideOutputs(string path)
    {
        await Assert.ThrowsAsync<AgentException>(() => new OpenCodeWorkspaceFiles(_root).SnapshotAsync(path, 100, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task SnapshotAsync_RejectsEmptyOrOversizedFilesWithoutLeavingSnapshots(int length)
    {
        OpenCodeWorkspaceFiles workspace = new(_root);
        await File.WriteAllBytesAsync(Path.Combine(_root, "outputs/data"), new byte[length]);
        await Assert.ThrowsAsync<AgentException>(() => workspace.SnapshotAsync("outputs/data", 100, default));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_home, ".file-exchange")));
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    [InlineData("fifo")]
    public async Task SnapshotAsync_RejectsLinksAndSpecialFilesWithoutBlocking(string kind)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        OpenCodeWorkspaceFiles workspace = new(_root);
        string external = Path.Combine(_home, "external");
        await File.WriteAllTextAsync(external, "external");
        string target = Path.Combine(_root, "outputs/data");
        if (kind == "symlink")
        {
            File.CreateSymbolicLink(target, external);
        }
        else
        {
            System.Diagnostics.ProcessStartInfo start = new(kind == "fifo" ? "mkfifo" : "ln") { UseShellExecute = false };
            if (kind == "hardlink")
            {
                start.ArgumentList.Add(external);
            }
            start.ArgumentList.Add(target);
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<AgentException>(() => workspace.SnapshotAsync("outputs/data", 100, deadline.Token));
        Assert.Equal("external", await File.ReadAllTextAsync(external));
    }

    [Theory]
    [InlineData("销售表.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("大图.png", "image/png")]
    [InlineData("合同.pdf", "application/pdf")]
    public async Task ImportAsync_PreservesOriginalBytesAndRestoresModifiedOrEvictedCopy(string name, string mediaType)
    {
        byte[] data = [0, 255, 128, 10, 13, 0];
        RuntimeFile file = CreateFile("first", name, mediaType, data);
        OpenCodeWorkspaceFiles workspace = new(_root);
        RuntimeWorkspaceFile first = await RestoreInputAsync(workspace, file, data, CancellationToken.None);
        Assert.Equal($"inputs/first/{name}", first.RelativePath);
        string path = Path.Combine(_root, first.RelativePath);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        await File.WriteAllTextAsync(path, "changed");
        Assert.Equal(first, await RestoreInputAsync(workspace, file, data, CancellationToken.None));
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Directory.Delete(_root, true);
        OpenCodeWorkspaceFiles rebuilt = new(_root);
        Assert.Equal(first, await RestoreInputAsync(rebuilt, file, data, CancellationToken.None));
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ImportAsync_SameNamesRemainSeparateAndUnsafeNameCannotEscape()
    {
        OpenCodeWorkspaceFiles workspace = new(_root);
        byte[] data = Encoding.UTF8.GetBytes("original");
        RuntimeWorkspaceFile first = await RestoreInputAsync(workspace, CreateFile("a", "同名.xlsx", "application/octet-stream", data), data, default);
        RuntimeWorkspaceFile second = await RestoreInputAsync(workspace, CreateFile("b", "同名.xlsx", "application/octet-stream", data), data, default);
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        RuntimeWorkspaceFile escaped = await RestoreInputAsync(workspace, CreateFile("c", "../../outside\n.txt", "text/plain", data), data, default);
        Assert.Equal("inputs/c/.._.._outside_.txt", escaped.RelativePath);
        Assert.True(File.Exists(Path.Combine(_root, escaped.RelativePath)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    public async Task ImportAsync_RejectsUnsafeAssetId(string id)
    {
        await Assert.ThrowsAsync<AgentException>(() => RestoreInputAsync(new OpenCodeWorkspaceFiles(_root),
            CreateFile(id, "x", "text/plain", [1]), new byte[] { 1 }, default));
    }

    [Fact]
    public async Task ImportAsync_LinkTargetIsRejectedWithoutChangingExternalFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        byte[] data = [1, 2];
        OpenCodeWorkspaceFiles workspace = new(_root);
        RuntimeWorkspaceFile imported = await RestoreInputAsync(workspace, CreateFile("a", "input.bin", "application/octet-stream", data), data, default);
        string outside = Path.Combine(_root, "external.bin");
        await File.WriteAllTextAsync(outside, "external");
        string target = Path.Combine(_root, imported.RelativePath);
        File.Delete(target);
        File.CreateSymbolicLink(target, outside);
        await Assert.ThrowsAsync<AgentException>(() => RestoreInputAsync(workspace, imported.File, data, default));
        Assert.Equal("external", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task ImportAsync_CorruptBytesNeverBecomeAnInput()
    {
        OpenCodeWorkspaceFiles workspace = new(_root);
        await Assert.ThrowsAsync<AgentException>(() => RestoreInputAsync(workspace, CreateFile("a", "input.bin", "application/octet-stream", [1]), new byte[] { 2 }, default));
        Assert.False(File.Exists(Path.Combine(_root, "inputs/a/input.bin")));
    }

    [Fact]
    public async Task RestoreAsync_NestedOutputUsesExistingNativeToolDirectory()
    {
        OpenCodeWorkspaceFiles workspace = new(_root);
        string directory = Path.Combine(_root, "outputs", "reports");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
        byte[] bytes = [0, 1, 255];
        RuntimeFile file = CreateFile("sample", "result.bin", "application/octet-stream", bytes);

        RuntimeWorkspaceFile restored = await workspace.RestoreAsync(file, "outputs/reports/result.bin",
            (destination, token) => destination.WriteAsync(bytes, token).AsTask(), default);

        Assert.Equal("outputs/reports/result.bin", restored.RelativePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, "result.bin")));
    }

    private static Task<RuntimeWorkspaceFile> RestoreInputAsync(
        OpenCodeWorkspaceFiles workspace, RuntimeFile file, ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        workspace.RestoreAsync(file, null, (output, token) => output.WriteAsync(bytes, token).AsTask(), ct);

    private static RuntimeFile CreateFile(string id, string name, string mediaType, byte[] data) =>
        new(id, name, mediaType, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, true);
        }
    }
}
