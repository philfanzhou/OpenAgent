using Microsoft.Extensions.Logging;
using OpenAgent.Core.Conversation;

namespace OpenAgent.Core.Conversation.Store;

public sealed class ConversationStoreMetrics
{
    private long _hitCount;
    private long _missCount;
    private long _messagesLoaded;
    private long _messagesWritten;
    private long _readFailures;
    private long _writeFailures;
    private long _coldArchiveSuccesses;
    private long _coldArchiveFailures;

    // Latency accumulators (sum of milliseconds per operation type)
    private long _readLatencySum;
    private long _readOpCount;
    private long _writeLatencySum;
    private long _writeOpCount;
    private long _coldArchiveLatencySum;
    private long _coldArchiveOpCount;

    public void RecordHit() => Interlocked.Increment(ref _hitCount);
    public void RecordMiss() => Interlocked.Increment(ref _missCount);
    public void RecordMessagesLoaded(int count) => Interlocked.Add(ref _messagesLoaded, count);
    public void RecordMessagesWritten(int count) => Interlocked.Add(ref _messagesWritten, count);
    public void RecordReadFailure() => Interlocked.Increment(ref _readFailures);
    public void RecordWriteFailure() => Interlocked.Increment(ref _writeFailures);
    public void RecordReadLatency(long ms)
    {
        Interlocked.Add(ref _readLatencySum, ms);
        Interlocked.Increment(ref _readOpCount);
    }
    public void RecordWriteLatency(long ms)
    {
        Interlocked.Add(ref _writeLatencySum, ms);
        Interlocked.Increment(ref _writeOpCount);
    }
    public void RecordColdArchiveSuccess() => Interlocked.Increment(ref _coldArchiveSuccesses);
    public void RecordColdArchiveFailure() => Interlocked.Increment(ref _coldArchiveFailures);
    public void RecordColdArchiveLatency(long ms)
    {
        Interlocked.Add(ref _coldArchiveLatencySum, ms);
        Interlocked.Increment(ref _coldArchiveOpCount);
    }

    public void LogSnapshot(ILogger logger)
    {
        var readOps = Interlocked.Read(ref _readOpCount);
        var writeOps = Interlocked.Read(ref _writeOpCount);
        var coldOps = Interlocked.Read(ref _coldArchiveOpCount);

        var avgReadMs = readOps > 0 ? Interlocked.Read(ref _readLatencySum) / (double)readOps : 0;
        var avgWriteMs = writeOps > 0 ? Interlocked.Read(ref _writeLatencySum) / (double)writeOps : 0;
        var avgColdMs = coldOps > 0 ? Interlocked.Read(ref _coldArchiveLatencySum) / (double)coldOps : 0;

        ConversationLog.MetricsSnapshot(
            logger,
            Interlocked.Read(ref _hitCount),
            Interlocked.Read(ref _missCount),
            Interlocked.Read(ref _messagesLoaded),
            Interlocked.Read(ref _messagesWritten),
            Interlocked.Read(ref _readFailures),
            Interlocked.Read(ref _writeFailures),
            Interlocked.Read(ref _coldArchiveSuccesses),
            Interlocked.Read(ref _coldArchiveFailures),
            avgReadMs,
            avgWriteMs,
            avgColdMs);
    }
}
