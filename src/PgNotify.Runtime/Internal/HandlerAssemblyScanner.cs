using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PgNotify.Internal;

/// <summary>
/// Registers every notification handler an assembly declares, as scoped services under each
/// handler interface it implements. Which channels to <c>LISTEN</c> on is deliberately not derived
/// from this: a handler is keyed on an entity type, which knows nothing about channels — that is
/// the channel map's job.
/// </summary>
internal static class HandlerAssemblyScanner
{
    private static readonly Type[] EntityHandlerInterfaces =
    [
        typeof(IDatabaseNotificationHandler<>),
        typeof(IDatabaseInsertedHandler<>),
        typeof(IDatabaseUpdatedHandler<>),
        typeof(IDatabaseDeletedHandler<>),
    ];

    public static void RegisterHandlersFromAssembly(Assembly assembly, IServiceCollection services, HashSet<Type> entityTypes)
    {
        foreach (var type in assembly.GetTypes())
        {
            // An open generic handler class cannot be registered against a closed interface, and
            // its type arguments are exactly what routing keys on, so there is nothing to infer.
            if (type is not { IsClass: true, IsAbstract: false } || type.ContainsGenericParameters)
            {
                continue;
            }

            foreach (var handlerInterface in type.GetInterfaces())
            {
                if (!IsHandlerInterface(handlerInterface))
                {
                    continue;
                }

                // TryAddEnumerable, not Add: scanning the same assembly twice (two calls, or an
                // assembly reachable from two configured ones) would otherwise register the same
                // handler again and invoke it twice per notification.
                services.TryAddEnumerable(ServiceDescriptor.Scoped(handlerInterface, type));

                // The non-generic catch-all names no entity type, so it has nothing to compare
                // against a channel mapping - only the entity-keyed interfaces do.
                if (handlerInterface.IsGenericType)
                {
                    entityTypes.Add(handlerInterface.GetGenericArguments()[0]);
                }
            }
        }
    }

    private static bool IsHandlerInterface(Type candidate) =>
        candidate == typeof(IDatabaseNotificationHandler)
        || (candidate.IsGenericType && Array.IndexOf(EntityHandlerInterfaces, candidate.GetGenericTypeDefinition()) >= 0);
}
