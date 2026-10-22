namespace JobWatcher.Tests;

/// <summary>
/// A clock that moves only when told to or when a timer is created on it: a timer advances the clock by its due time
/// and fires at once, so code that really awaits <c>Task.Delay(gap, timeProvider, ct)</c> runs without sleeping, and
/// the test sees every wait it asked for.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _waits = [];
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>The due times of the timers created so far, in order.</summary>
    public IReadOnlyList<TimeSpan> Waits
    {
        get
        {
            lock (_waits)
            {
                return [.. _waits];
            }
        }
    }

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_waits)
        {
            _waits.Add(dueTime);
        }

        Advance(dueTime);
        ThreadPool.QueueUserWorkItem(_ => callback(state));

        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
