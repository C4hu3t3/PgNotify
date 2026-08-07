using Npgsql;

namespace PgNotify.Runtime.Tests;

public class NotificationConnectionStringTests
{
    [Fact]
    public void ForListening_turns_off_multiplexing_and_pooling()
    {
        var result = NotificationConnectionString.ForListening(
            "Host=db;Database=app;Username=u;Password=p;Multiplexing=true;Pooling=true");

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.Multiplexing.Should().BeFalse();
        parsed.Pooling.Should().BeFalse();
    }

    [Fact]
    public void ForListening_turns_pooling_off_even_when_it_was_left_at_its_default()
    {
        // Pooling defaults to true, so "not mentioned" is the common case, not the rare one.
        var result = NotificationConnectionString.ForListening("Host=db;Database=app");

        new NpgsqlConnectionStringBuilder(result).Pooling.Should().BeFalse();
    }

    [Fact]
    public void ForListening_preserves_everything_else()
    {
        var result = NotificationConnectionString.ForListening(
            "Host=db;Port=6432;Database=app;Username=u;Password=p;SSL Mode=Require;Application Name=writer;Timeout=7");

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.Host.Should().Be("db");
        parsed.Port.Should().Be(6432);
        parsed.Database.Should().Be("app");
        parsed.Username.Should().Be("u");
        parsed.Password.Should().Be("p");
        parsed.SslMode.Should().Be(SslMode.Require);
        parsed.ApplicationName.Should().Be("writer");
        parsed.Timeout.Should().Be(7);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForListening_rejects_an_empty_connection_string(string connectionString)
    {
        var act = () => NotificationConnectionString.ForListening(connectionString);

        act.Should().Throw<ArgumentException>();
    }
}
