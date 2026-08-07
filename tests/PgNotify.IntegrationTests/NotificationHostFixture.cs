using PgNotify.IntegrationTests.TestModels;

namespace PgNotify.IntegrationTests;

/// <summary>Shared, once-per-test-class PostgreSQL container + running notification listener.</summary>
public sealed class NotificationHostFixture : IAsyncLifetime
{
    public IntegrationHost Host { get; private set; } = null!;

    public async Task InitializeAsync() => Host = await IntegrationHost.StartAsync();

    public async Task DisposeAsync() => await Host.DisposeAsync();
}
