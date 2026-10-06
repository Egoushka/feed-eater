using FeedEater;
using FeedEater.Eval;
using FeedEater.Mcp;
using FeedEater.Storage;
using FeedEater.Ui;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

if (args.FirstOrDefault() == "eval")
{
    return await EvalCommand.RunAsync(args[1..]);
}

if (args.FirstOrDefault() == "taste")
{
    return await TasteCommand.RunAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddFeedEater(builder.Configuration);

var app = builder.Build();
app.Services.GetRequiredService<DatabaseMigrator>().Run();

var token = app.Configuration["Mcp:Token"];
if (string.IsNullOrEmpty(token))
{
    app.Logger.LogWarning("Mcp:Token is not set; /mcp refuses every request");
}

var feedEater = app.Services.GetRequiredService<IOptions<FeedEaterOptions>>().Value;
if (feedEater.RunJobs && feedEater.Telegram.Token.Length == 0)
{
    app.Logger.LogWarning("FeedEater:Telegram:Token is not set; the digest and the Telegram poller are off");
}

app.MapGet("/healthz", async (FeedDb db, CancellationToken ct) => await db.PingAsync(ct) ? Results.Ok() : Results.StatusCode(503));
app.MapFeedEaterMcp(token);
app.MapFeedEaterUi();
app.Run();
return 0;

public partial class Program;
