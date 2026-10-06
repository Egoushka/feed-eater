using System.Text;
using System.Text.Json;
using Dapper;
using DbUp;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FeedEater.Digest;
using FeedEater.Duels;
using FeedEater.Follow;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

/// <summary>What only shows once duels, follows, the autopsy, the taste map and vote weights are all on.</summary>
public sealed class CallbackKindsTests
{
    private static readonly long Big = long.MaxValue;   // 19 digits, the longest an id can be

    public static TheoryData<string, Type, long?, long?> Kinds => new()
    {
        { CallbackData.Vote(Big, 1), typeof(VoteCallback), Big, null },
        { CallbackData.Vote(Big, -1), typeof(VoteCallback), Big, null },
        { CallbackData.Idea(Big), typeof(IdeaCallback), Big, null },
        { CallbackData.Save(Big), typeof(SaveCallback), Big, null },
        { CallbackData.Follow(Big), typeof(FollowCallback), Big, null },
        { CallbackData.Unfollow(Big), typeof(UnfollowCallback), null, Big },
        { CallbackData.Duel(Big, 'a'), typeof(DuelCallback), null, null },
        { CallbackData.Duel(Big, 'b'), typeof(DuelCallback), null, null },
        { CallbackData.Duel(Big, 's'), typeof(DuelCallback), null, null },
        { CallbackData.Noop, typeof(NoopCallback), null, null },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_callback_kind_parses_back_to_its_own_type_and_names_only_what_it_belongs_to(string data, Type kind, long? item, long? follow)
    {
        Assert.True(Encoding.UTF8.GetByteCount(data) <= 64, data);
        Assert.IsType(kind, CallbackData.Parse(data));
        Assert.Equal(item, CallbackData.ItemIdOf(data));
        Assert.Equal(follow, CallbackData.FollowIdOf(data));
    }

    [Theory]
    [InlineData("d:1")]
    [InlineData("d:1:x")]
    [InlineData("d:x:a")]
    [InlineData("f:")]
    [InlineData("f:1:2")]
    [InlineData("u:-1")]
    [InlineData("v:1")]
    [InlineData("x:1")]
    public void Malformed_payloads_are_nothing_and_name_nothing(string data)
    {
        Assert.Null(CallbackData.Parse(data));
        Assert.Null(CallbackData.ItemIdOf(data));
        Assert.Null(CallbackData.FollowIdOf(data));
    }

    [Fact]
    public void A_button_row_built_for_the_longest_ids_stays_under_the_limit_with_every_button_on()
    {
        var rows = DigestFormatter.Buttons(Big, true, null, null, false, ButtonStyle.Default with { CanSave = true, ToPlane = true }, follow: true);

        Assert.All(rows.SelectMany(r => r), b => Assert.True(Encoding.UTF8.GetByteCount(b.Data) <= 64, b.Data));
        Assert.Contains(rows.SelectMany(r => r), b => b.Data == CallbackData.Follow(Big));
        Assert.Contains(rows.SelectMany(r => r), b => b.Data == CallbackData.Save(Big));
    }

    private static string Update(int id, IReadOnlyList<IReadOnlyList<Button>>? keyboard)
    {
        var markup = keyboard is null ? "" : ",\"reply_markup\":" + JsonSerializer.Serialize(new { inline_keyboard = keyboard.Select(r => r.Select(b => new { text = b.Text, callback_data = b.Data })) });
        return "{\"update_id\":" + id + ",\"message\":{\"message_id\":" + id + ",\"from\":{\"id\":42},\"chat\":{\"id\":42},\"text\":\"hm\",\"reply_to_message\":{\"message_id\":3" + markup + "}}}";
    }

    [Fact]
    public async Task A_reply_is_tied_to_what_it_replies_to_for_each_kind_of_message_the_bot_sends()
    {
        var duel = DuelFormatter.Message(9, new DuelItem { Id = 1, Title = "a", Url = "https://a.example/", Feed = "f" }, new DuelItem { Id = 2, Title = "b", Url = "https://b.example/", Feed = "f" });
        var root = FollowFormatter.Root("Story", "https://s.example/", 14, 5);
        var item = DigestFormatter.Buttons(6, true, 1, "proj", true, null, follow: true);
        var follow = DigestFormatter.Buttons(7, false, null, null, false, null, follow: false);
        var list = FollowFormatter.List([new ActiveFollow { Id = 8, Title = "One", EndsAt = DateTime.UtcNow }, new ActiveFollow { Id = 9, Title = "Two", EndsAt = DateTime.UtcNow }], TimeZoneInfo.Utc);
        var body = "{\"ok\":true,\"result\":[" + string.Join(',', new[] { Update(1, duel.Keyboard), Update(2, root.Keyboard), Update(3, item), Update(4, follow), Update(5, list.Keyboard), Update(6, null) }) + "]}";
        var client = new TelegramClient(new StubHandler((_, _) => StubHandler.Json(body)).Client("http://tg/botT/"));

        var messages = (await client.GetUpdatesAsync(1, 0, default)).Select(u => u.Message!).ToList();

        Assert.Equal((null, null), (messages[0].ReplyToItemId, messages[0].ReplyToFollowId));   // a duel: neither an item nor a follow
        Assert.Equal((null, 5), (messages[1].ReplyToItemId, messages[1].ReplyToFollowId));      // a follow's root message
        Assert.Equal((6, null), (messages[2].ReplyToItemId, messages[2].ReplyToFollowId));      // a digest item or search hit, 🧵 included
        Assert.Equal((7, null), (messages[3].ReplyToItemId, messages[3].ReplyToFollowId));      // a follow update
        Assert.Equal((null, 8), (messages[4].ReplyToItemId, messages[4].ReplyToFollowId));      // the /follows list: its first follow
        Assert.Equal((null, null), (messages[5].ReplyToItemId, messages[5].ReplyToFollowId));   // a message with no buttons
    }
}

[Collection(PostgresCollection.Name)]
public sealed class CombinationStorageTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_duel_vote_trains_at_half_weight_for_the_loser_and_a_later_vote_of_his_own_replaces_it()
    {
        var winner = await Seed.ItemAsync(pg, 1, "Winner", TestVectors.OneHot(1));
        var loser = await Seed.ItemAsync(pg, 1, "Loser", TestVectors.OneHot(2));
        var duels = new DuelStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var duel = await duels.CreateAsync(winner, loser, DateTimeOffset.UtcNow, default);

        await duels.AnswerAsync(duel, 'a', default);

        var up = Assert.Single(await feedback.PositiveWeightedAsync(10, default));
        var down = Assert.Single(await feedback.NegativeWeightedAsync(10, default));
        Assert.Equal(1, up.Weight);
        Assert.Equal(0.5, down.Weight);
        var labeled = await feedback.LabeledVectorsAsync(default);
        Assert.Equal([0.5, 1], labeled.Select(l => l.Weight).Order());

        await feedback.SetVoteAsync(loser, -1, default);   // the owner votes the same way himself: now a plain vote

        Assert.Equal(1, Assert.Single(await feedback.NegativeWeightedAsync(10, default)).Weight);
    }

    [Fact]
    public async Task A_vote_cast_by_hand_before_the_duel_is_answered_keeps_its_value_and_weight()
    {
        var first = await Seed.ItemAsync(pg, 1, "First", TestVectors.OneHot(1));
        var second = await Seed.ItemAsync(pg, 1, "Second", TestVectors.OneHot(2));
        var duels = new DuelStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var duel = await duels.CreateAsync(first, second, DateTimeOffset.UtcNow, default);
        await feedback.SetVoteAsync(second, 1, default, weight: 2);

        await duels.AnswerAsync(duel, 'a', default);

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var votes = (await c.QueryAsync<(long Item, int Value, double Weight)>("select item_id, value::int, weight::double precision from votes order by item_id")).ToList();
        Assert.Equal([(first, 1, 1.0), (second, 1, 2.0)], votes);
    }

    [Fact]
    public async Task Reset_empties_every_table_the_migrations_create()
    {
        var a = await Seed.ItemAsync(pg, 1, "A", TestVectors.OneHot(1));
        var b = await Seed.ItemAsync(pg, 1, "B", TestVectors.OneHot(2));
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var follow = await new FollowStore(pg.Db).StartAsync(a, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), default);
        await new FollowStore(pg.Db).AddSentAsync(follow, b, default);
        await new DuelStore(pg.Db).CreateAsync(a, b, DateTimeOffset.UtcNow, default);
        await c.ExecuteAsync("insert into repo_snapshots (item_id, repo, taken_at, stars) values (@a, 'o/r', now(), 3)", new { a });
        await c.ExecuteAsync("insert into autopsy (month, built_at, report, message) values ('2026-10-01', now(), '{}'::jsonb, 'm')");

        await pg.ResetAsync();

        var tables = (await c.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE' and table_name <> 'schemaversions'")).ToList();
        Assert.Contains("duels", tables);
        Assert.Contains("follows", tables);
        Assert.Contains("follow_items", tables);
        Assert.Contains("repo_snapshots", tables);
        Assert.Contains("autopsy", tables);
        foreach (var table in tables)
        {
            Assert.Equal(0, await c.ExecuteScalarAsync<long>($"select count(*) from \"{table}\""));
        }
    }

    private static string Database(string name, PostgresFixture pg) => new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = name, Pooling = false }.ConnectionString;

    private static IEnumerable<string> ScriptsUpTo(int last) => typeof(DatabaseMigrator).Assembly.GetManifestResourceNames()
        .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && int.Parse(n.Split(".Migrations.")[1][..4], System.Globalization.CultureInfo.InvariantCulture) <= last);

    private async Task<string> CreateAsync()
    {
        var name = $"migration_{Guid.NewGuid():N}";
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync($"create database {name}");
        return name;
    }

    private async Task DropAsync(string name)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync($"drop database {name} with (force)");
    }

    private static async Task<List<string>> TablesAsync(string connection)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        return (await c.QueryAsync<string>("select table_name from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE'")).ToList();
    }

    [Fact]
    public void The_migration_numbers_are_the_agreed_ones_with_0013_left_free()
    {
        var numbers = ScriptsUpTo(9999).Select(n => n.Split(".Migrations.")[1][..4]).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["0001", "0002", "0003", "0004", "0005", "0006", "0007", "0008", "0009", "0010", "0011", "0012", "0014", "0015", "0016"], numbers);
    }

    [Fact]
    public async Task Every_migration_applies_on_a_fresh_database()
    {
        var name = await CreateAsync();
        try
        {
            new DatabaseMigrator(Database(name, pg), NullLogger<DatabaseMigrator>.Instance).Run();

            var tables = await TablesAsync(Database(name, pg));
            Assert.All(new[] { "duels", "follows", "follow_items", "repo_snapshots", "autopsy", "votes" }, t => Assert.Contains(t, tables));
        }
        finally
        {
            await DropAsync(name);
        }
    }

    [Fact]
    public async Task A_database_with_0001_to_0011_applied_takes_0012_and_0014_to_0016_and_keeps_its_rows()
    {
        var name = await CreateAsync();
        var connection = Database(name, pg);
        try
        {
            var old = DeployChanges.To.PostgresqlDatabase(connection)
                .WithScripts(ScriptsUpTo(11).Select(n =>
                {
                    using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(n)!;
                    return new DbUp.Engine.SqlScript(n, new StreamReader(stream).ReadToEnd());
                }))
                .WithTransactionPerScript().LogToNowhere().Build().PerformUpgrade();
            Assert.True(old.Successful);
            await using (var c = new NpgsqlConnection(connection))
            {
                await c.OpenAsync();
                await c.ExecuteAsync("insert into feeds (id, title) values (3, 'Feed')");
                await c.ExecuteAsync(
                    """
                    insert into items (feed_id, url, canonical_url, title_hash, title, published_at)
                    values (3, 'https://x.example/1', 'https://x.example/1', '', 'One', now()), (3, 'https://x.example/2', 'https://x.example/2', '', 'Two', now())
                    """);
                await c.ExecuteAsync("insert into votes (item_id, value) values (1, 1), (2, -1)");
            }

            new DatabaseMigrator(connection, NullLogger<DatabaseMigrator>.Instance).Run();

            await using var after = new NpgsqlConnection(connection);
            await after.OpenAsync();
            Assert.Equal<double>([1, 1], await after.QueryAsync<double>("select weight::double precision from votes order by item_id"));
            var tables = await TablesAsync(connection);
            Assert.All(new[] { "duels", "follows", "follow_items", "repo_snapshots", "autopsy" }, t => Assert.Contains(t, tables));
            await after.ExecuteAsync("insert into duels (a, b) values (1, 2)");
            await after.ExecuteAsync("insert into follows (root_item_id, started_at, ends_at) values (1, now(), now())");
            Assert.Equal(["0012", "0014", "0015", "0016"], (await after.QueryAsync<string>("select scriptname from schemaversions order by scriptname"))
                .Select(s => s.Split(".Migrations.")[1][..4]).Where(n => string.CompareOrdinal(n, "0011") > 0));
        }
        finally
        {
            await DropAsync(name);
        }
    }
}
