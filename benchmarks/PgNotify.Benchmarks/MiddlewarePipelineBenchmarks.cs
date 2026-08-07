using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PgNotify.Dispatch;
using PgNotify.Internal;
using PgNotify.Serialization;

namespace PgNotify.Benchmarks;

/// <summary>
/// Measures the overhead <see cref="NotificationDispatchPipeline"/> adds per built-in middleware
/// stage (<c>UseLogging()</c>/<c>UseRetry()</c>/<c>UseMetrics()</c>), each wrapping the terminal
/// dispatcher exactly like ASP.NET Core middleware wraps a request delegate, against a bare
/// pipeline with no middleware at all.
/// </summary>
[MemoryDiagnoser]
public class MiddlewarePipelineBenchmarks
{
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;

    private NotificationDispatchPipeline _noMiddleware = null!;
    private NotificationDispatchPipeline _logging = null!;
    private NotificationDispatchPipeline _retry = null!;
    private NotificationDispatchPipeline _metrics = null!;
    private NotificationDispatchPipeline _all = null!;

    private NotificationContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        var channelMap = new NotificationChannelMap();
        channelMap.MapChannel("users", typeof(User));

        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<User>, NoOpUserUpdatedHandler>();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        var loggingMiddleware = new LoggingNotificationMiddleware(NullLogger<LoggingNotificationMiddleware>.Instance);
        var retryMiddleware = new RetryNotificationMiddleware(
            Options.Create(new PostgresNotificationsOptions()), NullLogger<RetryNotificationMiddleware>.Instance);
        var metricsMiddleware = new MetricsNotificationMiddleware();

        _noMiddleware = new NotificationDispatchPipeline([], channelMap, new NotificationEventHub());
        _logging = new NotificationDispatchPipeline([loggingMiddleware], channelMap, new NotificationEventHub());
        _retry = new NotificationDispatchPipeline([retryMiddleware], channelMap, new NotificationEventHub());
        _metrics = new NotificationDispatchPipeline([metricsMiddleware], channelMap, new NotificationEventHub());
        _all = new NotificationDispatchPipeline([loggingMiddleware, retryMiddleware, metricsMiddleware], channelMap, new NotificationEventHub());

        _context = new NotificationContext
        {
            Envelope = new NotificationEnvelope
            {
                Channel = "users",
                Entity = "User",
                Operation = NotificationOperation.Update,
                Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
                RawPayload = """{"entity":"User","operation":"updated","id":42,"name":"Ada Lovelace"}""",
            },
            Services = _scope.ServiceProvider,
            CancellationToken = CancellationToken.None,
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task NoMiddleware() => _noMiddleware.InvokeAsync(_context);

    [Benchmark]
    public Task Logging() => _logging.InvokeAsync(_context);

    [Benchmark]
    public Task Retry() => _retry.InvokeAsync(_context);

    [Benchmark]
    public Task Metrics() => _metrics.InvokeAsync(_context);

    [Benchmark]
    public Task LoggingRetryAndMetrics() => _all.InvokeAsync(_context);
}
