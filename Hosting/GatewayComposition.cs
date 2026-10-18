using System.Net;
using System.Text.Json.Serialization;
using FlexivManager.Api;
using FlexivManager.Components;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RobotGateway.Core;
using RobotGateway.Mock;

namespace FlexivManager.Hosting;

/// <summary>
/// Builds the two web hosts that live in one process:
/// <list type="bullet">
/// <item>Operator host (loopback only): owns the robot backend, the worker and the command service.</item>
/// <item>Monitoring host (intranet): shares only the read-only singletons (snapshot, events, catalog).
/// It has no backend and no command service, so nothing served from it can move the robot.</item>
/// </list>
/// </summary>
public static class GatewayComposition
{
    public const string OperatorSection = "Hosts:Operator";
    public const string MonitoringSection = "Hosts:Monitoring";

    public static WebApplication BuildOperatorApp(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(App).Assembly.GetName().Name,
        });
        configure?.Invoke(builder);

        var endpoint = GetEndpoint(builder.Configuration, OperatorSection, defaultPort: 8081);
        var address = endpoint.ParseAddress("Operator");
        // No authentication yet: the command host must not be reachable from the network.
        if (!IPAddress.IsLoopback(address))
            throw new InvalidOperationException($"{OperatorSection}:Address must be a loopback address (got {endpoint.Address}).");

        ConfigureWeb(builder, address, endpoint.Port, HostRole.Operator);
        AddRobotGateway(builder.Services, builder.Configuration, builder.Environment);
        // Registered after the worker: starts after it and stops before it.
        builder.Services.AddSingleton<MonitoringHostService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringHostService>());

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    public static WebApplication BuildMonitoringApp(IServiceProvider operatorServices, IConfiguration configuration, IHostEnvironment environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = environment.ContentRootPath,
            EnvironmentName = environment.EnvironmentName,
            ApplicationName = environment.ApplicationName,
        });
        builder.Configuration.AddConfiguration(configuration);

        var endpoint = GetEndpoint(configuration, MonitoringSection, defaultPort: 8080);
        ConfigureWeb(builder, endpoint.ParseAddress("Monitoring"), endpoint.Port, HostRole.Monitoring);

        // Read-only shared state only. Do NOT add IRobotBackend or RobotCommandService here.
        builder.Services.AddSingleton(operatorServices.GetRequiredService<SnapshotStore>());
        builder.Services.AddSingleton(operatorServices.GetRequiredService<EventLog>());
        builder.Services.AddSingleton(operatorServices.GetRequiredService<IProgramCatalog>());
        builder.Services.AddSingleton(operatorServices.GetRequiredService<TimeProvider>());
        // Lifetime is driven by the operator host (console / Windows service).
        builder.Services.AddSingleton<IHostLifetime, EmbeddedHostLifetime>();

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    public static HostEndpointOptions GetEndpoint(IConfiguration configuration, string section, int defaultPort)
    {
        var endpoint = configuration.GetSection(section).Get<HostEndpointOptions>() ?? new HostEndpointOptions();
        if (endpoint.Port == 0 && configuration[$"{section}:Port"] is null)
            endpoint.Port = defaultPort;
        return endpoint;
    }

    private static void ConfigureWeb(WebApplicationBuilder builder, IPAddress address, int port, HostRole role)
    {
        // Explicit endpoint: overrides ASPNETCORE_URLS / launch profile URLs.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(address, port));
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton(role);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    }

    private static void AddRobotGateway(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<RobotWorkerOptions>()
            .Bind(configuration.GetSection(RobotWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MockBackendOptions>()
            .Bind(configuration.GetSection(MockBackendOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        // Only the mock backend exists for now: no real robot is ever contacted.
        services.AddSingleton(sp => new MockBackend(
            sp.GetRequiredService<IOptions<MockBackendOptions>>().Value,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRobotBackend>(sp => sp.GetRequiredService<MockBackend>());

        services.AddSingleton<SnapshotStore>();
        services.AddSingleton(_ => new EventLog());
        services.AddSingleton<IProgramCatalog>(sp => LoadCatalog(sp, environment));
        services.AddSingleton<RobotCommandService>();
        services.AddHostedService<RobotWorker>();
    }

    private static ProgramCatalog LoadCatalog(IServiceProvider sp, IHostEnvironment environment)
    {
        var options = sp.GetRequiredService<IOptions<RobotWorkerOptions>>().Value;
        var path = Path.Combine(environment.ContentRootPath, options.CatalogPath);
        var catalog = ProgramCatalog.LoadJson(path);

        var events = sp.GetRequiredService<EventLog>();
        var time = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<ProgramCatalog>>();
        logger.LogInformation("Program catalog {Path}: {Count} program(s).", path, catalog.Programs.Count);
        foreach (var error in catalog.Errors)
        {
            logger.LogWarning("Program catalog: {Error}", error);
            events.Add(new RobotEvent(time.GetUtcNow(), EventLevel.Warning, "Catalog", error));
        }
        return catalog;
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // HTTP only for now (loopback + dedicated intranet); TLS and authentication come in a later phase.
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapReadOnlyApi();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }

    private sealed class EmbeddedHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
