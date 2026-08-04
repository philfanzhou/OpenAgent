namespace OpenAgent.Core.Conversation.Scripts;

internal static class EmbeddedResourceReader
{
    internal static string Read(string logicalName)
    {
        using Stream stream = typeof(EmbeddedResourceReader).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {logicalName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
