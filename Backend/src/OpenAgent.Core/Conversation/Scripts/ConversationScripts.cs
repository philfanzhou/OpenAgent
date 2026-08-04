namespace OpenAgent.Core.Conversation.Scripts;

internal static class ConversationScripts
{
    private const string ResourcePrefix = "OpenAgent.Core.Conversation";

    private static readonly Lazy<string> AppendMessagesResource = new(
        () => EmbeddedResourceReader.Read($"{ResourcePrefix}.Scripts.AppendMessages.lua"));
    private static readonly Lazy<string> LockReleaseResource = new(
        () => EmbeddedResourceReader.Read($"{ResourcePrefix}.Scripts.LockRelease.lua"));
    private static readonly Lazy<string> LockExtendResource = new(
        () => EmbeddedResourceReader.Read($"{ResourcePrefix}.Scripts.LockExtend.lua"));
    private static readonly Lazy<string> SqlServerSchemaResource = new(
        () => EmbeddedResourceReader.Read($"{ResourcePrefix}.Repository.Scripts.SqlServerSchema.sql"));
    private static readonly Lazy<string> SqliteSchemaResource = new(
        () => EmbeddedResourceReader.Read($"{ResourcePrefix}.Repository.Scripts.SqliteSchema.sql"));

    internal static string AppendMessages => AppendMessagesResource.Value;

    internal static string LockRelease => LockReleaseResource.Value;

    internal static string LockExtend => LockExtendResource.Value;

    internal static string SqlServerSchema => SqlServerSchemaResource.Value;

    internal static string SqliteSchema => SqliteSchemaResource.Value;
}
