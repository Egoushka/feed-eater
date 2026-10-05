using FeedEater;
using FeedEater.Mcp;
using FeedEater.Storage;
using Serilog;
using Serilog.Formatting.Compact;

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

app.MapGet("/healthz", async (FeedDb db, CancellationToken ct) => await db.PingAsync(ct) ? Results.Ok() : Results.StatusCode(503));
app.MapFeedEaterMcp(token);
app.Run();

public partial class Program;
