using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;
using OpenAgent.Engine.Registry;

namespace OpenAgent.Engine.Runtime;

internal class HeartbeatService : BackgroundService
{
    private readonly IEngineRegistry _registry;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private bool _portSet;

    public HeartbeatService(
        IEngineRegistry registry,
        ILogger<HeartbeatService> logger,
        IOptionsMonitor<HeartbeatOptions> options,
        IHostApplicationLifetime lifetime)
    {
        _registry = registry;
        _logger = logger;
        _options = options;

        lifetime.ApplicationStarted.Register(() =>
        {
            if (_registry is RedisRegistry redisRegistry)
            {
                var heartbeatOptions = _options.CurrentValue;
                var host = heartbeatOptions.AdvertisedHost;
                var port = heartbeatOptions.AdvertisedPort ?? DetectPort();
                redisRegistry.SetHost(string.IsNullOrWhiteSpace(host) ? Environment.MachineName : host);
                redisRegistry.SetPort(port);
                _portSet = true;
                EngineLog.PortDetected(_logger, port);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _registry.RegisterAsync();
                        EngineLog.EngineRegisteredAfterPortDetection(_logger);
                    }
                    catch (Exception ex)
                    {
                        EngineLog.InitialRegistrationFailed(_logger, ex);
                    }
                });
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EngineLog.HeartbeatServiceStarting(_logger);

        while (!_portSet && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_registry.IsRegistered)
                {
                    EngineLog.HeartbeatRetryingRegistration(_logger);
                    await _registry.RegisterAsync(stoppingToken);
                }
                else
                {
                    await _registry.HeartbeatAsync(stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(_options.CurrentValue.IntervalSeconds, 1)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EngineLog.HeartbeatFailed(_logger, ex);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(_options.CurrentValue.RetryDelaySeconds, 1)), stoppingToken); } catch { break; }
            }
        }

        EngineLog.HeartbeatServiceStopped(_logger);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await _registry.DeregisterAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            EngineLog.EngineDeregisterFailed(_logger, ex);
        }
    }

    private static int DetectPort()
    {
        var httpPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        if (!string.IsNullOrEmpty(httpPorts))
        {
            var firstPort = httpPorts.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstPort != null && int.TryParse(firstPort, out int port))
            {
                return port;
            }
        }

        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrEmpty(urls))
        {
            var entries = urls.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                if (entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(entry, UriKind.Absolute, out var parsedUri))
                    {
                        return parsedUri.Port;
                    }
                }
            }
        }

        return 80;
    }
}
