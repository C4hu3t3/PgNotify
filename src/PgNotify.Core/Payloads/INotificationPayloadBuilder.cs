namespace PgNotify.Payloads;

/// <summary>
/// Describes the shape of the JSON payload a notification trigger should emit.
/// </summary>
/// <remarks>
/// This runs once, at migration-generation time, not per-notification at runtime: it produces a
/// declarative field list that <c>PgNotify.Migrations</c> compiles into a single
/// <c>json_build_object(...)</c> SQL expression baked into the trigger function. There is no
/// runtime (.NET-side) payload construction for the outbound (database → channel) direction —
/// the database builds its own notification payload, which is what makes the pattern race-free
/// and avoids a second round trip back to the database after the notification is received.
/// </remarks>
public interface INotificationPayloadBuilder
{
    /// <summary>Computes the ordered list of fields to include in the JSON payload.</summary>
    IReadOnlyList<NotificationPayloadField> BuildFields(NotificationPayloadBuilderContext context);
}
