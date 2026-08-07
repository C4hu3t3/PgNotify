using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

public class TopicEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class SharedAlpha
{
    public int Id { get; set; }
}

public class SharedBeta
{
    public int Id { get; set; }
}

public class OverriddenChannelEntity
{
    public int Id { get; set; }
}

/// <summary>
/// One context exercising all three non-default channel strategies at once, since the interesting
/// part is how they coexist: two entities deliberately share a single channel, so routing has to
/// separate them by entity and operation rather than by channel.
/// </summary>
public sealed class ChannelStrategyDbContext(DbContextOptions<ChannelStrategyDbContext> options) : DbContext(options)
{
    public DbSet<TopicEntity> Topics => Set<TopicEntity>();
    public DbSet<SharedAlpha> Alphas => Set<SharedAlpha>();
    public DbSet<SharedBeta> Betas => Set<SharedBeta>();
    public DbSet<OverriddenChannelEntity> Overridden => Set<OverriddenChannelEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TopicEntity>(b =>
        {
            b.ToTable("topic_entities");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnDelete();
                o.WithTopicChannel();
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });

        modelBuilder.Entity<SharedAlpha>(b =>
        {
            b.ToTable("shared_alphas");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithSingleChannel("app_events");
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });

        modelBuilder.Entity<SharedBeta>(b =>
        {
            b.ToTable("shared_betas");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithSingleChannel("app_events");
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });

        modelBuilder.Entity<OverriddenChannelEntity>(b =>
        {
            b.ToTable("overridden_entities");
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithChannelName("a_named_channel");
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });
    }
}
