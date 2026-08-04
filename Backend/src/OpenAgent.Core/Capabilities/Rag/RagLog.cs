using Microsoft.Extensions.Logging;

namespace OpenAgent.Core.Capabilities.Rag;

internal static partial class RagLog
{
    // --- RagService: IndexDocumentAsync ---

    [LoggerMessage(EventId = 1271, Level = LogLevel.Warning, Message = "Target RAG instance not found or not accessible: {RagInstanceId}")]
    public static partial void TargetInstanceNotFound(ILogger logger, string? ragInstanceId);

    // --- RagService: SearchDetailedAsync ---

    [LoggerMessage(EventId = 1273, Level = LogLevel.Error, Message = "Error searching RAG instance {InstanceId}")]
    public static partial void SearchInstanceFailed(ILogger logger, Exception exception, string instanceId);

    // --- RagService: IndexToExternalAsync ---

    [LoggerMessage(EventId = 1274, Level = LogLevel.Warning, Message = "RAG instance {InstanceId} has no ApiEndpoint configured. Skipping indexing.")]
    public static partial void InstanceMissingApiEndpointSkippingIndexing(ILogger logger, string instanceId);

    [LoggerMessage(EventId = 1275, Level = LogLevel.Error, Message = "No RAG adapter found for instance {InstanceId}")]
    public static partial void NoAdapterFoundForIndexing(ILogger logger, string instanceId);

    [LoggerMessage(EventId = 1276, Level = LogLevel.Debug, Message = "RAG instance {InstanceId} adapter {AdapterName} does not support indexing")]
    public static partial void AdapterDoesNotSupportIndexing(ILogger logger, string instanceId, string adapterName);

    [LoggerMessage(EventId = 1278, Level = LogLevel.Error, Message = "Failed to index document to external RAG system for instance {InstanceId}")]
    public static partial void IndexFailed(ILogger logger, Exception exception, string instanceId);

    // --- RagService: SearchExternalDetailedAsync ---

    [LoggerMessage(EventId = 1279, Level = LogLevel.Warning, Message = "RAG instance {InstanceId} has no ApiEndpoint configured. Skipping search.")]
    public static partial void InstanceMissingApiEndpointSkippingSearch(ILogger logger, string instanceId);

    [LoggerMessage(EventId = 1280, Level = LogLevel.Error, Message = "No RAG adapter found for instance {InstanceId}")]
    public static partial void NoAdapterFoundForSearch(ILogger logger, string instanceId);

    [LoggerMessage(EventId = 1281, Level = LogLevel.Error, Message = "Failed to search external RAG system for instance {InstanceId}")]
    public static partial void SearchFailed(ILogger logger, Exception exception, string instanceId);

    // --- RagSearchTool ---

    [LoggerMessage(EventId = 1283, Level = LogLevel.Error, Message = "Error executing RAG search tool")]
    public static partial void SearchToolFailed(ILogger logger, Exception exception);
}
