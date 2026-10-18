using System.Net;
using System.Net.Http.Json;
using FlexivManager.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RobotGateway.Core;

namespace RobotGateway.Tests;

/// <summary>Builds the real two-host composition on dynamic loopback ports.</summary>
public sealed class HostingTests : IAsyncLifetime
{
    private WebApplication _operator = null!;
    private WebApplication _monitoring = null!;
    private readonly HttpClient _http = new();

    private static WebApplication BuildOperator(Dictionary<string, string?>? overrides = null) =>
        GatewayComposition.BuildOperatorApp([], builder =>
        {
            builder.Environment.EnvironmentName = "Testing";
            var settings = new Dictionary<string, string?>
            {
                ["Hosts:Operator:Address"] = "127.0.0.1",
                ["Hosts:Operator:Port"] = "0",
                ["Hosts:Monitoring:Address"] = "127.0.0.1",
                ["Hosts:Monitoring:Port"] = "0",
                ["Robot:CatalogPath"] = "does-not-exist.json",
            };
            foreach (var (key, value) in overrides ?? [])
                settings[key] = value;
            builder.Configuration.AddInMemoryCollection(settings);
        });

    public async Task InitializeAsync()
    {
        _operator = BuildOperator();
        await _operator.StartAsync();
        _monitoring = _operator.Services.GetRequiredService<MonitoringHostService>().App
            ?? throw new InvalidOperationException("Monitoring host not started.");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _operator.StopAsync();
        await _operator.DisposeAsync();
    }

    [Fact]
    public void OperatorHost_HasCommandService()
    {
        Assert.NotNull(_operator.Services.GetService<RobotCommandService>());
        Assert.True(_operator.Services.GetRequiredService<HostRole>().CanCommand);
    }

    [Fact]
    public void MonitoringHost_HasNoCommandServiceAndNoBackend()
    {
        Assert.Null(_monitoring.Services.GetService<RobotCommandService>());
        Assert.Null(_monitoring.Services.GetService<IRobotBackend>());
        Assert.False(_monitoring.Services.GetRequiredService<HostRole>().CanCommand);
    }

    [Fact]
    public void MonitoringHost_SharesTheSameSnapshotStore() =>
        Assert.Same(
            _operator.Services.GetRequiredService<SnapshotStore>(),
            _monitoring.Services.GetRequiredService<SnapshotStore>());

    [Fact]
    public async Task MonitoringHost_ServesReadOnlyApi()
    {
        var baseUrl = _monitoring.Urls.First();

        var status = await _http.GetFromJsonAsync<Dictionary<string, object>>($"{baseUrl}/api/status");
        Assert.NotNull(status);
        Assert.True(status.ContainsKey("state"));

        var events = await _http.GetAsync($"{baseUrl}/api/events");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
    }

    [Fact]
    public async Task ApiRejectsWrites()
    {
        var response = await _http.PostAsync($"{_monitoring.Urls.First()}/api/status", null);
        // 400/405 depending on which endpoint answers: what matters is that nothing is accepted.
        Assert.False(response.IsSuccessStatusCode, $"POST returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task MonitoringFailure_DoesNotStopTheOperatorHost()
    {
        var busyPort = new Uri(_monitoring.Urls.First()).Port;
        await using var app = BuildOperator(new() { ["Hosts:Monitoring:Port"] = busyPort.ToString() });

        await app.StartAsync();

        Assert.Null(app.Services.GetRequiredService<MonitoringHostService>().App);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"{app.Urls.First()}/api/status")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void OperatorHost_RefusesNonLoopbackAddress()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildOperator(new() { ["Hosts:Operator:Address"] = "0.0.0.0" }));
        Assert.Contains("loopback", ex.Message);
    }
}
