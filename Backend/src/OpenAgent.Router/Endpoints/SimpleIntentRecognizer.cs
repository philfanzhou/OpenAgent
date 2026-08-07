namespace OpenAgent.Router;

public class SimpleIntentRecognizer : IIntentRecognizer
{
    public Task<string> RecognizeAsync(string query, CancellationToken cancellationToken = default)
    {
        if (query.Contains("workflow", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("workflow");
        return Task.FromResult("chat");
    }
}
