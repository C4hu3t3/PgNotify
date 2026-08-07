namespace PgNotify.Internal;

/// <summary>
/// The requirement every user-supplied strategy/payload-builder type has to meet, checked where
/// the type is configured rather than where it is instantiated.
/// </summary>
internal static class NotificationCustomTypes
{
    /// <summary>
    /// Returns <paramref name="type"/>'s assembly-qualified name, having checked it can be
    /// reconstructed later. Only the name survives into the annotations, and EF Core rebuilds the
    /// design-time model from those, so a type that cannot be activated from its name would fail at
    /// the next <c>migrations add</c> instead of here.
    /// </summary>
    public static string RequireParameterlessConstructor(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.GetConstructor(Type.EmptyTypes) is null
            ? throw new InvalidOperationException(
                $"Type '{type}' must have a public parameterless constructor to be used as a custom notification strategy or payload builder, "
                + "so it can be reconstructed when EF Core builds the design-time model for migrations.")
            : type.AssemblyQualifiedName!;
    }
}
