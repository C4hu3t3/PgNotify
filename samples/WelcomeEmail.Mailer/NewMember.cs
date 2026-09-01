namespace WelcomeEmail.Mailer;

/// <summary>
/// The typed shape of a <c>Member</c> insert notification's payload. It binds against the JSON
/// <c>WelcomeEmail.WebApi</c>'s <c>WithPayload(m =&gt; new { m.Email, m.DisplayName })</c> emits:
/// <c>{"entity":"Member","operation":"insert","id":42,"email":"...","displayName":"..."}</c>.
/// </summary>
/// <remarks>
/// The property names here are the <b>entity property</b> names (<c>Email</c>, <c>DisplayName</c>),
/// not column names — a projected payload's keys always are, because it is deserialized into a
/// .NET type that knows nothing about storage naming. <c>Id</c> binds because the minimal and
/// projected shapes both put the key at the payload's top level; <c>envelope.ToTyped&lt;T&gt;()</c>
/// uses <c>JsonSerializerDefaults.Web</c>, so <c>email</c> → <c>Email</c> matches case-insensitively.
/// </remarks>
public sealed record NewMember(int Id, string Email, string DisplayName);
