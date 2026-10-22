using Azure.Security.KeyVault.Secrets;
using JobWatcher.Configuration;

namespace JobWatcher.Tests.Configuration;

public sealed class PrefixKeyVaultSecretManagerTests
{
    private static readonly PrefixKeyVaultSecretManager Manager = new();

    private static SecretProperties Properties(string name, bool? enabled = true) => new(name) { Enabled = enabled };

    [Theory]
    [InlineData("JobWatcher--Telegram--BotToken")]
    [InlineData("jobwatcher--Telegram--ChatId")]
    [InlineData("JOBWATCHER--Sources--Jooble--ApiKey")]
    public void Enabled_secrets_with_the_prefix_are_loaded_whatever_the_case(string name)
    {
        Assert.True(Manager.Load(Properties(name)));
    }

    [Theory]
    [InlineData("Telegram--BotToken")]
    [InlineData("QBeauty--Telegram--BotToken")]
    [InlineData("JobWatcher-Telegram--BotToken")]
    [InlineData("JobWatcherX--Telegram--BotToken")]
    [InlineData("XJobWatcher--Telegram--BotToken")]
    [InlineData("JobWatcher")]
    public void Secrets_of_other_applications_are_not_loaded(string name)
    {
        Assert.False(Manager.Load(Properties(name)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Disabled_secrets_are_not_loaded(bool? enabled)
    {
        Assert.False(Manager.Load(Properties("JobWatcher--Telegram--BotToken", enabled)));
    }

    [Theory]
    [InlineData("JobWatcher--Telegram--BotToken", "JobWatcher:Telegram:BotToken")]
    [InlineData("JobWatcher--Telegram--ChatId", "JobWatcher:Telegram:ChatId")]
    [InlineData("JobWatcher--Sources--Jooble--ApiKey", "JobWatcher:Sources:Jooble:ApiKey")]
    [InlineData("JobWatcher--Profile--AllowedRemoteRegions--0", "JobWatcher:Profile:AllowedRemoteRegions:0")]
    [InlineData("jobwatcher--PollIntervalMinutes", "jobwatcher:PollIntervalMinutes")]
    public void The_secret_name_maps_to_the_configuration_key(string name, string key)
    {
        Assert.Equal(key, Manager.GetKey(new KeyVaultSecret(name, "value")));
    }

    [Theory]
    [InlineData("Telegram--BotToken")]
    [InlineData("QBeauty--Telegram--BotToken")]
    public void A_secret_of_another_application_never_maps_to_a_key(string name)
    {
        Assert.Throws<ArgumentException>(() => Manager.GetKey(new KeyVaultSecret(name, "value")));
    }
}
