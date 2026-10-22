using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace JobWatcher.Configuration;

/// <summary>
/// Reads only this service's secrets from a vault that other applications may share: those whose name starts with
/// <c>JobWatcher--</c> (in any case). A name maps to a configuration key by turning <c>--</c> into <c>:</c>, so
/// <c>JobWatcher--Telegram--BotToken</c> becomes <c>JobWatcher:Telegram:BotToken</c> and
/// <c>JobWatcher--Profile--AllowedRemoteRegions--0</c> the first item of that list.
/// </summary>
/// <remarks>
/// The vault still lists the names of all its secrets (that needs the List permission), but the value of a secret
/// without the prefix is never requested.
/// </remarks>
internal sealed class PrefixKeyVaultSecretManager : KeyVaultSecretManager
{
    public const string Prefix = "JobWatcher--";

    public override bool Load(SecretProperties secret) =>
        secret.Enabled == true && HasPrefix(secret.Name) && base.Load(secret);

    public override string GetKey(KeyVaultSecret secret)
    {
        if (!HasPrefix(secret.Name))
        {
            throw new ArgumentException($"The secret {secret.Name} does not start with {Prefix}, so it is not this service's.", nameof(secret));
        }

        return base.GetKey(secret);
    }

    private static bool HasPrefix(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
