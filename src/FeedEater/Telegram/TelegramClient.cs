using System.Net.Http.Json;
using System.Text.Json;

namespace FeedEater.Telegram;

public sealed record TgCallback(string Id, long FromId, long ChatId, long MessageId, string? Data);

public sealed record TgMessage(long FromId, long ChatId, string? Text);

public sealed record TgUpdate(long UpdateId, TgCallback? Callback, TgMessage? Message = null);

public sealed class TelegramException(string message) : Exception(message);

/// <summary>The four Bot API calls feed-eater needs. The token is in the base address, so URIs are never logged.</summary>
public sealed class TelegramClient(HttpClient http)
{
    public async Task<long> SendAsync(long chatId, OutMessage message, CancellationToken ct)
    {
        var result = await CallAsync("sendMessage", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = message.Html,
            ["parse_mode"] = "HTML",
            ["link_preview_options"] = new { is_disabled = true },
            ["reply_markup"] = message.Keyboard is null ? null : Markup(message.Keyboard),
        }, ct);
        return result.GetProperty("message_id").GetInt64();
    }

    public Task EditButtonsAsync(long chatId, long messageId, IReadOnlyList<IReadOnlyList<Button>> keyboard, CancellationToken ct) =>
        CallAsync("editMessageReplyMarkup", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["reply_markup"] = Markup(keyboard),
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
            ? new TgMessage(from.GetProperty("id").GetInt64(), chat.GetProperty("id").GetInt64(), Json.Str(m, "text"))
            : null;

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
