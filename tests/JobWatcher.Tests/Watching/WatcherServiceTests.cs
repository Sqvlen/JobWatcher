using System.Collections.Concurrent;
using JobWatcher.Configuration;
using JobWatcher.Telegram;
using JobWatcher.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Watching;

public sealed class WatcherServiceTests : IAsyncLifetime
{
    // Generous so a slow machine never fails a test; a passing test never waits this long.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private WatcherService? _service;

    public Task InitializeAsync() => Task.CompletedTask;

    // Every test stops its service, so no polling loop outlives the test that started it.
    public async Task DisposeAsync()
    {
        if (_service is { } service)
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Generous);
            service.Dispose();
        }
    }

    private async Task StartAsync(WatcherService service)
    {
        _service = service;
        await service.StartAsync(CancellationToken.None);
    }

    private sealed class FakeCycle : IWatchCycle
    {
        private readonly SemaphoreSlim _started = new(0);
        private readonly object _gate = new();
        private int _calls;
        private int _running;
        private int _maxConcurrent;

        /// <summary>What each call does, given its 1-based number and the stopping token; null means return at once.</summary>
        public Func<int, CancellationToken, Task>? Behavior { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public int MaxConcurrent
        {
            get
            {
                lock (_gate)
                {
                    return _maxConcurrent;
                }
            }
        }

        public async Task RunOnceAsync(CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            lock (_gate)
            {
                _running++;
                _maxConcurrent = Math.Max(_maxConcurrent, _running);
            }

            _started.Release();
            try
            {
                await (Behavior?.Invoke(call, ct) ?? Task.CompletedTask);
            }
            finally
            {
                lock (_gate)
                {
                    _running--;
                }
            }
        }

        /// <summary>Waits until <paramref name="count"/> more calls have started since the previous wait.</summary>
        public async Task WaitForCallsAsync(int count)
        {
            for (var i = 1; i <= count; i++)
            {
                Assert.True(await _started.WaitAsync(Generous), $"Timed out waiting for cycle call {i} of {count}");
            }
        }
    }

    private sealed class FakeNotifier : IMessageNotifier
    {
        private readonly ConcurrentQueue<string> _sent = new();
        private int _attempts;

        /// <summary>What each send does, given its 1-based number; null means the message is delivered.</summary>
        public Func<int, Exception?>? FailureFor { get; init; }

        public string[] Sent => _sent.ToArray();

        public int Attempts => Volatile.Read(ref _attempts);

        public Task SendAsync(string html, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (FailureFor?.Invoke(attempt) is { } failure)
            {
                throw failure;
            }

            _sent.Enqueue(html);

            return Task.CompletedTask;
        }
    }

    private static string CycleAlert(int failures, string type) =>
        $"⚠️ Цикл перевірки не вдається ({failures} разів поспіль): {type}. Подробиці в журналі сервісу.";

    private sealed class RecordingLogger : ILogger<WatcherService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public (LogLevel Level, string Message, Exception? Exception)[] Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task Runs_cycle_immediately_on_start()
    {
        var cycle = new FakeCycle();
        var options = Options.Create(new WatcherOptions { PollIntervalMinutes = 60 });

        await StartAsync(new WatcherService(cycle, options, new FakeNotifier(), new RecordingLogger()));

        // The configured interval is an hour, so this call can only be the one made at once on start.
        await cycle.WaitForCallsAsync(1);
        Assert.Equal(1, cycle.Calls);
    }

    [Fact]
    public async Task Runs_cycle_repeatedly_on_interval()
    {
        var cycle = new FakeCycle();

        await StartAsync(new WatcherService(cycle, Fast, 3, new FakeNotifier(), new RecordingLogger()));

        await cycle.WaitForCallsAsync(3);
        Assert.True(cycle.Calls >= 3);
    }

    [Fact]
    public async Task Exception_in_cycle_is_logged_and_loop_continues()
    {
        var failure = new InvalidOperationException("storage is down");
        var cycle = new FakeCycle { Behavior = (call, _) => call == 1 ? Task.FromException(failure) : Task.CompletedTask };
        var log = new RecordingLogger();

        await StartAsync(new WatcherService(cycle, Fast, 3, new FakeNotifier(), log));

        // The second call starts only after the first one failed and was logged.
        await cycle.WaitForCallsAsync(2);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
    }

    [Fact]
    public async Task Cancellation_not_caused_by_stopping_is_logged_and_loop_continues()
    {
        // For example an HTTP timeout: it is a cancellation, but the service is not stopping.
        var timeout = new OperationCanceledException("The request timed out");
        var cycle = new FakeCycle { Behavior = (call, _) => call == 1 ? Task.FromException(timeout) : Task.CompletedTask };
        var log = new RecordingLogger();

        await StartAsync(new WatcherService(cycle, Fast, 3, new FakeNotifier(), log));

        await cycle.WaitForCallsAsync(2);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, timeout));
    }

    [Fact]
    public async Task Stops_promptly_on_cancellation()
    {
        // A cycle that runs until the stopping token cancels it.
        var cycle = new FakeCycle { Behavior = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        var log = new RecordingLogger();
        var service = new WatcherService(cycle, Fast, 3, new FakeNotifier(), log);
        await StartAsync(service);
        await cycle.WaitForCallsAsync(1);

        await service.StopAsync(CancellationToken.None).WaitAsync(Generous);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Stops_promptly_when_cancelled_while_waiting_for_the_next_tick()
    {
        var cycle = new FakeCycle();
        var log = new RecordingLogger();
        var service = new WatcherService(cycle, Hour, 3, new FakeNotifier(), log);
        await StartAsync(service);
        await cycle.WaitForCallsAsync(1);

        await service.StopAsync(CancellationToken.None).WaitAsync(Generous);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(1, cycle.Calls);
        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Does_not_start_a_cycle_while_the_previous_one_is_still_running()
    {
        var release = new TaskCompletionSource();
        var cycle = new FakeCycle { Behavior = (call, ct) => call == 1 ? release.Task.WaitAsync(ct) : Task.CompletedTask };

        await StartAsync(new WatcherService(cycle, Fast, 3, new FakeNotifier(), new RecordingLogger()));
        await cycle.WaitForCallsAsync(1);

        // The first cycle outlasts many intervals; a negative check can only be made by letting time pass.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, cycle.Calls);

        release.SetResult();
        await cycle.WaitForCallsAsync(2);
        Assert.Equal(1, cycle.MaxConcurrent);
    }

    [Fact]
    public async Task Alerts_once_when_failed_cycles_reach_the_threshold()
    {
        var cycle = new FakeCycle { Behavior = (_, _) => Task.FromException(new InvalidOperationException("disk /secret/path is read-only")) };
        var notifier = new FakeNotifier();

        await StartAsync(new WatcherService(cycle, Fast, 3, notifier, new RecordingLogger()));

        // A call starts only after the previous one, with its alert, has finished: three more cycles fail after
        // the alerting third one.
        await cycle.WaitForCallsAsync(6);
        var alert = Assert.Single(notifier.Sent);
        Assert.Equal(CycleAlert(3, nameof(InvalidOperationException)), alert);
        Assert.DoesNotContain("secret", alert);
        Assert.Equal(1, notifier.Attempts);
    }

    [Fact]
    public async Task Failures_below_the_threshold_do_not_alert()
    {
        var cycle = new FakeCycle
        {
            Behavior = (call, _) => call is 1 or 2 or 4 or 5 ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask,
        };
        var notifier = new FakeNotifier();

        await StartAsync(new WatcherService(cycle, Fast, 3, notifier, new RecordingLogger()));

        await cycle.WaitForCallsAsync(8);
        Assert.Equal(0, notifier.Attempts);
    }

    [Fact]
    public async Task Undelivered_alert_is_retried_next_failed_cycle_and_not_repeated_once_delivered()
    {
        var cycle = new FakeCycle { Behavior = (_, _) => Task.FromException(new TimeoutException("slow")) };
        var notifier = new FakeNotifier { FailureFor = attempt => attempt == 1 ? new TelegramDeliveryException("Telegram is down") : null };
        var log = new RecordingLogger();

        await StartAsync(new WatcherService(cycle, Fast, 2, notifier, log));

        await cycle.WaitForCallsAsync(6);
        Assert.Equal([CycleAlert(3, nameof(TimeoutException))], notifier.Sent);
        Assert.Equal(2, notifier.Attempts);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Telegram is down"));
    }

    [Fact]
    public async Task Alert_send_that_throws_unexpectedly_does_not_stop_the_loop()
    {
        var cycle = new FakeCycle { Behavior = (_, _) => Task.FromException(new InvalidOperationException("boom")) };
        var notifier = new FakeNotifier { FailureFor = _ => new HttpRequestException("socket closed") };

        await StartAsync(new WatcherService(cycle, Fast, 1, notifier, new RecordingLogger()));

        await cycle.WaitForCallsAsync(4);
        Assert.True(notifier.Attempts >= 3);
    }

    [Fact]
    public async Task A_successful_cycle_resets_the_streak_so_a_new_outage_alerts_again()
    {
        var cycle = new FakeCycle
        {
            Behavior = (call, _) => call is 1 or 2 or 4 or 5 ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask,
        };
        var notifier = new FakeNotifier();

        await StartAsync(new WatcherService(cycle, Fast, 2, notifier, new RecordingLogger()));

        await cycle.WaitForCallsAsync(8);
        Assert.Equal([CycleAlert(2, nameof(InvalidOperationException)), CycleAlert(2, nameof(InvalidOperationException))], notifier.Sent);
    }

    [Fact]
    public async Task Stopping_cancellation_never_alerts()
    {
        var cycle = new FakeCycle { Behavior = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        var notifier = new FakeNotifier();
        var service = new WatcherService(cycle, Fast, 1, notifier, new RecordingLogger());
        await StartAsync(service);
        await cycle.WaitForCallsAsync(1);

        await service.StopAsync(CancellationToken.None).WaitAsync(Generous);

        Assert.Equal(0, notifier.Attempts);
    }
}
