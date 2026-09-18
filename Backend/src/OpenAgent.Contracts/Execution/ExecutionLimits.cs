using System.Text;

namespace OpenAgent.Contracts.Execution;

/// <summary>Wire limits shared by the Engine and the isolated Runner.</summary>
public static class ExecutionLimits
{
    public const int MaxCodeBytes = 128 * 1024;
    public const int MaxFiles = 100;
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTotalFileBytes = 20 * 1024 * 1024;
    public const int MaxLogCharacters = 32 * 1024;
    public const int MaxWireBytes = 32 * 1024 * 1024;
    public const int MaxSessionKeyBytes = 64;

    public static bool IsSafeFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 160
        && name.Split('/').All(IsSafeNameSegment)
        && !name.EndsWith("/", StringComparison.Ordinal);

    private static bool IsSafeNameSegment(string? segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && segment.Length <= 120
        && segment is not "." and not ".."
        && char.IsLetterOrDigit(segment[0])
        && segment.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ' ');

    /// <summary>
    /// Conversation-scoped workspaces are reused across executions, so the key is
    /// restricted to a small character set and length; it never reaches a shell.
    /// </summary>
    public static bool IsSafeSessionKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length <= MaxSessionKeyBytes
        && key.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    public static void Validate(CodeExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) || Encoding.UTF8.GetByteCount(request.Code) > MaxCodeBytes)
        {
            throw new ArgumentException("Code is empty or exceeds the execution limit.");
        }
        string language = ExecutionLanguage.Normalize(request.Language)
            ?? throw new ArgumentException("The requested execution language is not supported.");
        if (!string.IsNullOrWhiteSpace(request.SessionKey) && !IsSafeSessionKey(request.SessionKey))
        {
            throw new ArgumentException("The session key contains unsupported characters.");
        }
        ValidateFiles(request.Files);
        // A custom entry replaces the language default, so only the effective entry
        // name stays reserved; packages may then legitimately ship their own main.py.
        IEnumerable<string> reservedEntries = string.IsNullOrWhiteSpace(request.EntryFileName)
            ? ExecutionLanguage.Supported.Select(ExecutionLanguage.EntryFileName)
            : [request.EntryFileName];
        foreach (string entryName in reservedEntries)
        {
            if (request.Files.Any(file => file.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"The input name {entryName} is reserved.");
            }
        }
        if (!string.IsNullOrWhiteSpace(request.EntryFileName)
            && (!IsSafeFileName(request.EntryFileName)
                || request.EntryFileName.Contains('/', StringComparison.Ordinal)
                || !request.EntryFileName.EndsWith(
                    ExecutionLanguage.EntryFileExtension(language),
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The entry file name is invalid for the requested language.");
        }
    }

    public static void ValidateFiles(IReadOnlyList<ExecutionFile>? files)
    {
        if (files == null || files.Count > MaxFiles)
        {
            throw new ArgumentException("Too many execution files.");
        }
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExecutionFile file in files)
        {
            if (file == null || !IsSafeFileName(file.Name) || !names.Add(file.Name)
                || file.Content == null || file.Content.Length > MaxFileBytes)
            {
                throw new ArgumentException("Invalid execution file name, duplicate name, or file size.");
            }
            total += file.Content.Length;
        }
        if (total > MaxTotalFileBytes)
        {
            throw new ArgumentException("Execution files exceed the total size limit.");
        }
    }
}
