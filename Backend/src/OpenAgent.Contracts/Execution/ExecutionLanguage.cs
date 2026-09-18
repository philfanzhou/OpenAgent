namespace OpenAgent.Contracts.Execution;

/// <summary>Language identifiers accepted by the isolated code Runner.</summary>
public static class ExecutionLanguage
{
    public const string Python = "python";
    public const string JavaScript = "javascript";

    public static readonly IReadOnlyList<string> Supported = [Python, JavaScript];

    /// <summary>Returns the canonical language identifier or <c>null</c> when unsupported.</summary>
    public static string? Normalize(string? language) => Supported.FirstOrDefault(
        supported => string.Equals(supported, language, StringComparison.OrdinalIgnoreCase));

    public static string EntryFileName(string language) => language switch
    {
        JavaScript => "main.mjs",
        _ => "main.py"
    };

    public static string EntryFileExtension(string language) => language switch
    {
        JavaScript => ".mjs",
        _ => ".py"
    };
}
