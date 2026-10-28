using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Runner;

/// <summary>Invalid or escaping workspace path (HTTP 400).</summary>
internal sealed class WorkspacePathException(string message) : Exception(message)
{
    public WorkspaceErrorKind Kind => WorkspaceErrorKind.InvalidPath;
}

/// <summary>Target file or directory missing (HTTP 404).</summary>
internal sealed class WorkspaceFileNotFoundException(string message) : Exception(message)
{
    public WorkspaceErrorKind Kind => WorkspaceErrorKind.NotFound;
}

/// <summary>edit 唯一性冲突：0 次或多于 1 次且未允许全量替换（HTTP 409）。</summary>
internal sealed class WorkspaceEditConflictException(string message) : Exception(message)
{
    public WorkspaceErrorKind Kind => WorkspaceErrorKind.EditConflict;
}

internal sealed class WorkspaceTooLargeException(string message) : Exception(message)
{
    public WorkspaceErrorKind Kind => WorkspaceErrorKind.TooLarge;
}

/// <summary>
/// 会话工作区的宿主侧文件操作：目录为 {WorkspaceRoot}/session-{key}/work，
/// 同一路径被 bind-mount 到持久会话沙箱的 /work（execute_code 与本服务的写入互相可见）。
/// 路径校验：仅接受工作区内的相对路径，拒绝 rooted/../，并逐级拒绝符号链接（防逃逸）；
/// 同会话操作以每键信号量串行化（跨会话互不影响）。
/// </summary>
internal sealed class WorkspaceStore(IOptions<RunnerOptions> options)
{
    private const int MaxListEntries = 500;
    private const int MaxReadLines = 2_000;
    private const int DefaultReadLines = 500;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    internal string SessionDirectory(string sessionKey) => Path.Combine(
        options.Value.WorkspaceRoot, SessionSandboxManager.SessionDirectoryPrefix + sessionKey);

    internal string WorkDirectory(string sessionKey) =>
        Path.Combine(SessionDirectory(sessionKey), "work");

    internal async Task<T> WithSessionAsync<T>(
        string sessionKey,
        Func<string, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!ExecutionLimits.IsSafeSessionKey(sessionKey))
        {
            throw new WorkspacePathException("Invalid session key.");
        }
        SemaphoreSlim gate = _gates.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(WorkDirectory(sessionKey)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task<WorkspaceListResult> ListAsync(string workRoot, string? path, string? pattern) =>
        Task.Run(() =>
        {
            string directory = string.IsNullOrWhiteSpace(path) ? workRoot : Resolve(workRoot, path, requireDirectory: true);
            string match = NormalizePattern(pattern);
            List<WorkspaceEntry> entries = [];
            foreach (string full in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(full);
                if (name == "channel")
                {
                    continue; // supervisor 通道目录不属于模型可见的工作区
                }
                if (match != "*" && !MatchesSimpleExpression(match, name))
                {
                    continue;
                }
                bool isDirectory = Directory.Exists(full);
                entries.Add(new WorkspaceEntry
                {
                    Path = ToRelative(workRoot, full),
                    IsDirectory = isDirectory,
                    LengthBytes = isDirectory ? 0 : new FileInfo(full).Length,
                    ModifiedUtc = File.GetLastWriteTimeUtc(full)
                });
                if (entries.Count >= MaxListEntries)
                {
                    break;
                }
            }
            return new WorkspaceListResult
            {
                Path = string.IsNullOrWhiteSpace(path) ? "." : path,
                Entries = entries
                    .OrderBy(entry => !entry.IsDirectory)
                    .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                    .ToList()
            };
        });

    internal Task<WorkspaceReadResult> ReadAsync(
        string workRoot,
        string path,
        int offsetLine,
        int limitLines)
    {
        offsetLine = offsetLine < 1 ? 1 : offsetLine;
        limitLines = limitLines < 1 ? DefaultReadLines : Math.Min(limitLines, MaxReadLines);
        return Task.Run(() =>
        {
            string full = Resolve(workRoot, path, requireDirectory: false);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                throw new WorkspaceFileNotFoundException($"The workspace file '{path}' does not exist.");
            }
            if (info.Length > ExecutionLimits.MaxFileBytes)
            {
                throw new WorkspaceTooLargeException(
                    $"The workspace file '{path}' is larger than the read limit; use execute_code to process it in chunks.");
            }
            string[] lines = File.ReadAllLines(full, Encoding.UTF8);
            if (offsetLine > lines.Length)
            {
                return new WorkspaceReadResult
                {
                    Path = path,
                    TotalLines = lines.Length,
                    StartLine = offsetLine,
                    EndLine = offsetLine - 1,
                    Content = string.Empty,
                    Truncated = false
                };
            }
            int count = Math.Min(limitLines, lines.Length - offsetLine + 1);
            var builder = new StringBuilder();
            for (int index = 0; index < count; index++)
            {
                builder.Append((offsetLine + index).ToString()).Append('\t').AppendLine(lines[offsetLine + index - 1]);
            }
            return new WorkspaceReadResult
            {
                Path = path,
                TotalLines = lines.Length,
                StartLine = offsetLine,
                EndLine = offsetLine + count - 1,
                Content = builder.ToString(),
                Truncated = offsetLine + count - 1 < lines.Length
            };
        });
    }

    internal Task<WorkspaceWriteResult> WriteAsync(string workRoot, string path, string content)
    {
        byte[] data = Encoding.UTF8.GetBytes(content);
        if (data.Length > ExecutionLimits.MaxFileBytes)
        {
            throw new WorkspaceTooLargeException(
                $"The content exceeds the per-file limit of {ExecutionLimits.MaxFileBytes} bytes.");
        }
        return Task.Run(() =>
        {
            string full = Resolve(workRoot, path, requireDirectory: false);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            return new WorkspaceWriteResult { Path = path, LengthBytes = data.Length };
        });
    }

    internal Task<WorkspaceEditResult> EditAsync(
        string workRoot,
        string path,
        string oldString,
        string newString,
        bool replaceAll)
    {
        if (oldString.Length == 0)
        {
            throw new WorkspacePathException("'old_string' must not be empty.");
        }
        return Task.Run(() =>
        {
            string full = Resolve(workRoot, path, requireDirectory: false);
            if (!File.Exists(full))
            {
                throw new WorkspaceFileNotFoundException(
                    $"The workspace file '{path}' does not exist; call read_workspace_file before editing.");
            }
            var info = new FileInfo(full);
            if (info.Length > ExecutionLimits.MaxFileBytes)
            {
                throw new WorkspaceTooLargeException(
                    $"The workspace file '{path}' is too large to edit; use execute_code instead.");
            }
            string text = File.ReadAllText(full, Encoding.UTF8);
            int occurrences = CountOccurrences(text, oldString);
            if (occurrences == 0)
            {
                throw new WorkspaceEditConflictException(
                    $"'old_string' was not found in '{path}'. The file may have changed since it was read; "
                    + "read_workspace_file the file again and retry with the exact current text (including whitespace).");
            }
            if (occurrences > 1 && !replaceAll)
            {
                throw new WorkspaceEditConflictException(
                    $"'old_string' occurs {occurrences} times in '{path}'. Include more surrounding lines to make it unique, "
                    + "or set replace_all=true to replace every occurrence.");
            }
            string updated = replaceAll
                ? text.Replace(oldString, newString, StringComparison.Ordinal)
                : ReplaceFirst(text, oldString, newString);
            byte[] data = Encoding.UTF8.GetBytes(updated);
            if (data.Length > ExecutionLimits.MaxFileBytes)
            {
                throw new WorkspaceTooLargeException("The edited file exceeds the per-file limit.");
            }
            File.WriteAllBytes(full, data);
            return new WorkspaceEditResult { Path = path, Replacements = replaceAll ? occurrences : 1 };
        });
    }

    internal Task<WorkspaceBytesResult> ReadBytesAsync(string workRoot, string path) =>
        Task.Run(() =>
        {
            string full = Resolve(workRoot, path, requireDirectory: false);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                throw new WorkspaceFileNotFoundException($"The workspace file '{path}' does not exist.");
            }
            if (info.Length > ExecutionLimits.MaxFileBytes)
            {
                throw new WorkspaceTooLargeException(
                    $"The workspace file '{path}' exceeds the export limit.");
            }
            byte[] data = File.ReadAllBytes(full);
            return new WorkspaceBytesResult
            {
                Path = path,
                LengthBytes = data.Length,
                ContentBase64 = Convert.ToBase64String(data)
            };
        });

    internal Task<WorkspaceWriteResult> UploadAsync(string workRoot, string path, string contentBase64)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            throw new WorkspacePathException("The uploaded content is not valid base64.");
        }
        if (data.Length > ExecutionLimits.MaxFileBytes)
        {
            throw new WorkspaceTooLargeException(
                $"The upload exceeds the per-file limit of {ExecutionLimits.MaxFileBytes} bytes.");
        }
        return Task.Run(() =>
        {
            string full = Resolve(workRoot, path, requireDirectory: false);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            return new WorkspaceWriteResult { Path = path, LengthBytes = data.Length };
        });
    }

    /// <summary>
    /// 相对路径解析：字符级校验（复用 ExecutionLimits 的文件名安全规则）+
    /// 全路径包含检查 + 逐级符号链接拒绝。三层缺一不可：字符校验挡明显穿越，
    /// 包含检查挡编码差异，符号链接拒绝挡沙箱内创建链接的逃逸。
    /// </summary>
    private static string Resolve(string workRoot, string? path, bool requireDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !ExecutionLimits.IsSafeFileName(path))
        {
            throw new WorkspacePathException(
                "Paths must be relative to the workspace root, without '..' or empty segments.");
        }
        string normalized = path.Replace('\\', '/');
        string full = Path.GetFullPath(Path.Combine(workRoot, normalized));
        string rootPrefix = Path.GetFullPath(workRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new WorkspacePathException("Paths must stay inside the workspace root.");
        }
        // 拒绝任何已存在的符号链接组件：沙箱内进程可在工作区创建指向外部的链接。
        string current = Path.GetFullPath(workRoot);
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            FileSystemInfo item = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : new FileInfo(next);
            if (item.LinkTarget != null)
            {
                throw new WorkspacePathException(
                    $"'{segment}' is a symbolic link; symbolic links are not allowed in workspace paths.");
            }
            if (requireDirectory && !Directory.Exists(next))
            {
                throw new WorkspaceFileNotFoundException($"The workspace directory '{path}' does not exist.");
            }
            current = next;
        }
        return full;
    }

    private static string ToRelative(string workRoot, string full) =>
        Path.GetRelativePath(Path.GetFullPath(workRoot), full).Replace('\\', '/');

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldString, string newString)
    {
        int index = text.IndexOf(oldString, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newString, text.AsSpan(index + oldString.Length));
    }

    private static string NormalizePattern(string? pattern) =>
        string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Replace('\\', '/').Split('/')[^1];

    /// <summary>仅支持 * 与 ? 的朴素通配（大小写不敏感，与主流文件系统行为一致）。</summary>
    private static bool MatchesSimpleExpression(string pattern, string name)
    {
        var builder = new StringBuilder("^");
        foreach (char character in pattern)
        {
            if (character == '*')
            {
                builder.Append(".*");
            }
            else if (character == '?')
            {
                builder.Append('.');
            }
            else
            {
                builder.Append(System.Text.RegularExpressions.Regex.Escape(character.ToString()));
            }
        }
        builder.Append('$');
        return System.Text.RegularExpressions.Regex.IsMatch(
            name,
            builder.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
