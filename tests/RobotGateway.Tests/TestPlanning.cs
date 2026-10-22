using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RobotGateway.Core;
using RobotGateway.Data;

namespace RobotGateway.Tests;

/// <summary>In-memory planning data for tests that do not need the database.</summary>
internal sealed class FakePlanning : IPlanningReader
{
    public List<Job> Jobs { get; } = [];
    public List<ProductionOrder> Orders { get; } = [];

    public Job AddJob(string code, string plan, int velocity = 100, bool enabled = true)
    {
        var job = new Job { Id = Jobs.Count + 1, ProductCode = code, PlanName = plan, VelocityScale = velocity, Enabled = enabled, Version = 1 };
        Jobs.Add(job);
        return job;
    }

    public ProductionOrder AddOrder(string number, string code, int quantity, OrderStatus status = OrderStatus.Pending, int done = 0)
    {
        var order = new ProductionOrder
        {
            Id = Orders.Count + 1, OrderNumber = number, ProductCode = code, Quantity = quantity,
            CompletedCount = done, Status = status, Position = Orders.Count + 1,
        };
        Orders.Add(order);
        return order;
    }

    public Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Job>>(Jobs);
    public Task<Job?> GetJobAsync(int id, CancellationToken ct = default) => Task.FromResult(Jobs.FirstOrDefault(j => j.Id == id));
    public Task<Job?> FindJobByCodeAsync(string productCode, CancellationToken ct = default) =>
        Task.FromResult(Jobs.FirstOrDefault(j => j.ProductCode == ProductCode.Normalize(productCode)));
    public Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int jobId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobHistoryEntry>>([]);
    public Task<IReadOnlyList<ProductionOrder>> ListOpenOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductionOrder>>(Orders.Where(o => o.IsOpen).OrderBy(o => o.Status == OrderStatus.Active ? 0 : 1).ThenBy(o => o.Position).ToList());
    public Task<IReadOnlyList<ProductionOrder>> ListClosedOrdersAsync(int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductionOrder>>(Orders.Where(o => !o.IsOpen).ToList());
    public Task<ProductionOrder?> GetOrderAsync(int id, CancellationToken ct = default) => Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));
    public Task<IReadOnlyList<CycleRecord>> ListCyclesAsync(int max, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CycleRecord>>([]);
}

/// <summary>Real SQLite store on a temporary file.</summary>
internal sealed class TempPlanningDb : IDisposable
{
    private readonly ServiceProvider _services;

    public TempPlanningDb(TimeProvider? time = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"planning-test-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddSqlitePlanningStore(Path);
        _services = services.BuildServiceProvider();
        Store = _services.GetRequiredService<SqlitePlanningStore>();
        Store.EnsureCreated();
    }

    public string Path { get; }
    public SqlitePlanningStore Store { get; }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(Path); } catch (IOException) { }
    }
}
