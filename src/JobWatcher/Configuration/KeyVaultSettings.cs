namespace JobWatcher.Configuration;

/// <summary>
/// The top-level <c>KeyVault</c> section (environment variable <c>KeyVault__VaultUri</c>), as in the customer's other
/// services. An empty <see cref="VaultUri"/> switches Azure Key Vault off.
/// </summary>
internal sealed class KeyVaultSettings
{
    public const string SectionName = "KeyVault";
    public string VaultUri { get; init; } = string.Empty;
}
