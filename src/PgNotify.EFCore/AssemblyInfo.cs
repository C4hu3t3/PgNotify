using System.Runtime.CompilerServices;

// PgNotify.Migrations owns UseNpgsqlNotifications(), which registers the marker extension;
// this assembly owns the marker itself, so that reading it needs EF Core Relational rather than
// the whole Npgsql provider (see NotificationsOptionsExtension's remarks).
[assembly: InternalsVisibleTo("PgNotify.Migrations")]
