namespace JobWatcher.Configuration;

/// <summary>
/// Azure Key Vault was configured but its secrets could not be read; the cause is the inner exception. The service
/// must not start without the secrets the vault was meant to provide.
/// </summary>
internal sealed class KeyVaultLoadException(Exception cause)
    : Exception("The secrets could not be read from Azure Key Vault.", cause);
