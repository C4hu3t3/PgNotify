using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>Same mapping as <see cref="IntegrationDbContext"/>, but with a custom trigger/function name prefix.</summary>
public sealed class PrefixedIntegrationDbContext(DbContextOptions<PrefixedIntegrationDbContext> options) : DbContext(options)
{
    public const string NamePrefix = "myapp_";

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
                o.WithNamePrefix(NamePrefix);
            });
        });
    }
}
