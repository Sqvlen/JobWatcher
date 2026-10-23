using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace JobWatcher.Configuration;

/// <summary>
/// Adds this service's secrets from Azure Key Vault to the configuration, the way the customer's qbeauty-ua does,
/// but only the secrets named <c>JobWatcher--…</c> (see <see cref="PrefixKeyVaultSecretManager"/>).
/// </summary>
internal static class AzureKeyVaultConfiguration
{
    private const string IsLocalEnvironmentVariable = "IS_LOCAL";

    /// <summary>
    /// Reads <c>KeyVault:VaultUri</c> from the configuration built so far; when it is set, adds the vault as the last
    /// source, so its values override appsettings.json, the Development file, the environment and the command line
    /// (only the local settings file, appsettings.Local.json, goes on top of it: see
    /// <see cref="HostSetup.AddVaultAndLocalSettings"/>). The secrets are read once, here; a rotated secret takes
    /// effect after a restart.
    /// </summary>
    /// <exception cref="KeyVaultLoadException">The URI is not an absolute https URL, or the vault could not be read.</exception>
    public static IHostApplicationBuilder AddAzureKeyVault(this IHostApplicationBuilder builder)
    {
        var excludeManagedIdentity = ShouldExcludeManagedIdentity(
            builder.Environment.IsDevelopment(), Environment.GetEnvironmentVariable(IsLocalEnvironmentVariable));

        AddAzureKeyVault(
            builder.Configuration,
            vaultUri => new SecretClient(
                vaultUri,
                new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = excludeManagedIdentity }),
                ClientOptions()),
            Console.Out);

        return builder;
    }

    /// <summary>
    /// The client's options: an unreachable vault is given up on after three attempts of at most 20 seconds each
    /// (plus the short pauses between them), so the start does not hang silently for longer than about a minute.
    /// </summary>
    internal static SecretClientOptions ClientOptions() =>
        new() { Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(20) } };

    /// <summary>
    /// Run locally (Development, or <c>IS_LOCAL=1</c>) the managed identity is skipped: there is none, and probing for
    /// it only slows the start down before the developer's own sign-in (<c>az login</c>) is tried.
    /// </summary>
    public static bool ShouldExcludeManagedIdentity(bool isDevelopment, string? isLocalValue) =>
        isDevelopment || isLocalValue == "1";

    /// <summary>
    /// The same with the client made by <paramref name="createClient"/>, so tests can answer for the vault. When the
    /// vault is used, one line on <paramref name="progress"/> says so before it is read (no address, no secret names).
    /// </summary>
    internal static void AddAzureKeyVault(IConfigurationManager configuration, Func<Uri, SecretClient> createClient, TextWriter progress)
    {
        if (ReadVaultUri(configuration) is not { } vaultUri)
        {
            return;
        }

        // TODO: english
        progress.WriteLine("Читаю секрети з Azure Key Vault…");

        try
        {
            // A configuration manager loads a source as soon as it is added, so a failure surfaces here.
            configuration.AddAzureKeyVault(
                createClient(vaultUri),
                new AzureKeyVaultConfigurationOptions { Manager = new PrefixKeyVaultSecretManager() });
        }
        catch (Exception ex)
        {
            throw new KeyVaultLoadException(ex);
        }
    }

    private static Uri? ReadVaultUri(IConfiguration configuration)
    {
        var value = configuration.GetSection(KeyVaultSettings.SectionName).Get<KeyVaultSettings>()?.VaultUri;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // The value itself is not shown: a secret might have been put into the wrong variable.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new KeyVaultLoadException(new UriFormatException(
                "KeyVault__VaultUri is not an absolute https URL. Set it to the vault's address, for example "
                + "https://<name>.vault.azure.net/, or to an empty value to switch Azure Key Vault off."));
        }

        return uri;
    }
}
