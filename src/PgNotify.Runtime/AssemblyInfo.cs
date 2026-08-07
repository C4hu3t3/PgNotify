using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PgNotify.Runtime.Tests")]
[assembly: InternalsVisibleTo("PgNotify.Benchmarks")]

// PgNotify.Runtime.EFCore itself needs nothing internal — it is written entirely against the
// public INotificationMappingSource surface, which is the point of that surface. Its tests assert
// on the resolved state (channels, connection string), which is internal.
[assembly: InternalsVisibleTo("PgNotify.Runtime.EFCore.Tests")]
