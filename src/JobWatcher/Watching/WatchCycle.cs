using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Filtering;
using JobWatcher.Formatting;
using JobWatcher.Parsing;
using JobWatcher.Ratings;
using JobWatcher.Sources;
using JobWatcher.Storage;
using JobWatcher.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobWatcher.Watching;

/// <summary>
/// One polling pass over all sources. Per source: fetch, drop invalid and repeated vacancies, then either
/// remember everything silently (first run) or announce the new vacancies that fit the profile, and log one
/// summary line with the counts (not on the first run of a source that returned vacancies, which has its own line,
/// nor when the fetch failed). A source that <see cref="IVacancySource.AnnouncesExistingOnFirstRun"/> has no silent
/// first run: its first fetch is announced like any other. A source that is also an <see cref="IVacancyEnricher"/> has the details of its new
/// vacancies read before the profile filter sees them, a limited number per cycle (never on the first run).
/// </summary>
/// <remarks>
/// A vacancy is recorded as seen only after Telegram accepted its message, so a failure never loses one; the
/// worst case (recording fails after a successful send) is a repeated message. Telegram is shared by every
/// source, so a failed vacancy send stops the whole cycle and the next cycle repeats it. A vacancy that keeps
/// failing is reported to the user and dropped only if that report got through. A vacancy whose message cannot even
/// be built is skipped without stopping the cycle and counted the same way. Not thread-safe: the single polling loop
/// is its only caller.
/// </remarks>
public sealed class WatchCycle(
    IEnumerable<IVacancySource> sources,
    VacancyFilter filter,
    ISeenStore store,
    ICompanyRatingProvider ratings,
    IMessageNotifier notifier,
    SourceHealth health,
    IOptions<WatcherOptions> options,
    ILogger<WatchCycle> log) : IWatchCycle
{
    /// <summary>The number of failed sends of the same vacancy after which the user is told about it.</summary>
    public const int PoisonAttemptThreshold = 3;

    // Failed sends (or failed message preparations) per vacancy since the process started; the key is the source
    // and the source's own id.
    private readonly Dictionary<(VacancySource Source, string SourceId), int> _failedSends = [];

    /// <summary>Builds a vacancy's message from the vacancy and its rating; replaceable only so tests can make it fail.</summary>
    internal Func<Vacancy, CompanyRating?, string> Compose { get; set; } = (vacancy, rating) =>
        MessageFormatter.Format(vacancy, RequirementsExtractor.Extract(vacancy.Description), rating);

    // The details already read of new vacancies that are not stored as seen yet (held back, refused by Telegram,
    // retried), so their pages are not read again every cycle; the key is the source and the source's own id. An entry
    // goes when its vacancy is stored as seen or is no longer new in its source's list, so the map never holds more
    // than the new vacancies of the last fetch. In memory only: after a restart the pages are read again.
    private readonly Dictionary<(VacancySource Source, string SourceId), ReadDetails> _details = [];

    /// <summary>How many vacancies' details are kept for reuse; for tests.</summary>
    internal int CachedDetailsCount => _details.Count;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var budget = new SendBudget(options.Value.MaxMessagesPerCycle);

        foreach (var source in sources)
        {
            if (!await WatchAsync(source, budget, ct))
            {
                log.LogWarning("Telegram is not accepting messages, so the cycle is stopped; the next cycle repeats what was not sent");

                return;
            }
        }
    }

    // False when Telegram failed and the cycle must stop.
    private async Task<bool> WatchAsync(IVacancySource source, SendBudget budget, CancellationToken ct)
    {
        if (await FetchAsync(source, ct) is not { } fetched)
        {
            return true;
        }

        // A vacancy labelled with another (or an unknown) source would be remembered under the wrong key, so it is
        // dropped; that is a bug in the adapter, hence a warning.
        var labelled = fetched.Where(v => v.Source == source.Source && v.Source.IsKnown()).ToList();
        if (labelled.Count < fetched.Count)
        {
            log.LogWarning(
                "Ignored {Count} vacancies of {Source} labelled with another source",
                fetched.Count - labelled.Count,
                source.Source.DisplayName());
        }

        var vacancies = ValidAndDistinct(labelled);
        if (vacancies.Count < labelled.Count)
        {
            log.LogInformation(
                "Ignored {Count} vacancies of {Source} with blank fields or a repeated id",
                labelled.Count - vacancies.Count,
                source.Source.DisplayName());
        }

        // Nothing is stored for an empty fetch, so an empty first fetch leaves the source in first-run mode, and the store
        // is not asked at all. The summary still says that nothing usable came back, so an empty source (or a broken
        // key, or a changed feed) does not look like a frozen service; with no first-run line there is no other count.
        if (vacancies.Count == 0)
        {
            LogSummary(source, fetched: fetched.Count, fresh: 0, matching: 0, sent: 0, heldBack: 0);

            return true;
        }

        if (!source.AnnouncesExistingOnFirstRun && !await store.HasAnyAsync(source.Source, ct))
        {
            await store.AddAsync(source.Source, vacancies.Select(v => v.SourceId), ct);
            log.LogInformation("First run of {Source}: {Count} existing vacancies marked as seen", source.Source.DisplayName(), vacancies.Count);

            return true;
        }

        var fresh = await NewVacanciesAsync(source, vacancies, ct);
        if (source is IVacancyEnricher enricher)
        {
            ForgetDetailsNotIn(source, fresh);
            if (enricher.IsEnabled && fresh.Count > 0)
            {
                fresh = await EnrichAsync(source, enricher, fresh, ct);
            }
        }

        var byMatch = fresh.ToLookup(filter.Matches);

        var notMatching = byMatch[false].Select(v => v.SourceId).ToList();
        if (notMatching.Count > 0)
        {
            await store.AddAsync(source.Source, notMatching, ct);
            ForgetDetails(source, notMatching);
        }

        var matching = byMatch[true].ToList();

        // The budget counts the messages of all sources, so this source's share is what is added to it from here on.
        var sentBefore = budget.Sent;
        var (heldBack, telegramStopped) = await DeliverAllAsync(source, matching, budget, ct);

        // Also logged when Telegram stopped the cycle, so the counts explain the stop.
        LogSummary(source, fetched.Count, fresh.Count, matching.Count, budget.Sent - sentBefore, heldBack);

        return !telegramStopped;
    }

    // Gives the new vacancies their details, in the order of the source. Details read in an earlier cycle are reused
    // (and count as read); otherwise up to MaxPerCycle vacancies have their details read, one after another. The rest
    // are deferred: neither filtered nor stored, so the next cycle finds them new again. A vacancy whose details cannot
    // be read goes on as the source listed it (and is not tried again while it stays new); that is no outage of the
    // source, so SourceHealth is not told.
    private async Task<List<Vacancy>> EnrichAsync(
        IVacancySource source, IVacancyEnricher enricher, List<Vacancy> fresh, CancellationToken ct)
    {
        var enriched = new List<Vacancy>(fresh.Count);
        var read = 0;
        var requests = 0;
        foreach (var vacancy in fresh)
        {
            var key = (source.Source, vacancy.SourceId);
            if (!_details.TryGetValue(key, out var details))
            {
                if (requests >= enricher.MaxPerCycle)
                {
                    continue;
                }

                requests++;
                details = await ReadDetailsAsync(source, enricher, vacancy, ct);
                _details[key] = details;
            }

            enriched.Add(details.Vacancy);
            if (details.Read)
            {
                read++;
            }
        }

        // Not a single page could be read: the page markup has probably changed, which deserves a look.
        log.Log(
            read == 0 ? LogLevel.Warning : LogLevel.Information,
            "{Source}: details read for {Read} of {Fresh} new vacancies, deferred {Deferred}",
            source.Source.DisplayName(),
            read,
            fresh.Count,
            fresh.Count - enriched.Count);

        return enriched;
    }

    private async Task<ReadDetails> ReadDetailsAsync(
        IVacancySource source, IVacancyEnricher enricher, Vacancy vacancy, CancellationToken ct)
    {
        try
        {
            return new ReadDetails(await enricher.EnrichAsync(vacancy, ct), Read: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only the type and the message: a stack trace per vacancy would flood the log.
            log.LogWarning(
                "Could not read the details of {Source} vacancy {SourceId}: {Reason}",
                source.Source.DisplayName(),
                vacancy.SourceId,
                $"{ex.GetType().Name}: {ex.Message}");

            return new ReadDetails(vacancy, Read: false);
        }
    }

    private void ForgetDetails(IVacancySource source, IEnumerable<string> sourceIds)
    {
        foreach (var id in sourceIds)
        {
            _details.Remove((source.Source, id));
        }
    }

    // Keeps only the details of vacancies that are still new in the source's list.
    private void ForgetDetailsNotIn(IVacancySource source, List<Vacancy> fresh)
    {
        var stillNew = fresh.Select(v => v.SourceId).ToHashSet();
        var gone = _details.Keys.Where(key => key.Source == source.Source && !stillNew.Contains(key.SourceId)).ToList();
        foreach (var key in gone)
        {
            _details.Remove(key);
        }
    }

    // The one line per source and cycle that tells how many vacancies were read and what became of them.
    private void LogSummary(IVacancySource source, int fetched, int fresh, int matching, int sent, int heldBack) =>
        log.LogInformation(
            "{Source}: fetched {Fetched}, new {New}, matching the profile {Matching}, sent {Sent}, held back {HeldBack}",
            source.Source.DisplayName(),
            fetched,
            fresh,
            matching,
            sent,
            heldBack);

    // Delivers the vacancies in order while the cycle's budget lasts. HeldBack is how many were left untried because the
    // budget ran out; Stopped means Telegram failed and the cycle must stop.
    private async Task<(int HeldBack, bool Stopped)> DeliverAllAsync(
        IVacancySource source, List<Vacancy> matching, SendBudget budget, CancellationToken ct)
    {
        for (var i = 0; i < matching.Count; i++)
        {
            if (!budget.HasRoom)
            {
                return (matching.Count - i, false);
            }

            if (!await DeliverAsync(source, matching[i], budget, ct))
            {
                return (0, true);
            }
        }

        return (0, false);
    }

    // Null when nothing came back: the source is not connected, or its fetch failed.
    private async Task<IReadOnlyList<Vacancy>?> FetchAsync(IVacancySource source, CancellationToken ct)
    {
        try
        {
            var fetched = await source.FetchAsync(ct);
            health.RecordSuccess(source.Source);

            return fetched;
        }
        catch (SourceNotConnectedException ex)
        {
            log.LogInformation("Source {Source} is not connected: {Reason}", source.Source.DisplayName(), ex.Reason);
            if (health.ShouldAnnounceNotConnected(source.Source)
                && await TrySendOperationalAsync(NotConnectedNotice(source.Source, ex.Reason), ct))
            {
                // Remembered only once delivered; otherwise the notice is offered again next cycle.
                health.MarkNotConnectedAnnounced(source.Source);
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The service is shutting down; that is no fault of the source.
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fetching {Source} failed", source.Source.DisplayName());
            if (health.RecordFailure(source.Source)
                && await TrySendOperationalAsync(DownAlert(source.Source, health.FailureThreshold), ct))
            {
                // Remembered only once delivered; otherwise the alert is offered again next cycle.
                health.MarkAlerted(source.Source);
            }

            return null;
        }
    }

    private static List<Vacancy> ValidAndDistinct(IEnumerable<Vacancy> fetched) =>
        fetched
            .Where(v => !string.IsNullOrWhiteSpace(v.SourceId)
                && !string.IsNullOrWhiteSpace(v.Title)
                && !string.IsNullOrWhiteSpace(v.Company)
                && !string.IsNullOrWhiteSpace(v.Url))
            .DistinctBy(v => v.SourceId)
            .ToList();

    private async Task<List<Vacancy>> NewVacanciesAsync(IVacancySource source, List<Vacancy> vacancies, CancellationToken ct)
    {
        var fresh = new List<Vacancy>();
        foreach (var vacancy in vacancies)
        {
            if (!await store.ContainsAsync(source.Source, vacancy.SourceId, ct))
            {
                fresh.Add(vacancy);
            }
        }

        return fresh;
    }

    // False when Telegram failed and the cycle must stop.
    private async Task<bool> DeliverAsync(IVacancySource source, Vacancy vacancy, SendBudget budget, CancellationToken ct)
    {
        string html;
        try
        {
            html = Compose(vacancy, await FindRatingAsync(vacancy.Company, ct));
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TelegramDeliveryException))
        {
            await HandleFailedPreparationAsync(source, vacancy, ex, ct);

            return true;
        }

        try
        {
            await notifier.SendAsync(html, ct);
        }
        catch (TelegramDeliveryException ex)
        {
            return await HandleFailedSendAsync(source, vacancy, ex, ct);
        }

        _failedSends.Remove((source.Source, vacancy.SourceId));
        budget.RecordSent();

        // Only now: were this to fail, the vacancy would be sent again, never lost.
        await store.AddAsync(source.Source, [vacancy.SourceId], ct);
        ForgetDetails(source, [vacancy.SourceId]);

        return true;
    }

    // The vacancy stays unseen, so the next cycle retries it. False (stop the cycle) unless the vacancy was
    // given up on and the user told.
    private async Task<bool> HandleFailedSendAsync(IVacancySource source, Vacancy vacancy, TelegramDeliveryException ex, CancellationToken ct)
    {
        log.LogWarning("Telegram did not accept vacancy {SourceId} of {Source}: {Reason}", vacancy.SourceId, source.Source.DisplayName(), ex.Message);

        // The same vacancy keeps failing and would block everything behind it. If the notice cannot be
        // delivered either, Telegram itself is the problem: keep the vacancy and try again next time.
        return await RecordFailureAndMaybeGiveUpAsync(source, vacancy, ct);
    }

    // The message of the vacancy could not even be built (a bug in the formatting of this vacancy's text). Only this
    // vacancy is skipped, the cycle goes on; it is retried next cycle and given up on like a vacancy Telegram refuses.
    private async Task HandleFailedPreparationAsync(IVacancySource source, Vacancy vacancy, Exception ex, CancellationToken ct)
    {
        log.LogWarning(
            "Preparing the message of vacancy {SourceId} of {Source} failed ({ExceptionType}: {Reason}); it is skipped this cycle",
            vacancy.SourceId,
            source.Source.DisplayName(),
            ex.GetType().Name,
            ex.Message);

        await RecordFailureAndMaybeGiveUpAsync(source, vacancy, ct);
    }

    // Counts one more failure of the vacancy. At the threshold the user is told, and only if that notice got through
    // is the vacancy marked seen and forgotten. True when the vacancy was given up on.
    private async Task<bool> RecordFailureAndMaybeGiveUpAsync(IVacancySource source, Vacancy vacancy, CancellationToken ct)
    {
        var key = (source.Source, vacancy.SourceId);
        var failures = _failedSends.GetValueOrDefault(key) + 1;
        _failedSends[key] = failures;
        if (failures < PoisonAttemptThreshold || !await TrySendOperationalAsync(PoisonNotice(vacancy), ct))
        {
            return false;
        }

        await store.AddAsync(source.Source, [vacancy.SourceId], ct);
        ForgetDetails(source, [vacancy.SourceId]);
        _failedSends.Remove(key);

        return true;
    }

    private async Task<CompanyRating?> FindRatingAsync(string company, CancellationToken ct)
    {
        // The source did not name the employer: there is no company to rate.
        if (company == Placeholders.NotSpecified)
        {
            return null;
        }

        try
        {
            return await ratings.GetAsync(company, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Rating lookup for {Company} failed; sending the vacancy without a rating", company);

            return null;
        }
    }

    // A status message never stops the cycle: a failure is logged and the caller learns whether it got through.
    private async Task<bool> TrySendOperationalAsync(string html, CancellationToken ct)
    {
        try
        {
            await notifier.SendAsync(html, ct);

            return true;
        }
        catch (TelegramDeliveryException ex)
        {
            log.LogWarning("Telegram did not accept a status message: {Reason}", ex.Message);

            return false;
        }
    }

    private static string DownAlert(VacancySource source, int failures) =>
        $"⚠️ Джерело {MessageFormatter.EscapeHtml(source.DisplayName())} не відповідає ({failures} невдалих спроб поспіль)";

    private static string NotConnectedNotice(VacancySource source, string reason) =>
        $"ℹ️ Джерело {MessageFormatter.EscapeHtml(source.DisplayName())} не підключено: {MessageFormatter.EscapeHtml(reason)}";

    private static string PoisonNotice(Vacancy vacancy) =>
        $"⚠️ Не вдалося надіслати вакансію {MessageFormatter.EscapeHtml(vacancy.Title)} "
        + $"({MessageFormatter.EscapeHtml(vacancy.Company)}): {MessageFormatter.EscapeHtml(vacancy.Url)}";

    /// <summary>The details of a vacancy as they were read; <c>Read</c> is false when they could not be read.</summary>
    private sealed record ReadDetails(Vacancy Vacancy, bool Read);

    /// <summary>Counts the vacancy messages delivered in one cycle against <c>MaxMessagesPerCycle</c> (null = no limit).</summary>
    private sealed class SendBudget(int? limit)
    {
        private int _sent;

        /// <summary>The vacancy messages delivered so far in this cycle, by all sources.</summary>
        public int Sent => _sent;

        public bool HasRoom => limit is not { } max || _sent < max;

        public void RecordSent() => _sent++;
    }
}
