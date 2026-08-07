using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options) : DbContext(options)
{
    public DbSet<TestUser> Users => Set<TestUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestUser>(b =>
        {
            b.ToTable("test_users");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate(x => x.Name);
                o.OnDelete();
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });
    }
}
