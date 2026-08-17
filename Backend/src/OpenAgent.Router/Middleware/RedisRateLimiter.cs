using System.Collections.Concurrent;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;
using StackExchange.Redis;

namespace OpenAgent.Router;

public class RedisRateLimiter : IRateLimiter
{
    private const string TokenBucketScript = """
        local key = KEYS[1]
        local rate = tonumber(ARGV[1])
        local capacity = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])
        local info = redis.call('HMGET', key, 'tokens', 'last_refreshed')
        local tokens = tonumber(info[1])
        local last_refreshed = tonumber(info[2])

        if tokens == nil then
            tokens = capacity
            last_refreshed = now
        end

        local delta = math.max(0, now - last_refreshed)
        local filled_tokens = math.min(capacity, tokens + ((delta * rate) / 1000))
        local allowed = 0
        local retry_after_ms = 0
        if filled_tokens >= 1 then
            allowed = 1
            filled_tokens = filled_tokens - 1
        else
            retry_after_ms = math.ceil(((1 - filled_tokens) / rate) * 1000)
        end

        redis.call('HMSET', key, 'tokens', filled_tokens, 'last_refreshed', now)
        redis.call('PEXPIRE', key, math.ceil((capacity / rate) * 1000) + 1000)
        return { allowed, retry_after_ms }
        """;

    private readonly ConcurrentDictionary<string, LocalBucket> _localBuckets = new(StringComparer.Ordinal);
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<RedisRateLimiter> _logger;
    private readonly RateLimitSettings _settings;
    private readonly TimeProvider _timeProvider;

    public RedisRateLimiter(
        IConfiguration configuration,
        ILogger<RedisRateLimiter> logger,
        IConnectionMultiplexer? redis = null)
        : this(
            RateLimitSettings.FromConfiguration(configuration),
            logger,
            redis,
            TimeProvider.System)
    {
    }

    internal RedisRateLimiter(
        RateLimitSettings settings,
        ILogger<RedisRateLimiter> logger,
        IConnectionMultiplexer? redis,
        TimeProvider timeProvider)
    {
        _settings = settings;
        _logger = logger;
        _redis = redis;
        _timeProvider = timeProvider;
    }

    public async Task<RateLimitDecision> AcquireAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        if (_redis == null)
        {
            return HandleRedisFailure(clientId, null);
        }

        try
        {
            IDatabase database = _redis.GetDatabase();
            long nowMilliseconds = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            RedisResult result = await database.ScriptEvaluateAsync(
                TokenBucketScript,
                [$"ratelimit:{clientId}"],
                [_settings.RequestsPerSecond, _settings.BurstCapacity, nowMilliseconds])
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            RedisResult[] values = (RedisResult[])result!;
            bool isAllowed = (long)values[0] == 1;
            TimeSpan retryAfter = TimeSpan.FromMilliseconds(Math.Max((long)values[1], 0));
            RateLimitDecision decision = new(isAllowed, retryAfter, false, "redis");
            RouterMeter.RecordRateLimitDecision(decision);
            return decision;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RedisException ex)
        {
            return HandleRedisFailure(clientId, ex);
        }
        catch (Exception ex)
        {
            return HandleRedisFailure(clientId, ex);
        }
    }

    private RateLimitDecision HandleRedisFailure(string clientId, Exception? exception)
    {
        if (exception != null)
        {
            RouterLog.RateLimitRedisFailed(_logger, exception, clientId, _settings.FailureMode.ToString());
        }
        else
        {
            RouterLog.RateLimitRedisNotConfigured(_logger, clientId, _settings.FailureMode.ToString());
        }

        RateLimitDecision decision = _settings.FailureMode switch
        {
            RateLimitFailureMode.FailOpen => new(true, TimeSpan.Zero, true, "fail_open"),
            RateLimitFailureMode.FailClosed => new(false, TimeSpan.FromSeconds(1), true, "fail_closed"),
            _ => AcquireLocal(clientId)
        };
        RouterMeter.RecordRateLimitDecision(decision);
        return decision;
    }

    private RateLimitDecision AcquireLocal(string clientId)
    {
        LocalBucket bucket = _localBuckets.GetOrAdd(
            clientId,
            _ => new LocalBucket(_settings.BurstCapacity, _timeProvider.GetUtcNow()));
        lock (bucket)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            double elapsedSeconds = Math.Max((now - bucket.LastRefreshed).TotalSeconds, 0);
            bucket.Tokens = Math.Min(
                _settings.BurstCapacity,
                bucket.Tokens + (elapsedSeconds * _settings.RequestsPerSecond));
            bucket.LastRefreshed = now;
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens--;
                return new(true, TimeSpan.Zero, true, "local");
            }

            double retrySeconds = (1 - bucket.Tokens) / _settings.RequestsPerSecond;
            return new(false, TimeSpan.FromSeconds(retrySeconds), true, "local");
        }
    }

    private sealed class LocalBucket(double tokens, DateTimeOffset lastRefreshed)
    {
        internal double Tokens { get; set; } = tokens;
        internal DateTimeOffset LastRefreshed { get; set; } = lastRefreshed;
    }
}
