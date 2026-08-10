namespace OpenAgent.Router.Models;

public sealed record IntentRecognitionResult(
    string AgentId,
    double Confidence);
