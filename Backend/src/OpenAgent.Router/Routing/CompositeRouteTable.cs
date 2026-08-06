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
        // Try dynamic discovery first
        var dynamicEndpoint = _dynamicRouteTable.GetTargetEndpoint(intent, tenantId, conversationId);
        if (!string.IsNullOrEmpty(dynamicEndpoint))
        {
            RouterLog.DynamicDiscoveryReturnedEndpoint(_logger, dynamicEndpoint);
            return dynamicEndpoint;
        }

        // Fall back to static configuration
        var staticEndpoint = _staticRouteTable.GetTargetEndpoint(intent, tenantId, conversationId);
        if (!string.IsNullOrEmpty(staticEndpoint))
        {
            RouterLog.FallbackToStaticEndpoint(_logger, staticEndpoint);
            return staticEndpoint;
        }

        RouterLog.NoEndpointForIntent(_logger, intent);
        return null;
    }
}
