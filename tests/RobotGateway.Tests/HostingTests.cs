using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlexivManager.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RobotGateway.Core;

namespace RobotGateway.Tests;

/// <summary>Builds the real two-host composition on dynamic loopback ports.</summary>
public sealed partial class HostingTests : IAsyncLifetime
{
    private const string Password = "secret-test";
    private const string ApiKey = "test-api-key-0123456789";
    private static readonly string PasswordHash = PasswordHasher.Hash(Password);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hosting-test-{Guid.NewGuid():N}.db");
    private readonly string _importDir = Directory.CreateTempSubdirectory("erp-import-test-").FullName;
    private WebApplication _operator = null!;
    private WebApplication _monitoring = null!;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private WebApplication BuildOperator(Dictionary<string, string?>? overrides = null) =>
        GatewayComposition.BuildOperatorApp([], builder =>
        {
            builder.Environment.EnvironmentName = "Testing";
            var settings = new Dictionary<string, string?>
            {
                ["Hosts:Operator:Address"] = "127.0.0.1",
                ["Hosts:Operator:Port"] = "0",
                ["Hosts:Monitoring:Address"] = "127.0.0.1",
                ["Hosts:Monitoring:Port"] = "0",
                ["Hosts:Monitoring:RequireLoginForEdit"] = "true",
                ["Hosts:Monitoring:Editor:UserName"] = "planner",
                ["Hosts:Monitoring:Editor:PasswordHash"] = PasswordHash,
                ["Hosts:Monitoring:Api:Key"] = ApiKey,
                ["Hosts:Monitoring:Api:ClientName"] = "erp",
                ["Planning:DatabasePath"] = _dbPath,
                ["Erp:ImportFolder"] = _importDir,
                ["Erp:PollInterval"] = "01:00:00",
                ["Erp:MinFileAge"] = "00:00:00",
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
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
        try { Directory.Delete(_importDir, recursive: true); } catch (IOException) { }
    }

    private string Remote(string path) => _monitoring.Urls.First() + path;
    private string Local(string path) => _operator.Urls.First() + path;

    /// <summary>Logs in on the monitoring host; returns the auth cookie or null.</summary>
    private async Task<string?> LoginAsync(string password)
    {
        var page = await _http.GetAsync(Remote("/account/login?returnUrl=/jobs/new"));
        var html = await page.Content.ReadAsStringAsync();
        var antiforgeryCookie = string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var field = TokenField().Match(html);
        Assert.True(field.Success, "antiforgery field not found");

        using var post = new HttpRequestMessage(HttpMethod.Post, Remote("/account/login"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [field.Groups["name"].Value] = WebUtility.HtmlDecode(field.Groups["value"].Value),
                ["userName"] = "planner",
                ["password"] = password,
                ["returnUrl"] = "/jobs/new",
            }),
        };
        post.Headers.Add("Cookie", antiforgeryCookie);
        var response = await _http.SendAsync(post);
        if (response.StatusCode != HttpStatusCode.Redirect)
            return null;
        Assert.Equal("/jobs/new", response.Headers.Location?.OriginalString);
        return response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).First(c => c.StartsWith(".FlexivManager.Planner"));
    }

    [GeneratedRegex("""<input type="hidden" name="(?<name>__RequestVerificationToken)" value="(?<value>[^"]+)" />""")]
    private static partial Regex TokenField();

    // ---- Roles -------------------------------------------------------------------------------

    [Fact]
    public void OperatorHost_Executes_ButCannotEdit()
    {
        Assert.NotNull(_operator.Services.GetService<RobotCommandService>());
        Assert.NotNull(_operator.Services.GetService<ScanService>());
        Assert.Null(_operator.Services.GetService<PlanningService>());
        Assert.Null(_operator.Services.GetService<IPlanningStore>());
        Assert.True(_operator.Services.GetRequiredService<HostRole>().CanCommand);
    }

    [Fact]
    public void MonitoringHost_Edits_ButHasNoCommandServiceAndNoBackend()
    {
        Assert.NotNull(_monitoring.Services.GetService<PlanningService>());
        Assert.Null(_monitoring.Services.GetService<RobotCommandService>());
        Assert.Null(_monitoring.Services.GetService<IRobotBackend>());
        Assert.Null(_monitoring.Services.GetService<ScanService>());
        Assert.False(_monitoring.Services.GetRequiredService<HostRole>().CanCommand);
    }

    [Fact]
    public void MonitoringHost_SharesTheSameSnapshotStore() =>
        Assert.Same(
            _operator.Services.GetRequiredService<SnapshotStore>(),
            _monitoring.Services.GetRequiredService<SnapshotStore>());

    // ---- Read-only API -----------------------------------------------------------------------

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/events")]
    [InlineData("/api/plans")]
    [InlineData("/api/jobs")]
    [InlineData("/api/orders")]
    [InlineData("/api/cycles")]
    [InlineData("/api/plan")]
    [InlineData("/api/plan?date=2026-01-31")]
    [InlineData("/api/report")]
    [InlineData("/api/report?date=2026-01-31")]
    public async Task ReadOnlyApi_IsOpenOnTheRemoteHost(string path) =>
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Remote(path))).StatusCode);

    [Fact]
    public async Task ApiRejectsWrites()
    {
        var response = await _http.PostAsync(Remote("/api/status"), null);
        // 400/405 depending on which endpoint answers: what matters is that nothing is accepted.
        Assert.False(response.IsSuccessStatusCode, $"POST returned {(int)response.StatusCode}");
    }

    // ---- REST write API ----------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, string? key = ApiKey)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (key is not null)
            request.Headers.Add(ApiKeyOptions.HeaderName, key);
        return await _http.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task WriteApi_WithoutTheRightKey_IsRefused(string? key)
    {
        var response = await SendAsync(HttpMethod.Post, Remote("/api/jobs"), new { productCode = "K1", planName = "PLAN-Home" }, key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await _monitoring.Services.GetRequiredService<IPlanningReader>().ListJobsAsync());
    }

    [Fact]
    public async Task WriteApi_WithKey_PlansTheDay_AndTheReportFollows()
    {
        await ImportCodesAsync("api1");
        var job = await SendAsync(HttpMethod.Post, Remote("/api/jobs"), new { productCode = "api1", planName = "PLAN-Home" });
        Assert.Equal(HttpStatusCode.Created, job.StatusCode);
        Assert.Equal("API1", (await JsonAsync(job)).GetProperty("productCode").GetString());

        var row = await SendAsync(HttpMethod.Post, Remote("/api/plan/rows"), new { productCode = "API1", quantity = 3, orderNumber = "ERP-7" });
        Assert.Equal(HttpStatusCode.Created, row.StatusCode);
        var created = await JsonAsync(row);
        Assert.Equal("erp", created.GetProperty("updatedBy").GetString());
        Assert.Equal("Pending", created.GetProperty("status").GetString());

        var plan = await JsonAsync(await _http.GetAsync(Remote("/api/plan")));
        Assert.Equal("API1", Assert.Single(plan.GetProperty("rows").EnumerateArray()).GetProperty("productCode").GetString());

        var report = await JsonAsync(await _http.GetAsync(Remote("/api/report")));
        Assert.Equal(3, report.GetProperty("totals").GetProperty("missing").GetInt32());
        Assert.Equal("Missing", report.GetProperty("rows")[0].GetProperty("progress").GetString());
    }

    [Fact]
    public async Task WriteApi_InvalidRow_Returns400WithTheErrors()
    {
        var response = await SendAsync(HttpMethod.Post, Remote("/api/plan/rows"), new { productCode = "NOPE", quantity = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await JsonAsync(response)).GetProperty("errors");
        Assert.Contains(errors.EnumerateArray(), e => e.GetString()!.Contains("quantità"));
    }

    [Fact]
    public async Task WriteApi_StaleVersion_Returns409()
    {
        await ImportCodesAsync("V1");
        var created = await JsonAsync(await SendAsync(HttpMethod.Post, Remote("/api/jobs"), new { productCode = "V1", planName = "PLAN-Home" }));
        var id = created.GetProperty("id").GetInt32();
        var version = created.GetProperty("version").GetInt32();

        var first = await SendAsync(HttpMethod.Put, Remote($"/api/jobs/{id}"), new { productCode = "V1", planName = "PLAN-Polish", version });
        var second = await SendAsync(HttpMethod.Put, Remote($"/api/jobs/{id}"), new { productCode = "V1", planName = "PLAN-Home", version });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task WriteApi_DoesNotExistOnTheOperatorHost()
    {
        var response = await SendAsync(HttpMethod.Post, Local("/api/jobs"), new { productCode = "L1", planName = "PLAN-Home" });

        Assert.False(response.IsSuccessStatusCode, $"POST returned {(int)response.StatusCode}");
        Assert.Empty(await _operator.Services.GetRequiredService<IPlanningReader>().ListJobsAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/robot")]
    [InlineData("/plans")]
    [InlineData("/jobs")]
    [InlineData("/jobs/new")]
    [InlineData("/orders")]
    [InlineData("/plan")]
    public async Task OperatorHost_RedirectsEveryOtherPageToOperator(string path)
    {
        var response = await _http.GetAsync(Local(path));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/operator", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task OperatorHost_ServesOperatorPage() =>
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Local("/operator"))).StatusCode);

    [Fact]
    public async Task OperatorHost_UnknownPath_IsNotFound() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(Local("/does-not-exist"))).StatusCode);

    [Fact]
    public void ApiKey_ComparesExactly_AndEmptyNeverMatches()
    {
        Assert.True(FlexivManager.Api.PlanningApi.KeyMatches(ApiKey, ApiKey));
        Assert.False(FlexivManager.Api.PlanningApi.KeyMatches(ApiKey + "x", ApiKey));
        Assert.False(FlexivManager.Api.PlanningApi.KeyMatches("", ""));
    }

    // ---- Login -------------------------------------------------------------------------------

    [Theory]
    [InlineData("/jobs/new")]
    [InlineData("/orders/new")]
    public async Task EditPages_RedirectToLogin(string path)
    {
        var response = await _http.GetAsync(Remote(path));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("/robot")]
    [InlineData("/plans")]
    [InlineData("/jobs")]
    [InlineData("/orders")]
    public async Task MonitoringPages_StayOpen(string path) =>
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Remote(path))).StatusCode);

    [Fact]
    public async Task Login_WithRightPassword_OpensEditPages()
    {
        var cookie = await LoginAsync(Password);
        Assert.NotNull(cookie);

        using var request = new HttpRequestMessage(HttpMethod.Get, Remote("/jobs/new"));
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsRefused() =>
        Assert.Null(await LoginAsync("wrong"));

    [Fact]
    public async Task LoginEndpoint_DoesNotExistOnTheOperatorHost() =>
        Assert.NotEqual(HttpStatusCode.OK, (await _http.GetAsync(Local("/account/login"))).StatusCode);

    // ---- Configuration -----------------------------------------------------------------------

    [Fact]
    public async Task EditPages_OpenWhenLoginDisabled()
    {
        var db = Path.Combine(Path.GetTempPath(), $"hosting-test-{Guid.NewGuid():N}.db");
        await using var app = BuildOperator(new()
        {
            ["Hosts:Monitoring:RequireLoginForEdit"] = "false",
            ["Planning:DatabasePath"] = db,
        });
        await app.StartAsync();
        var remote = app.Services.GetRequiredService<MonitoringHostService>().App!;

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(remote.Urls.First() + "/jobs/new")).StatusCode);
        await app.StopAsync();
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

    [Fact]
    public void Backend_DefaultsToMock() =>
        Assert.IsType<RobotGateway.Mock.MockBackend>(_operator.Services.GetRequiredService<IRobotBackend>());

    [Fact]
    public async Task FlexivBackend_IsSelectedByConfiguration_WithoutConnecting()
    {
        var db = Path.Combine(Path.GetTempPath(), $"hosting-test-{Guid.NewGuid():N}.db");
        await using var app = BuildOperator(new()
        {
            ["Robot:Backend"] = "Flexiv",
            ["Robot:Flexiv:SerialNumber"] = "Rizon4-062609",
            ["Planning:DatabasePath"] = db,
        });

        var backend = app.Services.GetRequiredService<IRobotBackend>();
        Assert.IsType<RobotGateway.Flexiv.FlexivBackend>(backend);
        Assert.False(backend.IsConnected); // built, never started: no connection attempt
        Assert.Contains("Rizon4-062609", backend.Name);
    }

    [Fact]
    public void RealRobot_UsesItsOwnDatabase_WithoutDemoData()
    {
        var mock = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Planning:DatabasePath"] = "data/planning.db",
        }).Build();
        var flexiv = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Planning:DatabasePath"] = "data/planning.db",
            ["Robot:Backend"] = "Flexiv",
        }).Build();

        Assert.Equal(Path.Combine("data", "planning.db"), Path.Combine(Path.GetDirectoryName(GatewayComposition.DatabasePath(mock))!, Path.GetFileName(GatewayComposition.DatabasePath(mock))));
        Assert.Equal(Path.Combine("data", "planning-flexiv.db"), GatewayComposition.DatabasePath(flexiv));
    }

    [Fact]
    public async Task RealRobot_NeverGetsDemoJobs()
    {
        var db = Path.Combine(Path.GetTempPath(), $"hosting-test-{Guid.NewGuid():N}.db");
        await using var app = BuildOperator(new()
        {
            ["Robot:Backend"] = "Flexiv",
            ["Robot:Flexiv:SerialNumber"] = "Rizon4-062609",
            ["Planning:DatabasePath"] = db,
            ["Planning:SeedDemoData"] = "true",
        });

        Assert.Empty(await app.Services.GetRequiredService<IPlanningReader>().ListJobsAsync());
    }

    [Fact]
    public void UnknownBackend_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => BuildOperator(new() { ["Robot:Backend"] = "Bogus" }));

    [Fact]
    public void PasswordHasher_VerifiesOnlyTheRightPassword()
    {
        Assert.True(PasswordHasher.Verify(Password, PasswordHash));
        Assert.False(PasswordHasher.Verify("other", PasswordHash));
        Assert.False(PasswordHasher.Verify(Password, "garbage"));
    }
}
