using Microsoft.EntityFrameworkCore.Metadata;
using PgNotify;

// Intentionally in the Microsoft.EntityFrameworkCore namespace, alongside EF Core's own metadata
// extension methods, matching EntityTypeNotificationExtensions.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Model-wide access to the notification configuration, for the listening side: which channels the
/// model's triggers publish on, and which entity type each one carries.
/// </summary>
public static class ModelNotificationExtensions
{
    /// <summary>
    /// Enumerates one <see cref="NotificationChannelBinding"/> per (entity, channel) pair the model
    /// notifies on. An entity appears several times when its channel-naming strategy gives each
    /// operation its own channel (the <c>topic</c> strategy), and several entities share an entry's
    /// channel when they share one (<c>WithSingleChannel</c>).
    /// </summary>
    /// <remarks>
    /// The operation is deliberately absent from the result: a channel is a channel whatever made
    /// it, the payload states which operation occurred, and duplicates would only have to be
    /// collapsed again by every caller.
    /// </remarks>
    public static IEnumerable<NotificationChannelBinding> GetNotificationChannels(this IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.GetNotificationConfiguration() is not { } configuration)
            {
                continue;
            }

            foreach (var channel in configuration.Operations.Expand()
                         .Select(configuration.GetChannelName)
                         .Distinct(StringComparer.Ordinal))
            {
                // EntityDisplayName, not ClrType.Name: it is what the trigger writes into the
                // payload's "entity" field, and the two disagree for a shared-type entity.
                yield return new NotificationChannelBinding(channel, configuration.EntityDisplayName, entityType.ClrType);
            }
        }
    }
}

/// <summary>One channel a model notifies on, and the entity type whose rows it carries.</summary>
/// <param name="Channel">The <c>LISTEN</c>/<c>NOTIFY</c> channel name.</param>
/// <param name="EntityName">The value the payload's <c>"entity"</c> field carries for this entity.</param>
/// <param name="ClrType">The entity's CLR type, which handlers are keyed on.</param>
public sealed record NotificationChannelBinding(string Channel, string EntityName, Type ClrType);
