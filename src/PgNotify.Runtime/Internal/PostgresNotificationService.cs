using System.Runtime.CompilerServices;
using PgNotify.Serialization;

namespace PgNotify.Internal;

internal sealed class PostgresNotificationService(NotificationEventHub hub) : IPostgresNotificationService
{
    public IAsyncEnumerable<NotificationEnvelope> Events<TEntity>(CancellationToken cancellationToken = default) =>
        hub.Subscribe(typeof(TEntity), cancellationToken);

    public async IAsyncEnumerable<NotificationEnvelope> Events<TEntity>(
        NotificationOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Filtered on the way out rather than by giving each operation its own broadcaster: the
        // routing key has no operation in it, and one stream per entity keeps the publish side a
        // single dictionary lookup.
        await foreach (var envelope in hub.Subscribe(typeof(TEntity), cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Operation == operation)
            {
                yield return envelope;
            }
        }
    }
}
