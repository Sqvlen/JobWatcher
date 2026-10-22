using JobWatcher.Domain;
using JobWatcher.Watching;

namespace JobWatcher.Tests.Watching;

public sealed class SourceHealthTests
{
    [Fact]
    public void Failure_alerts_exactly_once_at_threshold()
    {
        var health = new SourceHealth(3);

        Assert.False(health.RecordFailure(VacancySource.Dou));
        Assert.False(health.RecordFailure(VacancySource.Dou));
        Assert.True(health.RecordFailure(VacancySource.Dou));

        health.MarkAlerted(VacancySource.Dou);

        Assert.False(health.RecordFailure(VacancySource.Dou));
        Assert.False(health.RecordFailure(VacancySource.Dou));
    }

    [Fact]
    public void Alert_repeats_until_marked_alerted()
    {
        var health = new SourceHealth(2);

        Assert.False(health.RecordFailure(VacancySource.Dou));
        Assert.True(health.RecordFailure(VacancySource.Dou));
        Assert.True(health.RecordFailure(VacancySource.Dou));
        Assert.True(health.RecordFailure(VacancySource.Dou));

        health.MarkAlerted(VacancySource.Dou);

        Assert.False(health.RecordFailure(VacancySource.Dou));
    }

    [Fact]
    public void Success_resets_so_next_outage_alerts_again()
    {
        var health = new SourceHealth(2);
        health.RecordFailure(VacancySource.Dou);
        Assert.True(health.RecordFailure(VacancySource.Dou));
        health.MarkAlerted(VacancySource.Dou);

        health.RecordSuccess(VacancySource.Dou);

        Assert.False(health.RecordFailure(VacancySource.Dou));
        Assert.True(health.RecordFailure(VacancySource.Dou));
    }

    [Fact]
    public void Success_resets_alerted_flag()
    {
        var health = new SourceHealth(1);
        Assert.True(health.RecordFailure(VacancySource.Dou));
        health.MarkAlerted(VacancySource.Dou);
        Assert.False(health.RecordFailure(VacancySource.Dou));

        health.RecordSuccess(VacancySource.Dou);

        Assert.True(health.RecordFailure(VacancySource.Dou));
    }

    [Fact]
    public void Failures_are_tracked_per_source()
    {
        var health = new SourceHealth(3);
        health.RecordFailure(VacancySource.Dou);
        health.RecordFailure(VacancySource.Dou);
        Assert.False(health.RecordFailure(VacancySource.Jooble));

        Assert.True(health.RecordFailure(VacancySource.Dou));
        health.MarkAlerted(VacancySource.Dou);
        Assert.False(health.RecordFailure(VacancySource.Jooble));
        Assert.True(health.RecordFailure(VacancySource.Jooble));
    }

    [Fact]
    public void NotConnected_announced_once()
    {
        var health = new SourceHealth(3);

        Assert.True(health.ShouldAnnounceNotConnected(VacancySource.Djinni));
        health.MarkNotConnectedAnnounced(VacancySource.Djinni);

        Assert.False(health.ShouldAnnounceNotConnected(VacancySource.Djinni));
        Assert.False(health.ShouldAnnounceNotConnected(VacancySource.Djinni));
        Assert.True(health.ShouldAnnounceNotConnected(VacancySource.LinkedIn));
    }

    [Fact]
    public void NotConnected_announced_until_marked()
    {
        var health = new SourceHealth(3);

        Assert.True(health.ShouldAnnounceNotConnected(VacancySource.Djinni));
        Assert.True(health.ShouldAnnounceNotConnected(VacancySource.Djinni));

        health.MarkNotConnectedAnnounced(VacancySource.Djinni);

        Assert.False(health.ShouldAnnounceNotConnected(VacancySource.Djinni));
    }
}
