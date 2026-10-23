using Azure;
using Azure.Identity;
using JobWatcher.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JobWatcher.Tests;

public sealed class HostSetupTests
{
    [Fact]
    public void Content_root_is_the_folder_of_the_application_not_the_working_directory()
    {
        string[] args = ["--some", "arg"];

        var settings = HostSetup.Settings(args);

        Assert.Equal(AppContext.BaseDirectory, settings.ContentRootPath);
        Assert.Same(args, settings.Args);
    }

    [Fact]
    public void The_built_host_uses_the_application_folder_as_content_root()
    {
        var builder = Host.CreateApplicationBuilder(HostSetup.Settings([]));

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath));
    }

    // ---- appsettings.Local.json: the settings of this machine, secrets included (git and the image leave it out) ----

    [Fact]
    public void The_local_settings_file_overrides_the_sources_before_it_and_leaves_the_rest()
    {
        TempContentRoot.WithLocalFile("""{"JobWatcher":{"Telegram":{"ChatId":"-100777"},"Sources":{"Jooble":{"Enabled":false}}}}""", builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JobWatcher:Telegram:ChatId"] = "42",
                ["JobWatcher:Sources:Jooble:Enabled"] = "true",
                ["JobWatcher:DataDir"] = "./data",
            });

            builder.AddLocalSettings();

            Assert.Equal("-100777", builder.Configuration["JobWatcher:Telegram:ChatId"]);
            Assert.Equal("False", builder.Configuration["JobWatcher:Sources:Jooble:Enabled"]);
            Assert.Equal("./data", builder.Configuration["JobWatcher:DataDir"]);
        });
    }

    [Fact]
    public void A_missing_local_settings_file_changes_nothing()
    {
        TempContentRoot.WithLocalFile(null, builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["JobWatcher:Telegram:ChatId"] = "42" });

            builder.AddLocalSettings();

            Assert.Equal("42", builder.Configuration["JobWatcher:Telegram:ChatId"]);
        });
    }

    [Theory]
    [InlineData("JobWatcher:PollIntervalMinutes", "JobWatcher__PollIntervalMinutes")]
    [InlineData("JobWatcher:Sources:Dou:Enabled", "JobWatcher__Sources__Dou__Enabled")]
    [InlineData("JobWatcher:Profile:MinSalary:Period", "JobWatcher__Profile__MinSalary__Period")]
    public void A_value_of_the_wrong_type_gives_a_message_that_names_the_setting_but_not_the_value(string key, string variable)
    {
        const string value = "SECRET-LOOKING-abc";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddJobWatcher(config));
        var message = HostSetup.BindingFailureMessage(failure);

        Assert.NotNull(message);
        Assert.Contains(variable, message);
        Assert.DoesNotContain(value, message);
        Assert.DoesNotContain("SECRET", message);
    }

    [Fact]
    public void Other_invalid_operations_are_not_reported_as_configuration_errors()
    {
        Assert.Null(HostSetup.BindingFailureMessage(new InvalidOperationException("Sequence contains no elements")));
    }

    private const string KeyVaultHint =
        "Перевірте VaultUri, доступ (роль «Key Vault Secrets User» або політика Get+List) і вхід (`az login` для локального запуску; "
        + "managed identity або AZURE_CLIENT_ID/AZURE_TENANT_ID/AZURE_CLIENT_SECRET для контейнера)";

    [Fact]
    public async Task A_failing_key_vault_is_reported_by_type_and_first_line_with_a_hint_and_exit_code_1()
    {
        const string secret = "TOKEN-SECRET-VALUE";
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:VaultUri"] = "https://jobwatcher-test.vault.azure.net/",
            ["JobWatcher:Telegram:BotToken"] = secret,
        });
        // A fake failing loader: a credential that cannot sign in, whose message goes on with details on later lines.
        var failure = Assert.Throws<KeyVaultLoadException>(() => AzureKeyVaultConfiguration.AddAzureKeyVault(
            configuration,
            _ => throw new CredentialUnavailableException($"DefaultAzureCredential failed to retrieve a token.\n- details {secret}"),
            TextWriter.Null));
        var error = new StringWriter();

        var exitCode = await HostSetup.ReportKeyVaultFailureAsync(failure, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "Помилка Azure Key Vault:\n"
            + "- CredentialUnavailableException: DefaultAzureCredential failed to retrieve a token.\n"
            + KeyVaultHint + "\n",
            error.ToString().ReplaceLineEndings("\n"));
        Assert.DoesNotContain(secret, error.ToString());
        Assert.DoesNotContain("   at ", error.ToString());
    }

    [Fact]
    public async Task An_invalid_vault_uri_is_reported_with_the_setting_to_fix()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["KeyVault:VaultUri"] = "http://vault" });
        var failure = Assert.Throws<KeyVaultLoadException>(
            () => AzureKeyVaultConfiguration.AddAzureKeyVault(configuration, _ => throw new InvalidOperationException("not reached"), TextWriter.Null));
        var error = new StringWriter();

        var exitCode = await HostSetup.ReportKeyVaultFailureAsync(failure, error);

        Assert.Equal(1, exitCode);
        var lines = error.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Помилка Azure Key Vault:", lines[0]);
        Assert.StartsWith("- UriFormatException: ", lines[1]);
        Assert.Contains("KeyVault__VaultUri", lines[1]);
        Assert.Equal(KeyVaultHint, lines[2]);
    }

    [Theory]
    [InlineData(401, "Unauthorized: the token was not accepted.")]
    [InlineData(403, "Caller is not authorized to perform action on resource.")]
    [InlineData(404, "A vault with this address was not found.")]
    public async Task A_refusal_of_the_vault_is_reported_with_its_http_status(int status, string message)
    {
        var failure = new KeyVaultLoadException(new RequestFailedException(status, $"{message}\nStatus: {status}\nHeaders: ..."));
        var error = new StringWriter();

        var exitCode = await HostSetup.ReportKeyVaultFailureAsync(failure, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"- RequestFailedException ({status}): {message}",
            error.ToString().ReplaceLineEndings("\n").Split('\n')[1]);
    }

    [Fact]
    public async Task A_request_failure_without_a_status_and_other_exceptions_show_no_status()
    {
        foreach (var (cause, expected) in new (Exception, string)[]
        {
            (new RequestFailedException("No such host is known."), "- RequestFailedException: No such host is known."),
            (new TimeoutException("The vault did not answer."), "- TimeoutException: The vault did not answer."),
        })
        {
            var error = new StringWriter();

            await HostSetup.ReportKeyVaultFailureAsync(new KeyVaultLoadException(cause), error);

            Assert.Equal(expected, error.ToString().ReplaceLineEndings("\n").Split('\n')[1]);
        }
    }

    [Fact]
    public async Task A_very_long_first_line_is_shortened()
    {
        var failure = new KeyVaultLoadException(new TimeoutException(new string('x', 1000)));
        var error = new StringWriter();

        await HostSetup.ReportKeyVaultFailureAsync(failure, error);

        var line = error.ToString().ReplaceLineEndings("\n").Split('\n')[1];
        Assert.StartsWith("- TimeoutException: xxx", line);
        Assert.True(line.Length <= 250, $"The line is {line.Length} characters long.");
    }
}
