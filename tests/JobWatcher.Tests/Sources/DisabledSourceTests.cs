using JobWatcher.Domain;
using JobWatcher.Sources;

namespace JobWatcher.Tests.Sources;

public sealed class DisabledSourceTests
{
    [Fact]
    public async Task Fetch_throws_not_connected_with_source_and_reason()
    {
        var source = new DisabledSource(VacancySource.LinkedIn, "no lawful access route");

        var exception = await Assert.ThrowsAsync<SourceNotConnectedException>(
            () => source.FetchAsync(CancellationToken.None));

        Assert.Equal(VacancySource.LinkedIn, source.Source);
        Assert.Equal(VacancySource.LinkedIn, exception.SourceKind);
        Assert.Equal("Source LinkedIn is not connected: no lawful access route", exception.Message);
        Assert.Equal("no lawful access route", exception.Reason);
    }
}
