using System.Data;
using System.Data.Common;
using Npgsql;
using PgNotify.Internal;

namespace PgNotify.Runtime.Tests;

public class NotificationMappingBuilderTests
{
    private static NotificationMappingBuilder Builder() => new(new NotificationChannelMap());

    [Fact]
    public void UseConnection_proposes_the_LISTEN_adjusted_connection_string()
    {
        var builder = Builder();
        using var connection = new NpgsqlConnection("Host=db;Database=app;Username=u;Password=p");

        builder.UseConnection(connection);

        builder.ProposedConnectionStrings.Should().ContainSingle()
            .Which.Should().Be("Host=db;Database=app;Username=u;Password=p;Multiplexing=False;Pooling=False");
    }

    [Fact]
    public void UseConnection_with_an_NpgsqlConnection_also_records_a_credential_carrying_template()
    {
        // Never opened, and pointed at a host that doesn't resolve: CloneWith is local (it binds to
        // the source's already-known settings/data source), so nothing here touches a socket - the
        // real, over-the-wire proof that a template's clones actually authenticate lives in
        // PgNotify.IntegrationTests.NpgsqlDataSourceMappingTests.
        var builder = Builder();
        using var connection = new NpgsqlConnection("Host=unroutable.invalid;Database=app;Username=u;Password=p");

        builder.UseConnection(connection);

        var connectionString = builder.ProposedConnectionStrings.Single();
        builder.TemplateFor(connectionString).Should().NotBeNull();
    }

    [Fact]
    public void UseConnection_with_a_non_Npgsql_connection_proposes_no_template()
    {
        var builder = Builder();
        using var connection = new FakeDbConnection("Host=db;Database=app;Username=u;Password=p");

        builder.UseConnection(connection);

        var connectionString = builder.ProposedConnectionStrings.Single();
        builder.TemplateFor(connectionString).Should().BeNull();
    }

    [Fact]
    public void UseConnection_with_no_connection_string_proposes_nothing()
    {
        var builder = Builder();
        using var connection = new FakeDbConnection(connectionString: "");

        builder.UseConnection(connection);

        builder.ProposedConnectionStrings.Should().BeEmpty();
    }

    [Fact]
    public void UseConnection_rejects_a_null_connection()
    {
        var builder = Builder();

        var act = () => builder.UseConnection(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>The minimal <see cref="DbConnection"/> surface needed to exercise the non-Npgsql branch.</summary>
    private sealed class FakeDbConnection(string connectionString) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = connectionString;

        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    }
}
