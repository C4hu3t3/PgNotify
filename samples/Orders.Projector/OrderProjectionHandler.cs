using Microsoft.EntityFrameworkCore;
using Orders.Model;
using PgNotify;
using PgNotify.Serialization;

namespace Orders.Projector;

/// <summary>
/// One method implicitly satisfies all three operation-specific interfaces at once (their
/// signatures are identical), which is enough here since insert and update do the same thing —
/// re-read the row and upsert the snapshot — and only delete needs different handling. The
/// minimal payload (this sample's default, like every other) never carries <c>CustomerName</c> or
/// <c>Amount</c>, only the row's id — re-reading through <see cref="ProjectorDbContext"/> is what
/// PgNotify.Listener's <c>Runtime.EFCore</c> dependency (a DbContext of its own) is actually for,
/// beyond deriving the channel name.
/// </summary>
public sealed class OrderProjectionHandler(
    ProjectorDbContext db, SummaryStore store, ILogger<OrderProjectionHandler> logger)
    : IDatabaseInsertedHandler<Order>, IDatabaseUpdatedHandler<Order>, IDatabaseDeletedHandler<Order>
{
    public async Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var id = envelope.Keys["id"].GetInt32();

        if (envelope.Operation == NotificationOperation.Delete)
        {
            store.Remove(id);
            logger.LogInformation("Order {OrderId} removed from the summary", id);
            return;
        }

        // The row is still there for insert/update — a delete's row is already gone, which is
        // exactly why that branch above never queries for one.
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return;
        }

        store.Upsert(order);
        logger.LogInformation(
            "Order {OrderId} ({CustomerName}, {Amount:C}) projected after a {Operation} notification",
            id, order.CustomerName, order.Amount, envelope.Operation);
    }
}
