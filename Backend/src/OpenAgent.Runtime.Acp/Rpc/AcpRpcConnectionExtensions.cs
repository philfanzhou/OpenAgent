using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>
/// Explicit typed registration and calls over the raw engine API. Payloads convert through
/// the pinned protocol context only — no reflection scan, and no third-party RPC type.
/// </summary>
public static class AcpRpcConnectionExtensions
{
    /// <summary>Sends one typed request; the response projects through the registered payload type.</summary>
    public static Task<TResponse> RequestAsync<TRequest, TResponse>(this AcpRpcConnection connection, AcpMethod<TRequest, TResponse> method, TRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(method);
        return SendTypedAsync<TRequest, TResponse>(connection, method.Name, request, cancellationToken);
    }

    private static async Task<TResponse> SendTypedAsync<TRequest, TResponse>(AcpRpcConnection connection, string method, TRequest request, CancellationToken cancellationToken)
    {
        JsonElement raw = request is null
            ? throw new ArgumentNullException(nameof(request))
            : AcpJson.ToElement(request);
        JsonElement result = await connection.RequestAsync(method, raw, cancellationToken).ConfigureAwait(false);
        return AcpJson.FromElement<TResponse>(result);
    }

    /// <summary>Sends one typed notification.</summary>
    public static Task NotifyAsync<TParameters>(this AcpRpcConnection connection, AcpNotificationMethod<TParameters> method, TParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(method);
        JsonElement raw = parameters is null ? throw new ArgumentNullException(nameof(parameters)) : AcpJson.ToElement(parameters);
        return connection.NotifyAsync(method.Name, raw, cancellationToken);
    }

    /// <summary>Registers one typed request handler; parameter binding failures answer -32602 without invoking the handler.</summary>
    public static void Register<TRequest, TResponse>(this AcpRpcConnection connection, AcpMethod<TRequest, TResponse> method, Func<TRequest, CancellationToken, Task<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        connection.RegisterRequestHandler(method.Name, async (request, cancellationToken) =>
        {
            TRequest typed = Bind<TRequest>(request);
            TResponse response = await handler(typed, cancellationToken).ConfigureAwait(false);
            return response is null ? throw new AcpRpcException(new Error(ErrorCode.InternalError, "The handler returned no response.")) : AcpJson.ToElement(response);
        });
    }

    /// <summary>Registers one typed notification handler.</summary>
    public static void Register<TParameters>(this AcpRpcConnection connection, AcpNotificationMethod<TParameters> method, Func<TParameters, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        connection.RegisterNotificationHandler(method.Name, (notification, cancellationToken) => handler(Bind<TParameters>(notification), cancellationToken));
    }

    /// <summary>Registers one typed notification handler without a completion.</summary>
    public static void Register<TParameters>(this AcpRpcConnection connection, AcpNotificationMethod<TParameters> method, Action<TParameters> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        connection.Register(method, (parameters, _) =>
        {
            handler(parameters);
            return Task.CompletedTask;
        });
    }

    private static T Bind<T>(AcpRpcRequest request) => request.Parameters is { } parameters
        ? AcpJson.FromElement<T>(parameters)
        : throw new AcpRpcException(new Error(ErrorCode.InvalidParams, "The method requires parameters."));

    private static T Bind<T>(AcpRpcNotification notification) => notification.Parameters is { } parameters
        ? AcpJson.FromElement<T>(parameters)
        : throw new AcpRpcException(new Error(ErrorCode.InvalidParams, "The notification requires parameters."));
}
