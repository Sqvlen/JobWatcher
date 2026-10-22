namespace JobWatcher.Telegram;

/// <summary>Delivers one ready-made message to the user.</summary>
public interface IMessageNotifier
{
    /// <summary>Sends <paramref name="html"/> (Telegram HTML markup). Throws <see cref="TelegramDeliveryException"/> when it cannot be delivered.</summary>
    Task SendAsync(string html, CancellationToken ct);
}
