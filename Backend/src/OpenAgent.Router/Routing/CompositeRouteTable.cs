using OpenAgent.Router.Observability;

namespace OpenAgent.Router;

public class CompositeRouteTable : IRouteTable
{
    private readonly IRouteTable _dynamicRouteTable;
    private readonly IRouteTable _staticRouteTable;
    private readonly ILogger<CompositeRouteTable> _logger;

    public CompositeRouteTable(
        IRouteTable dynamicRouteTable,
        IRouteTable staticRouteTable,
        ILogger<CompositeRouteTable> logger)
    {
        _dynamicRouteTable = dynamicRouteTable;
        _staticRouteTable = staticRouteTable;
        _logger = logger;
    }

    public string? GetTargetEndpoint(string intent)
    {
        return GetTargetEndpoint(intent, tenantId: null, conversationId: null);
    }

    public string? GetTargetEndpoint(string intent, string? tenantId, string? conversationId)
    {
        return GetTargetEndpoint(intent, capability: null, tenantId, conversationId);
    }

    public string? GetTargetEndpoint(
        string intent,
        string? capability,
        string? tenantId,
        string? conversationId)
    {
        string? dynamicEndpoint = _dynamicRouteTable.GetTargetEndpoint(
            intent, capability, tenantId, conversationId);
        if (!string.IsNullOrEmpty(dynamicEndpoint))
        {
            RouterLog.DynamicDiscoveryReturnedEndpoint(_logger, dynamicEndpoint);
            return dynamicEndpoint;
        }

        string? staticEndpoint = _staticRouteTable.GetTargetEndpoint(
            intent, capability, tenantId, conversationId);
        if (!string.IsNullOrEmpty(staticEndpoint))
        {
            RouterLog.FallbackToStaticEndpoint(_logger, staticEndpoint);
            RouterMeter.RecordDiscoverySelection(intent, capability, "static_fallback");
            return staticEndpoint;
        }

        RouterLog.NoEndpointForIntent(_logger, intent);
        return null;
    }
}
