using System.Runtime.CompilerServices;

// NotificationFingerprint's line-ending stripping is only observable at unit-test granularity:
// MigrationTestHelper.GenerateDiffSql builds both sides of a diff in one process with one
// Environment.NewLine, so the CRLF-vs-LF failure mode it exists to prevent is invisible to that
// harness by construction. Testing it directly needs NotificationFingerprint itself, which is
// internal.
[assembly: InternalsVisibleTo("PgNotify.Migrations.Tests")]
