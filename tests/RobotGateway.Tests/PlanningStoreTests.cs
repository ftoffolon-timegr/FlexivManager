using RobotGateway.Core;

namespace RobotGateway.Tests;

/// <summary>Work-list rules on the real SQLite store.</summary>
public sealed class PlanningStoreTests : IDisposable
{
    private const string User = "planner";
    private readonly TempPlanningDb _db = new();

    public PlanningStoreTests()
    {
        _db.Store.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), User).GetAwaiter().GetResult();
        _db.Store.CreateJobAsync(new JobDraft("B2", "PLAN-B", null, true), User).GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    private Task<ProductionOrder> Add(string number, string code = "A1", int quantity = 2) =>
        _db.Store.CreateOrderAsync(new OrderDraft(number, code, quantity, null), User);

    private async Task<string[]> OpenNumbers() =>
        (await _db.Store.ListOpenOrdersAsync()).Select(o => o.OrderNumber).ToArray();

    private static CycleRecord Cycle(ProductionOrder order, CycleOutcome outcome) =>
        new(order.ProductCode, "PLAN-A", order.Id, order.OrderNumber, "test", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), outcome, null);

    [Fact]
    public async Task Orders_AreListedInInsertionOrder_AndCanBeMoved()
    {
        await Add("OP-1");
        var second = await Add("OP-2");
        var third = await Add("OP-3");

        await _db.Store.MoveOrderAsync(third.Id, -1, User);
        Assert.Equal(["OP-1", "OP-3", "OP-2"], await OpenNumbers());

        await _db.Store.MoveOrderAsync(second.Id, 1, User); // already last: no-op
        Assert.Equal(["OP-1", "OP-3", "OP-2"], await OpenNumbers());
    }

    [Fact]
    public async Task ActiveRow_KeepsItsPlace_AndCannotBeEditedOrMoved()
    {
        await Add("OP-1");
        var second = await Add("OP-2");
        await _db.Store.MarkOrderActiveAsync(second.Id, User);

        Assert.Equal(["OP-1", "OP-2"], await OpenNumbers());
        var version = (await _db.Store.GetOrderAsync(second.Id))!.Version;
        await Assert.ThrowsAsync<PlanningConflictException>(() =>
            _db.Store.UpdateOrderAsync(second.Id, version, new OrderDraft("OP-2", "A1", 9, null), User));
        await Assert.ThrowsAsync<PlanningConflictException>(() => _db.Store.MoveOrderAsync(second.Id, 1, User));
    }

    [Fact]
    public async Task CompletedCycles_AreCounted_AndCompleteTheOrder()
    {
        var order = await Add("OP-1", quantity: 2);
        await _db.Store.MarkOrderActiveAsync(order.Id, User);

        var afterFirst = await _db.Store.RecordCycleAsync(Cycle(order, CycleOutcome.Completed));
        Assert.Equal((1, OrderStatus.Active), (afterFirst!.CompletedCount, afterFirst.Status));

        var afterFault = await _db.Store.RecordCycleAsync(Cycle(order, CycleOutcome.Failed));
        Assert.Equal(1, afterFault!.CompletedCount); // a faulted cycle is not counted

        var afterSecond = await _db.Store.RecordCycleAsync(Cycle(order, CycleOutcome.Completed));
        Assert.Equal((2, OrderStatus.Completed), (afterSecond!.CompletedCount, afterSecond.Status));

        Assert.Empty(await OpenNumbers());
        Assert.Single(await _db.Store.ListClosedOrdersAsync(10));
        Assert.Equal(3, (await _db.Store.ListCyclesAsync(10)).Count);
    }

    [Fact]
    public async Task Cancel_KeepsOrderAsHistory_AndFreesTheNumber()
    {
        var order = await Add("OP-1");
        await _db.Store.MarkOrderActiveAsync(order.Id, User);

        var cancelled = await _db.Store.CancelOrderAsync(order.Id, User);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);

        // A cycle already running when the order was cancelled is recorded, status stays Cancelled.
        var after = await _db.Store.RecordCycleAsync(Cycle(order, CycleOutcome.Completed));
        Assert.Equal(OrderStatus.Cancelled, after!.Status);

        await Add("OP-1"); // number is unique among open orders only
        await Assert.ThrowsAsync<PlanningConflictException>(() => _db.Store.CancelOrderAsync(order.Id, User));
    }

    [Fact]
    public async Task OrderNumber_IsUniqueAmongOpenOrders()
    {
        await Add("OP-1");
        await Assert.ThrowsAsync<PlanningConflictException>(() => Add("OP-1", "B2"));
    }

    [Fact]
    public async Task SeveralRows_CanBeInProgress()
    {
        var first = await Add("OP-1");
        var second = await Add("OP-2");

        await _db.Store.MarkOrderActiveAsync(first.Id, User);
        await _db.Store.MarkOrderActiveAsync(second.Id, User);

        Assert.All(await _db.Store.ListOpenOrdersAsync(), o => Assert.Equal(OrderStatus.Active, o.Status));
    }

    [Fact]
    public async Task OrderNumber_IsOptional()
    {
        await Add("");
        await Add("", "B2");

        Assert.Equal(2, (await _db.Store.ListOpenOrdersAsync()).Count);
    }

    [Fact]
    public async Task Rows_BelongToTheirDay_AndMoveInsideIt()
    {
        var today = WorkDay.Today(TimeProvider.System);
        await Add("T-1");
        var tomorrow1 = await _db.Store.CreateOrderAsync(new OrderDraft("N-1", "A1", 1, null, today.AddDays(1)), User);
        var tomorrow2 = await _db.Store.CreateOrderAsync(new OrderDraft("N-2", "A1", 1, null, today.AddDays(1)), User);

        Assert.Equal(["T-1"], (await _db.Store.ListPlanAsync(today)).Select(o => o.OrderNumber));
        await _db.Store.MoveOrderAsync(tomorrow1.Id, -1, User); // first of its day: no-op, never crosses days
        await _db.Store.MoveOrderAsync(tomorrow2.Id, -1, User);
        Assert.Equal(["N-2", "N-1"], (await _db.Store.ListPlanAsync(today.AddDays(1))).Select(o => o.OrderNumber));
    }

    [Fact]
    public async Task CompletedCycles_CompleteTheRow_OthersDoNotCount()
    {
        var order = await Add("OP-1", quantity: 2);
        await _db.Store.RecordCycleAsync(Cycle(order, CycleOutcome.Completed));
        await _db.Store.RecordCycleAsync(Cycle(order, CycleOutcome.Stopped));
        var partial = (await _db.Store.GetOrderAsync(order.Id))!;
        Assert.Equal((1, OrderStatus.Active), (partial.CompletedCount, partial.Status));

        await _db.Store.RecordCycleAsync(Cycle(order, CycleOutcome.Completed));
        var done = (await _db.Store.GetOrderAsync(order.Id))!;
        Assert.Equal((2, 0, OrderStatus.Completed), (done.CompletedCount, done.Remaining, done.Status));
    }

    [Fact]
    public async Task CopyMissing_MovesTheRemainingQuantity_AndClosesTheSource()
    {
        var today = WorkDay.Today(TimeProvider.System);
        var partial = await Add("OP-1", quantity: 3);
        await _db.Store.RecordCycleAsync(Cycle(partial, CycleOutcome.Completed));
        var done = await Add("OP-2", quantity: 1);
        await _db.Store.RecordCycleAsync(Cycle(done, CycleOutcome.Completed));
        await Add("OP-3", "B2", 2);

        var created = await _db.Store.CopyMissingAsync(today, today.AddDays(1), User);

        Assert.Equal([("OP-1", 2), ("OP-3", 2)], created.Select(o => (o.OrderNumber, o.Quantity)));
        Assert.All(created, o => Assert.Equal(today.AddDays(1), o.WorkDate));
        var source = await _db.Store.ListPlanAsync(today);
        Assert.Equal([OrderStatus.CarriedOver, OrderStatus.Completed, OrderStatus.CarriedOver], source.Select(o => o.Status));
        Assert.Empty(await _db.Store.CopyMissingAsync(today, today.AddDays(1), User)); // nothing left to copy
    }

    [Fact]
    public async Task OneOffCycle_IsRecordedWithoutOrder()
    {
        var cycle = new CycleRecord("B2", "PLAN-B", null, null, "test", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CycleOutcome.Completed, null);

        Assert.Null(await _db.Store.RecordCycleAsync(cycle));
        Assert.Null((await _db.Store.ListCyclesAsync(1))[0].OrderNumber);
    }

    [Fact]
    public void EnsureCreated_DropsTheVelocityColumnsOfOlderDatabases()
    {
        Execute("ALTER TABLE Jobs ADD COLUMN VelocityScale INTEGER NOT NULL DEFAULT 50;"
            + "ALTER TABLE Cycles ADD COLUMN VelocityScale INTEGER NOT NULL DEFAULT 50;");

        _db.Store.EnsureCreated();

        Assert.Equal(0L, Execute("SELECT COUNT(*) FROM pragma_table_info('Jobs') WHERE name = 'VelocityScale'"));
        Assert.Equal(0L, Execute("SELECT COUNT(*) FROM pragma_table_info('Cycles') WHERE name = 'VelocityScale'"));
    }

    private object? Execute(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
