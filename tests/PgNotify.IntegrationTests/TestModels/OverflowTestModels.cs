using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Payloads;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>A row whose body is large enough to push a value-carrying payload past pg_notify's limit.</summary>
public class BigDoc
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
}

/// <summary>
/// Puts a column's <i>value</i> in the payload, which is what makes overflow reachable at all: the
/// built-in extended payload carries only metadata (keys, changed column names, timestamp), so no
/// row is ever big enough to blow the limit through it. This is the shape phase 1's
/// <c>WithPayload(x =&gt; new { ... })</c> will generate.
/// </summary>
public sealed class BodyIncludingPayloadBuilder : INotificationPayloadBuilder
{
    public IReadOnlyList<NotificationPayloadField> BuildFields(NotificationPayloadBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            NotificationPayloadField.Constant("entity", context.EntityDisplayName),
            NotificationPayloadField.Of("operation", NotificationPayloadFieldKind.Operation),
            NotificationPayloadField.Column("id", context.KeyColumns[0]),
            NotificationPayloadField.Column("body", "Body"),
        ];
    }
}

public sealed class BigDocDbContext(DbContextOptions<BigDocDbContext> options) : DbContext(options)
{
    public DbSet<BigDoc> Docs => Set<BigDoc>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BigDoc>(b =>
        {
            b.ToTable("big_docs");

            // Default overflow behavior - the point is that a user gets protection without asking.
            b.HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload<BodyIncludingPayloadBuilder>();
            });
        });
}

/// <summary>
/// <c>Body</c> is absent from the reduced payload, so it binds to <see langword="null"/> there —
/// which is exactly what <c>Truncated</c> tells a handler to expect.
/// </summary>
public sealed record BigDocInserted(int Id, string? Body, bool Truncated);
