namespace OpenAgent.Router.Endpoints;

internal static class ForwardingPathValidator
{
    internal static bool IsSafeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return true;

        foreach (string segment in action.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.Any(character =>
                    !char.IsAsciiLetterOrDigit(character)
                    && character is not '-' and not '_' and not '.' and not '~'))
            {
                return false;
            }
        }

        return true;
    }
}
