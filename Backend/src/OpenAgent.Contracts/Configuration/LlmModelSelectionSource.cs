namespace OpenAgent.Contracts.Configuration;

public enum LlmModelSelectionSource
{
    Agent = 0,
    Conversation = 1,
    Message = 2,
    AgentFallback = 3
}
