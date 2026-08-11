using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PgNotify.Runtime.Tests")]
[assembly: InternalsVisibleTo("PgNotify.Benchmarks")]

// PgNotify.Runtime.Replication needs nothing internal either, the same reason
// PgNotify.Runtime.EFCore.Tests's neighbor above doesn't: it is written entirely against
// INotificationPublisher (this project's own public seam for a delivery mechanism other than
// LISTEN/NOTIFY) plus NotificationEnvelope/NotificationContext, never NotificationChannelMap or
// NotificationDispatchPipeline directly.

// PgNotify.Runtime.EFCore itself needs nothing internal — it is written entirely against the
// public INotificationMappingSource surface, which is the point of that surface. Its tests assert
// on the resolved state (channels, connection string), which is internal.
[assembly: InternalsVisibleTo("PgNotify.Runtime.EFCore.Tests")]
