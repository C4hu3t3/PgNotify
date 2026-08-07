using Microsoft.Extensions.DependencyInjection;
using PgNotify.Internal;
using PgNotify.Runtime.Tests.TestModels;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Internal;

public class HandlerAssemblyScannerTests
{
    [Fact]
    public void Every_handler_interface_a_type_implements_is_registered_as_scoped()
    {
        var services = new ServiceCollection();

        HandlerAssemblyScanner.RegisterHandlersFromAssembly(typeof(RecordingUserUpdatedHandler).Assembly, services, []);

        services.Should().Contain(d =>
            d.ServiceType == typeof(IDatabaseUpdatedHandler<TestUser>)
            && d.ImplementationType == typeof(RecordingUserUpdatedHandler)
            && d.Lifetime == ServiceLifetime.Scoped);

        services.Should().Contain(d => d.ServiceType == typeof(IDatabaseInsertedHandler<TestUser>));
        services.Should().Contain(d => d.ServiceType == typeof(IDatabaseDeletedHandler<TestUser>));
        services.Should().Contain(d => d.ServiceType == typeof(IDatabaseNotificationHandler<TestUser>));
        services.Should().Contain(d => d.ServiceType == typeof(IDatabaseNotificationHandler));
    }

    [Fact]
    public void Scanning_the_same_assembly_twice_does_not_register_a_handler_twice()
    {
        // Otherwise every handler in it would run twice per notification, which no test of a
        // single scan can observe.
        var services = new ServiceCollection();

        HandlerAssemblyScanner.RegisterHandlersFromAssembly(typeof(RecordingUserUpdatedHandler).Assembly, services, []);
        HandlerAssemblyScanner.RegisterHandlersFromAssembly(typeof(RecordingUserUpdatedHandler).Assembly, services, []);

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IDatabaseUpdatedHandler<TestUser>)
            && d.ImplementationType == typeof(RecordingUserUpdatedHandler));
    }

    [Fact]
    public void An_open_generic_handler_class_is_skipped_rather_than_breaking_the_scan()
    {
        // ServiceDescriptor.Scoped(closedInterface, openImplementation) throws, so an unguarded
        // scan would fail at startup for the whole assembly because of one such type.
        var services = new ServiceCollection();

        var act = () => HandlerAssemblyScanner.RegisterHandlersFromAssembly(typeof(OpenGenericHandler<>).Assembly, services, []);

        act.Should().NotThrow();
        services.Should().NotContain(d => d.ImplementationType == typeof(OpenGenericHandler<>));
    }

    [Fact]
    public void A_type_implementing_no_handler_interface_is_ignored()
    {
        var services = new ServiceCollection();

        HandlerAssemblyScanner.RegisterHandlersFromAssembly(typeof(RecordingUserUpdatedHandler).Assembly, services, []);

        services.Should().NotContain(d => d.ImplementationType == typeof(TestUser));
    }

    [Fact]
    public void Entity_keyed_handler_interfaces_add_their_type_argument_to_entityTypes()
    {
        var services = new ServiceCollection();
        var entityTypes = new HashSet<Type>();

        HandlerAssemblyScanner.RegisterHandlersFromAssembly(typeof(RecordingUserUpdatedHandler).Assembly, services, entityTypes);

        entityTypes.Should().Contain(typeof(TestUser));
    }

    [Fact]
    public void The_non_generic_catch_all_handler_interface_adds_nothing_to_entityTypes()
    {
        var services = new ServiceCollection();
        var entityTypes = new HashSet<Type>();

        HandlerAssemblyScanner.RegisterHandlersFromAssembly(typeof(CatchAllOnlyHandler).Assembly, services, entityTypes);

        entityTypes.Should().NotContain(typeof(CatchAllOnlyHandler));
    }
}

/// <summary>Declared for <see cref="HandlerAssemblyScannerTests"/>: implements only the non-generic catch-all.</summary>
public sealed class CatchAllOnlyHandler : IDatabaseNotificationHandler
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Declared for <see cref="HandlerAssemblyScannerTests"/>: an open generic handler the scan must skip.</summary>
public sealed class OpenGenericHandler<TEntity> : IDatabaseNotificationHandler<TEntity>
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
