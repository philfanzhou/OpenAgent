namespace OpenAgent.Contracts.Helpers;

public static class DictionaryHelper
{
    public static bool TryGetValueIgnoreCase(
        IReadOnlyDictionary<string, object> context,
        string key,
        out object value)
    {
        foreach (var kvp in context)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }
}
