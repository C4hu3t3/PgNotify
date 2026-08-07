using System.Text;
using PgNotify;

namespace PgNotify.Core.Tests;

public class PostgresIdentifierTests
{
    [Fact]
    public void EnsureWithinLength_returns_short_identifiers_unchanged()
    {
        PostgresIdentifier.EnsureWithinLength("users").Should().Be("users");
    }

    [Fact]
    public void EnsureWithinLength_returns_exactly_63_bytes_unchanged()
    {
        var exactly63 = new string('x', 63);
        PostgresIdentifier.EnsureWithinLength(exactly63).Should().Be(exactly63);
    }

    [Fact]
    public void EnsureWithinLength_truncates_overlong_identifiers_to_max_length()
    {
        var overlong = new string('x', 200);
        var result = PostgresIdentifier.EnsureWithinLength(overlong);

        Encoding.UTF8.GetByteCount(result).Should().BeLessOrEqualTo(63);
    }

    [Fact]
    public void EnsureWithinLength_produces_different_suffixes_for_different_inputs()
    {
        var a = PostgresIdentifier.EnsureWithinLength(new string('x', 100) + "a");
        var b = PostgresIdentifier.EnsureWithinLength(new string('x', 100) + "b");

        a.Should().NotBe(b);
    }

    [Fact]
    public void EnsureWithinLength_handles_multibyte_characters_without_splitting_them()
    {
        // Each 'é' is 2 UTF-8 bytes; ensure truncation never cuts a character in half.
        var candidate = string.Concat(Enumerable.Repeat("é", 40));
        var result = PostgresIdentifier.EnsureWithinLength(candidate);

        var action = () => Encoding.UTF8.GetBytes(result);
        action.Should().NotThrow();
        Encoding.UTF8.GetByteCount(result).Should().BeLessOrEqualTo(63);
    }
}
