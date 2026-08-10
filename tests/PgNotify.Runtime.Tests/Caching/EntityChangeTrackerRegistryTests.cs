using Microsoft.Extensions.Logging;
using PgNotify.Caching;
using PgNotify.Internal;
using PgNotify.Runtime.Tests.TestModels;

namespace PgNotify.Runtime.Tests.Caching;

public class EntityChangeTrackerRegistryTests
{
    private static EntityChangeTrackerRegistry CreateRegistry(NotificationChannelMap channelMap, out RecordingLogger<EntityChangeTrackerRegistry> logger)
    {
        logger = new RecordingLogger<EntityChangeTrackerRegistry>();
        return new EntityChangeTrackerRegistry(default, channelMap, logger);
    }

    [Fact]
    public void Get_does_not_warn_before_the_mapping_is_resolved()
    {
        var channelMap = new NotificationChannelMap();
        using var registry = CreateRegistry(channelMap, out var logger);

        registry.Get("TestUser");

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Get_does_not_warn_for_an_entity_that_is_mapped_to_a_channel()
    {
        var channelMap = new NotificationChannelMap();
        channelMap.MapChannel("users", typeof(TestUser));
        channelMap.MarkMappingResolved();
        using var registry = CreateRegistry(channelMap, out var logger);

        registry.Get("TestUser");

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Get_warns_once_when_the_mapping_is_resolved_and_no_channel_maps_to_the_entity()
    {
        var channelMap = new NotificationChannelMap();
        channelMap.MapChannel("users", typeof(TestUser));
        channelMap.MarkMappingResolved();
        using var registry = CreateRegistry(channelMap, out var logger);

        registry.Get("RemovedEntity");
        registry.Get("RemovedEntity");

        logger.Warnings.Should().ContainSingle(w => w.Contains("RemovedEntity"));
    }

    [Fact]
    public void MarkChanged_never_warns_even_for_an_entity_name_with_no_mapped_channel()
    {
        // ChangeTrackingNotificationMiddleware feeds every notification through MarkChanged,
        // including entities reachable only through a non-generic MapChannel("...") - that is a
        // legitimate shape, not a misconfiguration, so this path must stay silent.
        var channelMap = new NotificationChannelMap();
        channelMap.MapChannel("legacy_audit");
        channelMap.MarkMappingResolved();
        using var registry = CreateRegistry(channelMap, out var logger);

        registry.MarkChanged("AuditEvent", DateTimeOffset.UtcNow);

        logger.Warnings.Should().BeEmpty();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
