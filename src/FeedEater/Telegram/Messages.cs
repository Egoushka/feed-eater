namespace FeedEater.Telegram;

public sealed record Button(string Text, string Data);

public sealed record OutMessage(string Html, IReadOnlyList<IReadOnlyList<Button>>? Keyboard = null);
