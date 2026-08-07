namespace OpenAgent.Router.Options;

internal sealed class IntentRecognitionOptions
{
    internal const string SectionName = "RouterSettings:IntentRecognition";

    public bool Enabled { get; set; } = true;
    public string AgentId { get; set; } = "intent-router";
    public string FallbackAgentId { get; set; } = "default";
    public double MinimumConfidence { get; set; } = 0.5;
    public int TimeoutMs { get; set; } = 5_000;
    public int MaxCandidates { get; set; } = 100;
    public int MaxMessageCharacters { get; set; } = 16_000;
    public int CatalogCacheSeconds { get; set; } = 15;
}
