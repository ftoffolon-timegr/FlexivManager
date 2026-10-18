using RobotGateway.Core;

namespace FlexivManager.Hosting;

/// <summary>
/// Starts and stops the read-only monitoring host together with the operator host.
/// A monitoring failure (e.g. port in use) is logged but never takes down the operator host, which owns STOP.
/// </summary>
public sealed class MonitoringHostService(
    IServiceProvider services,
    IConfiguration configuration,
    IHostEnvironment environment,
    EventLog events,
    TimeProvider time,
    ILogger<MonitoringHostService> logger) : IHostedService, IAsyncDisposable
{
    /// <summary>The running monitoring app; null when disabled or failed to start.</summary>
    public WebApplication? App { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var endpoint = GatewayComposition.GetEndpoint(configuration, GatewayComposition.MonitoringSection, defaultPort: 8080);
        if (!endpoint.Enabled)
        {
            logger.LogInformation("Monitoring host disabled.");
            return;
        }

        var app = GatewayComposition.BuildMonitoringApp(services, configuration, environment);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Monitoring host not started on {endpoint.Address}:{endpoint.Port}: {ex.Message}";
            logger.LogError(ex, "{Message}", message);
            events.Add(new RobotEvent(time.GetUtcNow(), EventLevel.Error, "Hosting", message));
            await app.DisposeAsync();
            return;
        }

        App = app;
        logger.LogInformation("Monitoring host (read-only) listening on {Urls}", string.Join(", ", app.Urls));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (App is not null)
            await App.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (App is not null)
            await App.DisposeAsync();
    }
}
