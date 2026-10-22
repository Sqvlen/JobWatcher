using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using JobWatcher.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace JobWatcher.Tests.Configuration;

public sealed class AzureKeyVaultConfigurationTests
{
    // A made-up vault; the fake transport below answers for it, nothing goes to the network.
    private const string VaultUri = "https://jobwatcher-test.vault.azure.net/";

    private static int SourceCount(IConfigurationBuilder configuration) => configuration.Sources.Count;

    private static ConfigurationManager Configuration(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(settings);

        return configuration;
    }

    private static SecretClient NeverCreated(Uri _) =>
        throw new InvalidOperationException("No Key Vault client may be created when Key Vault is off or misconfigured.");

    // ---- the credential decision (as in qbeauty-ua) ----

    [Theory]
    [InlineData(true, "1", true)]
    [InlineData(true, "0", true)]
    [InlineData(true, null, true)]
    [InlineData(true, "", true)]
    [InlineData(false, "1", true)]
    [InlineData(false, "0", false)]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    public void Managed_identity_is_excluded_in_development_or_when_IS_LOCAL_is_1(bool isDevelopment, string? isLocal, bool expected)
    {
        Assert.Equal(expected, AzureKeyVaultConfiguration.ShouldExcludeManagedIdentity(isDevelopment, isLocal));
    }

    // ---- switched off ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_vault_uri_nothing_is_added(string? vaultUri)
    {
        var configuration = Configuration(new() { ["KeyVault:VaultUri"] = vaultUri, ["JobWatcher:Telegram:ChatId"] = "42" });
        var sources = SourceCount(configuration);

        var progress = new StringWriter();

        AzureKeyVaultConfiguration.AddAzureKeyVault(configuration, NeverCreated, progress);

        Assert.Equal(sources, SourceCount(configuration));
        Assert.Equal("42", configuration["JobWatcher:Telegram:ChatId"]);
        // Nothing is said about a vault that is switched off.
        Assert.Equal("", progress.ToString());
    }

    [Fact]
    public void The_host_builder_without_a_vault_uri_gets_no_extra_source()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["KeyVault:VaultUri"] = "" });
        var sources = SourceCount(builder.Configuration);

        builder.AddAzureKeyVault();

        Assert.Equal(sources, SourceCount(builder.Configuration));
    }

    // ---- misconfigured ----

    [Theory]
    [InlineData("jobwatcher-test.vault.azure.net")]
    [InlineData("/secrets")]
    [InlineData("http://jobwatcher-test.vault.azure.net/")]
    [InlineData("ftp://jobwatcher-test.vault.azure.net/")]
    [InlineData("not a url")]
    public void An_invalid_vault_uri_fails_with_a_friendly_error_that_names_the_setting(string vaultUri)
    {
        var configuration = Configuration(new() { ["KeyVault:VaultUri"] = vaultUri });
        var progress = new StringWriter();

        var failure = Assert.Throws<KeyVaultLoadException>(
            () => AzureKeyVaultConfiguration.AddAzureKeyVault(configuration, NeverCreated, progress));

        Assert.Equal("", progress.ToString());
        var cause = Assert.IsType<UriFormatException>(failure.InnerException);
        Assert.Contains("KeyVault__VaultUri", cause.Message);
        Assert.Contains("https://", cause.Message);
    }

    [Fact]
    public void The_host_builder_with_an_invalid_vault_uri_fails_before_any_azure_call()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["KeyVault:VaultUri"] = "http://example.com/" });

        var failure = Assert.Throws<KeyVaultLoadException>(() => builder.AddAzureKeyVault());

        Assert.IsType<UriFormatException>(failure.InnerException);
    }

    [Fact]
    public void A_vault_that_refuses_access_fails_with_a_KeyVaultLoadException()
    {
        var vault = new FakeVault(forbidden: true);
        var configuration = Configuration(new() { ["KeyVault:VaultUri"] = VaultUri });

        var failure = Assert.Throws<KeyVaultLoadException>(
            () => AzureKeyVaultConfiguration.AddAzureKeyVault(configuration, vault.CreateClient, TextWriter.Null));

        var cause = Assert.IsType<RequestFailedException>(failure.InnerException);
        Assert.Equal(403, cause.Status);
    }

    [Fact]
    public void Any_failure_while_creating_the_client_is_a_KeyVaultLoadException()
    {
        var configuration = Configuration(new() { ["KeyVault:VaultUri"] = VaultUri });
        var unavailable = new TimeoutException("The vault did not answer.");

        var failure = Assert.Throws<KeyVaultLoadException>(
            () => AzureKeyVaultConfiguration.AddAzureKeyVault(configuration, _ => throw unavailable, TextWriter.Null));

        Assert.Same(unavailable, failure.InnerException);
    }

    [Fact]
    public void The_real_client_gives_up_on_an_unreachable_vault_within_about_a_minute()
    {
        var options = AzureKeyVaultConfiguration.ClientOptions();

        Assert.Equal(2, options.Retry.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(20), options.Retry.NetworkTimeout);
    }

    // ---- end to end through the real provider and a fake Key Vault REST service ----

    [Fact]
    public void Only_the_prefixed_enabled_secrets_are_read_and_they_override_the_other_sources()
    {
        var vault = new FakeVault(forbidden: false)
        {
            Secrets =
            {
                ["JobWatcher--Telegram--BotToken"] = ("123456:VAULT-TOKEN", true),
                ["JobWatcher--Telegram--ChatId"] = ("-100777", true),
                ["JobWatcher--Sources--Jooble--ApiKey"] = ("VAULT-JOOBLE-KEY", true),
                ["JobWatcher--Profile--AllowedRemoteRegions--0"] = ("Poland", true),
                ["jobwatcher--PollIntervalMinutes"] = ("30", true),
                ["JobWatcher--DataDir"] = ("/disabled", false),
                ["QBeauty--Telegram--BotToken"] = ("OTHER-APP-SECRET", true),
                ["Telegram--BotToken"] = ("UNPREFIXED-SECRET", true),
            },
            // The vault answers the list in two pages; the second has one of this service's secrets and a foreign one.
            SecondPage =
            {
                ["JobWatcher--Profile--Keyword"] = ("Rust", true),
                ["Other--Profile--Keyword"] = ("OTHER-PAGE-2-SECRET", true),
            },
        };
        // What appsettings.json, the Development file and the environment would have supplied.
        var configuration = Configuration(new()
        {
            ["KeyVault:VaultUri"] = VaultUri,
            ["JobWatcher:Telegram:BotToken"] = "123456:ENVIRONMENT-TOKEN",
            ["JobWatcher:Telegram:ChatId"] = "42",
            ["JobWatcher:DataDir"] = "./data",
            ["JobWatcher:Profile:AllowedRemoteRegions:0"] = "Ukraine",
            ["JobWatcher:Profile:AllowedRemoteRegions:1"] = "Europe",
        });
        var sources = SourceCount(configuration);
        var progress = new StringWriter();

        AzureKeyVaultConfiguration.AddAzureKeyVault(configuration, vault.CreateClient, progress);

        // One line before the vault is read, without the address or any secret name.
        Assert.Equal("Читаю секрети з Azure Key Vault…\n", progress.ToString().ReplaceLineEndings("\n"));

        Assert.Equal(sources + 1, SourceCount(configuration));
        // The vault wins over the sources added before it.
        Assert.Equal("123456:VAULT-TOKEN", configuration["JobWatcher:Telegram:BotToken"]);
        Assert.Equal("-100777", configuration["JobWatcher:Telegram:ChatId"]);
        Assert.Equal("VAULT-JOOBLE-KEY", configuration["JobWatcher:Sources:Jooble:ApiKey"]);
        Assert.Equal("30", configuration["JobWatcher:PollIntervalMinutes"]);
        // From the second page of the list.
        Assert.Equal("Rust", configuration["JobWatcher:Profile:Keyword"]);
        // A list item replaces only its own index.
        Assert.Equal("Poland", configuration["JobWatcher:Profile:AllowedRemoteRegions:0"]);
        Assert.Equal("Europe", configuration["JobWatcher:Profile:AllowedRemoteRegions:1"]);
        // A disabled secret is ignored, so the earlier value stays.
        Assert.Equal("./data", configuration["JobWatcher:DataDir"]);
        // The secrets of other applications in a shared vault are neither requested nor put into the configuration.
        Assert.Null(configuration["QBeauty:Telegram:BotToken"]);
        Assert.Null(configuration["Telegram:BotToken"]);
        Assert.DoesNotContain(configuration.AsEnumerable(), pair => pair.Value is "OTHER-APP-SECRET" or "UNPREFIXED-SECRET" or "OTHER-PAGE-2-SECRET");
        Assert.Equal(
            ["JobWatcher--Profile--AllowedRemoteRegions--0", "JobWatcher--Profile--Keyword", "JobWatcher--Sources--Jooble--ApiKey", "JobWatcher--Telegram--BotToken", "JobWatcher--Telegram--ChatId", "jobwatcher--PollIntervalMinutes"],
            vault.ValuesRequested.Order(StringComparer.Ordinal));
        // The fake really was asked for both pages of the list, with a token.
        Assert.Equal(2, vault.PagesListed);
    }

    // ---- fakes ----

    /// <summary>A Key Vault REST service in memory: the authentication challenge, the list of secrets and their values.</summary>
    private sealed class FakeVault(bool forbidden) : HttpMessageHandler
    {
        private const string Token = "fake-access-token";
        private readonly ConcurrentQueue<string> _valuesRequested = new();

        private const string SecondPagePath = "/secrets-page-2";

        public Dictionary<string, (string Value, bool Enabled)> Secrets { get; } = [];

        /// <summary>Listed on a second page, which the first page points to with its nextLink.</summary>
        public Dictionary<string, (string Value, bool Enabled)> SecondPage { get; } = [];

        public IReadOnlyList<string> ValuesRequested => [.. _valuesRequested];

        public int PagesListed { get; private set; }

        public SecretClient CreateClient(Uri vaultUri) =>
            new(vaultUri, new FakeCredential(), new SecretClientOptions
            {
                Transport = new HttpClientTransport(new HttpClient(this)),
                Retry = { MaxRetries = 0 },
            });

        // The configuration provider loads synchronously, so the SDK uses the synchronous path.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Answer(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Answer(request));

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            if (request.Headers.Authorization?.Parameter != Token)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Bearer authorization=\"https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000\", resource=\"https://vault.azure.net\"");

                return challenge;
            }

            if (forbidden)
            {
                return Json(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Caller is not authorized."}}""");
            }

            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            if (segments is ["secrets"])
            {
                return List(Secrets, nextLink: $"{VaultUri.TrimEnd('/')}{SecondPagePath}?api-version=7.6");
            }

            if (request.RequestUri.AbsolutePath == SecondPagePath)
            {
                return List(SecondPage, nextLink: null);
            }

            if (segments is ["secrets", var name, ..]
                && (Secrets.TryGetValue(name, out var found) || SecondPage.TryGetValue(name, out found)))
            {
                _valuesRequested.Enqueue(name);

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    value = found.Value,
                    id = $"{VaultUri}secrets/{name}/0123456789abcdef",
                    attributes = new { enabled = found.Enabled },
                }));
            }

            return Json(HttpStatusCode.NotFound, """{"error":{"code":"SecretNotFound","message":"Not found."}}""");
        }

        private HttpResponseMessage List(Dictionary<string, (string Value, bool Enabled)> secrets, string? nextLink)
        {
            PagesListed++;
            var items = secrets.Select(secret => new
            {
                id = $"{VaultUri}secrets/{secret.Key}",
                attributes = new { enabled = secret.Value.Enabled },
            });

            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = items, nextLink }));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private sealed class FakeCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new(Token, DateTimeOffset.UtcNow.AddHours(1));

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }
}
