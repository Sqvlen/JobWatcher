namespace JobWatcher.Telegram;

/// <summary>A message could not be delivered to Telegram. The text never contains the bot token.</summary>
public sealed class TelegramDeliveryException(string message) : Exception(message);
