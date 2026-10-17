using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>Wire frames the engine observes but cannot deliver to a caller.</summary>
public enum AcpRpcDiagnosticKind
{
    /// <summary>A response arrived whose ID matches no pending outbound call.</summary>
    UnmatchedResponse,

    /// <summary>A response for a call that already completed or was cancelled; the result is discarded verbatim.</summary>
    DuplicateResponse,

    /// <summary>A malformed response envelope was dropped; nothing is written back to avoid a response loop.</summary>
    MalformedResponse,

    /// <summary>A notification without a registered handler, or a request-kind method used as a notification.</summary>
    UnhandledNotification,

    /// <summary>A $/cancel_request named no pending outbound call and no running inbound handler.</summary>
    UnmatchedCancellation,

    /// <summary>An inbound request handler or notification handler ended abnormally; the wire already carries its error.</summary>
    HandlerFault,

    /// <summary>An inbound request was rejected locally before any handler ran.</summary>
    RejectedRequest
}

/// <summary>One diagnostic observation. Values are protocol metadata only; message bodies are never attached.</summary>
public sealed record AcpRpcDiagnosticEvent(AcpRpcDiagnosticKind Kind, string? Method, RpcId? Id, string Detail);
