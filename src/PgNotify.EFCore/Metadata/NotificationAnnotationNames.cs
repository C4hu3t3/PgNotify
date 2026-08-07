using Microsoft.EntityFrameworkCore.Metadata;

namespace PgNotify.Metadata;

/// <summary>
/// The relational annotation names used to persist notification configuration on
/// <see cref="IReadOnlyEntityType"/>. Every value is a plain <see cref="string"/> or
/// <see cref="bool"/> deliberately: EF Core's migrations snapshot code generator must be able to
/// turn these into C# literals for the generated <c>ModelSnapshot.cs</c> file, and primitive
/// types are guaranteed to round-trip through that pipeline without a custom
/// <c>IAnnotationCodeGenerator</c>.
/// </summary>
internal static class NotificationAnnotationNames
{
    private const string Prefix = "Notifications:";

    public const string Enabled = Prefix + "Enabled";
    public const string Operations = Prefix + "Operations";
    public const string WatchedUpdateColumns = Prefix + "WatchedUpdateColumns";
    public const string ChannelStrategyKind = Prefix + "ChannelStrategyKind";
    public const string ChannelStrategyArgument = Prefix + "ChannelStrategyArgument";
    public const string ChannelNameOverride = Prefix + "ChannelNameOverride";
    public const string PayloadBuilderKind = Prefix + "PayloadBuilderKind";
    public const string PayloadBuilderTypeName = Prefix + "PayloadBuilderTypeName";
    public const string PayloadOverflow = Prefix + "PayloadOverflow";
    public const string PayloadColumns = Prefix + "PayloadColumns";
    public const string NamePrefix = Prefix + "NamePrefix";

    /// <summary>Model-wide default for <see cref="NamePrefix"/>, set via <c>modelBuilder.HasNotificationNamePrefix(...)</c>.</summary>
    public const string DefaultNamePrefix = Prefix + "DefaultNamePrefix";
}

/// <summary>The <see cref="NotificationAnnotationNames.ChannelStrategyKind"/> discriminator values.</summary>
internal static class NotificationChannelStrategyKind
{
    public const string PerEntity = "PerEntity";
    public const string Single = "Single";
    public const string Topic = "Topic";
    public const string Custom = "Custom";
    public const string Projected = "Projected";
}

/// <summary>The <see cref="NotificationAnnotationNames.PayloadBuilderKind"/> discriminator values.</summary>
internal static class NotificationPayloadBuilderKind
{
    public const string Minimal = "Minimal";
    public const string Extended = "Extended";
    public const string Custom = "Custom";
    public const string Projected = "Projected";
}
