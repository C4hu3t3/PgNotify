using Microsoft.EntityFrameworkCore;
using PgNotify;

namespace PgNotify.IntegrationTests.TestModels;

public class ProjectedOrder
{
    public int Id { get; set; }
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public string InternalNote { get; set; } = "";
}

public sealed class ProjectedOrderDbContext(DbContextOptions<ProjectedOrderDbContext> options) : DbContext(options)
{
    public DbSet<ProjectedOrder> Orders => Set<ProjectedOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ProjectedOrder>(b =>
        {
            b.ToTable("projected_orders");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate();
                o.WithPayload(x => new { x.Status, x.Total });
            });
        });
}

/// <summary>
/// Binds because the payload was declared to have this shape — no knowledge of which payload
/// default the configuration style picked, and no unbound members.
/// </summary>
public sealed record ProjectedOrderUpdated(int Id, string Status, decimal Total);

/// <summary>
/// Composite key on purpose: a projected payload carries the key as a <c>keys</c> object rather
/// than a top-level <c>id</c>, and that shape had never been compiled by a real server.
/// </summary>
public class ProjectedLine
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class ProjectedLineDbContext(DbContextOptions<ProjectedLineDbContext> options) : DbContext(options)
{
    public DbSet<ProjectedLine> Lines => Set<ProjectedLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ProjectedLine>(b =>
        {
            b.ToTable("projected_lines");
            b.HasKey(x => new { x.OrderId, x.LineNumber });
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnDelete();
                o.WithPayload(x => x.Description);
            });
        });
}

public sealed record ProjectedLineInserted(Dictionary<string, int> Keys, string Description);

/// <summary>
/// The delete side is what really needs a server: the trigger has to read the projected column off
/// <c>OLD</c>. Referencing <c>NEW</c> in a DELETE trigger is a runtime error in PL/pgSQL, which no
/// amount of asserting on generated SQL text would catch.
/// </summary>
public sealed record ProjectedLineDeleted(Dictionary<string, int> Keys, string Description);

/// <summary>The hand-written shape a composite-key projection's payload is bound into.</summary>
public sealed record ProjectedLineShape(Dictionary<string, int> Keys, string Description);
