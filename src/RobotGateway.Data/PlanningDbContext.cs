using Microsoft.EntityFrameworkCore;
using RobotGateway.Core;

namespace RobotGateway.Data;

public sealed class PlanningDbContext(DbContextOptions<PlanningDbContext> options) : DbContext(options)
{
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<JobHistoryEntity> JobHistory => Set<JobHistoryEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<CycleEntity> Cycles => Set<CycleEntity>();
    public DbSet<ArticleEntity> Articles => Set<ArticleEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<JobEntity>(e =>
        {
            e.ToTable("Jobs");
            e.HasIndex(j => j.ProductCode).IsUnique();
            e.Property(j => j.ProductCode).HasMaxLength(ProductCode.MaxLength).IsRequired();
            e.Property(j => j.PlanName).HasMaxLength(200).IsRequired();
            e.Property(j => j.Description).HasMaxLength(PlanningValidator.MaxDescriptionLength);
            e.Property(j => j.Version).IsConcurrencyToken();
        });

        model.Entity<JobHistoryEntity>(e =>
        {
            e.ToTable("JobHistory");
            e.HasIndex(h => h.JobId);
        });

        model.Entity<OrderEntity>(e =>
        {
            e.ToTable("Orders");
            e.HasIndex(o => new { o.Status, o.Position });
            e.HasIndex(o => new { o.WorkDate, o.Position });
            e.HasIndex(o => o.OrderNumber);
            e.Property(o => o.OrderNumber).HasMaxLength(PlanningValidator.MaxOrderNumberLength).IsRequired();
            e.Property(o => o.ProductCode).HasMaxLength(ProductCode.MaxLength).IsRequired();
            e.Property(o => o.Notes).HasMaxLength(PlanningValidator.MaxNotesLength);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(o => o.Version).IsConcurrencyToken();
        });

        model.Entity<CycleEntity>(e =>
        {
            e.ToTable("Cycles");
            e.HasIndex(c => c.OrderId);
            e.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(16);
        });

        // Article codes received from the ERP. Keep in sync with SqlitePlanningStore.AddArticlesTable.
        model.Entity<ArticleEntity>(e =>
        {
            e.ToTable("Articles");
            e.HasIndex(a => a.Code).IsUnique();
            e.Property(a => a.Code).HasMaxLength(ProductCode.MaxLength).IsRequired();
            e.Property(a => a.RawCode).HasMaxLength(ProductCode.MaxLength).IsRequired();
            e.Property(a => a.SourceFile).HasMaxLength(260).IsRequired();
        });
    }
}

public sealed class JobEntity
{
    public int Id { get; set; }
    public string ProductCode { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public bool IsService { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed class JobHistoryEntity
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public int Version { get; set; }
    public string Action { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string User { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Json { get; set; } = "";
}

public sealed class OrderEntity
{
    public int Id { get; set; }
    public DateOnly WorkDate { get; set; }
    public string OrderNumber { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public int Quantity { get; set; }
    public int CompletedCount { get; set; }
    public OrderStatus Status { get; set; }
    public string? Notes { get; set; }
    public int Position { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed class CycleEntity
{
    public long Id { get; set; }
    public int? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public string ProductCode { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public CycleOutcome Outcome { get; set; }
    public string? Message { get; set; }
}

public sealed class ArticleEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string RawCode { get; set; } = "";
    public DateTimeOffset FirstImportedAt { get; set; }
    public DateTimeOffset LastImportedAt { get; set; }
    public string SourceFile { get; set; } = "";
}
