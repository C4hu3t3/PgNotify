using System.ComponentModel.DataAnnotations.Schema;
using PgNotify;

namespace Orders.Model;

/// <summary>
/// The one thing <c>Orders.WebApi</c> (which owns the migration) and <c>Orders.Projector</c>
/// (which maintains a read-model derived from the same table) share.
/// </summary>
/// <remarks>
/// Unlike <c>TaskBoard.Model.TaskItem</c>, both sides here *do* have their own <c>DbContext</c>,
/// so both are individually capable of deriving a channel name from their model via
/// <c>AddNotificationMappingFromDbContexts()</c> — no process is blind the way <c>TaskBoard.Watcher</c>
/// is. That is not the same as the two names being guaranteed to agree: they are two independent
/// <c>DbContext</c> types in two different projects, and EF Core's default table-naming convention
/// keys off the <c>DbSet</c> property name, not the entity type name. If <c>Orders.WebApi</c>'s
/// <c>DbSet&lt;Order&gt;</c> were called <c>Orders</c> and <c>Orders.Projector</c>'s were called
/// <c>OrderEntities</c>, each side would derive a different table/channel name from an otherwise
/// identical model, and the projector would silently never receive anything. <c>[Table("Order")]</c>
/// here removes that coordination risk at the source: both contexts map to the same table
/// regardless of what either calls its <c>DbSet</c>.
/// </remarks>
[NotifyChanges]
[Table("Order")]
public class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal Amount { get; set; }
}
