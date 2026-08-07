using PgNotify;
using PgNotify.Model;
using PgNotify.Naming;
using PgNotify.Payloads;
using PgNotify.Migrations.Internal;

namespace PgNotify.Migrations.Tests;

/// <summary>
/// Covers <see cref="NotificationFingerprint"/> directly. Everything else in this project drives
/// the diff through <c>MigrationTestHelper.GenerateDiffSql</c>, which builds both sides of a diff
/// in one process sharing one <see cref="Environment.NewLine"/> — the CRLF-vs-LF divergence this
/// type exists to guard against (a Windows developer and Linux CI fingerprinting the same model
/// differently) cannot occur inside that harness by construction, so it needs
/// <see cref="NotificationFingerprint"/> itself. See <c>AssemblyInfo.cs</c> for the
/// <c>InternalsVisibleTo</c> this test file needs.
/// </summary>
public class NotificationFingerprintTests
{
    private static readonly NotificationEntityConfiguration Config = new()
    {
        EntityDisplayName = "Product",
        TableName = "Products",
        Operations = NotificationOperations.Insert,
        KeyColumns = ["Id"],
        ChannelStrategy = PerEntityChannelNamingStrategy.Instance,
        PayloadBuilder = MinimalNotificationPayloadBuilder.Instance,
    };

    [Fact]
    public void Statements_differing_only_in_line_endings_hash_identically()
    {
        var lf = NotificationFingerprint.Compute(Config, ["CREATE OR REPLACE FUNCTION x()\nRETURNS trigger\nAS $$ ... $$;"]);
        var crlf = NotificationFingerprint.Compute(Config, ["CREATE OR REPLACE FUNCTION x()\r\nRETURNS trigger\r\nAS $$ ... $$;"]);

        lf.Should().Be(crlf);
    }

    [Fact]
    public void Statements_differing_in_interior_whitespace_hash_differently()
    {
        var single = NotificationFingerprint.Compute(Config, ["PERFORM pg_notify('my channel', payload);"]);
        var doubled = NotificationFingerprint.Compute(Config, ["PERFORM pg_notify('my  channel', payload);"]);

        single.Should().NotBe(doubled);
    }
}
