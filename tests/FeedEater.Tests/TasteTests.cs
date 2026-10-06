using Microsoft.Extensions.Options;
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class LogisticModelTests
{
    private static float[] Blend(int dim, int noise) => TestVectors.Blend(dim, noise, 0.25f);

    /// <summary>Liked items live around dimension 0, disliked ones around dimension 1, each with a little unrelated noise.</summary>
    private static List<LabeledVector> Data(int perClass) => Enumerable.Range(0, perClass)
        .SelectMany(i => new[] { new LabeledVector(Blend(0, 10 + i), true), new LabeledVector(Blend(1, 300 + i), false) }).ToList();

    [Fact]
    public void Learns_a_separable_taste_and_scores_unseen_items_the_right_way_round()
    {
        var model = LogisticModel.Train(Data(30))!;

        Assert.True(model.Probability(Blend(0, 900)) > 0.6);
        Assert.True(model.Probability(Blend(1, 901)) < 0.4);
    }

    [Fact]
    public void Explicit_weight_one_trains_exactly_what_unweighted_data_trained_before_weights_existed()
    {
        // Probabilities recorded from the pre-weight implementation on Data(30).
        var model = LogisticModel.Train(Data(30).Select(d => d with { Weight = 1 }).ToList())!;

        Assert.Equal(0.9387600201703267, model.Probability(Blend(0, 900)), 12);
        Assert.Equal(0.061239979829673406, model.Probability(Blend(1, 901)), 12);
        Assert.Equal(0.9467188338180208, model.Probability(TestVectors.OneHot(0)), 12);
        Assert.Equal(0.053281166181979096, model.Probability(TestVectors.OneHot(1)), 12);
        Assert.Equal(model.Probability(TestVectors.OneHot(0)), LogisticModel.Train(Data(30))!.Probability(TestVectors.OneHot(0)));
    }

    [Fact]
    public void Heavier_examples_pull_the_model_toward_them_and_a_weight_is_a_repeat_count()
    {
        var a = TestVectors.Blend(0, 10, 0.1f);
        var b = TestVectors.Blend(2, 11, 0.1f);
        var down = Enumerable.Range(0, 10).Select(i => new LabeledVector(Blend(1, 300 + i), false)).ToList();
        List<LabeledVector> Liked(double weightA) => Enumerable.Range(0, 5).Select(i => new LabeledVector(TestVectors.Blend(0, 20 + i, 0.1f), true, weightA))
            .Concat(Enumerable.Range(0, 5).Select(i => new LabeledVector(TestVectors.Blend(2, 40 + i, 0.1f), true))).ToList();

        var even = LogisticModel.Train(Liked(1).Concat(down).ToList())!;
        var heavy = LogisticModel.Train(Liked(3).Concat(down).ToList())!;

        Assert.True(heavy.Probability(a) > even.Probability(a));
        Assert.True(heavy.Probability(b) < even.Probability(b));

        var repeated = Liked(1).Take(5).SelectMany(d => new[] { d, d, d }).Concat(Liked(1).Skip(5)).Concat(down).ToList();
        Assert.Equal(heavy.Probability(a), LogisticModel.Train(repeated)!.Probability(a), 9);
    }

    [Fact]
    public void Needs_both_classes_and_is_deterministic()
    {
        Assert.Null(LogisticModel.Train([new LabeledVector(TestVectors.OneHot(0), true)]));
        Assert.Null(LogisticModel.Train([]));
        Assert.Equal(LogisticModel.Train(Data(10))!.Probability(TestVectors.OneHot(0)), LogisticModel.Train(Data(10))!.Probability(TestVectors.OneHot(0)));
    }

    [Fact]
    public void Refuses_with_a_clear_note_below_the_vote_floor_or_when_a_class_is_thin()
    {
        var o = new TasteOptions { Learn = true, MinVotes = 100 };

        var (few, note) = LearnedTaste.Prepare(Data(30), o);
        Assert.Null(few);
        Assert.Equal("Learned taste is switched on but needs 100 votes with at least 10 of each kind; there are 60 (30 up, 30 down). The default ranking is used.", note);

        var lopsided = Data(50).Concat(Enumerable.Range(0, 60).Select(i => new LabeledVector(Blend(0, 500 + i), true))).ToList();
        lopsided.RemoveAll(d => !d.Liked && lopsided.IndexOf(d) > 5 * 2);
        var (thin, thinNote) = LearnedTaste.Prepare(lopsided, o);
        Assert.Null(thin);
        Assert.Contains("at least 10 of each kind", thinNote, StringComparison.Ordinal);

        var (model, ok) = LearnedTaste.Prepare(Data(60), o);
        Assert.NotNull(model);
        Assert.Null(ok);
    }

    [Fact]
    public void The_vote_floors_count_rows_so_a_heavy_vote_never_reaches_them_early()
    {
        var o = new TasteOptions { Learn = true, MinVotes = 100 };
        var heavy = Data(30).Select(d => d with { Weight = 3 }).ToList();   // 60 rows, total weight 180

        var (few, note) = LearnedTaste.Prepare(heavy, o);
        Assert.Null(few);
        Assert.Equal("Learned taste is switched on but needs 100 votes with at least 10 of each kind; there are 60 (30 up, 30 down). The default ranking is used.", note);

        var thin = Enumerable.Range(0, 9).Select(i => new LabeledVector(Blend(0, 10 + i), true, 3))
            .Concat(Enumerable.Range(0, 91).Select(i => new LabeledVector(Blend(1, 300 + i), false))).ToList();
        Assert.Null(LearnedTaste.Prepare(thin, o).Model);

        Assert.NotNull(LearnedTaste.Prepare(Data(60).Select(d => d with { Weight = 0.5 }).ToList(), o).Model);
    }

    [Fact]
    public void Cross_validation_scores_a_separable_taste_high_and_chance_for_random_labels()
    {
        var (heuristic, learned) = LearnedTaste.CrossValidate(Data(30));
        Assert.True(heuristic > 0.95 && learned > 0.95);

        var random = Enumerable.Range(0, 60).Select(i => new LabeledVector(TestVectors.OneHot(i), i % 2 == 0)).ToList();
        var (_, chance) = LearnedTaste.CrossValidate(random);
        Assert.InRange(chance!.Value, 0.2, 0.8);
    }

    [Fact]
    public void Auc_handles_ties_and_missing_classes()
    {
        Assert.Equal(0.5, LearnedTaste.Auc([(1, true), (1, false)]));
        Assert.Equal(1.0, LearnedTaste.Auc([(2, true), (1, false)]));
        Assert.Null(LearnedTaste.Auc([(2, true)]));
    }

    [Fact]
    public void The_learned_term_is_bounded_and_absent_by_default()
    {
        var w = new WeightsOptions();
        var profiles = new[] { new Profile { Key = "homelab", Kind = "project", Description = "d", Embedding = TestVectors.OneHot(0) } };
        var x = TestVectors.OneHot(0);
        var plain = Scorer.Score(1, null, x, profiles, Taste.Empty, w);
        var model = LogisticModel.Train(Data(20))!;

        var with = Scorer.Score(1, null, x, profiles, Taste.Empty with { Learned = model }, w);

        Assert.Equal(1.0, plain.Score, 6);   // the profile fit alone: unchanged default ranking
        Assert.InRange(with.Score - plain.Score, -w.Learned / 2, w.Learned / 2);
        Assert.NotEqual(plain.Score, with.Score);
    }

    [Fact]
    public void The_report_refuses_with_few_votes_and_only_suggests_enabling_when_the_model_is_clearly_ahead()
    {
        var few = TasteReport.Render(Data(10), new TasteOptions());
        Assert.Contains("**Too few votes to learn anything.**", few, StringComparison.Ordinal);
        Assert.Contains("would be refused", few, StringComparison.Ordinal);
        Assert.Contains("is off", few, StringComparison.Ordinal);

        var enough = TasteReport.Render(Data(60), new TasteOptions { MinVotes = 100 });
        Assert.Contains("| learned (logistic regression on embeddings) |", enough, StringComparison.Ordinal);
        Assert.True(enough.Contains("Leave `FeedEater:Taste:Learn` off.", StringComparison.Ordinal) || enough.Contains("is reasonable", StringComparison.Ordinal));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class LabeledVectorsTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Returns_voted_items_with_embeddings_as_labeled_examples()
    {
        var feedback = new FeedbackStore(pg.Db);
        await feedback.SetVoteAsync(await Seed.ItemAsync(pg, 1, "liked", TestVectors.OneHot(3)), 1, default);
        await feedback.SetVoteAsync(await Seed.ItemAsync(pg, 1, "disliked", TestVectors.OneHot(4)), -1, default);
        await Seed.ItemAsync(pg, 1, "unvoted", TestVectors.OneHot(5));

        var data = await feedback.LabeledVectorsAsync(default);

        Assert.Equal([false, true], data.Select(d => d.Liked).Order());
        Assert.All(data, d => Assert.Equal(TestVectors.Dims, d.X.Length));
    }
}
