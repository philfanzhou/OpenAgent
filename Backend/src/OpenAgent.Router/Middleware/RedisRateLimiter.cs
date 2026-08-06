using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router;

public class RedisRateLimiter : IRateLimiter
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly IConfiguration _config;
    private readonly ILogger<RedisRateLimiter> _logger;

    public RedisRateLimiter(
        IConfiguration config,
        ILogger<RedisRateLimiter> logger,
        IConnectionMultiplexer? redis = null)
    {
        _config = config;
        _logger = logger;
        _redis = redis;
    }

    public async Task<bool> IsAllowedAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (_redis == null)
        {
            return true;
        }

        try
        {
            var db = _redis.GetDatabase();
            var rps = _config.GetValue<int>("RouterSettings:RateLimiting:RequestsPerSecond", 100);
            var burst = _config.GetValue<int>("RouterSettings:RateLimiting:BurstCapacity", 200);

            // Simple Lua script for Token Bucket rate limiting
            var script = @"
                local key = KEYS[1]
                local rate = tonumber(ARGV[1])
                local capacity = tonumber(ARGV[2])
                local now = tonumber(ARGV[3])
                local requested = 1

                local info = redis.call('HMGET', key, 'tokens', 'last_refreshed')
                local tokens = tonumber(info[1])
                local last_refreshed = tonumber(info[2])

                if tokens == nil then
                    tokens = capacity
                    last_refreshed = now
                end

                local delta = math.max(0, now - last_refreshed)
                local filled_tokens = math.min(capacity, tokens + (delta * rate))

                if filled_tokens >= requested then
                    local new_tokens = filled_tokens - requested
                    redis.call('HMSET', key, 'tokens', new_tokens, 'last_refreshed', now)
                    redis.call('EXPIRE', key, math.ceil(capacity / rate) + 1)
                    return 1
                else
                    redis.call('HMSET', key, 'tokens', filled_tokens, 'last_refreshed', now)
                    return 0
                end
            ";

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var result = await db.ScriptEvaluateAsync(script, new RedisKey[] { $"ratelimit:{clientId}" }, new RedisValue[] { rps, burst, now });
            return (int)result == 1;
        }
        catch (RedisConnectionException ex)
        {
            RouterLog.RateLimitConnectionFailed(_logger, ex, clientId);
            return true;
        }
        catch (Exception ex)
        {
            RouterLog.RateLimitUnexpectedError(_logger, ex, clientId);
            return true;
        }
    }
}
