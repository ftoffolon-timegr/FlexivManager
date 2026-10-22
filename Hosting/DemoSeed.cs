using RobotGateway.Core;
using RobotGateway.Data;

namespace FlexivManager.Hosting;

/// <summary>Sample jobs and orders for the mock robot (Planning:SeedDemoData, development only).</summary>
public static class DemoSeed
{
    public static async Task SeedIfEmptyAsync(SqlitePlanningStore store)
    {
        if ((await store.ListJobsAsync()).Count > 0)
            return;

        const string user = "demo-seed";
        await store.CreateJobAsync(new JobDraft("A100", "PLAN-PickPlace", 50, "Pick & place, tray A", true), user);
        await store.CreateJobAsync(new JobDraft("A200", "PLAN-PickPlace", 30, "Pick & place, fragile parts", true), user);
        await store.CreateJobAsync(new JobDraft("P300", "PLAN-Polish", 40, "Polishing", true), user);
        await store.CreateJobAsync(new JobDraft("HOME", "PLAN-Home", 20, "Back to home position", true), user);
        await store.CreateJobAsync(new JobDraft("FAULT", "PLAN-FaultDemo", 100, "Mock only: simulated collision after 3 s", true), user);

        await store.CreateOrderAsync(new OrderDraft("OP-0001", "A100", 3, "First demo order"), user);
        await store.CreateOrderAsync(new OrderDraft("OP-0002", "P300", 2, null), user);
        await store.CreateOrderAsync(new OrderDraft("OP-0003", "A200", 5, null), user);
    }
}
