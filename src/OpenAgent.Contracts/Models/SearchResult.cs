namespace OpenAgent.Contracts.Models;

public class SearchResult
{
    public string Content { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public double RelevanceScore { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string? RagInstanceId { get; set; }
}
