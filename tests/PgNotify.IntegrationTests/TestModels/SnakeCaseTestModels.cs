using Microsoft.EntityFrameworkCore;
using PgNotify;

namespace PgNotify.IntegrationTests.TestModels;

public class SnakeCaseInvoice
{
    public int Id { get; set; }
    public string InternalNote { get; set; } = "";
    public decimal TotalAmount { get; set; }
}

/// <summary>
/// Configured with <c>UseSnakeCaseNamingConvention()</c> from EFCore.NamingConventions (wired up
/// where the options are built, not here), so every table and column name this model produces is
/// rewritten: table <c>snake_case_invoices</c>, columns <c>internal_note</c>/<c>total_amount</c>.
/// </summary>
public sealed class SnakeCaseDbContext(DbContextOptions<SnakeCaseDbContext> options) : DbContext(options)
{
    public DbSet<SnakeCaseInvoice> Invoices => Set<SnakeCaseInvoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<SnakeCaseInvoice>().HasDatabaseNotifications(o =>
        {
            o.OnInsert();
            o.WithPayload(x => new { x.InternalNote, x.TotalAmount });
        });
}

/// <summary>
/// Declared in CLR terms throughout — the channel is the (rewritten) table name, but the payload's
/// members are the property names, which is the whole point: an event type must not have to know
/// how the storage layer spells things.
/// </summary>
public sealed record SnakeCaseInvoiceInserted(int Id, string InternalNote, decimal TotalAmount);
