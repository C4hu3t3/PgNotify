using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

public class TestProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ProductDbContext(DbContextOptions<ProductDbContext> options) : DbContext(options)
{
    public DbSet<TestProduct> Products => Set<TestProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestProduct>(b =>
        {
            b.ToTable("test_products");
            // Explicitly extended: RawPayloadShapeTests documents that shape's JSON, and the
            // default is the minimal one.
            b.HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Extended));
        });
    }
}
