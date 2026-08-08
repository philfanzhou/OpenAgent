using Microsoft.Extensions.Options;

namespace OpenAgent.Router.Options;

internal sealed class ExternalAgentOptionsValidator(
    IEnumerable<IExternalAgentAdapter> adapters) : IValidateOptions<ExternalAgentRoutingOptions>
{
    private readonly HashSet<string> _adapterNames = new(
        adapters.Select(adapter => adapter.Name),
        StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(
        string? name,
        ExternalAgentRoutingOptions options)
    {
        string[] unsupported = options.Agents
            .Select(agent => agent.Adapter)
            .Where(adapter => !_adapterNames.Contains(adapter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(adapter => adapter, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return unsupported.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"External Agent adapters are not registered: {string.Join(", ", unsupported)}");
    }
}
