using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace HttpCaching.WebApi;

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Article> Articles => Set<Article>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Article>(entity =>
        {
            // Pins the table - and therefore the per-entity channel - to the CLR type name, so it
            // matches the Channel constants in ArticleEvents.cs. Without it the DbSet property
            // would name the table "Articles".
            entity.ToTable("Article");

            // The extended payload is asked for explicitly, and this sample is the reason it exists
            // as an option: it is the only shape carrying `timestamp`, which IEntityChangeTracker
            // uses for LastModified and therefore for this sample's ETags. Everything else is left
            // at its default - all three operations, one channel per entity.
            entity.HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Extended));
        });
    }
}
