using System.Text;

namespace OpenAgent.Contracts.Execution;

/// <summary>Wire limits shared by the Engine and the isolated Runner.</summary>
public static class ExecutionLimits
{
    public const int MaxCodeBytes = 128 * 1024;
    public const int MaxFiles = 8;
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTotalFileBytes = 20 * 1024 * 1024;
    public const int MaxLogCharacters = 32 * 1024;
    public const int MaxWireBytes = 32 * 1024 * 1024;

    public static bool IsSafeFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 120
        && name is not "." and not ".."
        && char.IsLetterOrDigit(name[0])
        && name.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ' ');

    public static void Validate(CodeExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) || Encoding.UTF8.GetByteCount(request.Code) > MaxCodeBytes)
        {
            throw new ArgumentException("Code is empty or exceeds the execution limit.");
        }
        ValidateFiles(request.Files);
        if (request.Files.Any(file => file.Name.Equals("main.py", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The input name main.py is reserved.");
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
