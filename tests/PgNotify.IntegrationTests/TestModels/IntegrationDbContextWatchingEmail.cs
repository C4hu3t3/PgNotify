using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// Same table/channel/prefix as <see cref="IntegrationDbContext"/> (so it produces the exact same
/// function/trigger names), but watches <c>Email</c> instead of <c>Name</c> for updates - used to
/// prove <c>EnsureNotificationTriggersAsync</c> regenerates when the notification configuration
/// genuinely changes, rather than only ever skipping.
/// </summary>
public sealed class IntegrationDbContextWatchingEmail(DbContextOptions<IntegrationDbContextWatchingEmail> options) : DbContext(options)
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
                o.OnUpdate(x => x.Email);
                o.OnDelete();
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });
    }
}
