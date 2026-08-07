using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>Maps only <c>Id</c>/<c>Title</c> - <see cref="NoteEntity.Body"/> is ignored, even though it exists on the physical table.</summary>
public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options)
{
    public DbSet<NoteEntity> Notes => Set<NoteEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoteEntity>(b =>
        {
            b.ToTable("db_first_notes");
            b.Ignore(x => x.Body);
            b.HasDatabaseNotifications(o =>
            {
                o.OnUpdate();
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });
    }
}
