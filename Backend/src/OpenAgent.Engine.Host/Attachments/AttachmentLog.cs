namespace OpenAgent.Engine.Host.Attachments;

internal static partial class AttachmentLog
{
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Warning,
        Message = "Failed to delete attachment object during request rollback. ObjectKey={ObjectKey}, ExceptionType={ExceptionType}")]
    internal static partial void RollbackDeleteFailed(
        ILogger logger,
        Exception exception,
        string objectKey,
        string exceptionType);
}
