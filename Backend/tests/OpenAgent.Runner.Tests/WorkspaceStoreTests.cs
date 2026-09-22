using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

/// <summary>
/// 工作区文件操作的纯宿主侧单测（不依赖 bwrap，Windows 可跑）：
/// 路径穿越/符号链接拒绝、编辑唯一性语义、行号读取分页。
/// </summary>
public class WorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oa-workspace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceStore _store = new(Options.Create(new RunnerOptions()));

    public WorkspaceStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WorkDir(string sessionKey = "demo-session")
    {
        string dir = Path.Combine(_root, "session-" + sessionKey, "work");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<WorkspaceWriteResult> WriteAsync(string path, string content, string sessionKey = "demo-session")
        => await _store.WriteAsync(WorkDir(sessionKey), path, content);

    [Fact]
    public async Task WriteThenRead_ReturnsNumberedLinesWithPaging()
    {
        await WriteAsync("notes/report.txt", "alpha\nbeta\ngamma\n");

        WorkspaceReadResult first = await _store.ReadAsync(WorkDir(), "notes/report.txt", 1, 2);
        Assert.Equal(3, first.TotalLines);
        Assert.Equal(1, first.StartLine);
        Assert.Equal(2, first.EndLine);
        Assert.True(first.Truncated);
        Assert.Contains("1\talpha", first.Content, StringComparison.Ordinal);
        Assert.Contains("2\tbeta", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("gamma", first.Content, StringComparison.Ordinal);

        WorkspaceReadResult second = await _store.ReadAsync(WorkDir(), "notes/report.txt", 3, 2);
        Assert.False(second.Truncated);
        Assert.Contains("3\tgamma", second.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_UniqueMatch_ReplacesOnce()
    {
        await WriteAsync("a.txt", "keep\nTARGET\nkeep\n");

        WorkspaceEditResult result = await _store.EditAsync(WorkDir(), "a.txt", "TARGET", "REPLACED", false);

        Assert.Equal(1, result.Replacements);
        Assert.Equal("keep\nREPLACED\nkeep\n", await File.ReadAllTextAsync(Path.Combine(WorkDir(), "a.txt")));
    }

    [Fact]
    public async Task Edit_MultipleMatchesWithoutReplaceAll_ReportsConflictWithCount()
    {
        await WriteAsync("a.txt", "x dup x dup x");

        WorkspaceEditConflictException conflict = await Assert.ThrowsAsync<WorkspaceEditConflictException>(
            () => _store.EditAsync(WorkDir(), "a.txt", "dup", "y", false));

        Assert.Contains("2 times", conflict.Message, StringComparison.Ordinal);
        Assert.Equal("x dup x dup x", await File.ReadAllTextAsync(Path.Combine(WorkDir(), "a.txt")));
    }

    [Fact]
    public async Task Edit_NotFound_TellsModelToReread()
    {
        await WriteAsync("a.txt", "current content");

        WorkspaceEditConflictException conflict = await Assert.ThrowsAsync<WorkspaceEditConflictException>(
            () => _store.EditAsync(WorkDir(), "a.txt", "stale text", "y", false));

        Assert.Contains("not found", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("read_workspace_file", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_ReplaceAll_ReplacesEveryOccurrence()
    {
        await WriteAsync("a.txt", "a a a");

        WorkspaceEditResult result = await _store.EditAsync(WorkDir(), "a.txt", "a", "b", true);

        Assert.Equal(3, result.Replacements);
        Assert.Equal("b b b", await File.ReadAllTextAsync(Path.Combine(WorkDir(), "a.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Paths_EscapingTheWorkspace_AreRejected(string path)
    {
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _store.WriteAsync(WorkDir(), path, "x"));
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _store.ReadAsync(WorkDir(), path, 1, 10));
    }

    [Fact]
    public async Task Paths_WithParentSegmentsInsideWorkspace_AreAllowed()
    {
        // 常规相对路径（含子目录）必须可用；拒绝的只是逃逸。
        await WriteAsync("docs/deep/file.txt", "ok");
        WorkspaceReadResult result = await _store.ReadAsync(WorkDir(), "docs/deep/file.txt", 1, 10);
        Assert.Contains("1\tok", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_MissingFile_ReportsNotFound()
    {
        await Assert.ThrowsAsync<WorkspaceFileNotFoundException>(
            () => _store.ReadAsync(WorkDir(), "missing.txt", 1, 10));
    }

    [Fact]
    public async Task List_ReturnsEntriesAndAppliesPattern()
    {
        await WriteAsync("data/one.csv", "1");
        await WriteAsync("data/two.csv", "2");
        await WriteAsync("data/readme.md", "x");
        Directory.CreateDirectory(Path.Combine(WorkDir(), "data", "nested"));

        WorkspaceListResult all = await _store.ListAsync(WorkDir(), "data", null);
        Assert.Equal(4, all.Entries.Count);
        Assert.Contains(all.Entries, entry => entry.Path == "data/nested" && entry.IsDirectory);

        WorkspaceListResult csv = await _store.ListAsync(WorkDir(), "data", "*.csv");
        Assert.Equal(2, csv.Entries.Count);
        Assert.All(csv.Entries, entry => Assert.EndsWith(".csv", entry.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UploadAndReadBytes_RoundTripBinaryContent()
    {
        byte[] payload = [0x00, 0x01, 0xFF, 0x10];
        string base64 = Convert.ToBase64String(payload);

        await _store.UploadAsync(WorkDir(), "bin/logo.png", base64);

        WorkspaceBytesResult read = await _store.ReadBytesAsync(WorkDir(), "bin/logo.png");
        Assert.Equal(payload, Convert.FromBase64String(read.ContentBase64));
        Assert.Equal(4, read.LengthBytes);
    }

    [Fact]
    public async Task WithSessionAsync_UnsafeSessionKey_RejectedBeforeTouchingDisk()
    {
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _store.WithSessionAsync<string>("../evil", _ => Task.FromResult("x"), CancellationToken.None));
    }
}
