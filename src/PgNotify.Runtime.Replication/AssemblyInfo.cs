using System.Runtime.CompilerServices;

// PgNotify.IntegrationTests needs to stop/restart LogicalReplicationHostedService by itself
// (independent of the rest of the host) to prove the at-least-once guarantee end to end -- see
// ReplicationEndToEndTests. Nothing else in this assembly needs to be internal to it; everything
// else a consumer needs is the public surface (IReplicationMappingSource, ReplicationMappingBuilder,
// PostgresLogicalReplicationOptions, INotificationPublisher from PgNotify.Runtime).
[assembly: InternalsVisibleTo("PgNotify.IntegrationTests")]
