using System.Security.Cryptography;
using System.Text;

namespace PgNotify;

/// <summary>
/// Helpers for keeping generated PostgreSQL identifiers (channel names, trigger names, function
/// names) within the server's 63-byte <c>NAMEDATALEN</c> limit deterministically.
/// </summary>
/// <remarks>
/// PostgreSQL silently truncates identifiers longer than <c>NAMEDATALEN - 1</c> bytes (63 by
/// default). Silent truncation is dangerous here because two distinct, long entity/table names
/// could collide on the same truncated trigger or channel name. Instead, this type truncates
/// deterministically and appends a short content hash suffix, so collisions are astronomically
/// unlikely and the result is stable across repeated runs (required for idempotent migrations).
/// </remarks>
public static class PostgresIdentifier
{
    /// <summary>The default PostgreSQL <c>NAMEDATALEN</c>-derived maximum identifier length, in bytes.</summary>
    public const int MaxLength = 63;

    /// <summary>
    /// Returns <paramref name="candidate"/> unchanged if it fits within <paramref name="maxLength"/>
    /// UTF-8 bytes; otherwise returns a deterministically truncated version with an 8-character
    /// hash suffix so it fits while remaining stable and collision-resistant.
    /// </summary>
    public static string EnsureWithinLength(string candidate, int maxLength = MaxLength)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 16);

        if (Encoding.UTF8.GetByteCount(candidate) <= maxLength)
        {
            return candidate;
        }

        var suffix = "_" + ComputeShortHash(candidate);
        var budget = maxLength - Encoding.UTF8.GetByteCount(suffix);

        var truncated = TruncateToUtf8ByteLength(candidate, budget);
        return truncated + suffix;
    }

    private static string ComputeShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string TruncateToUtf8ByteLength(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var byteCount = 0;
        var charIndex = 0;
        while (charIndex < value.Length)
        {
            var charLength = char.IsSurrogatePair(value, charIndex) ? 2 : 1;
            var nextByteCount = byteCount + Encoding.UTF8.GetByteCount(value.AsSpan(charIndex, charLength));
            if (nextByteCount > maxBytes)
            {
                break;
            }

            byteCount = nextByteCount;
            charIndex += charLength;
        }

        return value[..charIndex];
    }
}
