namespace OpenAgent.Router.Options;

/// <summary>Connection to the decision model service (POST /v1/decisions). Disabled while Endpoint is empty.</summary>
internal sealed class DecisionModelOptions
{
    internal const string SectionName = "RouterSettings:IntentRecognition:DecisionModel";

    public string? Endpoint { get; set; }
    public int TimeoutMs { get; set; } = 3000;
    public double MinimumConfidence { get; set; } = 0.5;
    public Dictionary<string, string> Headers { get; set; } = [];

    internal bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint);

    internal static bool IsValid(DecisionModelOptions options) =>
        (!options.IsEnabled
            || (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        && options.TimeoutMs > 0
        && options.MinimumConfidence is >= 0 and <= 1;
}
