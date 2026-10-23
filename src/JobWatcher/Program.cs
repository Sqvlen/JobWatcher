using JobWatcher;
using JobWatcher.Configuration;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(HostSetup.Settings(args));

try
{
    // Azure Key Vault (when KeyVault__VaultUri is set) goes on top of the settings read so far, and before the services
    // are registered, because registering them binds the options. The local settings file (appsettings.Local.json) goes
    // on top of the vault, so it has the last word; it is also read before the vault, which takes its address from it.
    builder.AddVaultAndLocalSettings(vault => vault.AddAzureKeyVault());

    // Inside the try: registering the services already binds the options, which fails on a value of the wrong type.
    builder.Services.AddJobWatcher(builder.Configuration);
    using var host = builder.Build();

    // Validated here and not left to the host's start: the host would first log the failure with a stack trace.
    host.Services.GetRequiredService<IStartupValidator>().Validate();
    await host.RunAsync();
}
catch (OptionsValidationException ex)
{
    // A bad configuration is the user's to fix, so the reasons are printed plainly, without a stack trace.
    // The validator's messages name the setting and never include the bot token or an API key.
    await Console.Error.WriteLineAsync("Помилка конфігурації:");
    foreach (var failure in ex.Failures)
    {
        await Console.Error.WriteLineAsync($"- {failure}");
    }

    return 1;
}
catch (InvalidOperationException ex) when (HostSetup.BindingFailureMessage(ex) is { } failure)
{
    // A value that is not of the setting's type: named plainly, without the value or a stack trace.
    await Console.Error.WriteLineAsync("Помилка конфігурації:");
    await Console.Error.WriteLineAsync($"- {failure}");

    return 1;
}
catch (KeyVaultLoadException ex)
{
    // Without the secrets the vault was meant to provide the service must not start.
    return await HostSetup.ReportKeyVaultFailureAsync(ex, Console.Error);
}

return 0;
