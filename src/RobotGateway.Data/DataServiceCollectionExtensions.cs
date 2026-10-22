using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RobotGateway.Core;

namespace RobotGateway.Data;

public sealed class PlanningStoreOptions
{
    public const string SectionName = "Planning";

    /// <summary>SQLite file, relative to the content root.</summary>
    public string DatabasePath { get; set; } = "data/planning.db";

    /// <summary>Adds sample jobs and orders to an empty database (development only).</summary>
    public bool SeedDemoData { get; set; }
}

public static class DataServiceCollectionExtensions
{
    /// <summary>Registers <see cref="SqlitePlanningStore"/> (singleton) on the given absolute file path.</summary>
    public static IServiceCollection AddSqlitePlanningStore(this IServiceCollection services, string databaseFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databaseFile, Pooling = true }.ToString();
        services.AddDbContextFactory<PlanningDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<SqlitePlanningStore>();
        return services;
    }
}
