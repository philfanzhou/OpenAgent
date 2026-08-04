using Microsoft.Extensions.Logging;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.Repository;

internal sealed class SqlServerRetryPolicy
{
    private readonly int _retryCount;
    private readonly int _initialDelayMilliseconds;
    private readonly ILogger<SqlServerConversationRepository> _logger;

    internal SqlServerRetryPolicy(
        int retryCount,
        int initialDelayMilliseconds,
        ILogger<SqlServerConversationRepository> logger)
    {
        _retryCount = retryCount;
        _initialDelayMilliseconds = initialDelayMilliseconds;
        _logger = logger;
    }

    internal async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var delayMilliseconds = _initialDelayMilliseconds;
        for (var attempt = 0; attempt <= _retryCount; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < _retryCount)
            {
                ConversationLog.SqlServerRetryAttemptFailed(
                    _logger, exception, attempt + 1, delayMilliseconds);
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
                delayMilliseconds *= 2;
            }
        }
    }
}
