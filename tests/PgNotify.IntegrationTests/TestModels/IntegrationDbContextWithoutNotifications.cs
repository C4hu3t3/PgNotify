using Microsoft.EntityFrameworkCore;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// Same table as <see cref="IntegrationDbContext"/>, but with no
/// <c>HasDatabaseNotifications(...)</c> at all — stands in for a database-first project where the
/// notification configuration was removed from the code after the trigger was already deployed,
/// the scenario <c>FindOrphanedNotificationTriggersAsync</c> exists to surface.
/// </summary>
public sealed class IntegrationDbContextWithoutNotifications(DbContextOptions<IntegrationDbContextWithoutNotifications> options) : DbContext(options)
{
    public DbSet<TestUser> Users => Set<TestUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestUser>(b => b.ToTable("test_users"));
    }
}
