using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobWatcher.Configuration;
using Microsoft.Extensions.Options;

namespace JobWatcher.Telegram;

/// <summary>
/// Sends messages through the Telegram Bot API (<c>sendMessage</c>). A 429 answer is retried after the
/// <c>retry_after</c> pause Telegram asks for; any other failure becomes a <see cref="TelegramDeliveryException"/>.
/// </summary>
/// <remarks>
/// The bot token is part of the request URL, so the URL is never logged and never copied into an exception:
/// failure messages are built only from Telegram's own answer (status code, description) and the exception type.
/// </remarks>
public sealed class TelegramNotifier(
    HttpClient http,
    IOptions<WatcherOptions> options,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IMessageNotifier
{
    private const int MaxRetries = 3;
    private const int FallbackRetryAfterSeconds = 1;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, ct) => Task.Delay(span, ct));

    public async Task SendAsync(string html, CancellationToken ct)
    {
        var telegram = options.Value.Telegram;
        var url = $"https://api.telegram.org/bot{telegram.BotToken}/sendMessage";
        var request = new SendMessageRequest(telegram.ChatId, html, "HTML", new LinkPreviewOptions(IsDisabled: true));

        for (var retries = 0; ; retries++)
        {
            var (status, answer) = await PostAsync(url, request, ct);

            if (status == HttpStatusCode.OK && answer is { Ok: true })
                return;

            if (status == HttpStatusCode.TooManyRequests && retries < MaxRetries)
            {
                // Clamped: Task.Delay rejects a negative pause, and a negative retry_after would make it throw.
                var pause = TimeSpan.FromSeconds(Math.Max(0, answer?.Parameters?.RetryAfter ?? FallbackRetryAfterSeconds));
                await _delay(pause, ct);
                continue;
            }

            var gaveUp = status == HttpStatusCode.TooManyRequests ? $" (gave up after {MaxRetries} retries)" : "";
            throw new TelegramDeliveryException(
                $"Telegram sendMessage failed with HTTP {(int)status}: {answer?.Description ?? "no description"}{gaveUp}");
        }
    }

    private async Task<(HttpStatusCode Status, TelegramAnswer? Answer)> PostAsync(
        string url, SendMessageRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(url, request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return (response.StatusCode, ParseAnswer(body));
        }
        catch (HttpRequestException e)
        {
            // The exception text may quote the URL, so only its type and error kind are reported.
            throw new TelegramDeliveryException($"Telegram request failed ({e.HttpRequestError})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not cancelled by the caller: the HttpClient gave up waiting (its Timeout elapsed).
            throw new TelegramDeliveryException("Telegram request timed out");
        }
    }

    /// <summary>The answer body, or null when it is not a Telegram JSON answer (e.g. an HTML error page of a proxy).</summary>
    private static TelegramAnswer? ParseAnswer(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<TelegramAnswer>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("parse_mode")] string ParseMode,
        [property: JsonPropertyName("link_preview_options")] LinkPreviewOptions LinkPreviewOptions);

    private sealed record LinkPreviewOptions(
        [property: JsonPropertyName("is_disabled")] bool IsDisabled);

    private sealed record TelegramAnswer(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("parameters")] AnswerParameters? Parameters);

    private sealed record AnswerParameters(
        [property: JsonPropertyName("retry_after")] int? RetryAfter);
}
