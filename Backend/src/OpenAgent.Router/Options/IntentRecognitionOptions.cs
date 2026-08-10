namespace OpenAgent.Router.Options;

internal sealed class IntentRecognitionOptions
{
    internal const string SectionName = "RouterSettings:IntentRecognition";

    public bool Enabled { get; set; } = true;
    public string AgentId { get; set; } = "intent-router";
    public string FallbackAgentId { get; set; } = "default";
    public double MinimumConfidence { get; set; } = 0.5;
    public int TimeoutMs { get; set; } = 5000;

    internal static bool IsValid(IntentRecognitionOptions options) =>
        (!options.Enabled || !string.IsNullOrWhiteSpace(options.AgentId))
        && options.TimeoutMs > 0
        && options.MinimumConfidence is >= 0 and <= 1;
}
