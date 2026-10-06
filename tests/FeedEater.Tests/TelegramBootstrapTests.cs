using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class TelegramBootstrapTests
{
    private sealed class CapturingLogger : ILogger<TelegramClient>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    /// <summary>Nothing but the Telegram client is passed in: a handler that reached for anything else would throw.</summary>
    private static (CommandHandler Handler, StubHandler Telegram) Build(long allowedUserId)
    {
        var telegram = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}"""));
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { AllowedUserId = allowedUserId } });
        var handler = new CommandHandler(new TelegramClient(telegram.Client("http://tg/botT/")), null!, null!, null!, null!, null!, null!, options);
        return (handler, telegram);
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("  /start  ")]
    [InlineData("/start@my_feed_bot")]
    [InlineData("/start some-payload")]
    public async Task Without_an_owner_id_start_answers_with_the_senders_id_and_the_exact_setting(string text)
    {
        var (handler, telegram) = Build(0);

        await handler.HandleAsync(new TgMessage(77, 77, text), default);

        var call = Assert.Single(telegram.Calls);
        Assert.EndsWith("/sendMessage", call.Uri, StringComparison.Ordinal);
        Assert.Contains("\"chat_id\":77", call.Body, StringComparison.Ordinal);
        Assert.Contains("FeedEater__Telegram__AllowedUserId=77", call.Body, StringComparison.Ordinal);
        Assert.Contains("FeedEater:Telegram:AllowedUserId", call.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/digest")]
    [InlineData("/digest resend")]
    [InlineData("/search postgres")]
    [InlineData("/quiet on")]
    [InlineData("/startx")]
    [InlineData("hello")]
    [InlineData("what is this?")]
    [InlineData("")]
    public async Task Without_an_owner_id_everything_but_start_is_ignored(string text)
    {
        var (handler, telegram) = Build(0);

        await handler.HandleAsync(new TgMessage(77, 77, text), default);
        await handler.HandleAsync(new TgMessage(77, 77, text, ReplyToItemId: 5), default);

        Assert.Empty(telegram.Calls);
    }

    [Fact]
    public async Task A_message_without_text_or_from_the_zero_id_gets_no_answer()
    {
        var (handler, telegram) = Build(0);

        await handler.HandleAsync(new TgMessage(77, 77, null), default);
        await handler.HandleAsync(new TgMessage(0, 0, "/start"), default);

        Assert.Empty(telegram.Calls);
    }

    [Fact]
    public async Task Once_an_owner_is_set_start_is_not_the_bootstrap_any_more()
    {
        var (handler, telegram) = Build(42);

        await handler.HandleAsync(new TgMessage(42, 42, "/start"), default);
        await handler.HandleAsync(new TgMessage(99, 99, "/start"), default);

        Assert.Empty(telegram.Calls);
    }

    [Fact]
    public async Task A_send_to_chat_0_is_refused_with_a_log_line_and_never_reaches_Telegram()
    {
        var log = new CapturingLogger();
        var telegram = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"message_id":1}}"""));
        var client = new TelegramClient(telegram.Client("http://tg/botT/"), log);

        var error = await Assert.ThrowsAsync<TelegramException>(() => client.SendAsync(0, new OutMessage("digest"), default));

        Assert.Contains("AllowedUserId is 0", error.Message, StringComparison.Ordinal);
        Assert.Empty(telegram.Calls);
        Assert.Contains("AllowedUserId is 0", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_me_returns_the_bot_username()
    {
        var telegram = new StubHandler((_, _) => StubHandler.Json("""{"ok":true,"result":{"id":1,"is_bot":true,"username":"my_feed_bot"}}"""));

        var name = await new TelegramClient(telegram.Client("http://tg/botT/")).GetMeAsync(default);

        Assert.Equal("my_feed_bot", name);
        Assert.EndsWith("/getMe", telegram.Calls.Single().Uri, StringComparison.Ordinal);
    }
}
