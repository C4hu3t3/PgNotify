using BenchmarkDotNet.Attributes;
using PgNotify.Serialization;

namespace PgNotify.Benchmarks;

/// <summary>
/// Quantifies the two-pass payload parsing <c>docs/performance.md</c> calls out: the envelope is
/// parsed once into a <see cref="System.Text.Json.JsonDocument"/> by
/// <see cref="DefaultNotificationPayloadDeserializer"/> (to read <c>entity</c>/<c>operation</c>/
/// <c>keys</c>/<c>changed</c>/<c>timestamp</c>), then <see cref="NotificationEnvelopeExtensions.ToTyped{T}"/>
/// does an independent second <see cref="System.Text.Json.JsonSerializer.Deserialize{T}(string, System.Text.Json.JsonSerializerOptions?)"/>
/// pass over the same raw string for the strongly-typed conversion.
/// </summary>
[MemoryDiagnoser]
public class PayloadDeserializationBenchmarks
{
    private const string MinimalPayload = """{"entity":"User","operation":"updated","id":42}""";

    private const string ExtendedPayload =
        """{"entity":"User","operation":"updated","keys":{"id":42},"changed":["Name","Status"],"timestamp":"2026-07-24T12:00:00Z","id":42,"name":"Ada Lovelace"}""";

    private NotificationEnvelope _minimalEnvelope = null!;
    private NotificationEnvelope _extendedEnvelope = null!;

    [GlobalSetup]
    public void Setup()
    {
        _minimalEnvelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("users", MinimalPayload);
        _extendedEnvelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("users", ExtendedPayload);
    }

    [Benchmark(Baseline = true)]
    public NotificationEnvelope ParseEnvelope_Minimal() => DefaultNotificationPayloadDeserializer.Instance.Deserialize("users", MinimalPayload);

    [Benchmark]
    public NotificationEnvelope ParseEnvelope_Extended() => DefaultNotificationPayloadDeserializer.Instance.Deserialize("users", ExtendedPayload);

    [Benchmark]
    public UserUpdatedShape ToTyped_Minimal() => _minimalEnvelope.ToTyped<UserUpdatedShape>();

    [Benchmark]
    public UserUpdatedShape ToTyped_Extended() => _extendedEnvelope.ToTyped<UserUpdatedShape>();

    [Benchmark]
    public UserUpdatedShape ParseAndConvert_Extended()
    {
        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("users", ExtendedPayload);
        return envelope.ToTyped<UserUpdatedShape>();
    }
}
