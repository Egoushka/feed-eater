using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class TelegramClientTests
{
    private static (TelegramClient Client, StubHandler Handler) Build(string response)
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(response));
        return (new TelegramClient(handler.Client("http://tg/botT/")), handler);
    }

    [Fact]
    public async Task Send_posts_html_with_inline_buttons_and_returns_the_message_id()
    {
        var (client, handler) = Build("""{"ok":true,"result":{"message_id":77}}""");

        var id = await client.SendAsync(42, new OutMessage("<b>hi</b>", [[new Button("👍", "v:1:u")]]), default);

        Assert.Equal(77, id);
        var call = handler.Calls.Single();
        Assert.Equal("http://tg/botT/sendMessage", call.Uri);
        Assert.Contains("\"parse_mode\":\"HTML\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"inline_keyboard\":[[{\"text\":", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"callback_data\":\"v:1:u\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"is_disabled\":true", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_without_buttons_has_no_markup_and_answer_without_text_has_no_text()
    {
        var (client, handler) = Build("""{"ok":true,"result":{"message_id":1}}""");

        await client.SendAsync(42, new OutMessage("plain"), default);
        var (answerClient, answerHandler) = Build("""{"ok":true,"result":true}""");
        await answerClient.AnswerAsync("cb1", null, default);

        Assert.DoesNotContain("reply_markup", handler.Calls.Single().Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", answerHandler.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_ok_becomes_TelegramException_with_the_description()
    {
        var (client, _) = Build("""{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""");

        var error = await Assert.ThrowsAsync<TelegramException>(() => client.EditButtonsAsync(42, 7, [[new Button("x", "n")]], default));

        Assert.Contains("message is not modified", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_updates_parses_callbacks_and_messages_and_skips_other_updates()
    {
        var (client, handler) = Build(
            """
            {"ok":true,"result":[
              {"update_id":10,"callback_query":{"id":"cb1","from":{"id":42},"message":{"message_id":7,"chat":{"id":42}},"data":"v:5:u"}},
              {"update_id":11,"message":{"message_id":8,"text":"hi"}},
              {"update_id":12,"message":{"message_id":9,"from":{"id":42},"chat":{"id":42},"text":"/digest"}}
            ]}
            """);

        var updates = await client.GetUpdatesAsync(10, 50, default);

        Assert.Equal(new TgCallback("cb1", 42, 42, 7, "v:5:u"), updates[0].Callback);
        Assert.Null(updates[1].Callback);
        Assert.Null(updates[1].Message);
        Assert.Equal(new TgMessage(42, 42, "/digest"), updates[2].Message);
        Assert.Contains("\"allowed_updates\":[\"callback_query\",\"message\"]", handler.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exceptions_never_carry_the_token_from_the_base_address()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""{"ok":false,"error_code":401,"description":"Unauthorized"}""", System.Net.HttpStatusCode.Unauthorized));
        var client = new TelegramClient(handler.Client("http://tg/bot123456:SECRET-TOKEN/"));

        var error = await Assert.ThrowsAsync<TelegramException>(() => client.SendAsync(42, new OutMessage("x"), default));

        Assert.DoesNotContain("SECRET-TOKEN", error.ToString(), StringComparison.Ordinal);
    }
}
