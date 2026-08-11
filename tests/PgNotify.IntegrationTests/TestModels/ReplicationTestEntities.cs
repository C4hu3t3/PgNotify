using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

public class ReplicationOrder
{
    public int Id { get; set; }
    public string Status { get; set; } = "";
}

public sealed class ReplicationDbContext(DbContextOptions<ReplicationDbContext> options) : DbContext(options)
{
    public DbSet<ReplicationOrder> Orders => Set<ReplicationOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReplicationOrder>(b =>
        {
            b.ToTable("replication_orders");
            b.HasDatabaseNotifications(o => o
                .WithDelivery(NotificationDeliveryMode.LogicalReplication)
                .WithReplicaIdentityFull()
                .WithPayload(NotificationPayloadKind.Extended)
                .OnAny(x => x.Status));
        });
    }
}
