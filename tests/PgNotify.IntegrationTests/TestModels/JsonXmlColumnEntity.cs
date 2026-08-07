using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// Exercises the two PostgreSQL store types that have no comparison operator at all
/// (<c>json</c>, <c>xml</c>): both must be cast before the generated trigger compares
/// <c>NEW</c>/<c>OLD</c>, or the first <c>UPDATE</c> touching the row aborts with
/// <c>operator does not exist</c>.
/// </summary>
public class JsonXmlColumnEntity
{
    public int Id { get; set; }
    public string Metadata { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class JsonXmlColumnDbContext(DbContextOptions<JsonXmlColumnDbContext> options) : DbContext(options)
{
    public DbSet<JsonXmlColumnEntity> Documents => Set<JsonXmlColumnEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JsonXmlColumnEntity>(b =>
        {
            b.ToTable("json_xml_documents");
            b.Property(x => x.Metadata).HasColumnType("json");
            b.Property(x => x.Notes).HasColumnType("xml");
            b.HasDatabaseNotifications(o =>
            {
                o.OnUpdate();
                o.WithPayload(NotificationPayloadKind.Extended);
            });
        });
    }
}
