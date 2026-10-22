using RobotGateway.Core;

namespace RobotGateway.Tests;

/// <summary>Work-list rules on the real SQLite store.</summary>
public sealed class PlanningStoreTests : IDisposable
{
    private const string User = "planner";
    private readonly TempPlanningDb _db = new();

    public PlanningStoreTests()
    {
        _db.Store.CreateJobAsync(new JobDraft("A1", "PLAN-A", 50, null, true), User).GetAwaiter().GetResult();
        _db.Store.CreateJobAsync(new JobDraft("B2", "PLAN-B", 50, null, true), User).GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    private Task<ProductionOrder> Add(string number, string code = "A1", int quantity = 2) =>
        _db.Store.CreateOrderAsync(new OrderDraft(number, code, quantity, null), User);

    private async Task<string[]> OpenNumbers() =>
        (await _db.Store.ListOpenOrdersAsync()).Select(o => o.OrderNumber).ToArray();

    private static CycleRecord Cycle(ProductionOrder order, CycleOutcome outcome) =>
        new(order.ProductCode, "PLAN-A", 50, order.Id, order.OrderNumber, "test", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), outcome, null);

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
    public async Task ActiveOrder_StaysOnTop_AndCannotBeEditedOrMoved()
    {
        await Add("OP-1");
        var second = await Add("OP-2");
        await _db.Store.MarkOrderActiveAsync(second.Id, User);

        Assert.Equal(["OP-2", "OP-1"], await OpenNumbers());
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
    public async Task OneOffCycle_IsRecordedWithoutOrder()
    {
        var cycle = new CycleRecord("B2", "PLAN-B", 50, null, null, "test", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CycleOutcome.Completed, null);

        Assert.Null(await _db.Store.RecordCycleAsync(cycle));
        Assert.Null((await _db.Store.ListCyclesAsync(1))[0].OrderNumber);
    }
}
