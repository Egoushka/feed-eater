using System.Net.Http.Json;
using System.Text.Json;

namespace FeedEater.Telegram;

public sealed record TgCallback(string Id, long FromId, long ChatId, long MessageId, string? Data);

/// <summary>
/// A text message; <paramref name="ReplyToItemId"/> is the item whose message (digest item or search result) it replies to,
/// <paramref name="ReplyToFollowId"/> the follow whose root message it replies to.
/// </summary>
public sealed record TgMessage(long FromId, long ChatId, string? Text, long? ReplyToItemId = null, long? ReplyToFollowId = null);

public sealed record TgUpdate(long UpdateId, TgCallback? Callback, TgMessage? Message = null);

public sealed class TelegramException(string message) : Exception(message);

/// <summary>The Bot API calls feed-eater needs. The token is in the base address, so URIs are never logged.</summary>
public sealed class TelegramClient(HttpClient http, ILogger<TelegramClient>? logger = null)
{
    /// <summary>Chat 0 is what an unset <c>AllowedUserId</c> gives; Telegram would answer "chat not found" after the work was paid for, so nothing is sent.</summary>
    public async Task<long> SendAsync(long chatId, OutMessage message, CancellationToken ct, long? replyToMessageId = null)
    {
        if (chatId == 0)
        {
            logger?.LogWarning("Send refused: FeedEater:Telegram:AllowedUserId is 0. Send /start to the bot to get your id, set it and restart");
            throw new TelegramException("FeedEater:Telegram:AllowedUserId is 0");
        }

        var result = await CallAsync("sendMessage", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = message.Html,
            ["parse_mode"] = "HTML",
            ["link_preview_options"] = new { is_disabled = true },
            ["reply_markup"] = message.Keyboard is null ? null : Markup(message.Keyboard),
            ["reply_parameters"] = replyToMessageId is null ? null : new { message_id = replyToMessageId, allow_sending_without_reply = true },
        }, ct);
        return result.GetProperty("message_id").GetInt64();
    }

    /// <summary>The bot's username, which proves the token works.</summary>
    public async Task<string?> GetMeAsync(CancellationToken ct) => Json.Str(await CallAsync("getMe", new Dictionary<string, object?>(), ct), "username");

    public Task EditButtonsAsync(long chatId, long messageId, IReadOnlyList<IReadOnlyList<Button>> keyboard, CancellationToken ct) =>
        CallAsync("editMessageReplyMarkup", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["reply_markup"] = Markup(keyboard),
        }, ct);

    /// <summary>Replaces the text and, with no <c>reply_markup</c>, the buttons.</summary>
    public Task EditTextAsync(long chatId, long messageId, string html, CancellationToken ct) =>
        CallAsync("editMessageText", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["link_preview_options"] = new { is_disabled = true },
        }, ct);

    public Task AnswerAsync(string callbackId, string? text, CancellationToken ct) =>
        CallAsync("answerCallbackQuery", new Dictionary<string, object?>
        {
            ["callback_query_id"] = callbackId,
            ["text"] = text,
        }, ct);

    public async Task<IReadOnlyList<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var result = await CallAsync("getUpdates", new Dictionary<string, object?>
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new[] { "callback_query", "message" },
        }, ct);
        return result.EnumerateArray()
            .Select(u => new TgUpdate(
                u.GetProperty("update_id").GetInt64(),
                u.TryGetProperty("callback_query", out var c) ? Callback(c) : null,
                u.TryGetProperty("message", out var m) ? Message(m) : null))
            .ToList();
    }

    private static TgCallback Callback(JsonElement c)
    {
        var hasMessage = c.TryGetProperty("message", out var m);
        return new TgCallback(
            c.GetProperty("id").GetString()!,
            c.GetProperty("from").GetProperty("id").GetInt64(),
            hasMessage ? m.GetProperty("chat").GetProperty("id").GetInt64() : 0,
            hasMessage ? m.GetProperty("message_id").GetInt64() : 0,
            c.TryGetProperty("data", out var d) ? d.GetString() : null);
    }

    private static TgMessage? Message(JsonElement m) =>
        m.TryGetProperty("from", out var from) && m.TryGetProperty("chat", out var chat)
            ? new TgMessage(from.GetProperty("id").GetInt64(), chat.GetProperty("id").GetInt64(), Json.Str(m, "text"),
                m.TryGetProperty("reply_to_message", out var replied) ? ItemOf(replied) : null,
                replied.ValueKind == JsonValueKind.Object ? FollowOf(replied) : null)
            : null;

    /// <summary>Item messages carry the item id in their buttons' callback data, so a reply needs no stored message map.</summary>
    private static long? ItemOf(JsonElement message) => CallbackDataOf(message).Select(CallbackData.ItemIdOf).FirstOrDefault(id => id is not null);

    /// <summary>A follow's root message carries its stop button, which names the follow.</summary>
    private static long? FollowOf(JsonElement message) => CallbackDataOf(message).Select(CallbackData.FollowIdOf).FirstOrDefault(id => id is not null);

    private static IEnumerable<string?> CallbackDataOf(JsonElement message)
    {
        if (!message.TryGetProperty("reply_markup", out var markup) || markup.ValueKind != JsonValueKind.Object || !markup.TryGetProperty("inline_keyboard", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return rows.EnumerateArray()
            .Where(row => row.ValueKind == JsonValueKind.Array)
            .SelectMany(row => row.EnumerateArray())
            .Select(b => Json.Str(b, "callback_data"));
    }

    private static object Markup(IReadOnlyList<IReadOnlyList<Button>> keyboard) =>
        new { inline_keyboard = keyboard.Select(row => row.Select(b => new { text = b.Text, callback_data = b.Data })) };

    private async Task<JsonElement> CallAsync(string method, Dictionary<string, object?> payload, CancellationToken ct)
    {
        foreach (var key in payload.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            payload.Remove(key);
        }

        using var response = await http.PostAsJsonAsync(method, payload, ct);
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!body.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = Json.Str(body, "description") ?? response.StatusCode.ToString();
            throw new TelegramException($"Telegram {method}: {description}");
        }

        return body.GetProperty("result");
    }
}
