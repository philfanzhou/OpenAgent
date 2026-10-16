using System.Text.Json;
using System.Text.Json.Serialization;
namespace OpenAgent.Runtime.Acp.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(AudioContent), "audio")]
[JsonDerivedType(typeof(ResourceLink), "resource_link")]
[JsonDerivedType(typeof(EmbeddedResource), "resource")]
public abstract record ContentBlock : AcpData
{
    public JsonElement? Annotations { get; init; }
}
public sealed record TextContent(string Text) : ContentBlock;
public sealed record ImageContent(string Data, string MimeType, string? Uri = null) : ContentBlock;
public sealed record AudioContent(string Data, string MimeType) : ContentBlock;
public sealed record ResourceLink(string Uri, string Name, string? MimeType = null, string? Description = null, long? Size = null, string? Title = null) : ContentBlock;
public sealed record EmbeddedResource(ResourceContents Resource) : ContentBlock;
public sealed record ResourceContents(string Uri, string? MimeType = null, string? Text = null, string? Blob = null) : AcpData;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ToolContent), "content")]
[JsonDerivedType(typeof(ToolDiff), "diff")]
[JsonDerivedType(typeof(ToolTerminal), "terminal")]
public abstract record ToolCallContent : AcpData;
public sealed record ToolContent(ContentBlock Content) : ToolCallContent;
public sealed record ToolDiff(string Path, string NewText, string? OldText = null) : ToolCallContent;
public sealed record ToolTerminal(string TerminalId) : ToolCallContent;
public sealed record ToolLocation(string Path, uint? Line = null) : AcpData;
