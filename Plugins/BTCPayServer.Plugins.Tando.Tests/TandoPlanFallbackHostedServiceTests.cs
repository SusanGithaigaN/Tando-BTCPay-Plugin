using System.Collections.Concurrent;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Tando.Tests;

[Trait("FastTest", "FastTest")]
public class TandoPlanFallbackHostedServiceTests
{
    [Fact]
    public async Task ThrowingCheck_WaitsForTheIntervalAndLogs()
    {
        var logger = new CollectingLogger();
        var service = new ThrowingFallbackService();

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await service.StopAsync(CancellationToken.None);

        // With a 500ms interval that's at most 3 checks in 1.2s. A tight loop would run thousands.
        Assert.InRange(service.Calls, 1, 3);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception?.Message == "check failed");
    }

    private class ThrowingFallbackService() : TandoPlanFallbackHostedService(null!)
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override TimeSpan CheckInterval => TimeSpan.FromMilliseconds(500);

        protected override Task CheckAndSwapIfNeeded()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("check failed");
        }
    }

    private class CollectingLogger : ILogger<TandoPlanFallbackHostedService>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, exception));
    }
}