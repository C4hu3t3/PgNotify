using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>Same table/channel as <see cref="NotesDbContext"/>, but also maps <see cref="NoteEntity.Body"/>.</summary>
public sealed class NotesDbContextWithBody(DbContextOptions<NotesDbContextWithBody> options) : DbContext(options)
{
    public DbSet<NoteEntity> Notes => Set<NoteEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoteEntity>(b =>
        {
            b.ToTable("db_first_notes");
            b.HasDatabaseNotifications(o =>
            {
                o.OnUpdate();
                o.WithPayload(NotificationPayloadKind.Minimal);
            });
        });
    }
}
