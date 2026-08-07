using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace PgNotify.Runtime.EFCore.Tests.TestModels;

public sealed class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class Order
{
    public int Id { get; set; }

    public decimal Total { get; set; }
}

/// <summary>
/// EF Core caches the compiled model per DbContext type, so every test context registers this to
/// keep independent tests from sharing a stale model.
/// </summary>
public sealed class UncachedModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => new object();
}

public sealed class ProductContext(DbContextOptions<ProductContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate();
                o.OnDelete();
            });
        });
}

public sealed class OrderContext(DbContextOptions<OrderContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnDelete();
            });
        });
}

/// <summary>One entity, one channel per operation: the mapping must carry all three.</summary>
public sealed class TopicOrderContext(DbContextOptions<TopicOrderContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate();
                o.OnDelete();
                o.WithTopicChannel();
            });
        });
}

/// <summary>
/// Marked via UseNpgsqlNotificationsListening() (from PgNotify.EFCore), not the full
/// UseNpgsqlNotifications() (from PgNotify.Migrations) every other context here uses — the
/// listener-only entry point a process with no migrations stack would call.
/// </summary>
public sealed class ListeningOnlyContext(DbContextOptions<ListeningOnlyContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate();
                o.OnDelete();
            });
        });
}

/// <summary>A context that never called UseNpgsqlNotifications(), despite configuring entities.</summary>
public sealed class UnmarkedContext(DbContextOptions<UnmarkedContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Product>().ToTable("products");
}

/// <summary>
/// Two entities whose short names collide, sharing one channel: the payload's <c>"entity"</c> field
/// carries the short name only, so nothing arriving at runtime could tell them apart.
/// </summary>
public sealed class CollidingNamesContext(DbContextOptions<CollidingNamesContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Billing.Invoice>(b =>
        {
            b.ToTable("billing_invoices");
            b.HasDatabaseNotifications(o => o.WithSingleChannel("app_events"));
        });

        modelBuilder.Entity<Shipping.Invoice>(b =>
        {
            b.ToTable("shipping_invoices");
            b.HasDatabaseNotifications(o => o.WithSingleChannel("app_events"));
        });
    }
}
