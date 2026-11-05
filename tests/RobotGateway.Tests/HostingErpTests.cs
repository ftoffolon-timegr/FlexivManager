using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RobotGateway.Core;

namespace RobotGateway.Tests;

/// <summary>ERP interface on the real two-host composition.</summary>
public sealed partial class HostingTests
{
    /// <summary>As if the ERP had sent these article codes (jobs accept only imported codes).</summary>
    private Task ImportCodesAsync(params string[] codes) =>
        _operator.Services.GetRequiredService<IErpStore>()
            .ImportArticlesAsync(codes.Select(c => new ParsedArticle(ProductCode.Normalize(c), c)).ToList(), "test.txt");

    [Fact]
    public async Task JobsApi_RefusesACodeNotReceivedFromTheErp()
    {
        var response = await SendAsync(HttpMethod.Post, Remote("/api/jobs"), new { productCode = "NOT-IMPORTED", planName = "PLAN-Home" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/erp/production")]
    [InlineData("/api/erp/production?date=2026-01-31")]
    [InlineData("/api/erp/articles")]
    public async Task ErpReadApi_IsOpenOnBothHosts(string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Remote(path))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Local(path))).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task ErpImport_WithoutTheRightKey_IsRefused_AndTheFileStays(string? key)
    {
        var file = Path.Combine(_importDir, "codes.txt");
        await File.WriteAllTextAsync(file, "ART-1\r\n");

        var response = await SendAsync(HttpMethod.Post, Remote("/api/erp/articles/import"), null, key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task ErpImport_IsNotOnTheOperatorHost() =>
        Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, Local("/api/erp/articles/import"), null)).StatusCode);

    [Fact]
    public async Task ErpImport_WithKey_ReadsTheFolder_DeletesTheFile_AndListsTheCodes()
    {
        var file = Path.Combine(_importDir, "codes.txt");
        await File.WriteAllTextAsync(file, "art-1\r\nART-2\r\n\r\n");

        var response = await SendAsync(HttpMethod.Post, Remote("/api/erp/articles/import"), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scan = await JsonAsync(response);
        Assert.True(scan.GetProperty("success").GetBoolean());
        Assert.Equal(2, scan.GetProperty("codesAdded").GetInt32());
        Assert.StartsWith("API", scan.GetProperty("trigger").GetString());
        Assert.False(File.Exists(file));

        var articles = await JsonAsync(await _http.GetAsync(Remote("/api/erp/articles")));
        var list = articles.GetProperty("articles").EnumerateArray().ToList();
        Assert.Equal(["art-1", "ART-2"], list.Select(a => a.GetProperty("articleCode").GetString()));
        Assert.All(list, a => Assert.Equal(System.Text.Json.JsonValueKind.Null, a.GetProperty("planName").ValueKind));
    }

    [Fact]
    public async Task ErpImport_UnreachableFolder_Returns503()
    {
        Directory.Delete(_importDir, recursive: true);

        var response = await SendAsync(HttpMethod.Post, Remote("/api/erp/articles/import"), null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Directory.CreateDirectory(_importDir);
    }

    [Fact]
    public async Task ErpProduction_ListsTodaysCompletedCycles_WithTheErpSpelling()
    {
        var store = _operator.Services.GetRequiredService<IErpStore>();
        await store.ImportArticlesAsync([new ParsedArticle("ab-1".ToUpperInvariant(), "ab-1")], "codes.txt");
        var recorder = _operator.Services.GetRequiredService<RobotGateway.Data.SqlitePlanningStore>();
        var start = DateTimeOffset.Now.AddMinutes(-1);
        await recorder.RecordCycleAsync(new CycleRecord("AB-1", "PLAN-A", null, null, "test", start, TimeSpan.FromSeconds(42.4), CycleOutcome.Completed, null));

        var export = await JsonAsync(await _http.GetAsync(Remote("/api/erp/production")));

        var article = Assert.Single(export.GetProperty("articles").EnumerateArray());
        Assert.Equal("ab-1", article.GetProperty("articleCode").GetString());
        Assert.Equal("Completed", article.GetProperty("status").GetString());
        Assert.Equal(42, article.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(1, article.GetProperty("quantity").GetInt32());
        Assert.Equal("PLAN-A", article.GetProperty("robotProgram").GetString());
    }
}
