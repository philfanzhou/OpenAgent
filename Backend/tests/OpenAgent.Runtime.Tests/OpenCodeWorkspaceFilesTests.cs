using System.Security.Cryptography;
using System.Text;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeWorkspaceFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workspace-files-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("销售表.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("大图.png", "image/png")]
    [InlineData("合同.pdf", "application/pdf")]
    public async Task ImportAsync_PreservesOriginalBytesAndRestoresModifiedOrEvictedCopy(string name, string mediaType)
    {
        byte[] data = [0, 255, 128, 10, 13, 0];
        RuntimeFile file = CreateFile("first", name, mediaType, data);
        OpenCodeWorkspaceFiles workspace = new(_root);
        RuntimeWorkspaceFile first = await workspace.ImportAsync(file, data, CancellationToken.None);
        Assert.Equal($"inputs/first/{name}", first.RelativePath);
        string path = Path.Combine(_root, first.RelativePath);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        await File.WriteAllTextAsync(path, "changed");
        Assert.Equal(first, await workspace.ImportAsync(file, data, CancellationToken.None));
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Directory.Delete(_root, true);
        OpenCodeWorkspaceFiles rebuilt = new(_root);
        Assert.Equal(first, await rebuilt.ImportAsync(file, data, CancellationToken.None));
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ImportAsync_SameNamesRemainSeparateAndUnsafeNameCannotEscape()
    {
        OpenCodeWorkspaceFiles workspace = new(_root);
        byte[] data = Encoding.UTF8.GetBytes("original");
        RuntimeWorkspaceFile first = await workspace.ImportAsync(CreateFile("a", "同名.xlsx", "application/octet-stream", data), data, default);
        RuntimeWorkspaceFile second = await workspace.ImportAsync(CreateFile("b", "同名.xlsx", "application/octet-stream", data), data, default);
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        RuntimeWorkspaceFile escaped = await workspace.ImportAsync(CreateFile("c", "../../outside\n.txt", "text/plain", data), data, default);
        Assert.Equal("inputs/c/.._.._outside_.txt", escaped.RelativePath);
        Assert.True(File.Exists(Path.Combine(_root, escaped.RelativePath)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    public async Task ImportAsync_RejectsUnsafeAssetId(string id)
    {
        await Assert.ThrowsAsync<AgentException>(() => new OpenCodeWorkspaceFiles(_root)
            .ImportAsync(CreateFile(id, "x", "text/plain", [1]), new byte[] { 1 }, default));
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
        RuntimeWorkspaceFile imported = await workspace.ImportAsync(CreateFile("a", "input.bin", "application/octet-stream", data), data, default);
        string outside = Path.Combine(_root, "external.bin");
        await File.WriteAllTextAsync(outside, "external");
        string target = Path.Combine(_root, imported.RelativePath);
        File.Delete(target);
        File.CreateSymbolicLink(target, outside);
        await Assert.ThrowsAsync<AgentException>(() => workspace.ImportAsync(imported.File, data, default));
        Assert.Equal("external", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task ImportAsync_CorruptBytesNeverBecomeAnInput()
    {
        OpenCodeWorkspaceFiles workspace = new(_root);
        await Assert.ThrowsAsync<AgentException>(() => workspace.ImportAsync(CreateFile("a", "input.bin", "application/octet-stream", [1]), new byte[] { 2 }, default));
        Assert.False(File.Exists(Path.Combine(_root, "inputs/a/input.bin")));
    }

    private static RuntimeFile CreateFile(string id, string name, string mediaType, byte[] data) =>
        new(id, name, mediaType, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
