using System.Text.RegularExpressions;
using Azure;
using JobWatcher.Configuration;

namespace JobWatcher;

/// <summary>The parts of the start-up in Program.cs that tests can reach.</summary>
internal static partial class HostSetup
{
    /// <summary>
    /// The host's settings: appsettings.json is read from the application's own folder, so the service finds it
    /// whatever the working directory. Relative paths in the settings (such as <c>DataDir</c>) still resolve against the
    /// working directory.
    /// </summary>
    public static HostApplicationBuilderSettings Settings(string[] args) =>
        new() { Args = args, ContentRootPath = AppContext.BaseDirectory };

    /// <summary>
    /// The settings of this machine, secrets included, next to appsettings.json. Git leaves it out (.gitignore) and so does
    /// the Docker image (.dockerignore). The customer's qbeauty-ua reads a file of the same name in the same way.
    /// </summary>
    public const string LocalSettingsFile = "appsettings.Local.json";

    /// <summary>
    /// Adds <see cref="LocalSettingsFile"/>, when there is one, as the last source, so it overrides every source added
    /// before it. It is read once: a change takes effect after a restart, as with the vault.
    /// </summary>
    public static IHostApplicationBuilder AddLocalSettings(this IHostApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile(LocalSettingsFile, optional: true, reloadOnChange: false);

        return builder;
    }

    /// <summary>
    /// Adds the vault (by <paramref name="addVault"/>) and the local settings so that the local file wins over the vault,
    /// and still sets the vault's address: the vault reads <c>KeyVault:VaultUri</c> from the settings built before it, so
    /// the file is read once before it, for that address, and once after it, for its priority.
    /// </summary>
    public static IHostApplicationBuilder AddVaultAndLocalSettings(
        this IHostApplicationBuilder builder, Action<IHostApplicationBuilder> addVault)
    {
        builder.AddLocalSettings();
        addVault(builder);
        builder.AddLocalSettings();

        return builder;
    }

    /// <summary>
    /// The text shown to the user when a setting has a value that cannot be converted to the setting's type (for
    /// example <c>JobWatcher__PollIntervalMinutes=abc</c>); null when <paramref name="exception"/> is not such a
    /// failure. The text names the environment variable but never contains the value, which might be a secret put
    /// into the wrong variable.
    /// </summary>
    public static string? BindingFailureMessage(InvalidOperationException exception)
    {
        if (BindingFailure().Match(exception.Message) is not { Success: true } match)
        {
            return null;
        }

        var variable = match.Groups["path"].Value.Replace(":", "__", StringComparison.Ordinal);

        return $"Значення параметра {variable} має неправильний формат (наприклад, текст замість числа чи true/false). "
            + $"Виправте змінну середовища {variable}.";
    }

    private const int MaxKeyVaultReasonLength = 200;

    /// <summary>
    /// Tells the user plainly why the secrets could not be read from Azure Key Vault and returns the exit code, 1.
    /// Only the cause's type (with the HTTP status of a refusal) and the first line of its message are shown, without
    /// a stack trace: the further lines of the Azure SDK's messages hold diagnostic details, and no secret value or
    /// credential is ever printed.
    /// </summary>
    public static async Task<int> ReportKeyVaultFailureAsync(KeyVaultLoadException exception, TextWriter error)
    {
        var cause = exception.InnerException ?? exception;
        var reason = cause.Message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        if (reason.Length > MaxKeyVaultReasonLength)
        {
            reason = reason[..MaxKeyVaultReasonLength] + "…";
        }

        await error.WriteLineAsync("Помилка Azure Key Vault:");
        // A refusal of the vault carries its HTTP status (401, 403, 404...), which tells what to fix.
        var status = cause is RequestFailedException { Status: > 0 } refusal ? $" ({refusal.Status})" : "";
        await error.WriteLineAsync($"- {cause.GetType().Name}{status}: {reason}");
        await error.WriteLineAsync(
            "Перевірте VaultUri, доступ (роль «Key Vault Secrets User» або політика Get+List) і вхід (`az login` для локального запуску; "
            + "managed identity або AZURE_CLIENT_ID/AZURE_TENANT_ID/AZURE_CLIENT_SECRET для контейнера)");

        return 1;
    }

    // The configuration binder's message: "Failed to convert configuration value at 'JobWatcher:PollIntervalMinutes'
    // to type 'System.Int32'." (older versions also quote the value before "at"). Only the path is taken.
    [GeneratedRegex(@"^Failed to convert configuration value\b.*\bat '(?<path>[^']+)' to type '[^']*'\.?$", RegexOptions.Singleline | RegexOptions.RightToLeft)]
    private static partial Regex BindingFailure();
}
