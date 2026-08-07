using PgNotify.Reconnect;

namespace PgNotify.Runtime.Tests.Reconnect;

public class ExponentialBackoffReconnectPolicyTests
{
    [Fact]
    public void GetDelay_grows_exponentially_up_to_the_max()
    {
        var policy = new ExponentialBackoffReconnectPolicy(
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromSeconds(10));

        var delay1 = policy.GetDelay(1)!.Value.TotalMilliseconds;
        var delay2 = policy.GetDelay(2)!.Value.TotalMilliseconds;
        var delay3 = policy.GetDelay(3)!.Value.TotalMilliseconds;

        // Jitter adds up to 30%, so compare against the pre-jitter floor for each attempt.
        delay1.Should().BeGreaterOrEqualTo(100).And.BeLessThan(100 * 1.3 + 1);
        delay2.Should().BeGreaterOrEqualTo(200).And.BeLessThan(200 * 1.3 + 1);
        delay3.Should().BeGreaterOrEqualTo(400).And.BeLessThan(400 * 1.3 + 1);
    }

    [Fact]
    public void GetDelay_never_exceeds_max_delay_plus_jitter()
    {
        var policy = new ExponentialBackoffReconnectPolicy(
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromSeconds(1));

        var delay = policy.GetDelay(50)!.Value.TotalMilliseconds;

        delay.Should().BeLessOrEqualTo(1000 * 1.3 + 1);
    }

    [Fact]
    public void GetDelay_returns_null_once_max_attempts_exceeded()
    {
        var policy = new ExponentialBackoffReconnectPolicy(maxAttempts: 3);

        policy.GetDelay(3).Should().NotBeNull();
        policy.GetDelay(4).Should().BeNull();
    }

    [Fact]
    public void GetDelay_is_unlimited_by_default()
    {
        var policy = new ExponentialBackoffReconnectPolicy();

        policy.GetDelay(1000).Should().NotBeNull();
    }

    [Fact]
    public void GetDelay_rejects_non_positive_attempt()
    {
        var policy = new ExponentialBackoffReconnectPolicy();

        var act = () => policy.GetDelay(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
