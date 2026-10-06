using FeedEater.Storage;

namespace FeedEater.Ranking;

public sealed record LabeledVector(float[] X, bool Liked, double Weight = 1);

/// <summary>
/// A ridge logistic regression on unit-length embeddings, trained by plain gradient descent with the classes weighted equally by total vote weight.
/// Small and deterministic: a few hundred votes train in well under a second.
/// </summary>
public sealed class LogisticModel
{
    private const int Epochs = 300;
    private const double LearningRate = 2.0;
    private const double Ridge = 0.01;

    private readonly float[] _weights;
    private readonly double _bias;

    private LogisticModel(float[] weights, double bias) => (_weights, _bias) = (weights, bias);

    /// <summary>Probability that the item is one he would like.</summary>
    public double Probability(float[] x) => 1 / (1 + Math.Exp(-(Vectors.Dot(x, _weights) + _bias)));

    public static LogisticModel? Train(IReadOnlyList<LabeledVector> data)
    {
        var up = data.Where(d => d.Liked).Sum(d => d.Weight);
        var down = data.Where(d => !d.Liked).Sum(d => d.Weight);
        if (up == 0 || down == 0)
        {
            return null;
        }

        var dims = data[0].X.Length;
        var w = new double[dims];
        var bias = 0d;
        var (upWeight, downWeight) = (0.5 / up, 0.5 / down);
        var grad = new double[dims];
        for (var epoch = 0; epoch < Epochs; epoch++)
        {
            Array.Clear(grad);
            var gradBias = 0d;
            foreach (var d in data)
            {
                var z = bias;
                for (var i = 0; i < dims; i++)
                {
                    z += w[i] * d.X[i];
                }

                var error = (1 / (1 + Math.Exp(-z)) - (d.Liked ? 1 : 0)) * (d.Liked ? upWeight : downWeight) * d.Weight;
                for (var i = 0; i < dims; i++)
                {
                    grad[i] += error * d.X[i];
                }

                gradBias += error;
            }

            for (var i = 0; i < dims; i++)
            {
                w[i] -= LearningRate * (grad[i] + Ridge * w[i]);
            }

            bias -= LearningRate * gradBias;
        }

        return new LogisticModel(w.Select(x => (float)x).ToArray(), bias);
    }
}

/// <summary>Whether the learned term may be used, and the model when it may.</summary>
public static class LearnedTaste
{
    /// <summary>Per-class minimum next to the total: one class alone teaches nothing. Both floors count votes, not weights.</summary>
    internal const int MinPerClass = 10;

    public static (LogisticModel? Model, string? Note) Prepare(IReadOnlyList<LabeledVector> data, TasteOptions o)
    {
        var up = data.Count(d => d.Liked);
        var down = data.Count - up;
        if (data.Count < o.MinVotes || up < MinPerClass || down < MinPerClass)
        {
            return (null, $"Learned taste is switched on but needs {o.MinVotes} votes with at least {MinPerClass} of each kind; there are {data.Count} ({up} up, {down} down). The default ranking is used.");
        }

        return (LogisticModel.Train(data), null);
    }

    /// <summary>Held-out agreement with the votes for the heuristic ranking and for the learned model, 5 folds.</summary>
    public static (double? Heuristic, double? Learned) CrossValidate(IReadOnlyList<LabeledVector> data)
    {
        var heuristic = new List<(double Score, bool Liked)>();
        var learned = new List<(double Score, bool Liked)>();
        for (var fold = 0; fold < 5; fold++)
        {
            var train = data.Where((_, i) => i % 5 != fold).ToList();
            var test = data.Where((_, i) => i % 5 == fold).ToList();
            var model = LogisticModel.Train(train);
            var pos = Vectors.WeightedCentroid(train.Where(d => d.Liked).Select(d => new WeightedVector(d.X, d.Weight)).ToList());
            var neg = Vectors.WeightedCentroid(train.Where(d => !d.Liked).Select(d => new WeightedVector(d.X, d.Weight)).ToList());
            foreach (var t in test)
            {
                if (pos is not null && neg is not null)
                {
                    heuristic.Add((Vectors.Dot(t.X, pos) - Vectors.Dot(t.X, neg), t.Liked));
                }

                if (model is not null)
                {
                    learned.Add((model.Probability(t.X), t.Liked));
                }
            }
        }

        return (Auc(heuristic), Auc(learned));
    }

    /// <summary>Chance a random liked item outscores a random disliked one (ties count half); null when a class is empty.</summary>
    public static double? Auc(IReadOnlyList<(double Score, bool Liked)> scored)
    {
        var up = scored.Where(s => s.Liked).Select(s => s.Score).ToList();
        var down = scored.Where(s => !s.Liked).Select(s => s.Score).ToList();
        return up.Count == 0 || down.Count == 0
            ? null
            : up.Sum(a => down.Sum(b => a > b ? 1.0 : a == b ? 0.5 : 0.0)) / ((double)up.Count * down.Count);
    }
}
