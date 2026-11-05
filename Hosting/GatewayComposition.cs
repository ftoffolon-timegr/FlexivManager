using System.Net;
using System.Text.Json.Serialization;
using FlexivManager.Api;
using FlexivManager.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RobotGateway.Core;
using RobotGateway.Data;
using RobotGateway.Flexiv;
using RobotGateway.Mock;

namespace FlexivManager.Hosting;

/// <summary>
/// Builds the two web hosts that live in one process:
/// <list type="bullet">
/// <item>Operator host (local, loopback only): owns the robot backend, the worker, the command service and
/// the cycle recorder. Executes jobs; cannot edit planning data.</item>
/// <item>Monitoring host (remote, intranet): read-only robot data plus planning (jobs, work plan) behind the
/// editor login. It has no backend and no command service, so nothing served from it can move the robot.</item>
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
        // No authentication on the local station: the command host must not be reachable from the network.
        if (!IPAddress.IsLoopback(address))
            throw new InvalidOperationException($"{OperatorSection}:Address must be a loopback address (got {endpoint.Address}).");

        ConfigureWeb(builder, address, endpoint.Port, HostRole.Operator);
        // The operator host has no login: the planner policy only guards pages that show
        // "editing is available on the remote station" here anyway.
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(o => o.AddPolicy(Policies.Planner, p => p.RequireAssertion(_ => true)));

        AddRobotGateway(builder.Services, builder.Configuration, builder.Environment);
        // Registered after the worker: starts after it and stops before it.
        builder.Services.AddSingleton<MonitoringHostService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringHostService>());

        var app = builder.Build();
        InitializeDatabase(app.Services, builder.Configuration);
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

        // Shared state. Do NOT add IRobotBackend or RobotCommandService here.
        var store = operatorServices.GetRequiredService<SqlitePlanningStore>();
        builder.Services.AddSingleton(operatorServices.GetRequiredService<SnapshotStore>());
        builder.Services.AddSingleton(operatorServices.GetRequiredService<EventLog>());
        builder.Services.AddSingleton(operatorServices.GetRequiredService<TimeProvider>());
        builder.Services.AddSingleton(operatorServices.GetRequiredService<IOptions<RobotWorkerOptions>>());
        builder.Services.AddSingleton<IPlanningStore>(store);
        builder.Services.AddSingleton<IPlanningReader>(store);
        builder.Services.AddSingleton<IErpStore>(store);
        builder.Services.AddSingleton(operatorServices.GetRequiredService<ArticleImporter>());
        builder.Services.AddSingleton(operatorServices.GetRequiredService<ProductionExportService>());

        // Planning edits behind the single-account login.
        var onlyImportedCodes = operatorServices.GetRequiredService<IOptions<ErpOptions>>().Value.OnlyImportedCodes;
        builder.Services.Configure<PlanningOptions>(o =>
        {
            o.RequireAuthenticatedUser = endpoint.RequireLoginForEdit;
            o.OnlyImportedCodes = onlyImportedCodes;
        });
        builder.Services.AddSingleton<PlanningService>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.LoginPath = AccountEndpoints.LoginPath;
                o.LogoutPath = AccountEndpoints.LogoutPath;
                o.Cookie.Name = ".FlexivManager.Planner";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
            });
        builder.Services.AddAuthorization(o => o.AddPolicy(Policies.Planner, p =>
        {
            if (endpoint.RequireLoginForEdit) p.RequireAuthenticatedUser();
            else p.RequireAssertion(_ => true);
        }));

        // Lifetime is driven by the operator host (console / Windows service).
        builder.Services.AddSingleton<IHostLifetime, EmbeddedHostLifetime>();

        var app = builder.Build();
        if (endpoint.RequireLoginForEdit && endpoint.Editor.PasswordHash.Length == 0)
            app.Logger.LogWarning("No editor password configured (Hosts:Monitoring:Editor:PasswordHash): planning is read-only.");
        if (endpoint.Api.Key.Length == 0)
            app.Logger.LogInformation("No API key configured (Hosts:Monitoring:Api:Key): the REST write API refuses every call.");
        ConfigurePipeline(app);
        app.MapAccountEndpoints(endpoint);
        app.MapPlanningApi(endpoint.Api);
        app.MapErpImportApi(endpoint.Api);
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
        builder.Services.AddCascadingAuthenticationState();
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

        // Robot:Backend selects the robot: Mock (default, simulator) or Flexiv (real robot through the RDK wrapper).
        var backend = configuration[$"{RobotWorkerOptions.SectionName}:Backend"] ?? "Mock";
        if (backend.Equals("Flexiv", StringComparison.OrdinalIgnoreCase))
        {
            services.AddOptions<FlexivBackendOptions>().Bind(configuration.GetSection(FlexivBackendOptions.SectionName));
            services.AddSingleton<IRobotBackend>(sp => new FlexivBackend(sp.GetRequiredService<IOptions<FlexivBackendOptions>>().Value));
        }
        else if (backend.Equals("Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(sp => new MockBackend(
                sp.GetRequiredService<IOptions<MockBackendOptions>>().Value,
                sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IRobotBackend>(sp => sp.GetRequiredService<MockBackend>());
        }
        else
        {
            throw new InvalidOperationException($"Robot:Backend '{backend}' is not supported (use Mock or Flexiv).");
        }

        services.AddSqlitePlanningStore(Path.Combine(environment.ContentRootPath, DatabasePath(configuration)));
        // Local station reads planning data; edits exist only on the monitoring host.
        services.AddSingleton<IPlanningReader>(sp => sp.GetRequiredService<SqlitePlanningStore>());

        AddErpInterface(services, configuration, environment);

        services.AddSingleton<SnapshotStore>();
        services.AddSingleton(_ => new EventLog());
        services.AddSingleton<CycleEvents>();
        services.AddSingleton<RobotCommandService>();
        services.AddSingleton<ScanService>();
        services.AddHostedService<RobotWorker>();
        services.AddHostedService(sp => new CycleRecorder(
            sp.GetRequiredService<CycleEvents>(),
            sp.GetRequiredService<SqlitePlanningStore>(),
            sp.GetRequiredService<EventLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CycleRecorder>>()));
    }

    /// <summary>
    /// ERP interface: article file import (polled here, on the always-running operator host; the manual scan is
    /// exposed on the monitoring host) and the production export (read on both hosts).
    /// </summary>
    private static void AddErpInterface(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<ErpOptions>()
            .Bind(configuration.GetSection(ErpOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IErpStore>(sp => sp.GetRequiredService<SqlitePlanningStore>());
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ErpOptions>>().Value;
            return new ArticleImporter(
                options,
                ResolveImportFolder(options.ImportFolder, environment.ContentRootPath),
                sp.GetRequiredService<IErpStore>(),
                sp.GetRequiredService<EventLog>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ArticleImporter>>());
        });
        services.AddHostedService<ArticleImportService>();
        services.AddSingleton<ProductionExportService>();
    }

    /// <summary>
    /// Absolute import folder. A relative path (local test folder) is resolved against the content root and
    /// created; an absolute or UNC path is the ERP's and is never created here.
    /// </summary>
    public static string ResolveImportFolder(string folder, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return "";
        if (Path.IsPathRooted(folder))
            return folder;
        var full = Path.GetFullPath(Path.Combine(contentRoot, folder));
        Directory.CreateDirectory(full);
        return full;
    }

    private static bool IsRealRobot(IConfiguration configuration) =>
        string.Equals(configuration[$"{RobotWorkerOptions.SectionName}:Backend"], "Flexiv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Planning:DatabasePath; with the real robot "-flexiv" is appended to the file name, so codes created for
    /// the simulator (and the demo data) never show up on the real robot and vice versa.
    /// </summary>
    public static string DatabasePath(IConfiguration configuration)
    {
        var path = (configuration.GetSection(PlanningStoreOptions.SectionName).Get<PlanningStoreOptions>() ?? new()).DatabasePath;
        if (!IsRealRobot(configuration))
            return path;
        var directory = Path.GetDirectoryName(path) ?? "";
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}-flexiv{Path.GetExtension(path)}");
    }

    private static void InitializeDatabase(IServiceProvider services, IConfiguration configuration)
    {
        var store = services.GetRequiredService<SqlitePlanningStore>();
        store.EnsureCreated();
        var options = configuration.GetSection(PlanningStoreOptions.SectionName).Get<PlanningStoreOptions>() ?? new();
        // Demo codes point to simulator plans: never put them in front of an operator on the real robot.
        if (options.SeedDemoData && !IsRealRobot(configuration))
            DemoSeed.SeedIfEmptyAsync(store).GetAwaiter().GetResult();
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // HTTP only for now (loopback + dedicated intranet); TLS comes in a later phase.
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAuthentication();
        app.UseAuthorization();
        if (app.Services.GetRequiredService<HostRole>().CanCommand)
            app.UseOperatorPagesOnly();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapReadOnlyApi();
        app.MapErpReadApi();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }

    private sealed class EmbeddedHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
