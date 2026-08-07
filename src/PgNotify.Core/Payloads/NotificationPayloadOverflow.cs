namespace PgNotify.Payloads;

/// <summary>
/// What the generated trigger should do when a notification payload would exceed PostgreSQL's
/// <c>pg_notify</c> limit of 7999 bytes.
/// </summary>
/// <remarks>
/// This matters more than it first looks: exceeding the limit raises <c>22023</c>
/// ("payload string too long") <i>inside the trigger</i>, which aborts the transaction that wrote
/// the row. Without a fallback, an oversized payload does not merely lose a notification — it
/// fails the user's <c>INSERT</c> or <c>UPDATE</c>.
/// </remarks>
public enum NotificationPayloadOverflow
{
    /// <summary>
    /// Send a reduced payload — entity, operation, the row's key, and <c>"truncated": true</c> —
    /// instead of failing. The consumer can tell it received a reduced payload from the flag and
    /// re-read the row. This is the default.
    /// </summary>
    Truncate,

    /// <summary>
    /// Let <c>pg_notify</c> raise, aborting the write. Appropriate only where a reduced payload is
    /// worse than no write at all — CDC or audit pipelines that assume completeness — and chosen
    /// deliberately, since it makes row size a correctness concern for every writer.
    /// </summary>
    Fail,
}
