using JobWatcher.Configuration;
using JobWatcher.Domain;
using JobWatcher.Filtering;
using JobWatcher.Ratings;
using JobWatcher.Sources;
using JobWatcher.Storage;
using JobWatcher.Telegram;
using JobWatcher.Watching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JobWatcher;

public static class ServiceCollectionExtensions
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    // Shown to the user in Telegram as it is, so it stays short, plain Ukrainian and free of secrets.
    private const string LinkedInNotConnectedReason =
        "LinkedIn не пропонує дозволеного доступу до вакансій (відкритого API чи стрічки немає, "
        + "а умови забороняють автоматичний збір). Подробиці: docs/sources/linkedin.md";

    /// <summary>
    /// Registers everything the watcher needs: the validated options, storage, Telegram, the filter, the watch cycle
    /// with its hosted service, and a source for each job board whose <c>Enabled</c> setting is true (for LinkedIn a
    /// stand-in that reports it is not connected, because there is no lawful way to read it).
    /// </summary>
    /// <remarks>
    /// The sources are chosen once, here, from the configuration as it is now; changing <c>Enabled</c> later needs a
    /// restart. The watch cycle is a singleton, so the typed <see cref="HttpClient"/>s it holds live as long as the
    /// process and the factory never rotates their handlers; that is acceptable because the connection pool closes
    /// an idle connection (after a minute by default), so between cycles a client reconnects, and resolves DNS anew.
    /// </remarks>
    public static IServiceCollection AddJobWatcher(this IServiceCollection services, IConfiguration config)
    {
        // Options come first: a bad configuration has to stop the host before the hosted service runs its first cycle.
        services.AddSingleton<IValidateOptions<WatcherOptions>, WatcherOptionsValidator>();
        services
            .AddOptions<WatcherOptions>()
            .Bind(config.GetSection(WatcherOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<ISeenStore>(sp =>
            new SqliteSeenStore(Path.Combine(ReadOptions(sp).DataDir, "seen.db")));

        // SECURITY: the bot token is part of the request URL, and the default HttpClient logging handlers write the
        // whole URL at Information level, so they are removed from this client.
        services
            .AddHttpClient<TelegramNotifier>(client => client.Timeout = HttpTimeout)
            .RemoveAllLoggers();
        services.AddSingleton<IMessageNotifier>(sp => sp.GetRequiredService<TelegramNotifier>());

        services.AddSingleton(sp => new SourceHealth(ReadOptions(sp).FailureAlertThreshold));
        services.AddSingleton(sp => new VacancyFilter(ReadOptions(sp).Profile));
        services.AddSingleton<ICompanyRatingProvider, NullCompanyRatingProvider>();
        services.AddSingleton<IWatchCycle, WatchCycle>();

        AddEnabledSources(services, config);

        services.AddHostedService<WatcherService>();

        return services;
    }

    private static void AddEnabledSources(IServiceCollection services, IConfiguration config)
    {
        var sources = config.GetSection(WatcherOptions.SectionName).Get<WatcherOptions>()?.Sources ?? new SourcesOptions();

        // Each source is registered as a single instance: JoobleSource remembers when it last fetched, to keep
        // within the request quota of the API key.
        if (sources.Dou.Enabled)
        {
            services.AddHttpClient<DouRssSource>(client => client.Timeout = HttpTimeout);
            services.AddSingleton<IVacancySource>(sp => sp.GetRequiredService<DouRssSource>());
        }

        if (sources.Jooble.Enabled)
        {
            // SECURITY: the API key is part of the request URL; see the Telegram client above.
            services
                .AddHttpClient<JoobleSource>(client => client.Timeout = HttpTimeout)
                .RemoveAllLoggers();
            services.AddSingleton<IVacancySource>(sp => sp.GetRequiredService<JoobleSource>());
        }

        // The Djinni feed URL carries no secret (only a search filter), so the default HTTP logging stays on. Its client
        // keeps no cookies (the anonymous cookie Djinni sets on the feed must not travel with the vacancy page requests)
        // and follows no redirects (only djinni.co pages are requested; a redirect fails like any non-success answer).
        // The single instance is also the reader of the vacancy pages (IVacancyEnricher), which remembers when it last
        // requested a page.
        if (sources.Djinni.Enabled)
        {
            services
                .AddHttpClient<DjinniSource>(client => client.Timeout = HttpTimeout)
                .ConfigurePrimaryHttpMessageHandler((handler, _) => AnonymousAndNoRedirects(handler));
            services.AddSingleton<IVacancySource>(sp => sp.GetRequiredService<DjinniSource>());
        }

        // LinkedIn has no route that is lawful without the customer's own account (docs/sources/linkedin.md), so there
        // is no adapter. Enabling it registers a stand-in that makes every cycle report "not connected", so the user
        // is told once in Telegram instead of the setting doing nothing.
        if (sources.LinkedIn.Enabled)
        {
            services.AddSingleton<IVacancySource>(new DisabledSource(VacancySource.LinkedIn, LinkedInNotConnectedReason));
        }
    }

    private static void AnonymousAndNoRedirects(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.UseCookies = false;
                sockets.AllowAutoRedirect = false;
                break;
            case HttpClientHandler classic:
                classic.UseCookies = false;
                classic.AllowAutoRedirect = false;
                break;
        }
    }

    private static WatcherOptions ReadOptions(IServiceProvider sp) => sp.GetRequiredService<IOptions<WatcherOptions>>().Value;
}
