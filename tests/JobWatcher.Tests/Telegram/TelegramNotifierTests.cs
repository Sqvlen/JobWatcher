using System.Net;
using System.Text;
using System.Text.Json;
using JobWatcher.Configuration;
using JobWatcher.Telegram;
using Microsoft.Extensions.Options;

namespace JobWatcher.Tests.Telegram;

public sealed class TelegramNotifierTests
{
    private const string Token = "123456:SECRET-bot-token";
    private const string ChatId = "42";

    private static readonly string TokenUrl = $"https://api.telegram.org/bot{Token}/sendMessage";

    private delegate Task<HttpResponseMessage> Step(CancellationToken ct);

    private static Step Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
        _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    private static Step Ok() => Respond(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":1}}""");

    private static Step TooManyRequests(int retryAfter) => Respond(
        HttpStatusCode.TooManyRequests,
        $$$"""{"ok":false,"error_code":429,"description":"Too Many Requests: retry after {{{retryAfter}}}","parameters":{"retry_after":{{{retryAfter}}}}}""");

    private static Step BadRequest(string description) => Respond(
        HttpStatusCode.BadRequest,
        $$"""{"ok":false,"error_code":400,"description":"{{description}}"}""");

    private static Step Fail(Exception exception) => _ => throw exception;

    private sealed class FakeHandler(IEnumerable<Step> steps) : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new(steps);

        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct)));

            if (_steps.Count == 0)
                throw new InvalidOperationException("The notifier sent more requests than the test scripted.");

            return await _steps.Dequeue()(ct);
        }
    }

    private sealed class Fixture
    {
        public Fixture(IEnumerable<Step> steps, Action<HttpClient>? configureClient = null)
        {
            Handler = new FakeHandler(steps);
            var http = new HttpClient(Handler);
            configureClient?.Invoke(http);

            Notifier = new TelegramNotifier(
                http,
                Options.Create(new WatcherOptions { Telegram = { BotToken = Token, ChatId = ChatId } }),
                (span, _) =>
                {
                    Delays.Add(span);
                    return Task.CompletedTask;
                });
        }

        public FakeHandler Handler { get; }

        public List<TimeSpan> Delays { get; } = [];

        public TelegramNotifier Notifier { get; }
    }

    private static Fixture Script(params Step[] steps) => new(steps);

    [Fact]
    public async Task Posts_to_sendMessage_with_chat_id_text_and_html_parse_mode()
    {
        var fixture = Script(Ok());

        await fixture.Notifier.SendAsync("<b>Senior .NET</b> &amp; more", CancellationToken.None);

        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TokenUrl, request.Url);
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal(ChatId, root.GetProperty("chat_id").GetString());
        Assert.Equal("<b>Senior .NET</b> &amp; more", root.GetProperty("text").GetString());
        Assert.Equal("HTML", root.GetProperty("parse_mode").GetString());
        Assert.True(root.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task Retries_after_429_using_retry_after_then_succeeds()
    {
        var fixture = Script(TooManyRequests(7), Ok());

        await fixture.Notifier.SendAsync("hi", CancellationToken.None);

        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], fixture.Delays);
    }

    [Fact]
    public async Task Negative_retry_after_is_treated_as_no_pause()
    {
        // Task.Delay throws ArgumentOutOfRangeException for a negative pause; the notifier must retry at once instead.
        var fixture = Script(TooManyRequests(-5), Ok());

        await fixture.Notifier.SendAsync("hi", CancellationToken.None);

        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Equal([TimeSpan.Zero], fixture.Delays);
    }

    [Fact]
    public async Task Throws_after_3_failed_429_retries()
    {
        var fixture = Script(TooManyRequests(1), TooManyRequests(2), TooManyRequests(3), TooManyRequests(4));

        var exception = await Assert.ThrowsAsync<TelegramDeliveryException>(
            () => fixture.Notifier.SendAsync("hi", CancellationToken.None));

        Assert.Equal(4, fixture.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)], fixture.Delays);
        Assert.Contains("429", exception.Message);
    }

    [Fact]
    public async Task Throws_TelegramDeliveryException_on_400_with_telegram_description()
    {
        var fixture = Script(BadRequest("Bad Request: chat not found"));

        var exception = await Assert.ThrowsAsync<TelegramDeliveryException>(
            () => fixture.Notifier.SendAsync("hi", CancellationToken.None));

        Assert.Contains("Bad Request: chat not found", exception.Message);
        Assert.Contains("400", exception.Message);
        Assert.Single(fixture.Handler.Requests);
        Assert.Empty(fixture.Delays);
    }

    [Fact]
    public async Task Throws_TelegramDeliveryException_when_telegram_answers_ok_false_with_http_200()
    {
        var fixture = Script(Respond(HttpStatusCode.OK, """{"ok":false,"description":"Something is off"}"""));

        var exception = await Assert.ThrowsAsync<TelegramDeliveryException>(
            () => fixture.Notifier.SendAsync("hi", CancellationToken.None));

        Assert.Contains("Something is off", exception.Message);
    }

    [Fact]
    public async Task Throws_TelegramDeliveryException_on_non_json_error_body()
    {
        var fixture = Script(Respond(HttpStatusCode.BadGateway, "<html>Bad Gateway</html>", "text/html"));

        var exception = await Assert.ThrowsAsync<TelegramDeliveryException>(
            () => fixture.Notifier.SendAsync("hi", CancellationToken.None));

        Assert.Contains("502", exception.Message);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Throws_TelegramDeliveryException_on_network_error()
    {
        var fixture = Script(Fail(new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable)));

        await Assert.ThrowsAsync<TelegramDeliveryException>(
            () => fixture.Notifier.SendAsync("hi", CancellationToken.None));

        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Throws_TelegramDeliveryException_when_http_client_times_out()
    {
        // The step waits for the token HttpClient links to its own Timeout, so this is a genuine HttpClient timeout.
        var fixture = new Fixture(
            [async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage();
            }],
            http => http.Timeout = TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TelegramDeliveryException>(
            () => fixture.Notifier.SendAsync("hi", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_requested_by_the_caller_is_not_wrapped()
    {
        using var cts = new CancellationTokenSource();
        var fixture = Script(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Notifier.SendAsync("hi", cts.Token));
    }

    [Theory]
    [InlineData("rate-limited")]
    [InlineData("rejected")]
    [InlineData("non-json")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Exception_text_never_contains_bot_token(string failure)
    {
        // Network and timeout exceptions deliberately carry the full URL in their text (the worst case).
        Step[] steps = failure switch
        {
            "rate-limited" => [TooManyRequests(1), TooManyRequests(1), TooManyRequests(1), TooManyRequests(1)],
            "rejected" => [BadRequest("Bad Request: chat not found")],
            "non-json" => [Respond(HttpStatusCode.BadGateway, $"<html>upstream {TokenUrl} failed</html>", "text/html")],
            "network" => [Fail(new HttpRequestException($"Name resolution failed for {TokenUrl}"))],
            "timeout" => [Fail(new TaskCanceledException($"Timed out calling {TokenUrl}", new TimeoutException(TokenUrl)))],
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var fixture = Script(steps);

        var exception = await Assert.ThrowsAsync<TelegramDeliveryException>(
            () => fixture.Notifier.SendAsync("hi", CancellationToken.None));

        // ToString() also covers any inner exception and the stack trace.
        var text = exception.ToString();
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("SECRET-bot-token", text);
        Assert.DoesNotContain("api.telegram.org", text);
    }
}
